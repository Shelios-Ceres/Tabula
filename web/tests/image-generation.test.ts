import { describe, expect, mock, test } from "bun:test";

Object.defineProperty(globalThis, "localStorage", {
    value: { getItem: () => null, setItem: () => undefined, removeItem: () => undefined },
    configurable: true,
});

mock.module("@/services/image-storage", () => ({
    imageToDataUrl: async (image: { dataUrl: string }) => image.dataUrl,
    resolveImageUrl: async (key: string, fallback: string) => (key === "image:mask" ? "data:image/png;base64,mask" : fallback),
    uploadImage: async () => ({ url: "data:image/png;base64,image", width: 1024, height: 1024, bytes: 4, mimeType: "image/png" }),
}));
mock.module("@/services/file-storage", () => ({ resolveMediaUrl: async (_key: string, fallback: string) => fallback }));

const { buildGenerationConfig, isGenerationCanceled, resolveMetadataMask } = await import("@/lib/canvas/canvas-generation-helpers");
const { buildImageGenerationMetadata } = await import("@/lib/canvas/canvas-node-factory");
const { defaultConfig, resolveSameChannelMainModel } = await import("@/stores/use-config-store");

const ref = (id: string, storageKey?: string) => ({ id, name: `${id}.png`, type: "image/png", dataUrl: `data:image/png;base64,${id}`, ...(storageKey ? { storageKey } : {}) });

describe("canvas image generation parameter preservation", () => {
    test("cancellation is recognized by code independently of translated error text", () => {
        expect(isGenerationCanceled(Object.assign(new Error("canceled by user"), { code: "ERR_CANCELED" }))).toBe(true);
        expect(isGenerationCanceled(new DOMException("Aborted", "AbortError"))).toBe(true);
        expect(isGenerationCanceled(Object.assign(new Error("network failed"), { code: "ERR_NETWORK" }))).toBe(false);
    });

    test("persists output options and independent mask while keeping mask out of references", () => {
        const config = { ...defaultConfig, model: "gpt-image-2.5-sunburst", size: "1536x1024", quality: "max", background: "transparent", outputFormat: "webp" as const, outputCompression: 0, moderation: "low" as const };
        const metadata = buildImageGenerationMetadata("edit", config, 1, [ref("source", "image:source")], ref("mask", "image:mask"));
        expect(metadata).toMatchObject({ size: "1536x1024", quality: "max", background: "transparent", outputFormat: "webp", outputCompression: 0, moderation: "low", maskReference: "image:mask", references: ["image:source"] });
        expect(metadata.references).not.toContain("image:mask");
    });

    test("restores a stored mask as a separate PNG reference", async () => {
        const mask = await resolveMetadataMask({ generationType: "edit", maskReference: "image:mask" });
        expect(mask).toMatchObject({ id: "mask", name: "mask.png", type: "image/png", dataUrl: "data:image/png;base64,mask", storageKey: "image:mask" });
    });

    test("uses videoSize only for video and preserves image size plus compression zero", () => {
        const config = { ...defaultConfig, size: "1536x1024", videoSize: "720x1280", outputCompression: 0 };
        expect(buildGenerationConfig(config, undefined, "image").size).toBe("1536x1024");
        expect(buildGenerationConfig(config, undefined, "video").size).toBe("720x1280");
        expect(buildGenerationConfig(config, undefined, "image").outputCompression).toBe(0);
    });

    test("canvas Responses resolves the displayed main model after changing channels", () => {
        const channels = ["a", "b"].map((id) => ({ id, name: id, provider: "openai" as const, baseUrl: "https://api.openai.com", apiKey: "test-only", apiFormat: "openai" as const, models: [{ name: "gpt-image-2.5-sunburst", capability: "image" as const }, { name: `text-${id}`, capability: "text" as const }] }));
        const config = { ...defaultConfig, channels, model: "b::gpt-image-2.5-sunburst", imageModel: "b::gpt-image-2.5-sunburst", textModel: "a::text-a", imageNative: { openai: { api: "responses" as const, mainModel: "a::text-a" } } };
        const resolved = buildGenerationConfig(config, undefined, "image");
        expect(resolved.imageNative.openai?.mainModel).toBe("text-b");
        expect(resolved.imageNative.openai?.mainModel).toBe(resolveSameChannelMainModel(config, config.model, config.imageNative.openai.mainModel));
        expect(config.imageNative.openai.mainModel).toBe("a::text-a");
        expect(resolveSameChannelMainModel({ ...config, textModel: "b::removed-model" }, config.model)).toBe("text-b");
    });

    test("editing model changes normalize native size while keeping supported edited values", () => {
        const models = [{ name: "gemini-3.1-flash-image", capability: "image" as const }, { name: "gemini-3-pro-image", capability: "image" as const }];
        const config = { ...defaultConfig, channels: [{ id: "google", name: "Google", provider: "google" as const, baseUrl: "https://generativelanguage.googleapis.com", apiKey: "test-only", apiFormat: "gemini" as const, models }], model: "google::gemini-3-pro-image", imageModel: "google::gemini-3-pro-image", imageNative: { google: { imageSize: "512" as const, aspectRatio: "1:8" } } };
        const changed = buildGenerationConfig(config, undefined, "image");
        expect(changed.imageNative.google?.imageSize).toBe("1K");
        expect(changed.imageNative.google?.aspectRatio).toBe("1:1");
        const edited = buildGenerationConfig({ ...config, imageNative: { google: { imageSize: "4K", aspectRatio: "3:4" } } }, undefined, "image");
        expect(edited.imageNative.google).toMatchObject({ imageSize: "4K", aspectRatio: "3:4" });
    });
});
