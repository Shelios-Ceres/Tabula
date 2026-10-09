import { channelApiBaseUrl, decodeChannelModel, modelOptionName, resolveImageProvider, resolveModelChannel, resolveModelScript, snapshotImageNativeOptions, type AiConfig } from "@/stores/use-config-store";
import { imageToDataUrl, uploadImage, type UploadedImage } from "@/services/image-storage";
import { getNativeImageCapabilities } from "@/lib/image-model-capabilities";
import type { ImageGenerationResult, ReferenceImage } from "@/types/image";
import type { ImageConversation, ImageGenerationEvent, ImageJobOptions, ImageJobResult, NativeImageRequest } from "@/types/image-generation";
import { requestGeneration as legacyGeneration, requestEdit as legacyEdit } from "./image";
import { requestArkImageJob } from "./image-native/ark";
import { requestGoogleImageJob } from "./image-native/google";
import { requestOpenAIImageJob } from "./image-native/openai";
import { requestXaiImageJob } from "./image-native/xai";

/** Preserve vendor steps, replacing only image bytes with local Blob references. */
type HistoryImageCache = Map<string, Promise<Pick<UploadedImage, "storageKey" | "mimeType">>>;
const historyImageKey = (value: string) => value.startsWith("data:image/") ? value.slice(value.indexOf(",") + 1) : value;
async function transformHistory(value: unknown, hydrate: boolean, fallbackFormat = "png", savedImages: HistoryImageCache = new Map()): Promise<unknown> {
    if (Array.isArray(value)) return Promise.all(value.map((entry) => transformHistory(entry, hydrate, fallbackFormat, savedImages)));
    if (!value || typeof value !== "object") return value;
    const record = value as Record<string, unknown>;
    if (hydrate && typeof record.__imageStorageKey === "string") {
        const url = await imageToDataUrl({ id: "history", name: "history.png", type: String(record.mimeType || "image/png"), dataUrl: "", storageKey: record.__imageStorageKey });
        return record.encoding === "base64" ? url.slice(url.indexOf(",") + 1) : url;
    }
    const next: Record<string, unknown> = {};
    for (const [key, child] of Object.entries(record)) {
        const isDataUrl = typeof child === "string" && /^data:image\//.test(child) && (key === "image_url" || key === "data");
        const isImageBytes = typeof child === "string" && ((key === "data" && record.type === "image") || (key === "result" && record.type === "image_generation_call"));
        if (!hydrate && (isDataUrl || isImageBytes)) {
            const mime = typeof record.mime_type === "string" ? record.mime_type : `image/${record.output_format || fallbackFormat}`;
            const cacheKey = historyImageKey(child);
            if (!savedImages.has(cacheKey)) savedImages.set(cacheKey, uploadImage(isDataUrl ? child : `data:${mime};base64,${child}`));
            const stored = await savedImages.get(cacheKey)!;
            if (!stored.storageKey) throw new Error("无法保存会话中的图片");
            next[key] = { __imageStorageKey: stored.storageKey, mimeType: stored.mimeType, encoding: isDataUrl ? "data-url" : "base64" };
        } else next[key] = await transformHistory(child, hydrate, fallbackFormat, savedImages);
    }
    return next;
}

