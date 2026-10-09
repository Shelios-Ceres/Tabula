import { imageToDataUrl } from "@/services/image-storage";
import { readImageMeta } from "@/lib/image-utils";
import type { ImageGenerationResult } from "@/types/image";
import type { ImageGenerationEvent, ImageJobOptions, ImageJobResult, ImageLayer, ImageSelection, ImageTransportContext, NativeImageRequest } from "@/types/image-generation";
import { withLocalProxy } from "@/stores/use-config-store";
import { getNativeImageCapabilities } from "@/lib/image-model-capabilities";
import { checkResponse, readSse } from "./common";

function emit(options: ImageJobOptions, event: ImageGenerationEvent) { options.onEvent?.(event); }
function id() { return crypto.randomUUID(); }
function url(context: ImageTransportContext) {
    const base = context.baseUrl.trim().replace(/\/+$/, "");
    const api = /\/api\/v3$/i.test(base) ? base : `${base}/api/v3`;
    return withLocalProxy(`${api}/images/generations`);
}
function promptWithSelections(prompt: string, selections: ImageSelection[] | undefined) {
    if (!selections?.length) return prompt;
    const coordinate = (value: number) => Math.min(999, Math.round(value * 1000));
    const tags = selections.map((selection) => {
        const prefix = selection.imageIndex === undefined ? "" : `图 ${selection.imageIndex + 1} `;
        if (selection.kind === "point") return `${prefix}<point>${coordinate(selection.x)} ${coordinate(selection.y)}</point>`;
        return `${prefix}<bbox>${coordinate(selection.x1)} ${coordinate(selection.y1)} ${coordinate(selection.x2)} ${coordinate(selection.y2)}</bbox>`;
    });
    return `${prompt}\n${tags.join(" ")}`;
}
function outputFormat(request: NativeImageRequest) { return request.outputFormat; }
function errorMessage(value: unknown) {
    if (!value || typeof value !== "object") return "方舟图片请求失败";
    const record = value as Record<string, unknown>;
    const error = record.error && typeof record.error === "object" ? record.error as Record<string, unknown> : {};
    return String(error.message || record.message || record.msg || "方舟图片请求失败");
}
function itemResult(item: unknown, request: NativeImageRequest, response: Record<string, unknown> = {}, layer?: ImageLayer, role: ImageGenerationResult["role"] = "image") {
    if (!item || typeof item !== "object") return null;
    const value = item as Record<string, unknown>;
    const format = role === "layer" ? "png" : String(value.output_format || value.format || response.outputFormat || outputFormat(request));
    const dataUrl = typeof value.b64_json === "string" ? `data:image/${format};base64,${value.b64_json}` : typeof value.url === "string" ? value.url : typeof value.image_url === "string" ? value.image_url : undefined;
    if (!dataUrl) return null;
    return { id: typeof value.id === "string" ? value.id : id(), dataUrl, role, layer, response: { ...response, ...(typeof value.size === "string" ? { size: value.size } : {}), outputFormat: format } as ImageGenerationResult["response"] };
}
function layerFrom(value: unknown): ImageLayer | undefined {
    if (!value || typeof value !== "object") return undefined;
    const item = value as Record<string, unknown>;
    const box = item.bounding_box && typeof item.bounding_box === "object" ? item.bounding_box as Record<string, unknown> : undefined;
    const asBox = (key: string) => Array.isArray(box?.[key]) ? box?.[key] as [number, number, number, number] : undefined;
    return { zIndex: typeof item.z_index === "number" ? item.z_index : 0, name: typeof item.name === "string" ? item.name : undefined, description: typeof item.description === "string" ? item.description : undefined, boundingBox: box ? { absolute: asBox("absolute"), normalized: asBox("normalized") } : undefined };
}
function validSize(size: string, tiers: readonly string[], range: readonly [number, number] = [0, Infinity]) {
    if (tiers.includes(size)) return true;
    const match = /^(\d+)x(\d+)$/i.exec(size);
    if (!match) return false;
    const width = Number(match[1]); const height = Number(match[2]);
    if (!Number.isSafeInteger(width) || !Number.isSafeInteger(height) || width < 1 || height < 1) return false;
    const pixels = width * height; const ratio = width / height;
    return pixels >= range[0] && pixels <= range[1] && ratio >= 1 / 16 && ratio <= 16;
}
function validateSelections(selections: ImageSelection[] | undefined, referenceCount: number) {
    for (const selection of selections || []) {
        const values = selection.kind === "point" ? [selection.x, selection.y] : [selection.x1, selection.y1, selection.x2, selection.y2];
        if (values.some((value) => !Number.isFinite(value) || value < 0 || value > 1)) throw new Error("选区坐标必须是 0 到 1 之间的有限数值");
        if (selection.kind === "rectangle" && (selection.x1 > selection.x2 || selection.y1 > selection.y2)) throw new Error("矩形选区坐标必须按左上到右下填写");
        if (selection.imageIndex !== undefined && (!Number.isInteger(selection.imageIndex) || selection.imageIndex < 0 || selection.imageIndex >= referenceCount)) throw new Error("选区 imageIndex 超出参考图范围");
    }
}
async function hasAlpha(dataUrl: string) {
    if (typeof createImageBitmap !== "function") return false;
    try {
        const bitmap = await createImageBitmap(await (await fetch(dataUrl)).blob());
        const canvas = typeof OffscreenCanvas !== "undefined" ? new OffscreenCanvas(bitmap.width, bitmap.height) : document.createElement("canvas");
        canvas.width = bitmap.width; canvas.height = bitmap.height;
        const context = canvas.getContext("2d");
        if (!context) return false;
        context.drawImage(bitmap, 0, 0);
        return context.getImageData(0, 0, bitmap.width, bitmap.height).data.some((_, index, pixels) => index % 4 === 3 && pixels[index] < 255);
    } catch { return false; }
}
function parsePayload(payload: Record<string, unknown>, request: NativeImageRequest, requestId?: string) {
    const response = {
        provider: "ark" as const,
        protocol: "ark-images" as const,
        ...(typeof payload.model === "string" ? { model: payload.model } : {}),
        ...(typeof payload.output_format === "string" ? { outputFormat: payload.output_format as "png" | "jpeg" | "webp" } : {}),
        ...(typeof payload.size === "string" ? { size: payload.size } : {}),
        ...(typeof payload.background === "string" ? { background: payload.background } : {}),
        rawUsage: payload.usage as Record<string, unknown> | undefined,
        requestId,
    };
    const data = Array.isArray(payload.data) ? payload.data : [];
    const images: ImageGenerationResult[] = [];
    data.forEach((entry, entryIndex) => {
        const item = entry as Record<string, unknown>;
        const isLayer = Boolean(request.native.ark?.layerDecomposition) && entryIndex > 0;
        const base = itemResult(item, request, isLayer ? { ...response, outputFormat: "png" } : response, isLayer ? layerFrom(item) : undefined, isLayer ? "layer" : request.native.ark?.layerDecomposition ? "base" : "image");
        if (base) images.push(base);
    });
    return { images, response };
}

