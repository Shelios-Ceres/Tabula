import { describe, expect, mock, test } from "bun:test";
import { arkImageSizePresets, getNativeImageCapabilities } from "@/lib/image-model-capabilities";

mock.module("@/services/image-storage", () => ({ imageToDataUrl: async (image: { dataUrl: string }) => image.dataUrl }));
mock.module("@/lib/image-utils", () => ({ readImageMeta: async () => ({ width: 1024, height: 1024, mimeType: "image/png" }) }));
mock.module("@/stores/use-config-store", () => ({ withLocalProxy: (url: string) => `proxy:${url}` }));
mock.module("@/services/api/image-native/common", () => ({
    checkResponse: async (response: Response) => { if (!response.ok) throw new Error("request failed"); },
    readSse: async (response: Response, callback: (event: { event: string; data: string }) => void) => {
        const text = await response.text();
        for (const block of text.split("\n\n")) {
            const event = block.match(/^event: (.+)$/m)?.[1];
            const data = block.match(/^data: (.+)$/m)?.[1];
            if (data) callback({ event: event || "message", data });
        }
    },
}));

const { requestArkImageJob } = await import("@/services/api/image-native/ark");

const context = { baseUrl: "https://ark.example/api/v3", apiKey: "secret", channelId: "ark" };
const request = (overrides: Record<string, unknown> = {}) => ({
    model: "doubao-seedream-5-0-lite-260128", prompt: "一只猫", references: [], count: 2,
    quality: "auto", size: "2K", background: "auto", outputFormat: "jpeg" as const,
    outputCompression: 80, moderation: "auto" as const, native: { ark: { sequential: true, watermark: true, optimizePrompt: "standard" as const }, ...overrides },
});

