import { describe, expect, mock, test } from "bun:test";

Object.defineProperty(globalThis, "localStorage", { value: { getItem: () => null, setItem: () => undefined, removeItem: () => undefined }, configurable: true });

const adapterCalls = { google: 0, ark: 0, openai: 0 };
let googleMode: "two" | "partial" | "cancel" | "late-error" = "two";
let receivedConversation: any;
let historyUsesOutput = false;
const stored = new Map<string, string>();
mock.module("@/services/image-storage", () => ({
    uploadImage: async (dataUrl: string) => {
        if (dataUrl.includes("fail")) throw new Error("download failed");
        const key = `image:${stored.size + 1}`;
        stored.set(key, dataUrl);
        return { url: `blob:${key}`, storageKey: key, width: 100, height: 100, bytes: 10, mimeType: dataUrl.match(/^data:([^;]+)/)?.[1] || "image/png" };
    },
    imageToDataUrl: async (image: { dataUrl: string; storageKey?: string }) => image.storageKey ? stored.get(image.storageKey) || image.dataUrl : image.dataUrl,
}));
mock.module("@/services/api/image-native/google", () => ({ requestGoogleImageJob: async (_context: unknown, _request: unknown, options: { onEvent?: (event: unknown) => void; conversation?: unknown }) => { adapterCalls.google += 1; receivedConversation = options.conversation; if (googleMode === "cancel") throw new DOMException("aborted", "AbortError"); const images = googleMode === "late-error" ? [{ id: "completed-before-error", dataUrl: "data:image/png;base64,received" }] : googleMode === "partial" ? [{ id: "bad", dataUrl: "data:image/png;base64,fail" }, { id: "ok", dataUrl: "data:image/png;base64,ok", response: { model: "provider-result-model" } }] : [{ id: "g1", dataUrl: "data:image/png;base64,one" }, { id: "g2", dataUrl: "data:image/png;base64,two" }]; images.forEach((image, index) => options.onEvent?.({ type: "output", index, image })); if (googleMode === "late-error") throw Object.assign(new Error("stream interrupted"), { code: "STREAM_INTERRUPTED", requestId: "test-request" }); return { images, texts: googleMode === "partial" ? ["caption"] : [], errors: [], outputOrder: googleMode === "partial" ? [{ kind: "image", index: 0 }, { kind: "text", index: 0 }, { kind: "image", index: 1 }] : undefined, conversation: { provider: "google", protocol: "google-interactions", model: "gemini-3-pro-image", stored: false, createdAt: "now", history: [{ type: "image", data: historyUsesOutput ? images[0].dataUrl : "data:image/png;base64,history", mime_type: "image/png" }, { type: "thought", signature: "keep-signature" }, ...(historyUsesOutput ? [{ type: "image_generation_call", result: images[0].dataUrl.split(",")[1], output_format: "png" }] : [])] } }; } }));
mock.module("@/services/api/image-native/ark", () => ({ requestArkImageJob: async () => { adapterCalls.ark += 1; return { images: [{ id: "a1", dataUrl: "data:image/png;base64,ark" }], texts: [], errors: [] }; } }));
mock.module("@/services/api/image-native/openai", () => ({ requestOpenAIImageJob: async () => { adapterCalls.openai += 1; return { images: [{ id: "o1", dataUrl: "data:image/png;base64,openai" }], texts: [], errors: [] }; } }));

const { runImageJob } = await import("@/services/api/image-jobs");
const channel = (id: string, baseUrl: string, model: string) => ({ id, name: id, baseUrl, apiKey: "dummy-key", apiFormat: "openai" as const, models: [{ name: model, capability: "image" as const }] });
const config = (model: string, channelValue: ReturnType<typeof channel>) => ({ model: `${channelValue.id}::${model}`, imageModel: `${channelValue.id}::${model}`, apiKey: "dummy-key", baseUrl: channelValue.baseUrl, apiFormat: "openai" as const, channels: [channelValue], count: "2", quality: "auto", size: "1024x1024", background: "auto", outputFormat: "png" as const, outputCompression: 0, moderation: "auto" as const, systemPrompt: "", imageNative: {}, textModel: "", models: [], imageModel: `${channelValue.id}::${model}` } as any);

