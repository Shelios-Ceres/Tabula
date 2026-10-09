import { imageRequestError, requestEdit, requestGeneration, resolveRequestSize } from "@/services/api/image";
import { defaultConfig, type AiConfig } from "@/stores/use-config-store";
import type { ImageGenerationResult, ImageResponseMetadata } from "@/types/image";
import type { ImageJobOptions, ImageJobResult, ImageTransportContext, NativeImageRequest } from "@/types/image-generation";
import { checkResponse, imageToDataUrl, readSse, withLocalProxy } from "./common";

type RecordValue = Record<string, any>;
const formats = new Set(["png", "jpeg", "webp"]);

function emit(options: ImageJobOptions, event: Parameters<NonNullable<ImageJobOptions["onEvent"]>>[0]) {
    options.onEvent?.(event);
}

function mimeType(format: string) {
    return format === "jpeg" ? "image/jpeg" : `image/${format}`;
}

function endpoint(context: ImageTransportContext, path: string) {
    const baseUrl = context.baseUrl.replace(/\/+$/, "");
    return withLocalProxy(`${/\/v1$/i.test(baseUrl) ? baseUrl : `${baseUrl}/v1`}${path}`);
}

function headers(context: ImageTransportContext) {
    return { Authorization: `Bearer ${context.apiKey}` };
}

function safeError(value: unknown, status?: number, requestId?: string) {
    const record = value && typeof value === "object" ? value as RecordValue : {};
    return imageRequestError({ ...record, status, requestId }, "OpenAI request failed");
}

function outputFormat(record: RecordValue, fallback: NativeImageRequest["outputFormat"]) {
    return formats.has(record.output_format) ? record.output_format as NativeImageRequest["outputFormat"] : fallback;
}

function resultFromItem(item: RecordValue, format: NativeImageRequest["outputFormat"], response: ImageResponseMetadata): ImageGenerationResult | null {
    if (typeof item.b64_json === "string") return { id: crypto.randomUUID(), dataUrl: `data:${mimeType(format)};base64,${item.b64_json}`, response };
    if (typeof item.url === "string") return { id: crypto.randomUUID(), dataUrl: item.url, response };
    return null;
}

function imageItems(payload: RecordValue) {
    return [
        ...(Array.isArray(payload.data) ? payload.data : []),
        ...(Array.isArray(payload.images) ? payload.images : []),
        ...(Array.isArray(payload.results) ? payload.results : []),
        ...(typeof payload.b64_json === "string" || typeof payload.url === "string" ? [payload] : []),
    ] as RecordValue[];
}

function imagesResult(payload: RecordValue, request: NativeImageRequest, requestId?: string): ImageGenerationResult[] {
    const format = outputFormat(payload, request.outputFormat);
    const metadata: ImageResponseMetadata = {
        provider: "openai",
        protocol: "openai-images",
        model: request.model,
        outputFormat: format,
        quality: typeof payload.quality === "string" ? payload.quality : undefined,
        size: typeof payload.size === "string" ? payload.size : undefined,
        background: typeof payload.background === "string" ? payload.background : undefined,
        usage: payload.usage,
        requestId,
    };
    return imageItems(payload).map((item) => resultFromItem(item, outputFormat(item, format), metadata)).filter((image): image is ImageGenerationResult => Boolean(image));
}

function nativeImagesConfig(context: ImageTransportContext, request: NativeImageRequest): AiConfig {
    const channelId = context.channelId || "native-openai";
    const selectedModel = `${channelId}::${request.model}`;
    return {
        ...defaultConfig,
        baseUrl: context.baseUrl,
        apiKey: context.apiKey,
        apiFormat: "openai",
        channels: [{
            id: channelId,
            name: "Native OpenAI",
            vendor: "openai",
            baseUrl: context.baseUrl,
            apiKey: context.apiKey,
            models: [{ name: request.model, capability: "image" }],
        }],
        model: selectedModel,
        imageModel: selectedModel,
        models: [selectedModel],
        systemPrompt: "",
        quality: request.quality,
        size: request.size,
        background: request.background,
        outputFormat: request.outputFormat,
        outputCompression: request.outputCompression,
        moderation: request.moderation,
        imageNative: request.native,
        count: String(request.count),
    };
}

