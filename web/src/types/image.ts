import type { ImageConversation, ImageLayer, ImageProtocol, ImageProvider } from "./image-generation";

export type ReferenceImage = {
    id: string;
    name: string;
    type: string;
    dataUrl: string;
    url?: string;
    storageKey?: string;
};

export type ImageOutputFormat = "png" | "jpeg" | "webp";
export type ImageUsage = {
    input_tokens: number;
    output_tokens: number;
    total_tokens: number;
    input_tokens_details?: { image_tokens?: number; text_tokens?: number };
    output_tokens_details?: { image_tokens?: number; text_tokens?: number };
};

/** Usage belongs to the API request and may be shared by several output images. */
export type ImageResponseMetadata = {
    provider?: ImageProvider;
    protocol?: ImageProtocol;
    model?: string;
    rawUsage?: Record<string, unknown>;
    providerMetadata?: Record<string, unknown>;
    conversation?: ImageConversation;
    background?: string;
    quality?: string;
    size?: string;
    outputFormat?: ImageOutputFormat;
    usage?: ImageUsage;
    requestId?: string;
};

export type ImageGenerationResult = {
    id: string;
    dataUrl: string;
    response?: ImageResponseMetadata;
    role?: "image" | "base" | "layer";
    layer?: ImageLayer;
    storageKey?: string;
    width?: number;
    height?: number;
    bytes?: number;
    mimeType?: string;
};
