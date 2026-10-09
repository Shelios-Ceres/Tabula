import { describe, expect, test } from "bun:test";
import { applyImageGenerationEvent, failImageGeneration, finishGenerationRequests, mergeImageRetryResult, runningGenerationNodeIds, type CanvasGenerationRequest } from "@/lib/canvas/image-generation-state";
import { CanvasNodeType, type CanvasNodeData, type CanvasNodeImage } from "@/types/canvas";
import type { ImageGenerationResult } from "@/types/image";

const slot = (id: string, status: CanvasNodeImage["status"]): CanvasNodeImage => ({ id, status, content: status === "success" ? `blob:${id}` : "", storageKey: status === "success" ? `image:${id}` : undefined, naturalWidth: 800, naturalHeight: 600, bytes: 10, mimeType: "image/png" });
const root = (images: CanvasNodeImage[]): CanvasNodeData => ({ id: "root", type: CanvasNodeType.Image, title: "request", position: { x: 40, y: 60 }, width: 400, height: 300, metadata: { status: "loading", prompt: "saved prompt", model: "saved-model", imageRequestCount: 3, images } });
const output = (id: string): ImageGenerationResult => ({ id, dataUrl: `blob:${id}`, storageKey: `image:${id}`, width: 800, height: 600, bytes: 10, mimeType: "image/png" });
const request = (targetNodeId: string, runningNodeId: string, controller: AbortController): CanvasGenerationRequest => ({ targetNodeId, originNodeId: runningNodeId, runningNodeId, controller });

describe("canvas image lifecycle", () => {
    test("a failed blank image leaves neither the root nor its slot loading", () => {
        const node = failImageGeneration(root([slot("pending", "loading")]), "SSL failure");
        expect(node.metadata?.status).toBe("error");
        expect(node.metadata?.images?.[0]).toMatchObject({ status: "error", errorDetails: "SSL failure" });
        expect(node.metadata?.prompt).toBe("saved prompt");
        expect(node.metadata?.model).toBe("saved-model");
    });

    test("late stream failure preserves delivered images and fails only pending slots", () => {
        const pending = root([slot("a", "loading"), slot("b", "loading"), slot("c", "loading")]);
        const partial = applyImageGenerationEvent(pending, { type: "output", index: 1, image: output("saved") });
        const failed = failImageGeneration(partial, "stream interrupted");
        expect(failed.metadata?.status).toBe("success");
        expect(failed.metadata?.content).toBe("blob:saved");
        expect(failed.metadata?.images?.map((image) => image.status)).toEqual(["error", "success", "error"]);
        expect(failed.metadata?.images?.[1]).toBe(partial.metadata?.images?.[1]);
    });

    test("cancellation preserves completed images and their actual storage keys", () => {
        const node = failImageGeneration(root([slot("saved", "success"), slot("pending", "loading")]), "Request canceled");
        expect(node.metadata?.status).toBe("success");
        expect(node.metadata?.storageKey).toBe("image:saved");
        expect(node.metadata?.images?.[1].errorDetails).toBe("Request canceled");
    });

    test("a provider slot error is retained when a later transport error occurs", () => {
        const partial = applyImageGenerationEvent(root([slot("a", "loading"), slot("b", "loading")]), { type: "output-error", index: 0, error: { index: 0, message: "provider rejected this output" } });
        const failed = failImageGeneration(partial, "network failure");
        expect(failed.metadata?.images?.map((image) => image.errorDetails)).toEqual(["provider rejected this output", "network failure"]);
    });

    test("a later error event cannot erase an already delivered image", () => {
        const saved = applyImageGenerationEvent(root([slot("a", "loading")]), { type: "output", index: 0, image: output("saved") });
        expect(applyImageGenerationEvent(saved, { type: "output-error", index: 0, error: { index: 0, message: "late failure" } })).toBe(saved);
    });

    test("retry returning only errors does not erase successful siblings", () => {
        const success = slot("saved", "success");
        const node = mergeImageRetryResult(root([success, slot("retry", "loading")]), "retry", { images: [], texts: [], errors: [{ index: 0, message: "storage full" }] }, "no image");
        expect(node.metadata?.images).toHaveLength(2);
        expect(node.metadata?.images?.[0]).toBe(success);
        expect(node.metadata?.images?.[1]).toMatchObject({ id: "retry", status: "error", errorDetails: "storage full" });
        expect(node.metadata?.status).toBe("success");
    });

    test("single-slot retry uses the delivered local result without uploading again", () => {
        const node = mergeImageRetryResult(root([slot("saved", "success"), slot("retry", "loading")]), "retry", { images: [output("replacement")], texts: [], errors: [] }, "no image");
        expect(node.metadata?.images?.map((image) => image.id)).toEqual(["saved", "retry"]);
        expect(node.metadata?.images?.[1]).toMatchObject({ storageKey: "image:replacement", content: "blob:replacement", status: "success" });
        expect(node.metadata?.imageRequestCount).toBe(3);
    });

    test("unexpected multiple retry outputs expand only the requested slot", () => {
        const before = slot("before", "success");
        const after = slot("after", "success");
        const node = mergeImageRetryResult(root([before, slot("retry", "loading"), after]), "retry", { images: [output("first"), output("extra")], texts: [], errors: [{ index: 8, message: "partial failure" }] }, "no image");
        expect(node.metadata?.images?.map((image) => image.id)).toEqual(["before", "retry", "extra", "retry-error-8-0", "after"]);
        expect(node.metadata?.images?.[0]).toBe(before);
        expect(node.metadata?.images?.[4]).toBe(after);
    });

    test("retry does not reinsert a slot deleted while its request was in flight", () => {
        const current = root([slot("saved", "success")]);
        expect(mergeImageRetryResult(current, "deleted", { images: [output("late")], texts: [], errors: [] }, "no image")).toBe(current);
    });

    test("retry preserves sibling output ordering and maps its own new text and images", () => {
        const current = root([slot("before", "success"), slot("retry", "loading"), slot("after", "success")]);
        current.metadata = { ...current.metadata, imageTexts: ["original"], imageOutputOrder: [{ kind: "image", index: 0 }, { kind: "text", index: 0 }, { kind: "image", index: 2 }] };
        const node = mergeImageRetryResult(current, "retry", { images: [output("first"), output("extra")], texts: ["retry text"], errors: [], outputOrder: [{ kind: "text", index: 0 }, { kind: "image", index: 0 }, { kind: "image", index: 1 }] }, "no image");
        expect(node.metadata?.imageTexts).toEqual(["original", "retry text"]);
        expect(node.metadata?.imageOutputOrder).toEqual([{ kind: "image", index: 0 }, { kind: "text", index: 0 }, { kind: "image", index: 3 }, { kind: "text", index: 1 }, { kind: "image", index: 1 }, { kind: "image", index: 2 }]);
    });
});