function validateStreamingImages(request: NativeImageRequest) {
    if (!Number.isInteger(request.count) || request.count < 1 || request.count > 10) throw new Error("图片数量必须是 1–10 的整数");
    if (!formats.has(request.outputFormat)) throw new Error("输出格式无效");
    if (request.outputFormat === "jpeg" && request.background === "transparent") throw new Error("JPEG 不支持透明背景");
}

async function imagesApi(context: ImageTransportContext, request: NativeImageRequest, options: ImageJobOptions): Promise<ImageJobResult> {
    emit(options, { type: "started" });
    if (!request.native.stream) {
        const config = nativeImagesConfig(context, request);
        const legacyImages = request.references.length || options.mask
            ? await requestEdit(config, request.prompt, request.references, { signal: options.signal, mask: options.mask })
            : await requestGeneration(config, request.prompt, { signal: options.signal });
        const images = legacyImages.map((image) => ({
            ...image,
            response: { ...image.response, provider: "openai" as const, protocol: "openai-images" as const, model: request.model },
        }));
        images.forEach((image, index) => emit(options, { type: "output", index, image }));
        const result = { images, texts: [], errors: [] } satisfies ImageJobResult;
        emit(options, { type: "completed", result });
        return result;
    }

    validateStreamingImages(request);
    return streamImages(context, request, options, Boolean(request.references.length || options.mask));
}

async function streamImages(context: ImageTransportContext, request: NativeImageRequest, options: ImageJobOptions, editing: boolean): Promise<ImageJobResult> {
    const common = {
        model: request.model,
        prompt: request.prompt,
        n: request.count,
        quality: request.quality,
        size: request.size,
        background: request.background,
        output_format: request.outputFormat,
        moderation: request.moderation,
        ...(request.outputFormat === "jpeg" || request.outputFormat === "webp" ? { output_compression: request.outputCompression } : {}),
        stream: true,
        partial_images: Math.max(0, Math.min(3, request.native.openai?.partialImages ?? 0)),
    };
    const form = editing ? new FormData() : undefined;
    if (form) {
        for (const [key, value] of Object.entries(common)) if (value !== undefined && value !== "auto") form.set(key, String(value));
        for (const reference of request.references) {
            const dataUrl = await imageToDataUrl(reference);
            const blob = await (await fetch(dataUrl)).blob();
            form.append(request.references.length > 1 ? "image[]" : "image", new File([blob], reference.name || "reference.png", { type: blob.type || "image/png" }));
        }
        if (options.mask) {
            const dataUrl = await imageToDataUrl(options.mask);
            const blob = await (await fetch(dataUrl)).blob();
            form.set("mask", new File([blob], options.mask.name || "mask.png", { type: blob.type || "image/png" }));
        }
    }
    const response = await fetch(endpoint(context, editing ? "/images/edits" : "/images/generations"), {
        method: "POST",
        headers: form ? headers(context) : { ...headers(context), "Content-Type": "application/json" },
        body: form || JSON.stringify(common),
        signal: options.signal,
    });
    let final: RecordValue | undefined;
    const eventPrefix = editing ? "image_edit" : "image_generation";
    await readSse(response, ({ event, data }) => {
        if (data === "[DONE]") return;
        const payload = JSON.parse(data) as RecordValue;
        const eventType = payload.type || event;
        if (eventType === "error" || eventType === `${eventPrefix}.failed` || eventType === "failed") throw safeError(payload, response.status, response.headers.get("x-request-id") || undefined);
        if (eventType === `${eventPrefix}.partial_image` || eventType === "partial_image") {
            const preview = resultFromItem(payload, outputFormat(payload, request.outputFormat), { outputFormat: outputFormat(payload, request.outputFormat) });
            if (preview) emit(options, { type: "preview", image: preview });
        }
        if (eventType === `${eventPrefix}.completed` || eventType === "completed") final = payload;
    });
    if (!final) throw new Error("Images 流式响应未返回最终结果");
    const images = imagesResult(final, request, response.headers.get("x-request-id") || undefined);
    if (!images.length) throw new Error("Images 未返回最终图片");
    const result = { images, texts: [], errors: [] } satisfies ImageJobResult;
    images.forEach((image, index) => emit(options, { type: "output", index, image }));
    emit(options, { type: "completed", result });
    return result;
}