describe("Ark native image adapter", () => {
    test("submits ratio presets through size without a fabricated aspect_ratio field", async () => {
        const originalFetch = globalThis.fetch;
        const bodies: Record<string, any>[] = [];
        globalThis.fetch = async (_url, init) => { bodies.push(JSON.parse(String(init?.body))); return new Response(JSON.stringify({ data: [{ b64_json: "fixture" }] })); };
        try {
            for (const model of ["doubao-seedream-5-0-pro-260628", "doubao-seedream-5-0-260128"]) {
                for (const resolution of getNativeImageCapabilities("ark", model).sizes) {
                    const size = arkImageSizePresets[resolution]["9:21"];
                    await requestArkImageJob(context, { ...request(), model, count: 1, native: { ark: { size, watermark: false } } });
                    const body = bodies.at(-1)!;
                    expect(body.size).toBe(size);
                    expect(body.aspect_ratio).toBeUndefined();
                    expect(body.watermark).toBe(false);
                }
            }
        } finally { globalThis.fetch = originalFetch; }
    });
    test("uses an existing /api/v3 base and maps Lite sequential generation", async () => {
        let calledUrl = ""; let calledBody: Record<string, unknown> = {};
        const originalFetch = globalThis.fetch;
        globalThis.fetch = async (input, init) => { calledUrl = String(input); calledBody = JSON.parse(String(init?.body)); return new Response(JSON.stringify({ model: "doubao-seedream-5-0-lite-260128", output_format: "jpeg", data: [{ b64_json: "fixture", size: "2048x2048" }] }), { status: 200 }); };
        try {
            const result = await requestArkImageJob(context, request(), {});
            expect(calledUrl).toBe("proxy:https://ark.example/api/v3/images/generations");
            expect(calledBody.sequential_image_generation).toBe("auto");
            expect(calledBody.sequential_image_generation_options).toEqual({ max_images: 2 });
            expect(calledBody.response_format).toBe("b64_json");
            expect(calledBody.watermark).toBe(true);
            expect(calledBody.background).toBeUndefined();
            expect(result.images[0].dataUrl).toBe("data:image/jpeg;base64,fixture");
            expect(result.images[0].response).toMatchObject({ model: "doubao-seedream-5-0-lite-260128", size: "2048x2048", outputFormat: "jpeg" });
            expect(result.images[0].response?.background).toBeUndefined();
        } finally { globalThis.fetch = originalFetch; }
    });

    test("keeps URL response compatibility while requesting base64", async () => {
        const originalFetch = globalThis.fetch;
        let body: Record<string, unknown> = {};
        globalThis.fetch = async (_input, init) => { body = JSON.parse(String(init?.body)); return new Response(JSON.stringify({ data: [{ url: "https://cdn/image.jpg" }] }), { status: 200 }); };
        try {
            const result = await requestArkImageJob(context, { ...request(), count: 1, native: { ark: {} } }, {});
            expect(body.response_format).toBe("b64_json");
            expect(result.images[0].dataUrl).toBe("https://cdn/image.jpg");
        } finally { globalThis.fetch = originalFetch; }
    });

    test("always sends the application watermark choice", async () => {
        const originalFetch = globalThis.fetch; const values: unknown[] = [];
        globalThis.fetch = async (_input, init) => { values.push(JSON.parse(String(init?.body)).watermark); return new Response(JSON.stringify({ data: [{ url: "https://cdn/image.jpg" }] }), { status: 200 }); };
        try {
            await requestArkImageJob(context, { ...request(), count: 1, native: { ark: {} } }, {});
            await requestArkImageJob(context, { ...request(), count: 1, native: { ark: { watermark: false } } }, {});
            await requestArkImageJob(context, { ...request(), count: 1, native: { ark: { watermark: true } } }, {});
            expect(values).toEqual([false, false, true]);
        } finally { globalThis.fetch = originalFetch; }
    });

    test("converts selection fractions to Ark prompt tags", async () => {
        let body = ""; const originalFetch = globalThis.fetch;
        globalThis.fetch = async (_input, init) => { body = String(init?.body); return new Response(JSON.stringify({ data: [{ url: "https://cdn/image.jpg" }] }), { status: 200 }); };
        try {
            await requestArkImageJob(context, { ...request(), model: "doubao-seedream-5-0-pro-260628", count: 1, native: { ark: {} }, references: [{ id: "a", name: "a", type: "image/png", dataUrl: "data:image/png;base64,a" }, { id: "b", name: "b", type: "image/png", dataUrl: "data:image/png;base64,b" }] }, { selections: [{ kind: "point", x: .5, y: 1, imageIndex: 1 }, { kind: "rectangle", x1: .7, y1: .25, x2: .9995, y2: 1 }] });
            expect(JSON.parse(body).prompt).toContain("图 2 <point>500 999</point>");
            expect(JSON.parse(body).prompt).toContain("<bbox>700 250 999 999</bbox>");
        } finally { globalThis.fetch = originalFetch; }
    });

    test("applies exact pixel and aspect-ratio limits by model", async () => {
        const originalFetch = globalThis.fetch;
        globalThis.fetch = async () => new Response(JSON.stringify({ data: [{ url: "https://cdn/image.jpg" }] }), { status: 200 });
        try {
            await expect(requestArkImageJob(context, { ...request(), model: "doubao-seedream-5-0-pro-260628", count: 1, size: "2048x1024", native: { ark: {} } }, {})).resolves.toBeTruthy();
            await expect(requestArkImageJob(context, { ...request(), model: "doubao-seedream-5-0-pro-260628", count: 1, size: "512x512", native: { ark: {} } }, {})).rejects.toThrow();
            await expect(requestArkImageJob(context, { ...request(), count: 1, size: "16384x1024", native: { ark: {} } }, {})).resolves.toBeTruthy();
        } finally { globalThis.fetch = originalFetch; }
    });

    test("accepts JSON-typed SSE and retains partial failure codes", async () => {
        const originalFetch = globalThis.fetch;
        const frames = [
            { type: "image_generation.partial_succeeded", image_index: 1, url: "https://cdn/second.png", size: "2048x2048" },
            { type: "image_generation.partial_failed", image_index: 0, error: { code: "ImageGenerationError", message: "first failed" } },
            { type: "image_generation.completed", usage: { generated_images: 1 } },
        ];
        globalThis.fetch = async () => new Response(frames.map((frame) => `data: ${JSON.stringify(frame)}\n\n`).join(""));
        try {
            const result = await requestArkImageJob(context, request({ stream: true }));
            expect(result.images).toHaveLength(1);
            expect(result.images[0].dataUrl).toBe("https://cdn/second.png");
            expect(result.images[0].response?.size).toBe("2048x2048");
            expect(result.errors).toEqual([{ index: 0, message: "first failed", code: "ImageGenerationError" }]);
        } finally { globalThis.fetch = originalFetch; }
    });
});
