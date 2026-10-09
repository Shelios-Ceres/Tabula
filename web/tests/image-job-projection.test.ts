import { describe, expect, test } from "bun:test";

const { projectImageJob } = await import("@/lib/canvas/image-job-projection");
const root = { id: "root", type: "image", title: "job", position: { x: 10, y: 20 }, width: 200, height: 200, metadata: {} } as any;
const image = (id: string, mimeType: string, role: "base" | "layer", zIndex: number, boundingBox?: { absolute?: [number, number, number, number]; normalized?: [number, number, number, number] }) => ({ id, dataUrl: `data:${mimeType};base64,x`, mimeType, width: 1000, height: 1000, bytes: 10, role, layer: { zIndex, boundingBox } });

describe("image job canvas projection", () => {
    test("projects bbox coordinates into ordered group children and preserves actual MIME", () => {
        const nodes = projectImageJob(root, { images: [image("layer", "image/png", "layer", 2, { normalized: [250, 100, 750, 600] }), image("base", "image/jpeg", "base", 0)], texts: [], errors: [] });
        expect(nodes[0].type).toBe("group");
        expect(nodes.slice(1).map((node) => node.id)).toEqual(["base", "layer"]);
        expect(nodes[1].metadata?.mimeType).toBe("image/jpeg");
        expect(nodes[2].metadata?.mimeType).toBe("image/png");
        expect(nodes[2].position).toEqual({ x: 60, y: 40 });
        expect(nodes[2].width).toBe(100);
        expect(nodes[2].height).toBe(100);
    });

    test("supports absolute pixel bounding boxes", () => {
        const nodes = projectImageJob(root, { images: [image("base", "image/jpeg", "base", 0), image("layer", "image/png", "layer", 1, { absolute: [100, 200, 500, 600] })], texts: [], errors: [] });
        expect(nodes[2].position).toEqual({ x: 30, y: 60 });
        expect(nodes[2].width).toBe(80);
        expect(nodes[2].height).toBe(80);
    });
});
