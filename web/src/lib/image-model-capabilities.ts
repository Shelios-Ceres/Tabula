import type { ImageProvider, NativeImageOptions } from "@/types/image-generation";
import type { ImageOutputFormat } from "@/types/image";
import { arkImageRatios, findImageFamily, normalizeModelId, standardImageRatios } from "@/lib/model-families";

export const imageOutputFormats = ["png", "jpeg", "webp"] as const;
export const imageBackgrounds = ["auto", "opaque", "transparent"] as const;

const baseQualities = ["auto", "high", "medium", "low"] as const;
const image25Qualities = ["auto", "low", "medium", "high", "xhigh", "max"] as const;

export function getImageModelCapabilities(model: string) {
    const name = normalizeModelId(model);
    const image25 = /^gpt-image-2\.5-(sunburst|flare)$/.exec(name);
    const isGptImage = name.startsWith("gpt-image-");
    return {
        isGptImage,
        isImage25: Boolean(image25),
        qualities: image25 ? image25Qualities : baseQualities,
        maxImages: isGptImage ? 10 : 15,
        labelKey: image25 ? `settingsPanels.image.modelLabels.${image25[1] === "flare" ? "fast" : "precise"}` : undefined,
    };
}

export { arkImageRatios };

// Application presets: preserve the exact ratio and stay below the tier's square pixel area.
// The 8px grid is only for these presets, not an additional API input restriction.
export const arkImageSizePresets: Record<string, Record<string, string>> = Object.fromEntries(
    Object.entries({ "1K": 1024, "1.5K": 1536, "2K": 2048, "3K": 3072, "4K": 4096 }).map(([resolution, pixels]) => [
        resolution,
        Object.fromEntries(arkImageRatios.map((ratio) => {
            const [width, height] = ratio === "21:9" ? [7, 3] : ratio === "9:21" ? [3, 7] : ratio.split(":").map(Number);
            const unit = Math.floor(Math.sqrt(pixels * pixels / (width * height)) / 8) * 8;
            return [ratio, `${width * unit}x${height * unit}`];
        })),
    ]),
);

export function findArkImageSizePreset(size: string) {
    for (const [resolution, presets] of Object.entries(arkImageSizePresets)) {
        const ratio = arkImageRatios.find((value) => presets[value] === size.toLowerCase());
        if (ratio) return { resolution, ratio };
    }
    return undefined;
}

export function getNativeImageCapabilities(provider: ImageProvider, model: string) {
    const gpt = getImageModelCapabilities(model);
    const family = provider === "openai" ? undefined : findImageFamily(provider, model);
    return {
        provider,
        family: family?.id,
        supported: provider === "openai" ? gpt.isGptImage : Boolean(family),
        qualities: provider === "openai" ? gpt.qualities : [],
        sizes: family?.sizes || [],
        ratios: family?.ratios || standardImageRatios,
        pixels: family?.pixels,
        outputFormats: family?.outputFormats || [],
        maxReferences: family?.maxReferences || 16,
        maxImages: family?.maxImages || gpt.maxImages,
        supportsMask: provider === "openai",
        supportsCoordinates: Boolean(family?.layers),
        supportsLayers: Boolean(family?.layers),
        supportsSequential: Boolean(family?.sequential),
        supportsFastPrompt: Boolean(family?.fastPrompt),
        supportsStream: provider === "openai" || Boolean(family?.stream),
        supportsWebSearch: Boolean(family?.webSearch),
        supportsImageSearch: Boolean(family?.imageSearch),
        supportsThinking: Boolean(family?.thinkingLevels),
        thinkingLevels: family?.thinkingLevels || ["auto"],
        supportsConversation: provider === "google" || provider === "openai",
    };
}

export function resolveNativeImageOptions(provider: ImageProvider, model: string, value: NativeImageOptions): NativeImageOptions {
    const capabilities = getNativeImageCapabilities(provider, model);
    const native = structuredClone(value || {});
    if (!capabilities.supportsStream) native.stream = false;
    if (provider === "google") {
        const google = { ...native.google };
        if (!capabilities.sizes.includes(google.imageSize || "")) google.imageSize = capabilities.sizes[0] as NonNullable<NativeImageOptions["google"]>["imageSize"];
        if (!capabilities.ratios.includes(google.aspectRatio || "")) google.aspectRatio = capabilities.ratios[0];
        if (!capabilities.thinkingLevels.includes(google.thinkingLevel || "")) google.thinkingLevel = "auto";
        if (!capabilities.supportsThinking) google.thinkingLevel = "auto";
        if (!capabilities.supportsWebSearch) google.webSearch = false;
        if (!capabilities.supportsImageSearch) google.imageSearch = false;
        native.google = google;
    }
    if (provider === "ark") {
        const ark = { ...native.ark };
        if (typeof ark.watermark !== "boolean") ark.watermark = false;
        const preset = findArkImageSizePreset(ark.size || "");
        if (preset && !capabilities.sizes.includes(preset.resolution)) ark.size = arkImageSizePresets[capabilities.sizes[0]][preset.ratio];
        if (!capabilities.sizes.includes(ark.size || "") && !/^\d+x\d+$/i.test(ark.size || "")) ark.size = capabilities.sizes[0];
        if (!capabilities.supportsSequential) ark.sequential = false;
        if (!capabilities.supportsFastPrompt) ark.optimizePrompt = "standard";
        if (!capabilities.supportsWebSearch) ark.webSearch = false;
        if (!capabilities.supportsLayers) {
            ark.layerDecomposition = false;
            ark.transparent = false;
        }
        native.ark = ark;
    }
    if (provider === "xai") {
        const xai = { ...native.xai };
        if (!capabilities.sizes.includes(xai.resolution || "")) xai.resolution = capabilities.sizes[0] as NonNullable<NativeImageOptions["xai"]>["resolution"];
        if (xai.aspectRatio !== "auto" && !capabilities.ratios.includes(xai.aspectRatio || "")) xai.aspectRatio = "auto";
        native.xai = xai;
    }
    return native;
}

export function resolveNativeImageCount(provider: ImageProvider, model: string, native: NativeImageOptions, requested: number, referenceCount = 0) {
    const capabilities = getNativeImageCapabilities(provider, model);
    if (provider === "google" || (provider === "openai" && native.openai?.api === "responses") || (provider === "ark" && native.ark?.sequential !== true) || capabilities.maxImages === 1) return 1;
    const max = provider === "ark" ? Math.max(1, capabilities.maxImages - referenceCount) : capabilities.maxImages;
    return Math.max(1, Math.min(max, Math.floor(Number(requested)) || 1));
}

export function resolveNativeImageOutputFormat(provider: ImageProvider, model: string, native: NativeImageOptions, value: ImageOutputFormat): ImageOutputFormat {
    const capabilities = getNativeImageCapabilities(provider, model);
    if (!capabilities.supported) return value;
    if (provider === "google") return "jpeg";
    if (provider === "ark" && !capabilities.outputFormats.includes("png")) return "jpeg";
    if (provider === "ark" && (value === "webp" || native.ark?.transparent)) return "png";
    return value;
}
