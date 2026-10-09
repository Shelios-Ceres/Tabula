import axios from "axios";

import i18n from "@/i18n";
import { buildApiUrl, channelApiBaseUrl, resolveModelRequestConfig, resolveModelScript, withLocalProxy, type AiConfig, type ModelChannel } from "@/stores/use-config-store";
import { normalizePluginImages, runModelPlugin } from "./model-plugin";
import { nanoid } from "nanoid";
import { dataUrlToFile, readImageMeta } from "@/lib/image-utils";
import { buildImageReferencePromptText } from "@/lib/image-reference-prompt";
import { imageToDataUrl } from "@/services/image-storage";
import { imageSizePresets } from "@/lib/media-size";
import type { ImageGenerationResult, ImageOutputFormat, ImageResponseMetadata, ReferenceImage } from "@/types/image";
import { getImageModelCapabilities, imageBackgrounds, imageOutputFormats } from "@/lib/image-model-capabilities";

const apiText = (key: string, options?: Record<string, unknown>) => i18n.t(`apiErrors.${key}`, options);

export type AiTextMessage = {
    role: "system" | "user" | "assistant";
    content: string | Array<{ type: "text"; text: string } | { type: "image_url"; image_url: { url: string } }>;
};

type ResponseToolCall = {
    id: string;
    type: "function";
    function: { name: string; arguments: string };
    thoughtSignature?: string;
};

type ResponseInputMessage =
    | AiTextMessage
    | { type: "function_call"; call_id: string; name: string; arguments: string; thoughtSignature?: string }
    | { role: "tool"; tool_call_id: string; content: string };

type ResponseFunctionTool = {
    type: "function";
    function: {
        name: string;
        description?: string;
        parameters: Record<string, unknown>;
        strict?: boolean;
    };
};

type ToolResponseResult = {
    content: string;
    toolCalls: ResponseToolCall[];
};

type ToolChoice = "auto" | "required" | { type: "function"; name: string };
type ResponseMessageContent = AiTextMessage["content"] | string;
type ResponseInputContent = { type: "input_text"; text: string } | { type: "input_image"; image_url: string };
type ResponseInputItem =
    | { role: "system" | "user" | "assistant"; content: string | ResponseInputContent[] }
    | { type: "function_call"; call_id: string; name: string; arguments: string }
    | { type: "function_call_output"; call_id: string; output: string };
type ResponseApiToolDefinition = {
    type: "function";
    name: string;
    description?: string;
    parameters: Record<string, unknown>;
    strict?: boolean;
};
type ResponseApiOutputItem =
    | { type?: "message"; content?: Array<{ type?: string; text?: string }> }
    | { type?: "function_call"; id?: string; call_id?: string; name?: string; arguments?: string };
type ResponseApiPayload = {
    id?: string;
    output?: ResponseApiOutputItem[];
    output_text?: string;
    error?: { message?: string };
    code?: number;
    msg?: string;
};
type ResponseStreamState = { buffer: string; text: string; payload?: ResponseApiPayload; error?: string };

type ImageApiResponse = {
    data?: Array<Record<string, unknown>>;
    error?: { message?: string };
    code?: number;
    msg?: string;
    background?: string;
    quality?: string;
    size?: string;
    output_format?: string;
    usage?: ImageResponseMetadata["usage"];
};
type GeminiPart = {
    text?: string;
    inlineData?: { mimeType?: string; data?: string };
    inline_data?: { mime_type?: string; mimeType?: string; data?: string };
    fileData?: { mimeType?: string; fileUri?: string };
    functionCall?: { id?: string; name?: string; args?: Record<string, unknown> };
    functionResponse?: { id?: string; name?: string; response?: Record<string, unknown> };
    thoughtSignature?: string;
    thought_signature?: string;
};
type GeminiContent = { role?: "user" | "model"; parts: GeminiPart[] };
type GeminiPayload = {
    candidates?: Array<{ content?: { parts?: GeminiPart[] }; finishReason?: string }>;
    models?: Array<{ name?: string }>;
    error?: { message?: string };
    promptFeedback?: { blockReason?: string };
};
type GeminiStreamState = { buffer: string; text: string; toolCalls: ResponseToolCall[]; error?: string };
export type RequestOptions = { signal?: AbortSignal; mask?: ReferenceImage };

const QUALITY_BASE: Record<string, number> = {
    low: 1024,
    medium: 2048,
    high: 2880,
    standard: 1024,
    hd: 2048,
};
const QUALITY_ALIASES: Record<string, string> = {
    "1k": "low",
    "2k": "medium",
    "4k": "high",
};
const DEFAULT_IMAGE_SHORT_SIDE = 1024;
const IMAGE_SIZE_STEP = 16;
const IMAGE_MIN_PIXELS = 655360;
const IMAGE_MAX_PIXELS = 8294400;
const IMAGE_MAX_EDGE = 3840;
const IMAGE_MAX_RATIO = 3;


function normalizeQuality(quality: string, model?: string) {
    const value = quality.trim().toLowerCase();
    const normalized = QUALITY_ALIASES[value] || value;
    if (model && getImageModelCapabilities(model).isGptImage) {
        const capabilities = getImageModelCapabilities(model);
        if (!(capabilities.qualities as readonly string[]).includes(normalized)) throw new Error(apiText("invalidImageQuality", { quality }));
    }
    return QUALITY_BASE[normalized] || normalized === "xhigh" || normalized === "max" ? normalized : undefined;
}

