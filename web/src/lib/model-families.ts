/**
 * Model family profiles. Capabilities are matched by family instead of exact model IDs,
 * so snapshot dates, -preview/-latest suffixes and vendor prefixes resolve to the same family.
 */

/** Strip channel prefix, `models/`, vendor prefix and trailing snapshot/preview suffixes. */
export function normalizeModelId(model: string) {
    let name = (model.split("::").at(-1) || "").trim().toLowerCase().replace(/^models\//, "").replace(/^[a-z0-9_.-]+\//, "");
    for (let previous = ""; previous !== name;) {
        previous = name;
        name = name.replace(/-(?:preview|latest|exp|\d{4}-\d{2}-\d{2}|\d{6})$/, "");
    }
    return name;
}

export const standardImageRatios = ["1:1", "2:3", "3:2", "3:4", "4:3", "4:5", "5:4", "9:16", "16:9", "21:9"];
export const arkImageRatios = [...standardImageRatios, "9:21"];

export const xaiImageRatios = ["1:1", "3:4", "4:3", "9:16", "16:9", "2:3", "3:2", "9:19.5", "19.5:9", "9:20", "20:9", "1:2", "2:1", "21:9", "5:2"];

export type ImageFamily = {
    id: string;
    provider: "openai" | "google" | "ark" | "xai";
    match: RegExp;
    sizes: readonly string[];
    ratios: readonly string[];
    maxReferences: number;
    maxImages: number;
    /** Ark custom pixel range: [min, max] total pixels. */
    pixels?: readonly [number, number];
    outputFormats?: readonly ("png" | "jpeg")[];
    thinkingLevels?: readonly string[];
    stream?: boolean;
    webSearch?: boolean;
    imageSearch?: boolean;
    sequential?: boolean;
    layers?: boolean;
    fastPrompt?: boolean;
};

// Order matters: more specific patterns first.
export const imageFamilies: readonly ImageFamily[] = [
    { id: "gemini-nano-banana-2.1", provider: "google", match: /^gemini-nano-banana-2\.1/, sizes: ["1K", "2K", "4K"], ratios: standardImageRatios, maxReferences: 14, maxImages: 1, thinkingLevels: ["auto", "minimal", "medium", "high"], stream: true, webSearch: true, imageSearch: true },
    { id: "gemini-3.1-flash-lite-image", provider: "google", match: /^gemini-3\.1-flash-lite-image/, sizes: ["1K"], ratios: standardImageRatios, maxReferences: 14, maxImages: 1, thinkingLevels: ["auto", "minimal", "high"], stream: true },
    { id: "gemini-3.1-flash-image", provider: "google", match: /^gemini-3\.1-flash-image/, sizes: ["512", "1K", "2K", "4K"], ratios: [...standardImageRatios, "1:4", "4:1", "1:8", "8:1"], maxReferences: 14, maxImages: 1, thinkingLevels: ["auto", "minimal", "high"], stream: true, webSearch: true, imageSearch: true },
    { id: "gemini-3-pro-image", provider: "google", match: /^gemini-3-pro-image/, sizes: ["1K", "2K", "4K"], ratios: standardImageRatios, maxReferences: 14, maxImages: 1, thinkingLevels: ["auto"], stream: true, webSearch: true },
    { id: "gemini-2.5-flash-image", provider: "google", match: /^gemini-2\.5-flash-image/, sizes: ["1K"], ratios: standardImageRatios, maxReferences: 14, maxImages: 1, stream: true },
    // Unknown future Gemini image models: conservative common options, no model-specific switches.
    { id: "gemini-image", provider: "google", match: /^gemini-.*(?:image|banana)/, sizes: ["1K", "2K", "4K"], ratios: standardImageRatios, maxReferences: 14, maxImages: 1, stream: true },
    { id: "seedream-5-pro", provider: "ark", match: /seedream-5-0-pro$/, sizes: ["1K", "1.5K", "2K"], ratios: arkImageRatios, maxReferences: 10, maxImages: 1, pixels: [921_600, 4_624_220], outputFormats: ["png", "jpeg"], layers: true, fastPrompt: true },
    { id: "seedream-5-lite", provider: "ark", match: /seedream-5-0(?:-lite)?$/, sizes: ["2K", "3K", "4K"], ratios: arkImageRatios, maxReferences: 14, maxImages: 15, pixels: [3_686_400, 16_777_216], outputFormats: ["png", "jpeg"], stream: true, webSearch: true, sequential: true },
    { id: "seedream-4.5", provider: "ark", match: /seedream-4-5$/, sizes: ["2K", "4K"], ratios: arkImageRatios, maxReferences: 14, maxImages: 15, pixels: [3_686_400, 16_777_216], outputFormats: ["jpeg"], stream: true, sequential: true },
    { id: "seedream-4.0", provider: "ark", match: /seedream-4-0$/, sizes: ["1K", "2K", "4K"], ratios: arkImageRatios, maxReferences: 14, maxImages: 15, pixels: [921_600, 16_777_216], outputFormats: ["jpeg"], stream: true, sequential: true },
    { id: "grok-imagine-image", provider: "xai", match: /^grok-imagine-image/, sizes: ["1k", "1.5k", "2k"], ratios: xaiImageRatios, maxReferences: 5, maxImages: 10 },
];

/** Models offered by "添加推荐模型" in the channel editor. Capabilities come from guessCapability. */
export const vendorRecommendedModels: Record<"openai" | "google" | "ark" | "xai", readonly string[]> = {
    openai: ["gpt-image-2.5-flare", "gpt-image-2.5-sunburst", "gpt-image-2", "gpt-5.5", "gpt-4o-mini-tts"],
    google: ["gemini-nano-banana-2.1", "gemini-3-pro-image", "gemini-3.1-flash-image", "gemini-3.1-flash-lite-image", "veo-3.1-generate-preview", "veo-3.1-fast-generate-preview", "veo-3.1-lite-generate-preview"],
    ark: ["doubao-seedream-5-0-pro-260628", "doubao-seedream-5-0-260128", "doubao-seedream-4-5-251128", "doubao-seedream-4-0-250828", "doubao-seedance-2-5-260628", "doubao-seedance-2-0-260128", "doubao-seedance-2-0-fast-260128"],
    xai: ["grok-imagine-image-2.0", "grok-imagine-video-1.5"],
};

export function findImageFamily(provider: string, model: string) {
    const name = normalizeModelId(model);
    return imageFamilies.find((family) => family.provider === provider && family.match.test(name));
}

export type VideoProtocol = "gemini" | "ark" | "xai";

export type VideoFamily = {
    id: string;
    protocol?: VideoProtocol;
    match: RegExp;
    /** Either an explicit list of allowed seconds or an inclusive range. */
    seconds: readonly number[] | { min: number; max: number };
    /** Vertical pixel tiers; "2160" is sent as 4k. */
    resolutions: readonly string[];
    /** "auto" means the provider decides (Seedance adaptive). */
    ratios: readonly string[];
    frames: boolean;
    maxReferences: number;
    audio: boolean;
    watermark: boolean;
};

const seedanceRatios = ["16:9", "4:3", "1:1", "3:4", "9:16", "21:9", "auto"];

export const videoFamilies: readonly VideoFamily[] = [
    { id: "veo-3.1", protocol: "gemini", match: /^veo-3\.1-generate/, seconds: [4, 6, 8], resolutions: ["720", "1080", "2160"], ratios: ["16:9", "9:16"], frames: true, maxReferences: 3, audio: false, watermark: false },
    { id: "veo-3.1-fast", protocol: "gemini", match: /^veo-3\.1-fast/, seconds: [4, 6, 8], resolutions: ["720", "1080", "2160"], ratios: ["16:9", "9:16"], frames: true, maxReferences: 0, audio: false, watermark: false },
    { id: "veo-3.1-lite", protocol: "gemini", match: /^veo-3\.1-lite/, seconds: [4, 6, 8], resolutions: ["720", "1080"], ratios: ["16:9", "9:16"], frames: true, maxReferences: 0, audio: false, watermark: false },
    { id: "veo", protocol: "gemini", match: /^veo-/, seconds: [4, 6, 8], resolutions: ["720", "1080"], ratios: ["16:9", "9:16"], frames: true, maxReferences: 0, audio: false, watermark: false },
    { id: "seedance-2.5", protocol: "ark", match: /seedance-2-5/, seconds: { min: 4, max: 30 }, resolutions: ["480", "720", "1080"], ratios: seedanceRatios, frames: true, maxReferences: 30, audio: true, watermark: true },
    { id: "seedance-2.0-fast", protocol: "ark", match: /seedance-2-0-(?:fast|mini)/, seconds: { min: 4, max: 15 }, resolutions: ["480", "720"], ratios: seedanceRatios, frames: true, maxReferences: 9, audio: true, watermark: true },
    { id: "seedance-2.0", protocol: "ark", match: /seedance-2-0/, seconds: { min: 4, max: 15 }, resolutions: ["480", "720", "1080"], ratios: seedanceRatios, frames: true, maxReferences: 9, audio: true, watermark: true },
    { id: "seedance", protocol: "ark", match: /seedance/, seconds: { min: 2, max: 12 }, resolutions: ["480", "720", "1080"], ratios: seedanceRatios, frames: true, maxReferences: 4, audio: true, watermark: true },
    { id: "grok-imagine-video", protocol: "xai", match: /^grok-imagine-video/, seconds: { min: 1, max: 15 }, resolutions: ["480", "720", "1080"], ratios: ["16:9", "9:16", "1:1", "4:3", "3:4", "3:2", "2:3"], frames: true, maxReferences: 7, audio: false, watermark: false },
];

/** Options shown for models without a known family. */
export const genericVideoFamily: VideoFamily = { id: "generic", match: /.*/, seconds: { min: 4, max: 30 }, resolutions: ["480", "720", "1080"], ratios: ["1:1", "3:4", "4:3", "16:9", "9:16", "21:9", "auto"], frames: true, maxReferences: 16, audio: true, watermark: true };

export function getVideoFamily(model: string): VideoFamily {
    const name = normalizeModelId(model);
    return videoFamilies.find((family) => family.match.test(name)) || genericVideoFamily;
}

/** Snap seconds to the family's allowed list or range. */
export function snapVideoSeconds(family: VideoFamily, value: string | number, fallback = 6) {
    const seconds = Math.round(Number(value) || fallback);
    if (Array.isArray(family.seconds)) return family.seconds.reduce((best, item) => (Math.abs(item - seconds) < Math.abs(best - seconds) ? item : best));
    const range = family.seconds as { min: number; max: number };
    return Math.max(range.min, Math.min(range.max, seconds));
}

/** Snap a resolution tier ("720", "720p", "low"...) to the nearest supported tier. */
export function snapVideoResolution(family: VideoFamily, value: string) {
    const raw = String(value || "").trim().toLowerCase();
    const pixels = raw === "4k" ? 2160 : raw === "low" ? 480 : Number(raw.replace(/p$/, "")) || 720;
    return family.resolutions.reduce((best, item) => (Math.abs(Number(item) - pixels) < Math.abs(Number(best) - pixels) ? item : best));
}

export function snapVideoRatio(family: VideoFamily, ratio: string) {
    return family.ratios.includes(ratio) ? ratio : family.ratios[0];
}