async function responseInput(request: NativeImageRequest) {
    return [{
        role: "user",
        content: [
            { type: "input_text", text: request.prompt },
            ...(await Promise.all(request.references.map(async (reference) => ({ type: "input_image", image_url: await imageToDataUrl(reference) })))),
        ],
    }];
}

async function responsesBody(request: NativeImageRequest, options: ImageJobOptions, input: unknown[]) {
    const openai = request.native.openai || {};
    const mainModel = openai.mainModel?.trim();
    if (!mainModel) throw new Error("Responses API 必须配置 mainModel");
    if (request.count !== 1) throw new Error("Responses 图像工具仅支持图片数量 1");
    const stored = openai.store === true;
    const usePreviousResponse = stored && options.conversation?.stored === true && Boolean(options.conversation.remoteId);
    const maskUrl = options.mask ? await imageToDataUrl(options.mask) : undefined;
    const tool = {
        type: "image_generation",
        model: request.model,
        quality: request.quality,
        size: request.size,
        background: request.background,
        output_format: request.outputFormat,
        ...(request.outputFormat === "jpeg" || request.outputFormat === "webp" ? { output_compression: request.outputCompression } : {}),
        moderation: request.moderation,
        ...(maskUrl ? { input_image_mask: { image_url: maskUrl } } : {}),
        ...(request.native.stream ? { partial_images: Math.max(0, Math.min(3, openai.partialImages ?? 0)) } : {}),
    };
    return {
        mainModel,
        input,
        body: {
            model: mainModel,
            input: usePreviousResponse ? input : [...(options.conversation?.history || []), ...input],
            store: stored,
            tools: [tool],
            tool_choice: { type: "image_generation" },
            ...(usePreviousResponse ? { previous_response_id: options.conversation!.remoteId } : {}),
        } satisfies RecordValue,
    };
}

function responseResult(payload: RecordValue, request: NativeImageRequest, mainModel: string, requestId?: string) {
    const images: ImageGenerationResult[] = [];
    const texts: string[] = [];
    const outputOrder: NonNullable<ImageJobResult["outputOrder"]> = [];
    const metadata: ImageResponseMetadata = {
        provider: "openai",
        protocol: "openai-responses",
        model: mainModel,
        rawUsage: payload.usage,
        requestId,
        providerMetadata: {
            responseId: payload.id,
            responseModel: payload.model,
            imageGenerationModel: request.model,
            usageNote: "Responses usage covers the full response and is not an Images API usage object.",
        },
    };
    for (const output of Array.isArray(payload.output) ? payload.output : []) {
        if (output?.type === "image_generation_call" && typeof output.result === "string") {
            const format = outputFormat(output, request.outputFormat);
            const image = resultFromItem({ b64_json: output.result }, format, { ...metadata, outputFormat: format });
            if (image) outputOrder.push({ kind: "image", index: images.push(image) - 1 });
        }
        if (output?.type === "message") {
            for (const content of Array.isArray(output.content) ? output.content : []) {
                if (typeof content?.text === "string") outputOrder.push({ kind: "text", index: texts.push(content.text) - 1 });
            }
        }
    }
    return { images, texts, outputOrder, metadata };
}

function responseConversation(payload: RecordValue, request: NativeImageRequest, options: ImageJobOptions, context: ImageTransportContext, mainModel: string, input: unknown[]) {
    return {
        provider: "openai" as const,
        protocol: "openai-responses" as const,
        model: mainModel,
        remoteId: typeof payload.id === "string" ? payload.id : undefined,
        stored: request.native.openai?.store === true,
        createdAt: new Date().toISOString(),
        channelId: context.channelId,
        history: [...(options.conversation?.history || []), ...input, ...(Array.isArray(payload.output) ? payload.output : [])],
    };
}