/** Only "transparent" is forwarded; any other value (incl. empty) means keep the default opaque background. */
function normalizeBackground(background: string | undefined, isGptImage = false) {
    const value = background?.trim().toLowerCase();
    if (isGptImage) {
        if (!value || value === "auto") return "auto";
        if ((imageBackgrounds as readonly string[]).includes(value)) return value;
        throw new Error(apiText("invalidImageBackground", { background }));
    }
    return value === "transparent" ? "transparent" : undefined;
}

/** Map "quality + ratio" to an explicit pixel dimension like "3840x2160". */
function resolveSize(quality: string | undefined, ratio: string): string {
    const parsedRatio = parseImageRatio(ratio);
    const scale = quality === "high" ? "4k" : quality === "medium" || quality === "hd" ? "2k" : "1k";
    const preset = imageSizePresets[scale][ratio];
    if (preset) return preset;
    const basePixels = quality ? QUALITY_BASE[quality] : undefined;
    const isLandscape = parsedRatio.width >= parsedRatio.height;
    const longRatio = isLandscape ? parsedRatio.width / parsedRatio.height : parsedRatio.height / parsedRatio.width;
    let longSide: number;
    let shortSide: number;

    if (basePixels) {
        const targetPixels = basePixels * basePixels;
        const longSideRaw = Math.sqrt(targetPixels * longRatio);
        longSide = Math.floor(longSideRaw / IMAGE_SIZE_STEP) * IMAGE_SIZE_STEP;
        shortSide = Math.round(longSide / longRatio / IMAGE_SIZE_STEP) * IMAGE_SIZE_STEP;
    } else {
        shortSide = DEFAULT_IMAGE_SHORT_SIDE;
        longSide = Math.round((shortSide * longRatio) / IMAGE_SIZE_STEP) * IMAGE_SIZE_STEP;
    }

    const width = isLandscape ? longSide : shortSide;
    const height = isLandscape ? shortSide : longSide;
    validateImageSize(width, height);
    return `${width}x${height}`;
}

function parseRatioValue(value: string) {
    const parts = value.split(":");
    if (parts.length !== 2) throw new Error(apiText("invalidImageSizeFormat"));
    const w = Number(parts[0]);
    const h = Number(parts[1]);
    if (!Number.isFinite(w) || !Number.isFinite(h) || w <= 0 || h <= 0) throw new Error(apiText("positiveImageRatio"));
    return { width: w, height: h };
}

function parseImageRatio(value: string) {
    const ratio = parseRatioValue(value);
    if (Math.max(ratio.width, ratio.height) / Math.min(ratio.width, ratio.height) > IMAGE_MAX_RATIO) throw new Error(apiText("imageRatioLimit"));
    return ratio;
}

function parseImageDimensions(value: string) {
    const match = value.match(/^(\d+)x(\d+)$/i);
    if (!match) return null;
    return { width: Number(match[1]), height: Number(match[2]) };
}

function validateImageSize(width: number, height: number) {
    if (!Number.isInteger(width) || !Number.isInteger(height) || width <= 0 || height <= 0) throw new Error(apiText("positiveImageDimensions"));
    if (width % IMAGE_SIZE_STEP !== 0 || height % IMAGE_SIZE_STEP !== 0) throw new Error(apiText("imageDimensionStep"));
    if (Math.max(width, height) > IMAGE_MAX_EDGE) throw new Error(apiText("imageEdgeLimit"));
    if (Math.max(width, height) / Math.min(width, height) > IMAGE_MAX_RATIO) throw new Error(apiText("imageRatioLimit"));
    const pixels = width * height;
    if (pixels < IMAGE_MIN_PIXELS || pixels > IMAGE_MAX_PIXELS) throw new Error(apiText("imagePixelLimit"));
}

export function resolveRequestSize(quality: string | undefined, size: string) {
    const value = size.trim();
    if (!value || value.toLowerCase() === "auto") return undefined;
    const dimensions = parseImageDimensions(value);
    if (dimensions) {
        validateImageSize(dimensions.width, dimensions.height);
        return `${dimensions.width}x${dimensions.height}`;
    }
    if (value.includes(":")) return resolveSize(quality, value);
    throw new Error(apiText("invalidImageSizeFormat"));
}

function resolveImageSource(item: Record<string, unknown>, outputFormat: ImageOutputFormat = "png") {
    if (typeof item.b64_json === "string" && item.b64_json) {
        return `data:image/${outputFormat};base64,${item.b64_json}`;
    }
    if (typeof item.url === "string" && item.url) {
        return item.url;
    }
    return null;
}

function parseImagePayload(payload: ImageApiResponse, response?: ImageResponseMetadata) {
    if (typeof payload.code === "number" && payload.code !== 0) {
        throw new Error(payload.msg || apiText("requestFailed"));
    }
    // Support data, images, and results response fields used by different APIs.
    const payloadFormat = typeof payload.output_format === "string" && (imageOutputFormats as readonly string[]).includes(payload.output_format) ? payload.output_format as ImageOutputFormat : undefined;
    const outputFormat = payloadFormat || response?.outputFormat || "png";
    const effectiveResponse: ImageResponseMetadata | undefined = response || payloadFormat || payload.quality || payload.size || payload.background || payload.usage
        ? {
            ...(response || {}),
            ...(payload.background ? { background: payload.background } : {}),
            ...(payload.quality ? { quality: payload.quality } : {}),
            ...(payload.size ? { size: payload.size } : {}),
            outputFormat,
            ...(payload.usage ? { usage: payload.usage } : {}),
        }
        : undefined;
    const imageList = payload.data
        || (payload as Record<string, unknown>).images as Array<Record<string, unknown>> | undefined
        || (payload as Record<string, unknown>).results as Array<Record<string, unknown>> | undefined
        || [];
    const images = imageList
        .map((item) => resolveImageSource(item, outputFormat))
        .filter((value): value is string => Boolean(value))
        .map((dataUrl) => ({ id: nanoid(), dataUrl, ...(effectiveResponse ? { response: effectiveResponse } : {}) }));

    if (images.length === 0) {
        // Check whether the response contains data in an unrecognized format.
        const rawKeys = Object.keys(payload).filter((k) => k !== "code" && k !== "msg" && k !== "error");
        throw new Error(rawKeys.length > 0
            ? apiText("unknownImageResponse", { fields: rawKeys.join(", ") })
            : apiText("noImageReturned"));
    }

    return images;
}

