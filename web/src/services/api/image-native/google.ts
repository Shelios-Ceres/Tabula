import { getNativeImageCapabilities } from "@/lib/image-model-capabilities";
import type { ImageGenerationResult, ImageResponseMetadata } from "@/types/image";
import type { ImageJobOptions, ImageJobResult, ImageTransportContext, NativeImageRequest } from "@/types/image-generation";
import { checkResponse, imageToDataUrl, readSse, withLocalProxy } from "./common";

type RecordValue = Record<string, any>;
type OutputOrder = NonNullable<ImageJobResult["outputOrder"]>;

function emit(options: ImageJobOptions, event: Parameters<NonNullable<ImageJobOptions["onEvent"]>>[0]) {
    options.onEvent?.(event);
}

function validate(request: NativeImageRequest, options: ImageJobOptions) {
    if (request.count !== 1) throw new Error("Google Interactions API 仅支持图片数量 1");
    if (options.mask) throw new Error("Google Interactions API 不支持蒙版编辑");
    if (request.outputFormat !== "jpeg") throw new Error("Google Interactions API 仅支持 JPEG 输出格式");

    const google = request.native.google || {};
    const capabilities = getNativeImageCapabilities("google", request.model);
    if (!capabilities.supported) throw new Error(`当前原生渠道不支持型号 ${request.model}`);
    if (request.references.length > capabilities.maxReferences) throw new Error(`${request.model} 最多支持 ${capabilities.maxReferences} 张参考图`);
    if (google.imageSize && !capabilities.sizes.includes(google.imageSize)) throw new Error(`${request.model} 不支持图像尺寸 ${google.imageSize}`);
    if (google.aspectRatio && !capabilities.ratios.includes(google.aspectRatio)) throw new Error(`${request.model} 不支持宽高比 ${google.aspectRatio}`);
    if (google.thinkingLevel && google.thinkingLevel !== "auto" && (!capabilities.supportsThinking || !capabilities.thinkingLevels.includes(google.thinkingLevel))) {
        throw new Error(`${request.model} 不支持 thinkingLevel ${google.thinkingLevel}`);
    }
    if (google.webSearch && !capabilities.supportsWebSearch) throw new Error(`${request.model} 不支持 web_search`);
    if (google.imageSearch && !capabilities.supportsImageSearch) throw new Error(`${request.model} 不支持 image_search`);
}

/** The channel base URL already carries the selected API version (v1 or v1beta). */
function endpoint(context: ImageTransportContext) {
    return withLocalProxy(`${context.baseUrl.replace(/\/+$/, "")}/interactions`);
}

function imageBlock(dataUrl: string) {
    const match = /^data:([^;,]+)(?:;[^,]*)?,(.*)$/s.exec(dataUrl);
    return { type: "image", mime_type: match?.[1] || "image/png", data: match?.[2] || dataUrl };
}

async function inputBlocks(request: NativeImageRequest) {
    return [
        { type: "text", text: request.prompt },
        ...(await Promise.all(request.references.map(async (reference) => imageBlock(await imageToDataUrl(reference))))),
    ];
}

function requestBody(request: NativeImageRequest, options: ImageJobOptions, input: unknown[]) {
    const google = request.native.google || {};
    const usePreviousInteraction = google.store === true && options.conversation?.stored === true && Boolean(options.conversation.remoteId);
    const userInput = { type: "user_input", content: input };
    const history = options.conversation?.history || [];
    const responseFormat = [
        ...(google.includeText ? [{ type: "text" }] : []),
        {
            type: "image",
            mime_type: "image/jpeg",
            ...(google.aspectRatio ? { aspect_ratio: google.aspectRatio } : {}),
            ...(google.imageSize ? { image_size: google.imageSize } : {}),
        },
    ];

    return {
        model: request.model,
        input: usePreviousInteraction ? [userInput] : [...history, userInput],
        ...(usePreviousInteraction ? { previous_interaction_id: options.conversation!.remoteId } : {}),
        store: google.store === true,
        ...(google.webSearch || google.imageSearch ? {
            tools: [{
                type: "google_search",
                search_types: [...(google.webSearch ? ["web_search"] : []), ...(google.imageSearch ? ["image_search"] : [])],
            }],
        } : {}),
        ...(google.thinkingLevel && google.thinkingLevel !== "auto" ? { generation_config: { thinking_level: google.thinkingLevel } } : {}),
        response_format: google.includeText ? responseFormat : responseFormat[0],
    };
}

