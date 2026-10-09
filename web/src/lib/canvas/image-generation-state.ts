import type { CanvasNodeData, CanvasNodeImage } from "@/types/canvas";
import type { ImageGenerationEvent, ImageJobResult } from "@/types/image-generation";
import { canvasJobImage } from "./image-job-projection";
import { fitNodeSize } from "./canvas-node-size";

export type CanvasGenerationRequest = {
    targetNodeId: string;
    originNodeId: string;
    runningNodeId: string;
    controller: AbortController;
};

export function runningGenerationNodeIds(requests: Iterable<CanvasGenerationRequest>) {
    return new Set([...requests].filter((request) => !request.controller.signal.aborted).map((request) => request.runningNodeId));
}

export function finishGenerationRequests(requests: Map<string, CanvasGenerationRequest>, controller: AbortController) {
    return new Map([...requests].filter(([, request]) => request.controller !== controller));
}

export function applyImageGenerationEvent(node: CanvasNodeData, event: ImageGenerationEvent): CanvasNodeData {
    if (event.type !== "output" && event.type !== "output-error") return node;
    const images = [...(node.metadata?.images || [])];
    const pending = images.findIndex((image) => image.status === "loading");
    const index = event.index ?? (pending < 0 ? images.length : pending);
    // 输出序号可能跳号或乱序到达：中间先补加载占位，避免数组出现空位
    for (let gap = images.length; gap < index; gap++) images[gap] = { id: `${node.id}-pending-${gap}`, content: "", naturalWidth: 0, naturalHeight: 0, bytes: 0, mimeType: "", status: "loading" };
    if (event.image) images[index] = canvasJobImage(event.image);
    else if (event.error && images[index]?.status !== "success") images[index] = { id: images[index]?.id || `${node.id}-error-${index}`, content: "", naturalWidth: 0, naturalHeight: 0, bytes: 0, mimeType: "", status: "error", errorDetails: event.error.message };
    else return node;
    const first = images.find((image) => image.status === "success" && image.content);
    return { ...node, metadata: { ...node.metadata, images, ...(first ? imageMetadata(first) : {}) } };
}

export function failImageGeneration(node: CanvasNodeData, errorDetails: string, imageId?: string): CanvasNodeData {
    const images = node.metadata?.images?.map((image) => (imageId ? image.id === imageId : image.status === "loading") && image.status !== "success" ? { ...image, status: "error" as const, errorDetails } : image);
    const first = images?.find((image) => image.status === "success" && image.content);
    return {
        ...node,
        metadata: {
            ...node.metadata,
            ...(!node.metadata?.content && first ? imageMetadata(first) : {}),
            images,
            status: node.metadata?.content || first ? "success" : "error",
            errorDetails,
        },
    };
}

export function mergeImageRetryResult(node: CanvasNodeData, imageId: string, result: ImageJobResult, fallbackError: string): CanvasNodeData {
    const previous = node.metadata?.images || [];
    const target = previous.findIndex((image) => image.id === imageId);
    if (target < 0) return node;
    if (!result.images.length) return failImageGeneration(node, result.errors[0]?.message || fallbackError, imageId);
    const replacements = result.images.map((image, index) => ({ ...canvasJobImage(image), id: index ? image.id : imageId }));
    const errors: CanvasNodeImage[] = result.errors.map((error, index) => ({ id: `${imageId}-error-${error.index}-${index}`, content: "", naturalWidth: 0, naturalHeight: 0, bytes: 0, mimeType: "", status: "error", errorDetails: error.message }));
    const images = previous.flatMap((image) => image.id === imageId ? [...replacements, ...errors] : [image]);
    const primary = images.find((image) => image.id === node.metadata?.primaryImageId && image.status === "success" && image.content) || images.find((image) => image.status === "success" && image.content)!;
    const primaryChanged = primary.id === imageId || !node.metadata?.content;
    const edge = Math.max(node.width, node.height);
    const size = primaryChanged && !node.metadata?.freeResize ? fitNodeSize(primary.naturalWidth, primary.naturalHeight, edge, edge) : { width: node.width, height: node.height };
    const texts = [...(node.metadata?.imageTexts || []), ...result.texts];
    const offset = replacements.length + errors.length - 1;
    const outputOrder = [
        ...(node.metadata?.imageOutputOrder || []).filter((item) => item.kind !== "image" || item.index !== target).map((item) => item.kind === "image" && item.index > target ? { ...item, index: item.index + offset } : item),
        ...(result.outputOrder || result.images.map((_, index) => ({ kind: "image" as const, index }))).map((item) => ({ ...item, index: item.index + (item.kind === "image" ? target : texts.length - result.texts.length) })),
    ];
    return {
        ...node,
        ...size,
        position: { x: node.position.x + (node.width - size.width) / 2, y: node.position.y + (node.height - size.height) / 2 },
        metadata: {
            ...node.metadata,
            ...(primaryChanged ? imageMetadata(primary) : {}),
            images,
            primaryImageId: primary.id,
            count: images.filter((image) => image.status === "success").length,
            status: "success",
            errorDetails: result.errors.length ? result.errors.map((error) => error.message).join("\n") : undefined,
            imageConversation: primaryChanged ? result.conversation : node.metadata?.imageConversation,
            imageTexts: texts,
            imageOutputOrder: outputOrder,
        },
    };
}

function imageMetadata(image: CanvasNodeImage) {
    return { content: image.content, storageKey: image.storageKey, naturalWidth: image.naturalWidth, naturalHeight: image.naturalHeight, bytes: image.bytes, mimeType: image.mimeType, imageResponse: image.imageResponse, primaryImageId: image.id };
}