type GptImageOptions = { quality?: string; background?: string; output_format?: ImageOutputFormat; output_compression?: number; moderation?: "auto" | "low" };

function gptImageOptions(config: AiConfig, model: string): GptImageOptions {
    const capabilities = getImageModelCapabilities(model);
    if (!capabilities.isGptImage) return {};
    const quality = normalizeQuality(config.quality, model);
    const outputFormat = String(config.outputFormat || "png").toLowerCase() as ImageOutputFormat;
    if (!(imageOutputFormats as readonly string[]).includes(outputFormat)) throw new Error(apiText("invalidImageFormat", { format: config.outputFormat }));
    const background = normalizeBackground(config.background, true);
    if (outputFormat === "jpeg" && background === "transparent") throw new Error(apiText("transparentJpeg", { defaultValue: "JPEG 不支持透明背景" }));
    const compression = Number(config.outputCompression);
    if (!Number.isInteger(compression) || compression < 0 || compression > 100) throw new Error(apiText("invalidImageCompression", { defaultValue: "压缩质量必须为 0–100 的整数" }));
    if (config.moderation !== "auto" && config.moderation !== "low") throw new Error(apiText("invalidImageModeration", { defaultValue: "审核级别无效" }));
    return {
        ...(quality ? { quality } : {}),
        ...(background ? { background } : {}),
        output_format: outputFormat,
        ...(outputFormat === "jpeg" || outputFormat === "webp" ? { output_compression: Math.floor(compression) } : {}),
        moderation: config.moderation === "low" ? "low" : "auto",
    };
}

async function validateMask(mask: ReferenceImage | undefined, firstReference: ReferenceImage | undefined) {
    if (!mask) return;
    if (!firstReference) throw new Error(apiText("maskRequiresImage", { defaultValue: "遮罩编辑需要一张主图" }));
    if (!/^image\/png$/i.test(mask.type || mask.dataUrl.match(/^data:([^;]+)/)?.[1] || "")) throw new Error(apiText("maskMustBePng", { defaultValue: "遮罩必须是 PNG 图片" }));
    const [sourceDataUrl, maskDataUrl] = await Promise.all([imageToDataUrl(firstReference), imageToDataUrl(mask)]);
    const [sourceMeta, maskMeta] = await Promise.all([readImageMeta(sourceDataUrl), readImageMeta(maskDataUrl)]);
    if (sourceMeta.width !== maskMeta.width || sourceMeta.height !== maskMeta.height) throw new Error(apiText("maskSizeMismatch", { defaultValue: "遮罩尺寸必须与主图一致" }));
    if (typeof document === "undefined" || typeof Image === "undefined") throw new Error(apiText("maskAlphaUnavailable", { defaultValue: "无法读取遮罩透明通道" }));
    {
        const image = new Image();
        const alpha = await new Promise<boolean>((resolve, reject) => {
            image.onload = () => {
                const canvas = document.createElement("canvas");
                canvas.width = image.naturalWidth;
                canvas.height = image.naturalHeight;
                const context = canvas.getContext("2d");
                if (!context) return reject(new Error(apiText("maskAlphaUnavailable", { defaultValue: "无法读取遮罩透明通道" })));
                try {
                    context.drawImage(image, 0, 0);
                    const pixels = context.getImageData(0, 0, canvas.width, canvas.height).data;
                    for (let index = 3; index < pixels.length; index += 4) {
                        if (pixels[index] < 255) return resolve(true);
                    }
                    resolve(false);
                } catch {
                    reject(new Error(apiText("maskAlphaUnavailable", { defaultValue: "无法读取遮罩透明通道" })));
                }
            };
            image.onerror = () => reject(new Error(apiText("maskAlphaUnavailable", { defaultValue: "无法读取遮罩透明通道" })));
            image.src = maskDataUrl;
        });
        if (!alpha) throw new Error(apiText("maskNeedsAlpha", { defaultValue: "遮罩必须包含透明区域" }));
    }
}

function readApiErrorMessage(value: unknown): string {
    if (!value) return "";
    if (typeof value === "string") {
        // The value may be serialized JSON, such as error.message, or a plain-text error.
        try {
            const parsed = JSON.parse(value);
            const inner = readApiErrorMessage(parsed) || value;
            // Treat an empty parsed object such as "{}" as having no useful message.
            if (inner === value && typeof parsed === "object" && Object.keys(parsed).length === 0) return "";
            return inner;
        } catch {
            // Detect HTML error pages.
            if (/<[a-z][\s\S]*>/i.test(value)) return apiText("htmlError", { preview: `${value.slice(0, 80)}...` });
            return value;
        }
    }
    if (typeof value !== "object") return "";
    const payload = value as { msg?: unknown; message?: unknown; error?: unknown; detail?: unknown };
    // error may be a string or an object containing a message.
    const errorMsg =
        typeof payload.error === "string"
            ? payload.error
            : (payload.error as { message?: unknown })?.message;
    return (
        readApiErrorMessage(payload.msg) ||
        readApiErrorMessage(payload.message) ||
        readApiErrorMessage(errorMsg) ||
        readApiErrorMessage(payload.detail) ||
        ""
    );
}