export async function requestArkImageJob(context: ImageTransportContext, request: NativeImageRequest, options: ImageJobOptions = {}): Promise<ImageJobResult> {
    const ark = request.native.ark || {};
    const endpointId = ark.endpoint?.id?.trim();
    if (endpointId && !/^ep-[A-Za-z0-9-]+$/.test(endpointId)) throw new Error("推理接入点应填写 ep- 开头的 Endpoint ID；底层型号仍选择对应的 Seedream 型号");
    const capabilities = getNativeImageCapabilities("ark", request.model);
    if (!capabilities.supported) throw new Error(`方舟 adapter 暂不支持型号 ${request.model}，请使用 Seedream 4.0 / 4.5 / 5.0 系列`);
    if (!Number.isInteger(request.count) || request.count < 1) throw new Error("图片数量必须是正整数");
    if (!(capabilities.outputFormats as readonly string[]).includes(request.outputFormat)) throw new Error(`${request.model} 仅支持 ${capabilities.outputFormats.join(" / ").toUpperCase()} 输出`);
    if (request.references.length > capabilities.maxReferences) throw new Error(`${request.model} 最多支持 ${capabilities.maxReferences} 张参考图`);
    if (!validSize(ark.size || request.size, capabilities.sizes, capabilities.pixels)) throw new Error(`${request.model} 不支持该图片尺寸`);
    if (ark.optimizePrompt === "fast" && !capabilities.supportsFastPrompt) throw new Error(`${request.model} 不支持 fast 提示词优化`);
    if (request.native.stream && !capabilities.supportsStream) throw new Error(`${request.model} 不支持流式输出`);
    if (ark.webSearch && !capabilities.supportsWebSearch) throw new Error(`${request.model} 不支持 web_search`);
    if ((request.count > 1 || ark.sequential) && !capabilities.supportsSequential) throw new Error(`${request.model} 不支持多图或连续生成`);
    if (request.count > 1 && !ark.sequential) throw new Error("生成多张图片必须启用连续生成（sequential）");
    if (request.references.length + request.count > capabilities.maxImages && ark.sequential) throw new Error(`参考图与输出数量之和不能超过 ${capabilities.maxImages}`);
    if (ark.layerDecomposition && (!capabilities.supportsLayers || request.references.length !== 1 || request.count !== 1)) throw new Error("图层分解仅支持 Seedream 5.0 Pro、单张参考图和单张输出");
    if (ark.transparent && (!capabilities.supportsLayers || request.references.length !== 1 || request.outputFormat !== "png")) throw new Error("透明图层仅支持 Seedream 5.0 Pro、单张参考图和 PNG 输出");
    validateSelections(options.selections, request.references.length);
    const refs = await Promise.all(request.references.map(async (reference) => {
        const dataUrl = await imageToDataUrl(reference);
        await readImageMeta(dataUrl);
        return dataUrl;
    }));
    const sequential = capabilities.supportsSequential && ark.sequential === true && request.count > 1;
    const body: Record<string, unknown> = {
        model: endpointId || request.model,
        prompt: promptWithSelections(request.prompt, options.selections),
        size: ark.size || request.size,
        response_format: "b64_json",
        // Seedream 4.x has no output_format and always returns JPEG.
        ...(capabilities.outputFormats.length > 1 ? { output_format: outputFormat(request) } : {}),
        watermark: ark.watermark === true,
        optimize_prompt_options: { mode: ark.optimizePrompt === "fast" ? "fast" : "standard" },
        ...(refs.length ? { image: refs } : {}),
        ...(ark.transparent ? { background: "transparent" } : {}),
        ...(capabilities.supportsLayers && ark.layerDecomposition ? { layer_decomposition: true } : {}),
        ...(sequential ? { sequential_image_generation: "auto", sequential_image_generation_options: { max_images: request.count } } : {}),
        ...(ark.webSearch ? { tools: [{ type: "web_search" }] } : {}),
    };
    if (ark.transparent && !(await hasAlpha(refs[0]))) throw new Error("透明图层要求输入参考图包含 alpha 通道");
    emit(options, { type: "started" });
    const stream = request.native.stream === true;
    const response = await fetch(url(context), { method: "POST", headers: { Authorization: `Bearer ${context.apiKey}`, "Content-Type": "application/json", Accept: stream ? "text/event-stream" : "application/json" }, body: JSON.stringify({ ...body, ...(stream ? { stream: true } : {}) }), signal: options.signal });
    await checkResponse(response);
    const images: ImageGenerationResult[] = [];
    const errors: ImageJobResult["errors"] = [];
    let usage: Record<string, unknown> | undefined;
    let completed = !stream;
    if (stream) {
        await readSse(response, ({ event, data }: { event: string; data: string }) => {
            let payload: Record<string, unknown> = {}; try { payload = JSON.parse(data); } catch { return; }
            const kind = event && event !== "message" ? event : String(payload.type || "");
            if (payload.usage && typeof payload.usage === "object") usage = payload.usage as Record<string, unknown>;
            const index = typeof payload.image_index === "number" ? payload.image_index : images.length;
            if (kind === "image_generation.partial_failed") { const detail = payload.error as Record<string, unknown> | undefined; const error = { index, message: errorMessage(payload), code: typeof detail?.code === "string" ? detail.code : typeof payload.code === "string" ? payload.code : undefined }; errors.push(error); emit(options, { type: "output-error", index, error }); return; }
            if (kind === "error" || payload.error) throw new Error(errorMessage(payload));
            if (kind === "image_generation.partial_succeeded") { const parsed = parsePayload({ ...payload, data: [payload] }, request, response.headers.get("x-request-id") || undefined); const image = parsed.images[0]; if (image) { images[index] = image; emit(options, { type: "output", index, image }); } return; }
            if (kind === "image_generation.completed") completed = true;
        });
    } else {
        const payload = await response.json() as Record<string, unknown>;
        if (payload.error || payload.code && !Array.isArray(payload.data)) throw new Error(errorMessage(payload));
        const parsed = parsePayload(payload, request, response.headers.get("x-request-id") || undefined);
        images.push(...parsed.images);
        usage = payload.usage as Record<string, unknown> | undefined;
        parsed.images.forEach((image, index) => emit(options, { type: "output", index, image }));
    }
    if (stream && !completed) throw new Error("方舟流式响应未收到 image_generation.completed 终态事件");
    if (!images.length && !errors.length) throw new Error("方舟响应未返回图片");
    const result: ImageJobResult = { images: images.filter(Boolean), texts: [], errors, providerMetadata: { rawUsage: usage, model: request.model, requestModel: endpointId || request.model } };
    result.images.forEach((image) => { image.response = { ...image.response, rawUsage: usage, providerMetadata: result.providerMetadata }; });
    emit(options, { type: "completed", result });
    return result;
}
