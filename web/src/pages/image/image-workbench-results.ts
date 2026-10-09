import type { AiConfig } from "@/stores/use-config-store";
import type { ImageGenerationResult, ReferenceImage } from "@/types/image";
import type { ImageConversation, ImageJobResult, ImageOutputError, ImageProvider } from "@/types/image-generation";

export type GenerationLogConfig = Pick<AiConfig, "model" | "imageModel" | "quality" | "size" | "count" | "background" | "outputFormat" | "outputCompression" | "moderation" | "imageNative">;
export type ImageJobSnapshot = { text: string; config: AiConfig; references: ReferenceImage[]; conversation?: ImageConversation };
export type GeneratedImage = ImageGenerationResult & {
    durationMs: number;
    width: number;
    height: number;
    bytes: number;
    requestConfig?: GenerationLogConfig;
    requestPrompt?: string;
};
export type GenerationResult = {
    id: string;
    status: "pending" | "success" | "failed";
    image?: GeneratedImage;
    error?: string;
    outputError?: ImageOutputError;
    outputIndex?: number;
    retry?: ImageJobSnapshot;
};
export type WorkbenchRetry = { index: number; wholeJob: boolean; snapshot: ImageJobSnapshot };

export function requiresWholeJobRetry(snapshot: ImageJobSnapshot, provider: ImageProvider) {
    return provider === "ark" && snapshot.config.imageNative.ark?.layerDecomposition === true;
}

export function buildWorkbenchRetry(current: GenerationResult[], index: number, provider: ImageProvider) {
    const target = current[index];
    const snapshot = target?.retry;
    if (!snapshot || target.status === "pending") return undefined;
    const wholeJob = requiresWholeJobRetry(snapshot, provider);
    if (target.status !== "failed" && !wholeJob) return undefined;
    return { snapshot: wholeJob ? snapshot : { ...snapshot, config: { ...snapshot.config, count: "1" } }, retry: { index, wholeJob, snapshot } };
}

export function workbenchRequestError(value: unknown, fallback: string): ImageOutputError {
    const error = value && typeof value === "object" ? value as Record<string, unknown> : {};
    return { index: -1, message: typeof error.message === "string" ? error.message : fallback, ...(typeof error.code === "string" ? { code: error.code } : {}), ...(typeof error.status === "number" ? { status: error.status } : {}), ...(typeof error.requestId === "string" ? { requestId: error.requestId } : {}) };
}

export function upsertWorkbenchResult(current: GenerationResult[], index: number | undefined, next: GenerationResult) {
    const existingImage = next.image ? current.findIndex((item) => item.image?.id === next.image!.id) : -1;
    const target = existingImage >= 0 ? existingImage : index === undefined
        ? current.findIndex((item) => item.status === "pending")
        : current.findIndex((item) => item.outputIndex === index && item.status !== "success");
    const value = { ...next, outputIndex: index ?? current[target]?.outputIndex };
    return target < 0 ? [...current, value] : current.map((item, itemIndex) => itemIndex === target ? value : item);
}

export function finishWorkbenchResults(current: GenerationResult[], images: GeneratedImage[], errors: ImageOutputError[], retry: ImageJobSnapshot, jobId: string) {
    let next = current;
    for (const image of images) next = upsertWorkbenchResult(next, undefined, { id: image.id, status: "success", image, retry });
    for (const error of errors) next = upsertWorkbenchResult(next, error.index, { id: `${jobId}:error:${error.index}`, status: "failed", error: error.message, outputError: error, retry });
    return next.filter((item) => item.status !== "pending");
}

export function failWorkbenchResults(current: GenerationResult[], error: ImageOutputError, retry: ImageJobSnapshot) {
    return current.map((item, index) => item.status === "pending" ? { ...item, status: "failed" as const, error: error.message, outputError: { ...error, index: item.outputIndex ?? index }, retry } : item);
}

export function mergeWorkbenchRetry(current: GenerationResult[], attempt: GenerationResult[], retry?: WorkbenchRetry) {
    if (!retry) return attempt;
    if (retry.wholeJob) return [...current.filter((item) => item.status === "success" || item.retry !== retry.snapshot), ...attempt];
    return [...current.slice(0, retry.index), ...attempt, ...current.slice(retry.index + 1)];
}

export function mergeWorkbenchOutputOrder(previous: ImageJobResult["outputOrder"], previousImages: GeneratedImage[], textOffset: number, next: ImageJobResult, images: GeneratedImage[]) {
    const imageIndex = new Map(images.map((image, index) => [image.id, index]));
    const remap = (order: ImageJobResult["outputOrder"], source: ImageGenerationResult[], offset: number) => (order || []).flatMap<NonNullable<ImageJobResult["outputOrder"]>[number]>((item) => {
        if (item.kind === "text") return [{ ...item, index: item.index + offset }];
        const index = imageIndex.get(source[item.index]?.id);
        return index === undefined ? [] : [{ ...item, index }];
    });
    return [...remap(previous, previousImages, 0), ...remap(next.outputOrder, next.images, textOffset)];
}