export function imageRequestError(error: unknown, fallback: string) {
    const source = error && typeof error === "object" ? error as Record<string, unknown> : {};
    const axiosError = axios.isAxiosError(error) ? error : undefined;
    const payload = axiosError?.response?.data ?? source;
    const record = payload && typeof payload === "object" ? payload as Record<string, unknown> : {};
    const detail = record.error && typeof record.error === "object" ? errorRecord(record.error) : {};
    const status = axiosError?.response?.status ?? (typeof source.status === "number" ? source.status : undefined);
    const canceled = axios.isCancel(error) || error instanceof DOMException && error.name === "AbortError";
    const network = !axiosError?.response && (source.code === "ERR_NETWORK" || error instanceof TypeError && error.message === "Failed to fetch");
    const code = canceled ? "ERR_CANCELED" : detail.code || record.code || source.code || (network ? "FETCH_NETWORK_ERROR" : undefined);
    const requestId = axiosError?.response?.headers?.["x-request-id"] || axiosError?.response?.headers?.["request-id"] || source.requestId || record.request_id || detail.request_id;
    const originalMessage = typeof source.originalMessage === "string" ? source.originalMessage : error instanceof Error ? error.message : undefined;
    const message = canceled ? apiText("requestCanceled") : network ? apiText("networkFailed", { defaultValue: "网络连接失败，浏览器未收到服务器响应。请检查网络、代理和接口地址，也可能是浏览器跨域拦截。" }) : readApiErrorMessage(payload) || readStatusError(status, originalMessage || fallback);
    const context = canceled ? "" : formatErrorContext(status, { code, request_id: requestId });
    return Object.assign(new Error(message.endsWith(context) ? message : `${message}${context}`), {
        ...(status !== undefined ? { status } : {}),
        ...(code ? { code: String(code) } : {}),
        ...(requestId ? { requestId: String(requestId) } : {}),
        ...(originalMessage ? { originalMessage } : {}),
    });
}

function readStatusError(status: number | undefined, fallback: string) {
    if (status === 401 || status === 403) return apiText("authenticationFailed");
    if (status === 429) return apiText("rateLimited");
    if (status === 404) return apiText("notFound");
    if (status === 502) return apiText("badGateway");
    if (status === 503) return apiText("serviceBusy");
    return status ? apiText("httpFailed", { status }) : fallback;
}

function formatErrorContext(status: number | undefined, data: unknown, headers?: Record<string, unknown>) {
    const record = data && typeof data === "object" ? data as Record<string, unknown> : {};
    const error = record.error && typeof record.error === "object" ? errorRecord(record.error) : {};
    const code = record.code || error.code;
    const requestId = headers?.["x-request-id"] || headers?.["request-id"] || record.request_id || error.request_id;
    const details = [status ? `status ${status}` : "", code ? `code ${String(code)}` : "", requestId ? `request ${String(requestId)}` : ""].filter(Boolean);
    return details.length ? ` (${details.join(", ")})` : "";
}

function errorRecord(value: object) { return value as Record<string, unknown>; }

function withSystemPrompt(config: AiConfig, prompt: string) {
    const systemPrompt = config.systemPrompt.trim();
    return systemPrompt ? `${systemPrompt}\n\n${prompt}` : prompt;
}

function aiApiUrl(config: AiConfig, path: string) {
    return buildApiUrl(config.baseUrl, path);
}

function aiHeaders(config: AiConfig, contentType?: string) {
    return {
        Authorization: `Bearer ${config.apiKey}`,
        ...(contentType ? { "Content-Type": contentType } : {}),
    };
}

function geminiBaseUrl(config: Pick<AiConfig, "baseUrl">) {
    const normalizedBaseUrl = config.baseUrl.trim().replace(/\/+$/, "");
    const lowerBaseUrl = normalizedBaseUrl.toLowerCase();
    return lowerBaseUrl.endsWith("/v1") || lowerBaseUrl.endsWith("/v1beta") ? normalizedBaseUrl : `${normalizedBaseUrl}/v1beta`;
}

