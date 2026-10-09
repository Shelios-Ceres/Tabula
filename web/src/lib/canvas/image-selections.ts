import type { ImageSelection } from "@/types/image-generation";

export type ImagePoint = { x: number; y: number };
export type ClientRectLike = { left: number; top: number; width: number; height: number };

export function clientPointToImage(clientX: number, clientY: number, rect: ClientRectLike, imageWidth: number, imageHeight: number): ImagePoint {
    return {
        x: clamp(((clientX - rect.left) / Math.max(1, rect.width)) * imageWidth, 0, imageWidth),
        y: clamp(((clientY - rect.top) / Math.max(1, rect.height)) * imageHeight, 0, imageHeight),
    };
}

export function pointSelection(point: ImagePoint, imageWidth: number, imageHeight: number): ImageSelection {
    return { kind: "point", x: fraction(point.x, imageWidth), y: fraction(point.y, imageHeight) };
}

export function rectangleSelection(start: ImagePoint, end: ImagePoint, imageWidth: number, imageHeight: number): ImageSelection {
    return {
        kind: "rectangle",
        x1: fraction(Math.min(start.x, end.x), imageWidth),
        y1: fraction(Math.min(start.y, end.y), imageHeight),
        x2: fraction(Math.max(start.x, end.x), imageWidth),
        y2: fraction(Math.max(start.y, end.y), imageHeight),
    };
}

export function selectionToImageBounds(selection: ImageSelection, imageWidth: number, imageHeight: number) {
    if (selection.kind === "point") {
        const x = clamp(selection.x, 0, 1) * imageWidth;
        const y = clamp(selection.y, 0, 1) * imageHeight;
        return { x1: x, y1: y, x2: x, y2: y };
    }
    return {
        x1: clamp(Math.min(selection.x1, selection.x2), 0, 1) * imageWidth,
        y1: clamp(Math.min(selection.y1, selection.y2), 0, 1) * imageHeight,
        x2: clamp(Math.max(selection.x1, selection.x2), 0, 1) * imageWidth,
        y2: clamp(Math.max(selection.y1, selection.y2), 0, 1) * imageHeight,
    };
}

function fraction(value: number, size: number) {
    return clamp(value / Math.max(1, size), 0, 1);
}

function clamp(value: number, min: number, max: number) {
    return Math.min(max, Math.max(min, value));
}
