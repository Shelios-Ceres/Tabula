import { describe, expect, mock, test } from "bun:test";

mock.module("../../canvas-agent/src/canvas/tools.js", () => ({ nextCanvasX: () => 0 }));

const { buildCanvasToolRequest } = await import("../../canvas-agent/src/canvas/operations.ts");

describe("canvas video generation operation", () => {
    test("maps videoSize to config metadata size", () => {
        const request = buildCanvasToolRequest("canvas_generate_video", { prompt: "animate", size: "1:1", videoSize: "16:9" }, null);
        const addNode = (request.input.ops as Array<{ type: string; nodeType?: string; metadata?: Record<string, unknown> }>).find((op) => op.type === "add_node" && op.nodeType === "config");
        expect(addNode?.metadata?.size).toBe("16:9");
    });

    test("falls back to size when videoSize is absent", () => {
        const request = buildCanvasToolRequest("canvas_generate_video", { prompt: "animate", size: "9:16" }, null);
        const addNode = (request.input.ops as Array<{ type: string; nodeType?: string; metadata?: Record<string, unknown> }>).find((op) => op.type === "add_node" && op.nodeType === "config");
        expect(addNode?.metadata?.size).toBe("9:16");
    });
});