function geminiModelName(model: string) {
    return model.trim().replace(/^models\//, "");
}

function geminiApiUrl(config: Pick<AiConfig, "baseUrl" | "model">, action?: "generateContent" | "streamGenerateContent") {
    const baseUrl = geminiBaseUrl(config);
    if (!action) return withLocalProxy(`${baseUrl}/models`);
    return withLocalProxy(`${baseUrl}/models/${encodeURIComponent(geminiModelName(config.model))}:${action}`);
}

function geminiHeaders(config: Pick<AiConfig, "apiKey">) {
    return {
        "x-goog-api-key": config.apiKey,
        "Content-Type": "application/json",
    };
}

function withSystemMessage<T extends ResponseInputMessage>(config: AiConfig, messages: T[]): ResponseInputMessage[] {
    const systemPrompt = config.systemPrompt.trim();
    return systemPrompt ? [{ role: "system" as const, content: systemPrompt }, ...messages] : messages;
}

function toResponseInput(messages: ResponseInputMessage[]): ResponseInputItem[] {
    return messages.flatMap((message): ResponseInputItem[] => {
        if ("type" in message) return [message];
        if (message.role === "tool") return [{ type: "function_call_output", call_id: message.tool_call_id, output: message.content }];
        return [{ role: message.role, content: toResponseContent(message.content || "") }];
    });
}

function toResponseContent(content: ResponseMessageContent): string | ResponseInputContent[] {
    if (!Array.isArray(content)) return String(content || "");
    return content.map((item) => (item.type === "text" ? { type: "input_text" as const, text: item.text } : { type: "input_image" as const, image_url: item.image_url.url }));
}

function toResponseTool(tool: ResponseFunctionTool): ResponseApiToolDefinition {
    return {
        type: "function",
        name: tool.function.name,
        description: tool.function.description,
        parameters: tool.function.parameters,
        strict: tool.function.strict,
    };
}

function parseToolResponse(payload: ResponseApiPayload): ToolResponseResult {
    const output = payload.output || [];
    const content =
        payload.output_text ||
        output
            .flatMap((item) => (item.type === "message" ? item.content || [] : []))
            .map((item) => item.text || "")
            .join("");
    const toolCalls = output
        .filter((item): item is Extract<ResponseApiOutputItem, { type?: "function_call" }> => item.type === "function_call")
        .map((item) => ({
            id: item.call_id || item.id || "",
            type: "function" as const,
            function: { name: item.name || "", arguments: item.arguments || "{}" },
        }))
        .filter((item) => item.id && item.function.name);
    return { content, toolCalls };
}

function isRecord(value: unknown): value is Record<string, unknown> {
    return Boolean(value && typeof value === "object" && !Array.isArray(value));
}

function responseErrorMessage(value: unknown) {
    if (!isRecord(value)) return "";
    const error = isRecord(value.error) ? value.error : undefined;
    const response = isRecord(value.response) ? value.response : undefined;
    const responseError = response && isRecord(response.error) ? response.error : undefined;
    return stringValue(value.msg) || stringValue(error?.message) || stringValue(responseError?.message);
}

function stringValue(value: unknown) {
    return typeof value === "string" ? value : "";
}

function validateResponsePayload(payload: ResponseApiPayload) {
    if (typeof payload.code === "number" && payload.code !== 0) throw new Error(payload.msg || apiText("requestFailed"));
    if (payload.error?.message) throw new Error(payload.error.message);
}

function validateGeminiPayload(payload: GeminiPayload) {
    if (payload.error?.message) throw new Error(payload.error.message);
    if (payload.promptFeedback?.blockReason) throw new Error(apiText("geminiRejected", { reason: payload.promptFeedback.blockReason }));
}

async function readFetchError(response: Response, fallback: string) {
    const text = await response.text();
    if (!text) return readStatusError(response.status, fallback);
    try {
        return responseErrorMessage(JSON.parse(text)) || readStatusError(response.status, fallback);
    } catch {
        return text.slice(0, 300) || readStatusError(response.status, fallback);
    }
}

function consumeResponseStreamBlock(block: string, state: ResponseStreamState, onDelta?: (text: string) => void) {
    const data = block
        .split(/\r?\n/)
        .filter((line) => line.startsWith("data:"))
        .map((line) => line.slice(5).replace(/^ /, ""))
        .join("\n")
        .trim();
    if (!data || data === "[DONE]") return;
    const event = JSON.parse(data) as Record<string, unknown>;
    const type = stringValue(event.type);
    const errorMessage = responseErrorMessage(event);
    if (errorMessage) state.error = errorMessage;
    if (type === "response.output_text.delta" && typeof event.delta === "string") {
        state.text += event.delta;
        onDelta?.(state.text);
    }
    if (type === "response.output_text.done" && !state.text && typeof event.text === "string") {
        state.text = event.text;
        onDelta?.(state.text);
    }
    if (type === "response.completed" && isRecord(event.response)) {
        state.payload = event.response as ResponseApiPayload;
    } else if (Array.isArray(event.output)) {
        state.payload = event as ResponseApiPayload;
    }
}

function consumeResponseStreamText(state: ResponseStreamState, text: string, onDelta?: (text: string) => void, flush = false) {
    state.buffer += text;
    for (;;) {
        const match = state.buffer.match(/\r?\n\r?\n/);
        if (!match) break;
        const index = match.index ?? 0;
        consumeResponseStreamBlock(state.buffer.slice(0, index), state, onDelta);
        state.buffer = state.buffer.slice(index + match[0].length);
    }
    if (flush && state.buffer.trim()) {
        consumeResponseStreamBlock(state.buffer, state, onDelta);
        state.buffer = "";
    }
}

async function requestStreamingResponse(config: AiConfig, body: Record<string, unknown>, onDelta?: (text: string) => void, options?: RequestOptions): Promise<ToolResponseResult> {
    const response = await fetch(aiApiUrl(config, "/responses"), {
        method: "POST",
        headers: { ...aiHeaders(config, "application/json"), Accept: "text/event-stream" },
        body: JSON.stringify({ ...body, stream: true }),
        signal: options?.signal,
    });
    if (!response.ok) throw new Error(await readFetchError(response, apiText("requestFailed")));
    if (!response.body) {
        const payload = (await response.json()) as ResponseApiPayload;
        validateResponsePayload(payload);
        return parseToolResponse(payload);
    }

    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    const state: ResponseStreamState = { buffer: "", text: "" };
    for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        consumeResponseStreamText(state, decoder.decode(value, { stream: true }), onDelta);
        if (state.error) throw new Error(state.error);
    }
    consumeResponseStreamText(state, decoder.decode(), onDelta, true);
    if (state.error) throw new Error(state.error);
    if (!state.payload) return { content: state.text, toolCalls: [] };
    validateResponsePayload(state.payload);
    const result = parseToolResponse(state.payload);
    return { ...result, content: state.text || result.content };
}

