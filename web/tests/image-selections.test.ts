import { describe, expect, test } from "bun:test";

import { clientPointToImage, pointSelection, rectangleSelection, selectionToImageBounds } from "@/lib/canvas/image-selections";

describe("image selection coordinates", () => {
    test("maps viewport coordinates to source image coordinates and clamps outside input", () => {
        const rect = { left: 100, top: 50, width: 400, height: 200 };
        expect(clientPointToImage(300, 150, rect, 1600, 800)).toEqual({ x: 800, y: 400 });
        expect(clientPointToImage(20, 400, rect, 1600, 800)).toEqual({ x: 0, y: 800 });
        expect(pointSelection({ x: 800, y: 200 }, 1600, 800)).toEqual({ kind: "point", x: 0.5, y: 0.25 });
    });

    test("normalizes forward and reverse rectangle drags to the same bounding box", () => {
        const forward = rectangleSelection({ x: 100, y: 50 }, { x: 900, y: 450 }, 1000, 500);
        const reverse = rectangleSelection({ x: 900, y: 450 }, { x: 100, y: 50 }, 1000, 500);
        expect(forward).toEqual({ kind: "rectangle", x1: 0.1, y1: 0.1, x2: 0.9, y2: 0.9 });
        expect(reverse).toEqual(forward);
        expect(selectionToImageBounds(reverse, 2000, 1000)).toEqual({ x1: 200, y1: 100, x2: 1800, y2: 900 });
    });
});