describe("native image jobs facade", () => {
    test("delivers and saves completed images before propagating a later stream failure", async () => {
        googleMode = "late-error";
        const outputs: any[] = [];
        const google = channel("google", "https://generativelanguage.googleapis.com", "gemini-3-pro-image");
        try {
            let failure: any;
            try { await runImageJob(config("gemini-3-pro-image", google), "partial", [], { onEvent: (event) => { if (event.type === "output") outputs.push(event.image); } }); }
            catch (error) { failure = error; }
            expect(failure).toMatchObject({ message: "stream interrupted", code: "STREAM_INTERRUPTED", requestId: "test-request" });
            expect(outputs).toHaveLength(1);
            expect(outputs[0].id).toBe("completed-before-error");
            expect(outputs[0].storageKey).toBeDefined();
            expect(stored.get(outputs[0].storageKey)).toBe("data:image/png;base64,received");
        } finally { googleMode = "two"; adapterCalls.google = 0; }
    });

    test("routes Google native requests once and localizes every returned image", async () => {
        const google = channel("google", "https://generativelanguage.googleapis.com", "gemini-3-pro-image");
        const result = await runImageJob(config("gemini-3-pro-image", google), "draw two");
        expect(adapterCalls.google).toBe(1);
        expect(adapterCalls.openai).toBe(0);
        expect(result.images).toHaveLength(2);
        expect(result.images.every((image) => image.storageKey)).toBe(true);
    });

    test("retains partial errors when one output cannot be downloaded", async () => {
        googleMode = "partial";
        const google = channel("google", "https://generativelanguage.googleapis.com", "gemini-3-pro-image");
        const result = await runImageJob(config("gemini-3-pro-image", google), "partial");
        expect(result.images).toHaveLength(1);
        expect(result.errors).toEqual([{ index: 0, message: "download failed" }]);
        expect(result.texts).toEqual(["caption"]);
        expect(result.outputOrder).toEqual([{ kind: "text", index: 0 }, { kind: "image", index: 0 }]);
        expect(result.images[0].response?.model).toBe("provider-result-model");
    });

    test("does not report success after cancellation", async () => {
        googleMode = "cancel";
        const events: string[] = [];
        const controller = new AbortController();
        controller.abort();
        const google = channel("google", "https://generativelanguage.googleapis.com", "gemini-3-pro-image");
        await expect(runImageJob(config("gemini-3-pro-image", google), "cancel", [], { signal: controller.signal, onEvent: (event) => events.push(event.type) })).rejects.toThrow();
        expect(events).toContain("client-aborted");
        expect(events).not.toContain("completed");
    });

    test("archives image history to storage and rehydrates it before the next request", async () => {
        googleMode = "two";
        const google = channel("google", "https://generativelanguage.googleapis.com", "gemini-3-pro-image");
        const first = await runImageJob(config("gemini-3-pro-image", google), "archive");
        const history = first.conversation?.history as any[];
        expect(typeof history[0].data.__imageStorageKey).toBe("string");
        expect(history[0].data.encoding).toBe("data-url");
        expect(history[1].signature).toBe("keep-signature");
        await runImageJob(config("gemini-3-pro-image", google), "restore", [], { conversation: { ...first.conversation!, history } });
        expect(receivedConversation.history[0].data).toBe("data:image/png;base64,history");
        expect(receivedConversation.history[1].signature).toBe("keep-signature");
        expect(JSON.stringify(first.conversation)).not.toContain("dummy-key");
    });

    test("rejects a conversation from another channel before routing", async () => {
        const google = channel("google", "https://generativelanguage.googleapis.com", "gemini-3-pro-image");
        const other = channel("other", "https://other.example", "gemini-3-pro-image");
        await expect(runImageJob(config("gemini-3-pro-image", google), "wrong channel", [], { conversation: { provider: "google", protocol: "google-interactions", model: "gemini-3-pro-image", stored: false, createdAt: "now", channelId: other.id, baseUrl: other.baseUrl } })).rejects.toThrow();
    });

    test("reuses stored output bytes for Google and Responses history encodings", async () => {
        googleMode = "two";
        historyUsesOutput = true;
        const before = stored.size;
        try {
            const google = channel("google", "https://generativelanguage.googleapis.com", "gemini-3-pro-image");
            const result = await runImageJob(config("gemini-3-pro-image", google), "shared output");
            const history = result.conversation!.history as any[];
            expect(stored.size - before).toBe(2);
            expect(history[0].data.__imageStorageKey).toBe(result.images[0].storageKey);
            expect(history[2].result.__imageStorageKey).toBe(result.images[0].storageKey);
            expect(history[0].data.encoding).toBe("data-url");
            expect(history[2].result.encoding).toBe("base64");
        } finally { historyUsesOutput = false; }
    });
});