function toGeminiBody(config: AiConfig, messages: ResponseInputMessage[], extra?: Record<string, unknown>) {
    const systemText = [
        config.systemPrompt.trim(),
        ...messages.flatMap((message) => (!("type" in message) && message.role === "system" ? [geminiTextContent(message.content)] : [])),
    ]
        .filter(Boolean)
        .join("\n\n");
    const contents = toGeminiContents(messages.filter((message) => ("type" in message ? true : message.role !== "system")));
    return {
        contents,
        ...(systemText ? { systemInstruction: { parts: [{ text: systemText }] } } : {}),
        ...extra,
    };
}

function toGeminiContents(messages: ResponseInputMessage[]): GeminiContent[] {
    const callNameById = new Map<string, string>();
    return messages.flatMap((message): GeminiContent[] => {
        if ("type" in message) {
            callNameById.set(message.call_id, message.name);
            return [{ role: "model", parts: [{ functionCall: { id: message.call_id, name: message.name, args: jsonObject(message.arguments) }, ...(message.thoughtSignature ? { thoughtSignature: message.thoughtSignature } : {}) }] }];
        }
        if (message.role === "tool") {
            const name = callNameById.get(message.tool_call_id) || "tool_result";
            return [{ role: "user", parts: [{ functionResponse: { id: message.tool_call_id, name, response: { result: jsonValue(message.content) } } }] }];
        }
        return [{ role: message.role === "assistant" ? "model" : "user", parts: toGeminiParts(message.content) }];
    });
}

function toGeminiParts(content: ResponseMessageContent): GeminiPart[] {
    if (!Array.isArray(content)) return [{ text: String(content || "") }];
    return content.map((item) => (item.type === "text" ? { text: item.text } : toGeminiImagePart(item.image_url.url)));
}

function toGeminiImagePart(url: string): GeminiPart {
    const match = url.match(/^data:([^;,]+);base64,(.+)$/);
    if (match) return { inlineData: { mimeType: match[1], data: match[2] } };
    return { fileData: { fileUri: url, mimeType: "image/png" } };
}

function geminiTextContent(content: ResponseMessageContent) {
    if (!Array.isArray(content)) return String(content || "");
    return content.map((item) => (item.type === "text" ? item.text : item.image_url.url)).join("\n");
}

function jsonObject(value: string): Record<string, unknown> {
    const parsed = jsonValue(value);
    return isRecord(parsed) ? parsed : {};
}

function jsonValue(value: string): unknown {
    try {
        return JSON.parse(value);
    } catch {
        return value;
    }
}

function toGeminiToolOptions(tools: ResponseFunctionTool[], toolChoice: ToolChoice) {
    if (!tools.length) return {};
    const functionDeclarations = tools.map((tool) => ({
        name: tool.function.name,
        description: tool.function.description,
        parameters: tool.function.parameters,
    }));
    const functionCallingConfig =
        typeof toolChoice === "object"
            ? { mode: "ANY", allowedFunctionNames: [toolChoice.name] }
            : { mode: toolChoice === "required" ? "ANY" : "AUTO" };
    return {
        tools: [{ functionDeclarations }],
        toolConfig: { functionCallingConfig },
    };
}

async function requestGeminiStreamingResponse(config: AiConfig, body: Record<string, unknown>, onDelta?: (text: string) => void, options?: RequestOptions): Promise<ToolResponseResult> {
    const response = await fetch(`${geminiApiUrl(config, "streamGenerateContent")}?alt=sse`, {
        method: "POST",
        headers: geminiHeaders(config),
        body: JSON.stringify(body),
        signal: options?.signal,
    });
    if (!response.ok) throw new Error(await readFetchError(response, apiText("requestFailed")));
    if (!response.body) {
        const payload = (await response.json()) as GeminiPayload;
        return parseGeminiToolResponse(payload);
    }

    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    const state: GeminiStreamState = { buffer: "", text: "", toolCalls: [] };
    for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        consumeGeminiStreamText(state, decoder.decode(value, { stream: true }), onDelta);
        if (state.error) throw new Error(state.error);
    }
    consumeGeminiStreamText(state, decoder.decode(), onDelta, true);
    if (state.error) throw new Error(state.error);
    return { content: state.text, toolCalls: state.toolCalls };
}

function consumeGeminiStreamText(state: GeminiStreamState, text: string, onDelta?: (text: string) => void, flush = false) {
    state.buffer += text;
    for (;;) {
        const match = state.buffer.match(/\r?\n\r?\n/);
        if (!match) break;
        const index = match.index ?? 0;
        consumeGeminiStreamBlock(state.buffer.slice(0, index), state, onDelta);
        state.buffer = state.buffer.slice(index + match[0].length);
    }
    if (flush && state.buffer.trim()) {
        consumeGeminiStreamBlock(state.buffer, state, onDelta);
        state.buffer = "";
    }
}

function consumeGeminiStreamBlock(block: string, state: GeminiStreamState, onDelta?: (text: string) => void) {
    const data = block
        .split(/\r?\n/)
        .filter((line) => line.startsWith("data:"))
        .map((line) => line.slice(5).replace(/^ /, ""))
        .join("\n")
        .trim();
    if (!data || data === "[DONE]") return;
    const result = parseGeminiToolResponse(JSON.parse(data) as GeminiPayload);
    if (result.content) {
        state.text += result.content;
        onDelta?.(state.text);
    }
    state.toolCalls.push(...result.toolCalls);
}

