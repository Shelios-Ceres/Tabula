import { getNativeImageCapabilities } from "@/lib/image-model-capabilities";
import type { ImageGenerationResult, ImageOutputFormat, ImageResponseMetadata } from "@/types/image";
import type { ImageJobOptions, ImageJobResult, ImageTransportContext, NativeImageRequest } from "@/types/image-generation";
import { checkResponse, imageToDataUrl, withLocalProxy } from "./common";

type XaiImageItem = { b64_json?: string | null; url?: string | null; mime_type?: string | null };

function endpoint(context: ImageTransportContext, edit: boolean) {
    const base = context.baseUrl.replace(/\/+$/, "");
    return withLocalProxy(`${/\/v1$/i.test(base) ? base : `${base}/v1`}/images/${edit ? "edits" : "generations"}`);
}

function formatOf(mimeType: string | null | undefined): ImageOutputFormat {
    return mimeType === "image/png" ? "png" : mimeType === "image/webp" ? "webp" : "jpeg";
}

/** xAI Grok Imagine: aspect_ratio + resolution instead of size/quality; edits take image or images as URL/data URL. */
export async function requestXaiImageJob(context: ImageTransportContext, request: NativeImageRequest, options: ImageJobOptions = {}): Promise<ImageJobResult> {
    const capabilities = getNativeImageCapabilities("xai", request.model);
    if (!capabilities.supported) throw new Error(`xAI adapter 暂不支持型号 ${request.model}`);
    if (options.mask) throw new Error("xAI 图片接口不支持蒙版编辑");
    if (request.references.length > capabilities.maxReferences) throw new Error(`${request.model} 最多支持 ${capabilities.maxReferences} 张参考图`);
    if (!Number.isInteger(request.count) || request.count < 1 || request.count > capabilities.maxImages) throw new Error(`${request.model} 单次最多生成 ${capabilities.maxImages} 张`);
    const xai = request.native.xai || {};
    const refs = await Promise.all(request.references.map((reference) => imageToDataUrl(reference)));
    const body = {
        model: request.model,
        prompt: request.prompt,
        n: request.count,
        response_format: "b64_json",
        ...(xai.resolution ? { resolution: xai.resolution } : {}),
        ...(xai.aspectRatio && xai.aspectRatio !== "auto" ? { aspect_ratio: xai.aspectRatio } : {}),
        ...(refs.length === 1 ? { image: { url: refs[0] } } : refs.length ? { images: refs.map((url) => ({ url })) } : {}),
    };
    options.onEvent?.({ type: "started" });
    const response = await fetch(endpoint(context, refs.length > 0), {
        method: "POST",
        headers: { Authorization: `Bearer ${context.apiKey}`, "Content-Type": "application/json" },
        body: JSON.stringify(body),
        signal: options.signal,
    });
    await checkResponse(response);
    const payload = (await response.json()) as { data?: XaiImageItem[] };
    const requestId = response.headers.get("x-request-id") || undefined;
    const images: ImageGenerationResult[] = (payload.data || []).flatMap((item) => {
        const outputFormat = formatOf(item.mime_type);
        const dataUrl = item.b64_json ? `data:${item.mime_type || "image/jpeg"};base64,${item.b64_json}` : item.url;
        const metadata: ImageResponseMetadata = { provider: "xai", protocol: "xai-images", model: request.model, outputFormat, requestId };
        return dataUrl ? [{ id: crypto.randomUUID(), dataUrl, response: metadata }] : [];
    });
    if (!images.length) throw new Error("xAI 响应未返回图片");
    images.forEach((image, index) => options.onEvent?.({ type: "output", index, image }));
    const result: ImageJobResult = { images, texts: [], errors: [] };
    options.onEvent?.({ type: "completed", result });
    return result;
}
