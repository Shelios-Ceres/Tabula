import { fitNodeSize } from "./canvas-node-size";
import type { ImageGenerationResult } from "@/types/image";
import type { ImageJobResult } from "@/types/image-generation";
import { CanvasNodeType, type CanvasNodeData, type CanvasNodeImage } from "@/types/canvas";

export function canvasJobImage(image: ImageGenerationResult): CanvasNodeImage {
    return { id: image.id, content: image.dataUrl, storageKey: image.storageKey, naturalWidth: image.width || 0, naturalHeight: image.height || 0, bytes: image.bytes || 0, mimeType: image.mimeType || "", status: "success", imageResponse: image.response, role: image.role, layer: image.layer };
}

export function projectImageJob(root: CanvasNodeData, result: ImageJobResult): CanvasNodeData[] {
    const images = result.images.map(canvasJobImage);
    const first = images.find((image) => image.role === "base") || images[0];
    const metadata = { ...root.metadata, imageConversation: result.conversation, imageTexts: result.texts, imageOutputOrder: result.outputOrder };
    if (!first) return [{ ...root, metadata: { ...metadata, images: [], status: "error", errorDetails: result.errors[0]?.message || "接口没有返回图片" } }];
    const size = fitNodeSize(first.naturalWidth, first.naturalHeight, root.width, root.height);
    if (images.some((image) => image.role === "base" || image.role === "layer")) {
        const ordered = [...images].sort((a, b) => (a.layer?.zIndex || 0) - (b.layer?.zIndex || 0));
        const children = ordered.map((image): CanvasNodeData => {
            const box = image.layer?.boundingBox;
            const normalized = box?.normalized;
            const absolute = box?.absolute;
            const bounds = normalized ? normalized.map((value) => value / 1000) : absolute ? absolute.map((value, index) => value / (index % 2 === 0 ? first.naturalWidth : first.naturalHeight)) : [0, 0, 1, 1];
            const width = (bounds[2] - bounds[0]) * size.width;
            const height = (bounds[3] - bounds[1]) * size.height;
            if (![...bounds, width, height].every(Number.isFinite) || width <= 0 || height <= 0) throw new Error("图层返回了无效的位置或尺寸");
            return {
                id: image.id,
                type: CanvasNodeType.Image,
                title: image.layer?.name || (image.role === "base" ? "底图" : `图层 ${image.layer?.zIndex ?? ""}`),
                position: { x: root.position.x + bounds[0] * size.width, y: root.position.y + bounds[1] * size.height },
                width,
                height,
                metadata: { ...metadata, content: image.content, storageKey: image.storageKey, naturalWidth: image.naturalWidth, naturalHeight: image.naturalHeight, mimeType: image.mimeType, bytes: image.bytes, groupId: root.id, status: "success", imageLayer: image.layer, imageResponse: image.imageResponse, count: 1, imageNative: { ...metadata.imageNative, ark: { ...metadata.imageNative?.ark, layerDecomposition: false } } },
            };
        });
        return [{ ...root, type: CanvasNodeType.Group, ...size, metadata: { ...metadata, content: undefined, images: undefined, status: "success" } }, ...children];
    }
    const errors: CanvasNodeImage[] = result.errors.map((error) => ({ id: `${root.id}-error-${error.index}`, content: "", naturalWidth: 0, naturalHeight: 0, bytes: 0, mimeType: "", status: "error", errorDetails: error.message }));
    return [{
        ...root,
        ...size,
        metadata: { ...metadata, content: first.content, storageKey: first.storageKey, naturalWidth: first.naturalWidth, naturalHeight: first.naturalHeight, mimeType: first.mimeType, bytes: first.bytes, imageResponse: first.imageResponse, primaryImageId: first.id, images: [...images, ...errors], count: images.length, status: "success", errorDetails: result.errors.length ? result.errors.map((error) => error.message).join("\n") : undefined },
    }];
}