function parseGeminiToolResponse(payload: GeminiPayload): ToolResponseResult {
    validateGeminiPayload(payload);
    const parts = payload.candidates?.flatMap((candidate) => candidate.content?.parts || []) || [];
    const content = parts.map((part) => part.text || "").join("");
    const toolCalls = parts
        .map((part) => part.functionCall)
        .filter((call): call is NonNullable<GeminiPart["functionCall"]> => Boolean(call?.name))
        .map((call) => {
            const part = parts.find((item) => item.functionCall === call);
            const thoughtSignature = part?.thoughtSignature || part?.thought_signature;
            return {
                id: call.id || nanoid(),
                type: "function" as const,
                function: { name: call.name || "", arguments: JSON.stringify(call.args || {}) },
                ...(thoughtSignature ? { thoughtSignature } : {}),
            };
        });
    return { content, toolCalls };
}

export async function requestGeneration(config: AiConfig, prompt: string, options?: RequestOptions): Promise<ImageGenerationResult[]> {
    const requestConfig = resolveModelRequestConfig(config, config.model || config.imageModel);
    const script = resolveModelScript(config, config.model || config.imageModel);
    const capabilities = getImageModelCapabilities(requestConfig.model);
    const builtInGpt = !script && capabilities.isGptImage;
    if (builtInGpt && withSystemPrompt(requestConfig, prompt).length > 32000) throw new Error(apiText("imagePromptTooLong", { max: 32000 }));
    const requestedCount = Number(config.count);
    const n = builtInGpt ? requestedCount : Math.min(15, Math.floor(Math.abs(requestedCount)) || 1);
    if (builtInGpt && (!Number.isInteger(requestedCount) || requestedCount < 1 || requestedCount > capabilities.maxImages)) throw new Error(apiText("imageCountLimit", { max: capabilities.maxImages }));
    if (script) {
        const quality = normalizeQuality(config.quality);
        const requestSize = resolveRequestSize(quality, config.size);
        const background = normalizeBackground(config.background);
        const gptOptions = {};
        try {
            const result = await runModelPlugin({
                capability: "image",
                script,
                config: requestConfig,
                prompt: withSystemPrompt(requestConfig, prompt),
                images: [],
                params: { size: requestSize, quality, count: n, ...(background ? { background } : {}), ...gptOptions, outputFormat: config.outputFormat, outputCompression: config.outputCompression, moderation: config.moderation },
                signal: options?.signal,
            });
            return normalizePluginImages(result).map((dataUrl) => ({ id: nanoid(), dataUrl }));
        } catch (error) {
            throw imageRequestError(error, apiText("requestFailed"));
        }
    }
    const quality = normalizeQuality(config.quality, requestConfig.model);
    const requestSize = resolveRequestSize(builtInGpt ? undefined : quality, config.size);
    const background = normalizeBackground(config.background, builtInGpt);
    const gptOptions = builtInGpt ? gptImageOptions(config, requestConfig.model) : {};
    try {
        const response = await axios.post<ImageApiResponse>(
            aiApiUrl(requestConfig, "/images/generations"),
            {
                model: requestConfig.model,
                prompt: withSystemPrompt(requestConfig, prompt),
                n,
                ...(quality ? { quality } : {}),
                ...(requestSize ? { size: requestSize } : {}),
                ...(background ? { background } : {}),
                // gpt-image models reject response_format; they always return b64.
                ...(builtInGpt ? {} : { response_format: "b64_json" }),
                ...gptOptions,
            },
            {
                headers: aiHeaders(requestConfig, "application/json"),
                signal: options?.signal,
            },
        );
        const outputFormat = (gptOptions.output_format || "png") as ImageOutputFormat;
        const metadata: ImageResponseMetadata = { outputFormat, usage: response.data.usage, requestId: String(response.headers["x-request-id"] || response.headers["request-id"] || "") || undefined };
        const images = await parseImagePayload(response.data, metadata);
        return images;
    } catch (error) {
        throw imageRequestError(error, apiText("requestFailed"));
    }
}

