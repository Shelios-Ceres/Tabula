import { describe, expect, test } from "bun:test";

import { getImageModelCapabilities, getNativeImageCapabilities, resolveNativeImageCount, resolveNativeImageOptions } from "@/lib/image-model-capabilities";

describe("GPT Image model capabilities", () => {
    test("separates Pro image automatic thinking from configurable Flash image levels", () => {
        const pro = getNativeImageCapabilities("google", "gemini-3-pro-image");
        expect(pro.supportsThinking).toBe(true);
        expect(pro.thinkingLevels).toEqual(["auto"]);
        expect(pro.ratios).toHaveLength(10);
        expect(pro.ratios).toContain("16:9");
        expect(pro.sizes).toEqual(["1K", "2K", "4K"]);
        for (const model of ["gemini-3.1-flash-image", "gemini-3.1-flash-lite-image"]) {
            expect(getNativeImageCapabilities("google", model).thinkingLevels).toEqual(["auto", "minimal", "high"]);
        }
        const switched = resolveNativeImageOptions("google", "gemini-3-pro-image", { google: { imageSize: "512", aspectRatio: "1:8", thinkingLevel: "high" } });
        expect(switched.google).toEqual({ imageSize: "1K", aspectRatio: "1:1", thinkingLevel: "auto", imageSearch: false });
    });
    test("normalizes plugin counts by native request mode and reference count", () => {
        expect(resolveNativeImageCount("google", "gemini-3-pro-image", {}, 2)).toBe(1);
        expect(resolveNativeImageCount("openai", "gpt-image-2.5-flare", { openai: { api: "responses" } }, 3)).toBe(1);
        expect(resolveNativeImageCount("openai", "gpt-image-2.5-flare", {}, 99)).toBe(10);
        expect(resolveNativeImageCount("ark", "doubao-seedream-5-0-260128", { ark: { sequential: true } }, 6, 14)).toBe(1);
        expect(resolveNativeImageCount("ark", "doubao-seedream-5-0-pro-260628", {}, -3)).toBe(1);
    });
    test("recognizes both exact GPT Image 2.5 models and date snapshots", () => {
        for (const model of [
            "gpt-image-2.5-flare",
            "gpt-image-2.5-sunburst",
            "gpt-image-2.5-flare-2026-09-08",
            "gpt-image-2.5-sunburst-2026-09-08",
        ]) {
            const capabilities = getImageModelCapabilities(model);
            expect(capabilities.isGptImage).toBe(true);
            expect(capabilities.isImage25).toBe(true);
            expect(capabilities.qualities).toEqual(["auto", "low", "medium", "high", "xhigh", "max"]);
        }
    });

    test("keeps legacy GPT Image 2 on the original quality set", () => {
        const capabilities = getImageModelCapabilities("gpt-image-2");
        expect(capabilities.isGptImage).toBe(true);
        expect(capabilities.isImage25).toBe(false);
        expect(capabilities.qualities).toEqual(["auto", "high", "medium", "low"]);
    });

    test("does not classify similarly named or unrelated models as 2.5", () => {
        expect(getImageModelCapabilities("gpt-image-2.5").isImage25).toBe(false);
        expect(getImageModelCapabilities("gpt-image-2.5-flare-2026-09-09").isImage25).toBe(false);
        expect(getImageModelCapabilities("gemini-3-pro-image").isGptImage).toBe(false);
    });
});
