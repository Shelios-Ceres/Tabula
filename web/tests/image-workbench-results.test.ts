import { describe, expect, test } from "bun:test";

import { buildWorkbenchRetry, failWorkbenchResults, finishWorkbenchResults, mergeWorkbenchOutputOrder, mergeWorkbenchRetry, upsertWorkbenchResult, workbenchRequestError, type GeneratedImage, type GenerationResult, type ImageJobSnapshot } from "@/pages/image/image-workbench-results";

const snapshot = (native = {}): ImageJobSnapshot => ({ text: "test prompt", config: { count: "3", imageNative: native } as ImageJobSnapshot["config"], references: [] });
const image = (id: string): GeneratedImage => ({ id, dataUrl: `blob:${id}`, storageKey: `image:${id}`, width: 16, height: 16, bytes: 10, durationMs: 1 });
const pending = (retry: ImageJobSnapshot, count = 3): GenerationResult[] => Array.from({ length: count }, (_, outputIndex) => ({ id: `pending-${outputIndex}`, status: "pending", outputIndex, retry }));
const success = (id: string, retry: ImageJobSnapshot, outputIndex: number): GenerationResult => ({ id, status: "success", image: image(id), outputIndex, retry });
const failed = (id: string, retry: ImageJobSnapshot, outputIndex: number): GenerationResult => ({ id, status: "failed", error: "failed", outputIndex, retry });

describe("image workbench results", () => {
    test("a late mocked transport failure keeps emitted images and their stored file references", async () => {
        const retry = snapshot();
        let rows = pending(retry);
        const request = async () => {
            rows = upsertWorkbenchResult(rows, 0, success("saved", retry, 0));
            throw Object.assign(new Error("stream ended"), { code: "ECONNRESET", status: 502, requestId: "request-1" });
        };
        try { await request(); }
        catch (error) { rows = failWorkbenchResults(rows, workbenchRequestError(error, "fallback"), retry); }
        expect(rows.map((row) => row.status)).toEqual(["success", "failed", "failed"]);
        expect(rows[0].image?.storageKey).toBe("image:saved");
        expect(rows[1].outputError).toEqual({ index: 1, message: "stream ended", code: "ECONNRESET", status: 502, requestId: "request-1" });
    });

    test("an exception after every output does not invent another failed image", () => {
        const retry = snapshot();
        const rows = [success("saved", retry, 0)];
        expect(failWorkbenchResults(rows, { index: -1, message: "missing terminal event" }, retry)).toEqual(rows);
    });

    test("terminal metadata updates images by id without discarding an earlier completed output", () => {
        const retry = snapshot();
        const rows = [success("saved", retry, 0), ...pending(retry).slice(1)];
        const finalImage = { ...image("saved"), response: { requestId: "final-request" } };
        const result = finishWorkbenchResults(rows, [finalImage], [{ index: 1, message: "second failed" }], retry, "job");
        expect(result.map((row) => row.status)).toEqual(["success", "failed"]);
        expect(result[0].image?.response?.requestId).toBe("final-request");
        const errored = finishWorkbenchResults(result, [], [{ index: 0, message: "late request failure" }], retry, "job");
        expect(errored[0].image?.id).toBe("saved");
    });

    test("independent and Ark sequential retries request one image and replace only the failed slot", () => {
        for (const provider of ["openai", "ark"] as const) {
            const original = snapshot({ ark: { sequential: true } });
            const rows = [success("first", original, 0), failed("error", original, 1), success("third", original, 2)];
            const request = buildWorkbenchRetry(rows, 1, provider)!;
            expect(request.snapshot.config.count).toBe("1");
            expect(request.retry.wholeJob).toBe(false);
            expect(original.config.count).toBe("3");
            expect(request.snapshot.references).toBe(original.references);
            const merged = mergeWorkbenchRetry(rows, [success("replacement", request.snapshot, 0)], request.retry);
            expect(merged.map((row) => row.image?.id)).toEqual(["first", "replacement", "third"]);
            expect(merged[0]).toBe(rows[0]);
            expect(merged[2]).toBe(rows[2]);
        }
    });

    test("whole layer regeneration preserves old successes and unrelated failed slots", () => {
        const original = snapshot({ ark: { layerDecomposition: true } });
        const other = snapshot();
        const rows = [success("old-base", original, 0), failed("layer-error", original, 1), failed("other-error", other, 0)];
        const request = buildWorkbenchRetry(rows, 1, "ark")!;
        expect(request.retry.wholeJob).toBe(true);
        expect(request.snapshot).toBe(original);
        const merged = mergeWorkbenchRetry(rows, [success("new-base", original, 0), success("new-layer", original, 1)], request.retry);
        expect(merged.map((row) => row.id)).toEqual(["old-base", "other-error", "new-base", "new-layer"]);
        expect(buildWorkbenchRetry(rows, 1, "openai")!.retry.wholeJob).toBe(false);
        expect(buildWorkbenchRetry([rows[0]], 0, "ark")?.retry.wholeJob).toBe(true);
        expect(buildWorkbenchRetry([rows[0]], 0, "openai")).toBeUndefined();
    });

    test("mixed output indices remain attached to their images after a retry fills a gap", () => {
        const first = image("first");
        const third = image("third");
        const replacement = image("replacement");
        const order = mergeWorkbenchOutputOrder([{ kind: "image", index: 0 }, { kind: "text", index: 0 }, { kind: "image", index: 1 }], [first, third], 1, { images: [replacement], texts: ["retry text"], errors: [], outputOrder: [{ kind: "text", index: 0 }, { kind: "image", index: 0 }] }, [first, replacement, third]);
        expect(order).toEqual([{ kind: "image", index: 0 }, { kind: "text", index: 0 }, { kind: "image", index: 2 }, { kind: "text", index: 1 }, { kind: "image", index: 1 }]);
    });

    test("only ordinary diagnostic fields are retained from a thrown transport error", () => {
        expect(workbenchRequestError({ message: "failed", status: 503, code: "UPSTREAM", requestId: "request-2", config: { headers: { Authorization: "fixture" } }, request: {} }, "fallback")).toEqual({ index: -1, message: "failed", status: 503, code: "UPSTREAM", requestId: "request-2" });
    });
});