export async function requestEdit(config: AiConfig, prompt: string, references: ReferenceImage[], options?: RequestOptions): Promise<ImageGenerationResult[]> {
    const requestConfig = resolveModelRequestConfig(config, config.model || config.imageModel);
    const script = resolveModelScript(config, config.model || config.imageModel);
    const capabilities = getImageModelCapabilities(requestConfig.model);
    const builtInGpt = !script && capabilities.isGptImage;
    const requestedCount = Number(config.count);
    const n = builtInGpt ? requestedCount : Math.min(15, Math.floor(Math.abs(requestedCount)) || 1);
    if (builtInGpt && (!Number.isInteger(requestedCount) || requestedCount < 1 || requestedCount > capabilities.maxImages)) throw new Error(apiText("imageCountLimit", { max: capabilities.maxImages }));
    if (builtInGpt && (references.length < 1 || references.length > 16)) throw new Error(apiText("imageReferenceCountLimit", { max: 16 }));
    const requestPrompt = buildImageReferencePromptText(prompt, references);
    if (builtInGpt && withSystemPrompt(requestConfig, requestPrompt).length > 32000) throw new Error(apiText("imagePromptTooLong", { max: 32000 }));
    await validateMask(options?.mask, references[0]);
    if (script) {
        const quality = normalizeQuality(config.quality);
        const requestSize = resolveRequestSize(quality, config.size);
        const background = normalizeBackground(config.background);
        const gptOptions = {};
        const refs = await Promise.all(references.map((image) => imageToDataUrl(image)));
        try {
            const result = await runModelPlugin({
                capability: "image",
                script,
                config: requestConfig,
                prompt: withSystemPrompt(requestConfig, requestPrompt),
                images: refs,
                params: { size: requestSize, quality, count: n, ...(background ? { background } : {}), ...gptOptions, outputFormat: config.outputFormat, outputCompression: config.outputCompression, moderation: config.moderation, ...(options?.mask ? { maskDataUrl: await imageToDataUrl(options.mask) } : {}) },
                signal: options?.signal,
            });
            return normalizePluginImages(result).map((dataUrl) => ({ id: nanoid(), dataUrl }));
        } catch (error) {
            throw imageRequestError(error, apiText("requestFailed"));
        }
    }

    const quality = normalizeQuality(config.quality, requestConfig.model);
    const requestSize = resolveRequestSize(builtInGpt ? undefined : quality, config.size);
    const background = normalizeBackground(config.background, builtInGpt);
    const gptOptions = builtInGpt ? gptImageOptions(config, requestConfig.model) : {};
    const formData = new FormData();
    formData.set("model", requestConfig.model);
    formData.set("prompt", withSystemPrompt(requestConfig, requestPrompt));
    formData.set("n", String(n));
    // gpt-image models reject response_format; they always return b64.
    if (!builtInGpt) {
        formData.set("response_format", "b64_json");
    }
    if (gptOptions.output_format) formData.set("output_format", gptOptions.output_format);
    if (gptOptions.output_compression !== undefined) formData.set("output_compression", String(gptOptions.output_compression));
    if (gptOptions.moderation) formData.set("moderation", gptOptions.moderation);
    if (quality) {
        formData.set("quality", quality);
    }
    if (requestSize) {
        formData.set("size", requestSize);
    }
    if (background) {
        formData.set("background", background);
    }
    const files = await Promise.all(references.map(async (image) => dataUrlToFile({ ...image, dataUrl: await imageToDataUrl(image) })));
    const imageField = files.length > 1 ? "image[]" : "image";
    files.forEach((file) => formData.append(imageField, file));
    if (options?.mask) formData.set("mask", dataUrlToFile({ ...options.mask, dataUrl: await imageToDataUrl(options.mask) }));

    try {
        const response = await axios.post<ImageApiResponse>(aiApiUrl(requestConfig, "/images/edits"), formData, { headers: aiHeaders(requestConfig), signal: options?.signal });
        const outputFormat = (gptOptions.output_format || "png") as ImageOutputFormat;
        const metadata: ImageResponseMetadata = { outputFormat, usage: response.data.usage, requestId: String(response.headers["x-request-id"] || response.headers["request-id"] || "") || undefined };
        const images = await parseImagePayload(response.data, metadata);
        return images;
    } catch (error) {
        throw imageRequestError(error, apiText("requestFailed"));
    }
}

export async function requestImageQuestion(config: AiConfig, messages: AiTextMessage[], onDelta: (text: string) => void, options?: RequestOptions) {
    const requestConfig = resolveModelRequestConfig(config, config.model || config.textModel);
    const script = resolveModelScript(config, config.model || config.textModel);
    if (script) {
        try {
            const answer = await runModelPlugin<string>({
                capability: "text",
                script,
                config: requestConfig,
                messages: withSystemMessage(requestConfig, messages),
                signal: options?.signal,
                onDelta,
            });
            const text = String(answer ?? "").trim() || apiText("noContent");
            if (text === apiText("noContent")) onDelta(text);
            return text;
        } catch (error) {
            throw imageRequestError(error, apiText("requestFailed"));
        }
    }
    try {
        if (requestConfig.apiFormat === "gemini") {
            const answer = (await requestGeminiStreamingResponse(requestConfig, toGeminiBody(requestConfig, messages), onDelta, options)).content || apiText("noContent");
            if (answer === apiText("noContent")) onDelta(answer);
            return answer;
        }
        const answer = (await requestStreamingResponse(requestConfig, {
            model: requestConfig.model,
            input: toResponseInput(withSystemMessage(requestConfig, messages)),
            ...(requestConfig.reasoningEffort === "auto" ? {} : { reasoning: { effort: requestConfig.reasoningEffort } }),
        }, onDelta, options)).content || apiText("noContent");
        if (answer === apiText("noContent")) onDelta(answer);
        return answer;
    } catch (error) {
        throw imageRequestError(error, apiText("requestFailed"));
    }
}

export async function fetchChannelModels(channel: ModelChannel) {
    const baseUrl = channelApiBaseUrl(channel);
    try {
        if (channel.vendor === "google") {
            const response = await axios.get<GeminiPayload>(geminiApiUrl({ baseUrl, model: "" }), { headers: geminiHeaders(channel) });
            validateGeminiPayload(response.data);
            return (response.data.models || [])
                .map((model) => model.name?.replace(/^models\//, ""))
                .filter((id): id is string => Boolean(id))
                .sort((a, b) => a.localeCompare(b));
        }
        const response = await axios.get<{ data?: Array<{ id?: string }>; error?: { message?: string } }>(buildApiUrl(baseUrl, "/models"), {
            headers: {
                Authorization: `Bearer ${channel.apiKey}`,
            },
        });
        return (response.data.data || [])
            .map((model) => model.id)
            .filter((id): id is string => Boolean(id))
            .sort((a, b) => a.localeCompare(b));
    } catch (error) {
        throw imageRequestError(error, apiText("modelReadFailed"));
    }
}
