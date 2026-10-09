import { describe, expect, mock, test } from "bun:test";

Object.defineProperty(globalThis, "localStorage", { value: { getItem: () => null, setItem: () => undefined, removeItem: () => undefined }, configurable: true });
mock.module("@/services/image-storage", () => ({
    uploadImage: async (dataUrl: string) => ({ url: dataUrl, storageKey: "image:result", width: 1024, height: 1024, bytes: 10, mimeType: "image/png" }),
    imageToDataUrl: async (image: { dataUrl: string }) => image.dataUrl,
    resolveImageUrl: async (_key: string, fallback: string) => fallback,
}));
const { defaultConfig, normalizeChannelModels, snapshotImageNativeOptions } = await import("@/stores/use-config-store");
const { buildGenerationConfig } = await import("@/lib/canvas/canvas-generation-helpers");
const { buildImageGenerationMetadata } = await import("@/lib/canvas/canvas-node-factory");
const { runImageJob } = await import("@/services/api/image-jobs");
const pro = "doubao-seedream-5-0-pro-260628";
const lite = "doubao-seedream-5-0-260128";
const config = () => ({ ...defaultConfig, model: `ark::${pro}`, imageModel: `ark::${pro}`, channels: [{ id: "ark", name: "Ark", provider: "ark" as const, apiFormat: "openai" as const, baseUrl: "https://ark.cn-beijing.volces.com/api/v3", apiKey: "test-key", models: [{ name: pro, capability: "image" as const, endpointId: "ep-pro" }, { name: lite, capability: "image" as const, endpointId: "ep-lite" }] }] });

describe("Ark Endpoint bindings", () => {
    test("preserves channel bindings during normalization", () => {
        expect(normalizeChannelModels([{ name: pro, capability: "image", endpointId: " ep-pro " }])[0].endpointId).toBe("ep-pro");
    });

    test("freezes endpoint and direct Model ID choices across later channel changes", () => {
        const initial = config();
        const saved = buildImageGenerationMetadata("generation", buildGenerationConfig(initial, undefined, "image"), 1, []);
        initial.channels[0].models[0].endpointId = "ep-replacement";
        expect(snapshotImageNativeOptions(initial, initial.model, saved.imageNative).ark?.endpoint?.id).toBe("ep-pro");
        initial.channels[0].models[0].endpointId = "";
        const direct = snapshotImageNativeOptions(initial, initial.model);
        initial.channels[0].models[0].endpointId = "ep-replacement";
        expect(snapshotImageNativeOptions(initial, initial.model, direct).ark?.endpoint?.id).toBe("");
    });

    test("switching model or channel does not reuse the old Endpoint", () => {
        const initial = config();
        const saved = snapshotImageNativeOptions(initial, initial.model);
        expect(snapshotImageNativeOptions(initial, `ark::${lite}`, saved).ark?.endpoint?.id).toBe("ep-lite");
        const other = { ...initial, channels: [{ ...initial.channels[0], id: "other", models: [{ name: pro, capability: "image" as const, endpointId: "ep-other" }] }] };
        expect(snapshotImageNativeOptions(other, `other::${pro}`, saved).ark?.endpoint?.id).toBe("ep-other");
    });

    test("sends the Endpoint as model while retaining Pro/Lite capabilities and watermark false", async () => {
        const previousFetch = globalThis.fetch;
        const bodies: Record<string, any>[] = [];
        globalThis.fetch = (async (_url, init) => {
            bodies.push(JSON.parse(String(init?.body)));
            return new Response(JSON.stringify({ model: pro, data: [{ b64_json: "fixture", size: "1024x1024" }] }));
        }) as typeof fetch;
        try {
            await runImageJob(config(), "Pro");
            await runImageJob({ ...config(), model: `ark::${lite}`, count: "2", imageNative: { ark: { size: "2K", sequential: true } } }, "Lite");
            expect(bodies.map((body) => body.model)).toEqual(["ep-pro", "ep-lite"]);
            expect(bodies.every((body) => body.watermark === false)).toBe(true);
            expect(bodies[0].sequential_image_generation).toBeUndefined();
            expect(bodies[1].sequential_image_generation_options).toEqual({ max_images: 2 });
            await expect(runImageJob({ ...config(), count: "2" }, "invalid Pro group")).rejects.toThrow();
            expect(bodies).toHaveLength(2);
        } finally { globalThis.fetch = previousFetch; }
    });
});