export async function runImageJob(config: AiConfig, prompt: string, references: ReferenceImage[] = [], options: ImageJobOptions = {}): Promise<ImageJobResult> {
    if (options.signal?.aborted) {
        options.onEvent?.({ type: "client-aborted" });
        throw new DOMException("请求已取消", "AbortError");
    }
    const value = config.model || config.imageModel;
    const channel = resolveModelChannel(config, value);
    const provider = resolveImageProvider(config, value);
    const model = modelOptionName(value).replace(/^models\//, "");
    const native = snapshotImageNativeOptions(config, value);
    const context = { baseUrl: channelApiBaseUrl(channel), apiKey: channel.apiKey, channelId: channel.id };
    const address = new URL(context.baseUrl);
    if (address.username || address.password || address.search || address.hash) throw new Error("渠道地址不能携带账号或查询凭据，请在 API Key 字段配置鉴权");
    if (!context.apiKey.trim()) throw new Error("请先配置当前渠道的 API Key");
    const script = resolveModelScript(config, value);
    if (!script && !getNativeImageCapabilities(provider, model).supported) throw new Error(`当前渠道不支持型号 ${model}，请按官方型号配置`);
    const previous = options.conversation;
    if (previous && (previous.provider !== provider || previous.channelId !== channel.id || previous.baseUrl !== context.baseUrl)) throw new Error("该会话属于另一个渠道，请重新开始编辑分支");
    if (provider === "openai" && native.openai?.api === "responses") {
        const mainValue = native.openai.mainModel || config.textModel;
        const decoded = decodeChannelModel(mainValue);
        if (decoded && decoded.channelId !== channel.id) throw new Error("Responses 主模型必须与图像模型使用同一个 OpenAI 渠道");
        native.openai.mainModel = modelOptionName(mainValue);
        if (!channel.models.some((item) => item.name === native.openai?.mainModel && item.capability === "text")) throw new Error("请先在当前 OpenAI 渠道添加并选择 Responses 主模型");
    }
    const conversation = previous ? { ...previous, history: previous.history ? await transformHistory(previous.history, true) as unknown[] : undefined } : undefined;
    const request: NativeImageRequest = {
        model,
        prompt: [config.systemPrompt?.trim(), prompt.trim()].filter(Boolean).join("\n\n"),
        references,
        count: Number(config.count),
        quality: config.quality,
        size: config.size,
        background: config.background,
        outputFormat: config.outputFormat,
        outputCompression: config.outputCompression,
        moderation: config.moderation,
        native,
    };
    const localized = new Map<string, Promise<ImageGenerationResult>>();
    const historyImages: HistoryImageCache = new Map();
    const localize = (image: ImageGenerationResult) => {
        if (!localized.has(image.id)) localized.set(image.id, (async () => {
            const stored = await uploadImage(image.dataUrl, { signal: options.signal });
            if (!stored.storageKey) throw new Error("生成图片下载失败，请重新获取并保存结果");
            if (image.dataUrl.startsWith("data:image/")) historyImages.set(historyImageKey(image.dataUrl), Promise.resolve(stored));
            return { ...image, dataUrl: stored.url, storageKey: stored.storageKey, width: stored.width, height: stored.height, bytes: stored.bytes, mimeType: stored.mimeType };
        })());
        return localized.get(image.id)!;
    };
    let eventQueue = Promise.resolve();
    const onEvent = (event: ImageGenerationEvent) => {
        if (event.type === "completed") return;
        eventQueue = eventQueue.then(async () => {
            if (event.type === "output" && event.image) {
                try { options.onEvent?.({ ...event, image: await localize(event.image) }); }
                catch (error) { options.onEvent?.({ type: "output-error", index: event.index, error: { index: event.index ?? 0, message: error instanceof Error ? error.message : "结果保存失败" } }); }
            } else options.onEvent?.(event);
        });
    };
    let result: ImageJobResult;
    try {
        if (script) {
            // A model call script overrides the vendor adapter.
            const images = references.length ? await legacyEdit(config, prompt, references, options) : await legacyGeneration(config, prompt, options);
            result = { images, texts: [], errors: [] };
            images.forEach((image, index) => onEvent({ type: "output", index, image }));
        } else {
            const adapter = provider === "ark" ? requestArkImageJob : provider === "google" ? requestGoogleImageJob : provider === "xai" ? requestXaiImageJob : requestOpenAIImageJob;
            result = await adapter(context, request, { ...options, conversation, onEvent });
        }
        await eventQueue;
        options.signal?.throwIfAborted();
        const storedImages: ImageGenerationResult[] = [];
        const storedIndexes = new Map<number, number>();
        for (let index = 0; index < result.images.length; index++) {
            try {
                const image = await localize(result.images[index]);
                storedIndexes.set(index, storedImages.length);
                storedImages.push({ ...image, response: result.images[index].response });
            }
            catch (error) { result.errors.push({ index, message: error instanceof Error ? error.message : "结果保存失败" }); }
        }
        let savedConversation: ImageConversation | undefined;
        if (result.conversation) {
            const history = result.conversation.history;
            const archivedPrefix = previous?.history || [];
            const nextHistory = history && archivedPrefix.length ? [...archivedPrefix, ...history.slice(archivedPrefix.length)] : history;
            savedConversation = { ...result.conversation, channelId: channel.id, baseUrl: context.baseUrl, history: nextHistory ? await transformHistory(nextHistory, false, request.outputFormat, historyImages) as unknown[] : undefined };
        }
        options.signal?.throwIfAborted();
        const outputOrder = result.outputOrder?.flatMap((entry) => entry.kind === "text" ? [entry] : storedIndexes.has(entry.index) ? [{ ...entry, index: storedIndexes.get(entry.index)! }] : []);
        result = { ...result, images: storedImages.map((image) => ({ ...image, response: { ...image.response, provider, model: image.response?.model || model, conversation: savedConversation } })), conversation: savedConversation, outputOrder };
        options.onEvent?.({ type: "completed", result });
        return result;
    } catch (error) {
        await eventQueue;
        if (options.signal?.aborted) options.onEvent?.({ type: "client-aborted" });
        throw error;
    }
}

export async function requestImageGeneration(config: AiConfig, prompt: string, options?: ImageJobOptions) {
    const result = await runImageJob(config, prompt, [], options);
    if (!result.images.length) throw new Error(result.errors[0]?.message || "接口没有返回图片");
    return result.images;
}

export async function requestImageEdit(config: AiConfig, prompt: string, references: ReferenceImage[], options?: ImageJobOptions) {
    const result = await runImageJob(config, prompt, references, options);
    if (!result.images.length) throw new Error(result.errors[0]?.message || "接口没有返回图片");
    return result.images;
}