describe("canvas concurrent request bookkeeping", () => {
    test("finishing one task removes its source and child while another remains stoppable", () => {
        const first = new AbortController();
        const second = new AbortController();
        const requests = new Map([["source-a", request("source-a", "source-a", first)], ["result-a", request("result-a", "source-a", first)], ["source-b", request("source-b", "source-b", second)]]);
        expect([...runningGenerationNodeIds(requests.values())]).toEqual(["source-a", "source-b"]);
        const next = finishGenerationRequests(requests, first);
        expect([...next.keys()]).toEqual(["source-b"]);
        expect([...runningGenerationNodeIds(next.values())]).toEqual(["source-b"]);
    });

    test("an older task finally cannot remove a replacement request for the same node", () => {
        const old = new AbortController();
        const current = new AbortController();
        const requests = new Map([["node", request("node", "node", current)], ["old-result", request("old-result", "node", old)]]);
        const next = finishGenerationRequests(requests, old);
        expect(next.get("node")?.controller).toBe(current);
        expect([...runningGenerationNodeIds(next.values())]).toEqual(["node"]);
    });

    test("aborted requests are not shown as still running", () => {
        const controller = new AbortController();
        controller.abort();
        expect(runningGenerationNodeIds([request("node", "node", controller)]).size).toBe(0);
    });

    test("an output beyond the existing slots fills the gap with loading placeholders", () => {
        const skipped = applyImageGenerationEvent(root([slot("a", "loading")]), { type: "output", index: 2, image: output("late") });
        expect(skipped.metadata?.images?.map((image) => image.status)).toEqual(["loading", "loading", "success"]);
        const filled = applyImageGenerationEvent(skipped, { type: "output", index: 1, image: output("middle") });
        expect(filled.metadata?.images?.map((image) => image.status)).toEqual(["loading", "success", "success"]);
        expect(failImageGeneration(filled, "stream interrupted").metadata?.images?.map((image) => image.status)).toEqual(["error", "success", "success"]);
    });
});