function appendContent(step: RecordValue, key: "content" | "summary", delta: RecordValue) {
    const content = Array.isArray(step[key]) ? step[key] as RecordValue[] : [];
    step[key] = content;
    const part = delta.type === "thought_summary" ? delta.content : delta;
    if (!part || typeof part !== "object") return;
    const requestedIndex = delta.content_index ?? delta.block_index;
    const index = Number.isInteger(requestedIndex)
        ? Number(requestedIndex)
        : content.findLastIndex((item) => item?.type === part.type);
    const targetIndex = index >= 0 ? index : content.length;
    const previous = content[targetIndex];

    if (part.type === "text") content[targetIndex] = { ...previous, ...part, text: `${previous?.text || ""}${part.text || ""}` };
    else if (part.type === "image") content[targetIndex] = { ...previous, ...part, data: `${previous?.data || ""}${part.data || ""}` };
    else content[targetIndex] = { ...previous, ...part };
}

function appendStepDelta(step: RecordValue, delta: RecordValue) {
    if (delta.type === "text" || delta.type === "image") {
        appendContent(step, "content", delta);
        return;
    }
    if (delta.type === "thought_signature") {
        step.signature = delta.signature;
        return;
    }
    if (delta.type === "thought_summary") {
        appendContent(step, "summary", delta);
        return;
    }
    if (delta.type === "arguments_delta") {
        step.arguments = `${step.arguments || ""}${delta.arguments || ""}`;
        return;
    }
    Object.assign(step, delta);
}

function streamError(event: RecordValue) {
    const error = event.error && typeof event.error === "object" ? event.error : event.interaction?.error;
    return typeof error?.message === "string" ? error.message : "Google Interactions 流式请求失败";
}

function strings(value: unknown): string[] {
    if (typeof value === "string") return [value];
    if (!Array.isArray(value)) return [];
    return value.filter((item): item is string => typeof item === "string");
}

function groundingMetadata(steps: unknown[]) {
    const searchSuggestions: string[] = [];
    const searchQueries: string[] = [];
    const citations: Array<{ url: string; title?: string; start_index?: number; end_index?: number }> = [];
    for (const step of steps) {
        if (!step || typeof step !== "object") continue;
        const record = step as RecordValue;
        if (record.type === "google_search_result") {
            const results = Array.isArray(record.result) ? record.result : [record.result];
            for (const result of results) {
                if (!result || typeof result !== "object") continue;
                const value = result as RecordValue;
                searchSuggestions.push(...strings(value.search_suggestions));
                searchQueries.push(...strings(value.queries), ...strings(value.search_queries), ...strings(value.query));
            }
        }
        if (record.type !== "model_output") continue;
        for (const content of Array.isArray(record.content) ? record.content : []) {
            for (const annotation of Array.isArray(content?.annotations) ? content.annotations : []) {
                if (!annotation || typeof annotation !== "object") continue;
                const value = annotation as RecordValue;
                const citation = value.type === "url_citation" && value.url_citation && typeof value.url_citation === "object" ? value.url_citation : value;
                if (value.type !== "url_citation" || typeof citation.url !== "string") continue;
                citations.push({
                    url: citation.url,
                    ...(typeof citation.title === "string" ? { title: citation.title } : {}),
                    ...(typeof citation.start_index === "number" ? { start_index: citation.start_index } : {}),
                    ...(typeof citation.end_index === "number" ? { end_index: citation.end_index } : {}),
                });
            }
        }
    }
    return { searchSuggestions: [...new Set(searchSuggestions)], searchQueries: [...new Set(searchQueries)], citations };
}

async function streamPayload(response: Response, options: ImageJobOptions): Promise<RecordValue> {
    const steps = new Map<number, RecordValue>();
    let interaction: RecordValue = {};
    let completed = false;

    await readSse(response, ({ event, data }) => {
        if (data === "[DONE]") return;
        const payload = JSON.parse(data) as RecordValue;
        const eventType = payload.event_type || event;

        if (eventType === "error" || eventType === "interaction.failed" || eventType === "failed") throw new Error(streamError(payload));
        if (eventType === "interaction.created") {
            interaction = { ...interaction, ...(payload.interaction || {}) };
            return;
        }
        if (eventType === "interaction.completed") {
            interaction = { ...interaction, ...(payload.interaction || {}) };
            completed = true;
            if (interaction.status && interaction.status !== "completed") throw new Error(streamError(payload));
            return;
        }

        const index = payload.index;
        if (!Number.isInteger(index)) return;
        if (eventType === "step.start") {
            const step = payload.step;
            if (!step || typeof step !== "object") throw new Error("Google Interactions 流式响应缺少 step.start 内容");
            const started = structuredClone(step) as RecordValue;
            steps.set(index, started);
            if (started.type === "model_output") {
                for (const content of Array.isArray(started.content) ? started.content : []) {
                    if (content?.type === "text" && typeof content.text === "string") emit(options, { type: "text", text: content.text });
                }
            }
            return;
        }

        const step = steps.get(index);
        if (!step) throw new Error("Google Interactions 流式响应在 step.start 前发送了步骤数据");
        if (eventType === "step.delta") {
            const delta = payload.delta || {};
            if (step.type === "model_output" && delta.type === "text" && typeof delta.text === "string") emit(options, { type: "text", text: delta.text });
            appendStepDelta(step, delta);
        } else if (eventType === "step.stop") {
            Object.assign(step, payload.step || {}, payload.usage ? { usage: payload.usage } : {}, payload.step_usage ? { step_usage: payload.step_usage } : {});
            if (step.type === "model_output") {
                for (const content of Array.isArray(step.content) ? step.content : []) {
                    if (content?.type !== "image" || typeof content.data !== "string") continue;
                    emit(options, {
                        type: "preview",
                        image: {
                            id: crypto.randomUUID(),
                            dataUrl: content.data.startsWith("data:") ? content.data : `data:${content.mime_type || "image/png"};base64,${content.data}`,
                        },
                    });
                }
            }
        }
    });

    if (!completed) throw new Error("Google Interactions 流式响应未完成");
    return { ...interaction, steps: [...steps.entries()].sort(([left], [right]) => left - right).map(([, step]) => step) };
}

