import { expect, test } from "bun:test";
import { arkImageRatios, arkImageSizePresets, findArkImageSizePreset, getNativeImageCapabilities, resolveNativeImageOptions } from "@/lib/image-model-capabilities";

const pro = "doubao-seedream-5-0-pro-260628";
const lite = "doubao-seedream-5-0-260128";

test("every Pro/Lite ratio preset satisfies the official pixel and aspect limits", () => {
    for (const model of [pro, lite]) {
        const [min, max] = model === pro ? [921600, 4624220] : [3686400, 16777216];
        for (const resolution of getNativeImageCapabilities("ark", model).sizes) {
            for (const ratio of arkImageRatios) {
                const size = arkImageSizePresets[resolution][ratio];
                const [width, height] = size.split("x").map(Number);
                const [ratioWidth, ratioHeight] = ratio.split(":").map(Number);
                expect(width * height).toBeGreaterThanOrEqual(min);
                expect(width * height).toBeLessThanOrEqual(max);
                expect(width / height).toBeGreaterThanOrEqual(1 / 16);
                expect(width / height).toBeLessThanOrEqual(16);
                expect(width * ratioHeight).toBe(height * ratioWidth);
                expect(findArkImageSizePreset(size)).toEqual({ resolution, ratio });
            }
        }
    }
});

test("supports portrait 9:21 for Ark without adding it to Nano Banana Pro", () => {
    expect(arkImageSizePresets["2K"]["9:21"]).toBe("1320x3080");
    expect(arkImageSizePresets["4K"]["9:21"]).toBe("2664x6216");
    expect(getNativeImageCapabilities("google", "gemini-3-pro-image").ratios).not.toContain("9:21");
});

test("model switches retain the ratio and use a supported resolution", () => {
    const source = { ark: { size: arkImageSizePresets["1K"]["9:21"], watermark: false, endpoint: { id: "ep-test", model: pro } } };
    const switched = resolveNativeImageOptions("ark", lite, source);
    expect(switched.ark?.size).toBe(arkImageSizePresets["2K"]["9:21"]);
    expect(switched.ark?.watermark).toBe(false);
    expect(source.ark.size).toBe(arkImageSizePresets["1K"]["9:21"]);
    expect(resolveNativeImageOptions("ark", pro, { ark: { size: "2016x1900" } }).ark?.size).toBe("2016x1900");
    expect(resolveNativeImageOptions("ark", pro, { ark: { size: "2K" } }).ark?.size).toBe("2K");
});
