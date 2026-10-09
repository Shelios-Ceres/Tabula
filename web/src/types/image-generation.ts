import type { ImageGenerationResult, ReferenceImage } from "./image";

export type ImageProvider = "openai" | "google" | "ark" | "xai";
export type ImageProtocol = "openai-images" | "openai-responses" | "google-interactions" | "ark-images" | "xai-images";
export type ImageSelection =
    | { kind: "point"; x: number; y: number; imageIndex?: number }
    | { kind: "rectangle"; x1: number; y1: number; x2: number; y2: number; imageIndex?: number };

/** UI coordinates are fractions of the source image, independent of viewport zoom. */
export type NativeImageOptions = {
    stream?: boolean;
    openai?: {
        api?: "images" | "responses";
        mainModel?: string;
        store?: boolean;
        partialImages?: number;
    };
    google?: {
        imageSize?: "512" | "1K" | "2K" | "4K";
        aspectRatio?: string;
        thinkingLevel?: "auto" | "minimal" | "medium" | "high";
        webSearch?: boolean;
        imageSearch?: boolean;
        includeText?: boolean;
        store?: boolean;
    };
    ark?: {
        /** Frozen channel-model binding; an empty ID keeps direct Model ID calls. */
        endpoint?: { id: string; model: string };
        size?: string;
        sequential?: boolean;
        watermark?: boolean;
        optimizePrompt?: "standard" | "fast";
        webSearch?: boolean;
        layerDecomposition?: boolean;
        transparent?: boolean;
    };
    xai?: {
        resolution?: "1k" | "1.5k" | "2k";
        aspectRatio?: string;
    };
};

export type ImageConversation = {
    provider: ImageProvider;
    protocol: ImageProtocol;
    model: string;
    remoteId?: string;
    stored: boolean;
    createdAt: string;
    channelId?: string;
    baseUrl?: string;
    history?: unknown[];
};

export type ImageLayer = {
    zIndex: number;
    name?: string;
    description?: string;
    boundingBox?: { absolute?: [number, number, number, number]; normalized?: [number, number, number, number] };
};

export type ImageOutputError = { index: number; message: string; code?: string; status?: number; requestId?: string };
export type ImageJobResult = {
    images: ImageGenerationResult[];
    texts: string[];
    errors: ImageOutputError[];
    conversation?: ImageConversation;
    providerMetadata?: Record<string, unknown>;
    outputOrder?: Array<{ kind: "image" | "text"; index: number }>;
};

export type ImageGenerationEvent = {
    type: "started" | "preview" | "output" | "output-error" | "text" | "completed" | "client-aborted";
    index?: number;
    image?: ImageGenerationResult;
    text?: string;
    error?: ImageOutputError;
    result?: ImageJobResult;
};

export type ImageJobOptions = {
    signal?: AbortSignal;
    mask?: ReferenceImage;
    selections?: ImageSelection[];
    conversation?: ImageConversation;
    onEvent?: (event: ImageGenerationEvent) => void;
};

export type NativeImageRequest = {
    model: string;
    prompt: string;
    references: ReferenceImage[];
    count: number;
    quality: string;
    size: string;
    background: string;
    outputFormat: "png" | "jpeg" | "webp";
    outputCompression: number;
    moderation: "auto" | "low";
    native: NativeImageOptions;
};

/** Credentials are transport-only; never include this object in history or node metadata. */
export type ImageTransportContext = {
    baseUrl: string;
    apiKey: string;
    channelId: string;
};