function finish(payload: RecordValue, request: NativeImageRequest, options: ImageJobOptions, context: ImageTransportContext, input: unknown[], emitFinalText = true): ImageJobResult {
    const steps = Array.isArray(payload.steps) ? payload.steps : [];
    const grounding = groundingMetadata(steps);
    const providerMetadata = {
        interactionId: payload.id,
        interactionModel: payload.model,
        grounding: payload.grounding,
        searchSuggestions: [...new Set([...grounding.searchSuggestions, ...strings(payload.search_suggestions)])],
        citations: grounding.citations,
        ...(grounding.searchQueries.length ? { searchQueries: grounding.searchQueries } : {}),
    };
    const metadata: ImageResponseMetadata = {
        provider: "google",
        protocol: "google-interactions",
        model: typeof payload.model === "string" ? payload.model : request.model,
        rawUsage: payload.usage,
        providerMetadata,
    };
    const images: ImageGenerationResult[] = [];
    const texts: string[] = [];
    const outputOrder: OutputOrder = [];

    for (const step of steps) {
        if (step?.type !== "model_output") continue;
        for (const content of Array.isArray(step.content) ? step.content : []) {
            if (content?.type === "text" && typeof content.text === "string") {
                outputOrder.push({ kind: "text", index: texts.push(content.text) - 1 });
            }
            if (content?.type === "image" && typeof content.data === "string") {
                const image = {
                    id: crypto.randomUUID(),
                    dataUrl: content.data.startsWith("data:") ? content.data : `data:${content.mime_type || "image/png"};base64,${content.data}`,
                    response: metadata,
                } satisfies ImageGenerationResult;
                outputOrder.push({ kind: "image", index: images.push(image) - 1 });
            }
        }
    }
    if (!images.length) throw new Error(payload.error?.message || "Google Interactions 未返回图片");

    for (const entry of outputOrder) {
        if (entry.kind === "image") emit(options, { type: "output", index: entry.index, image: images[entry.index] });
        else if (emitFinalText) emit(options, { type: "text", text: texts[entry.index] });
    }
    return {
        images,
        texts,
        errors: [],
        outputOrder,
        providerMetadata,
        conversation: {
            provider: "google",
            protocol: "google-interactions",
            model: typeof payload.model === "string" ? payload.model : request.model,
            remoteId: typeof payload.id === "string" ? payload.id : undefined,
            stored: request.native.google?.store === true,
            createdAt: new Date().toISOString(),
            channelId: context.channelId,
            history: [...(options.conversation?.history || []), { type: "user_input", content: input }, ...steps],
        },
    };
}

export async function requestGoogleImageJob(context: ImageTransportContext, request: NativeImageRequest, options: ImageJobOptions = {}): Promise<ImageJobResult> {
    validate(request, options);
    emit(options, { type: "started" });
    const input = await inputBlocks(request);
    const response = await fetch(endpoint(context), {
        method: "POST",
        headers: { "x-goog-api-key": context.apiKey, "Content-Type": "application/json" },
        body: JSON.stringify({ ...requestBody(request, options, input), ...(request.native.stream ? { stream: true } : {}) }),
        signal: options.signal,
    });

    try {
        await checkResponse(response);
        const payload = request.native.stream ? await streamPayload(response, options) : await response.json() as RecordValue;
        if (payload.error || (payload.status && payload.status !== "completed")) throw new Error(streamError(payload));
        const result = finish(payload, request, options, context, input, !request.native.stream);
        emit(options, { type: "completed", result });
        return result;
    } catch (error) {
        if (options.signal?.aborted) emit(options, { type: "client-aborted" });
        throw error;
    }
}