function completeResponseResult(payload: RecordValue, request: NativeImageRequest, options: ImageJobOptions, context: ImageTransportContext, mainModel: string, input: unknown[], requestId?: string): ImageJobResult {
    if (payload.error || (payload.status && payload.status !== "completed")) throw safeError(payload, undefined, requestId);
    const { images, texts, outputOrder, metadata } = responseResult(payload, request, mainModel, requestId);
    if (!images.length) throw new Error("Responses 未返回最终图片");
    const result: ImageJobResult = {
        images,
        texts,
        errors: [],
        outputOrder,
        providerMetadata: metadata.providerMetadata,
        conversation: responseConversation(payload, request, options, context, mainModel, input),
    };
    for (const entry of outputOrder) {
        if (entry.kind === "image") emit(options, { type: "output", index: entry.index, image: images[entry.index] });
        else emit(options, { type: "text", text: texts[entry.index] });
    }
    return result;
}

async function responsesApi(context: ImageTransportContext, request: NativeImageRequest, options: ImageJobOptions): Promise<ImageJobResult> {
    emit(options, { type: "started" });
    const input = await responseInput(request);
    const { mainModel, body } = await responsesBody(request, options, input);
    if (request.native.stream) return streamResponses(context, request, options, mainModel, input, body);
    const response = await fetch(endpoint(context, "/responses"), {
        method: "POST",
        headers: { ...headers(context), "Content-Type": "application/json" },
        body: JSON.stringify(body),
        signal: options.signal,
    });
    await checkResponse(response);
    const payload = await response.json() as RecordValue;
    const result = completeResponseResult(payload, request, options, context, mainModel, input, response.headers.get("x-request-id") || undefined);
    emit(options, { type: "completed", result });
    return result;
}

async function streamResponses(context: ImageTransportContext, request: NativeImageRequest, options: ImageJobOptions, mainModel: string, input: unknown[], body: RecordValue): Promise<ImageJobResult> {
    const response = await fetch(endpoint(context, "/responses"), {
        method: "POST",
        headers: { ...headers(context), "Content-Type": "application/json" },
        body: JSON.stringify({ ...body, stream: true }),
        signal: options.signal,
    });
    let completed: RecordValue | undefined;
    await readSse(response, ({ event, data }) => {
        if (data === "[DONE]") return;
        const value = JSON.parse(data) as RecordValue;
        const eventType = value.type || event;
        if (eventType === "response.image_generation_call.partial_image" && typeof value.partial_image_b64 === "string") {
            emit(options, { type: "preview", image: { id: crypto.randomUUID(), dataUrl: `data:${mimeType(request.outputFormat)};base64,${value.partial_image_b64}` } });
        }
        if (eventType === "response.failed" || eventType === "response.incomplete" || eventType === "error") throw safeError(value.response || value, response.status, response.headers.get("x-request-id") || undefined);
        if (eventType === "response.completed") completed = value.response as RecordValue;
    });
    if (!completed) throw new Error("Responses 流式响应未返回 response.completed");
    const result = completeResponseResult(completed, request, options, context, mainModel, input, response.headers.get("x-request-id") || undefined);
    emit(options, { type: "completed", result });
    return result;
}

export async function requestOpenAIImageJob(context: ImageTransportContext, request: NativeImageRequest, options: ImageJobOptions = {}): Promise<ImageJobResult> {
    try {
        const normalized = { ...request, size: resolveRequestSize(undefined, request.size) || "auto" };
        return request.native.openai?.api === "responses"
            ? await responsesApi(context, normalized, options)
            : await imagesApi(context, normalized, options);
    } catch (error) {
        if (options.signal?.aborted) emit(options, { type: "client-aborted" });
        throw imageRequestError(error, "OpenAI request failed");
    }
}
