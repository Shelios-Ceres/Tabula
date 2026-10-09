import axios from "axios";
import { nanoid } from "nanoid";

import i18n from "@/i18n";
import { readFileAsDataUrl } from "@/lib/image-utils";
import { clampVideoSeconds, computeVideoSize, inferVideoRatio } from "@/lib/media-size";
import { getMediaBlob, resolveMediaUrl, uploadMediaFile, type UploadedFile } from "@/services/file-storage";
import { imageToDataUrl } from "@/services/image-storage";
import { getVideoFamily, snapVideoRatio, snapVideoResolution, snapVideoSeconds } from "@/lib/model-families";
import { boolConfig, buildApiUrl, modelOptionName, resolveModelChannel, resolveModelRequestConfig, resolveModelScript, withLocalProxy, type AiConfig } from "@/stores/use-config-store";
import { runModelPlugin } from "./model-plugin";
import type { ReferenceImage } from "@/types/image";
import type { ReferenceAudio, ReferenceVideo } from "@/types/media";

type ApiEnvelope<T> = T | { code?: number | string; data?: T | null; msg?: string; message?: string; error?: { message?: string } };
type RequestOptions = { signal?: AbortSignal };
type VideoMediaOptions = RequestOptions & { videos?: ReferenceVideo[]; audios?: ReferenceAudio[] };
const apiText = (key: string, options?: Record<string, unknown>) => i18n.t(`apiErrors.${key}`, options);

export type VideoGenerationResult = { blob?: Blob; url?: string; mimeType?: string };
export type VideoTaskProvider = "gemini" | "ark" | "xai";
export type VideoGenerationTask = { id: string; provider: VideoTaskProvider | "plugin"; model: string };
/** 2.5s × 360 ≈ 15 minutes; a timed-out task keeps its ID and can be queried again. */
export const VIDEO_POLL_ATTEMPTS = 360;
type GeminiInlineData = { bytesBase64Encoded: string; mimeType: string };
type GeminiVideoOperation = {
    name?: string;
    done?: boolean;
    error?: { message?: string };
    response?: { generateVideoResponse?: { generatedSamples?: Array<{ video?: { uri?: string } }> } };
};
export type VideoGenerationTaskState = { status: "pending" } | { status: "completed"; result: VideoGenerationResult } | { status: "failed"; error: string };

/** Results for scripted (plugin) video models, which run their own create+poll in one shot at task creation. */
const pluginVideoResults = new Map<string, VideoGenerationResult>();

function aiApiUrl(config: AiConfig, path: string) {
    return buildApiUrl(config.baseUrl, path);
}

function aiHeaders(config: AiConfig, contentType?: string) {
    return {
        Authorization: `Bearer ${config.apiKey}`,
        ...(contentType ? { "Content-Type": contentType } : {}),
    };
}

export async function requestVideoGeneration(config: AiConfig, prompt: string, references: ReferenceImage[] = [], options?: VideoMediaOptions): Promise<VideoGenerationResult> {
    return waitForVideoGenerationTask(config, await createVideoGenerationTask(config, prompt, references, options), options);
}

export async function waitForVideoGenerationTask(config: AiConfig, task: VideoGenerationTask, options?: RequestOptions): Promise<VideoGenerationResult> {
    for (let attempt = 0; attempt < VIDEO_POLL_ATTEMPTS; attempt += 1) {
        if (options?.signal?.aborted) throw new DOMException("Aborted", "AbortError");
        const state = await pollVideoGenerationTask(config, task, options);
        if (state.status === "completed") return state.result;
        if (state.status === "failed") throw videoTaskFailed(state.error);
        if (attempt === VIDEO_POLL_ATTEMPTS - 1) throw new Error(apiText("videoTimeout", { provider: "" }));
        await delay(2500, options?.signal);
    }
    throw new Error(apiText("videoTimeout", { provider: "" }));
}

export function isVideoTaskFailed(error: unknown) {
    return error instanceof Error && error.name === "VideoTaskFailed";
}

function videoTaskFailed(message: string) {
    const error = new Error(message);
    error.name = "VideoTaskFailed";
    return error;
}

export async function createVideoGenerationTask(config: AiConfig, prompt: string, references: ReferenceImage[] = [], options?: VideoMediaOptions): Promise<VideoGenerationTask> {
    const selectedModel = (config.model || config.videoModel).trim();
    const requestConfig = resolveModelRequestConfig(config, selectedModel);
    const script = resolveModelScript(config, selectedModel);
    if (script) return createPluginVideoTask(requestConfig, selectedModel, script, prompt, references, options);
    assertVideoConfig(requestConfig, requestConfig.model);
    const protocol = videoProtocol(requestConfig);
    if (protocol === "gemini") return createGeminiVideoTask(requestConfig, selectedModel, prompt, references, options);
    if (protocol === "ark") return createArkVideoTask(requestConfig, selectedModel, resolveModelChannel(config, selectedModel).models.find((item) => item.name === requestConfig.model)?.endpointId, prompt, references, options);
    return createXaiVideoTask(requestConfig, selectedModel, prompt, references, options);
}

/** Known families pick their official protocol; unknown models on a Google channel fall back to Veo. */
function videoProtocol(config: AiConfig): VideoTaskProvider {
    const protocol = getVideoFamily(config.model).protocol || (config.apiFormat === "gemini" ? "gemini" : undefined);
    if (!protocol) throw new Error(apiText("videoModelUnsupported", { model: config.model }));
    return protocol;
}

/** Family-normalized seconds, resolution tier and ratio; "2160" is sent as 4k. */
function videoParams(config: AiConfig, referenceMode: boolean) {
    const family = getVideoFamily(config.model);
    const resolution = snapVideoResolution(family, config.vquality);
    const ratio = snapVideoRatio(family, videoAspectRatio(config.size, "auto"));
    // Veo only accepts 8s for 1080p/4k and reference images.
    const seconds = family.protocol === "gemini" && (resolution !== "720" || referenceMode) ? 8 : snapVideoSeconds(family, config.videoSeconds);
    return { family, ratio, seconds, resolution: resolution === "2160" ? "4k" : `${resolution}p` };
}

export async function pollVideoGenerationTask(config: AiConfig, task: VideoGenerationTask, options?: RequestOptions): Promise<VideoGenerationTaskState> {
    if (task.provider === "plugin") {
        const result = pluginVideoResults.get(task.id);
        return result ? { status: "completed", result } : { status: "failed", error: apiText("pluginVideoExpired") };
    }
    const requestConfig = resolveModelRequestConfig(config, task.model);
    assertVideoConfig(requestConfig, requestConfig.model);
    if (task.provider === "gemini") return pollGeminiVideoTask(requestConfig, task, options);
    if (task.provider === "ark") return pollArkVideoTask(requestConfig, task, options);
    return pollXaiVideoTask(requestConfig, task, options);
}

async function createPluginVideoTask(config: AiConfig, model: string, script: string, prompt: string, references: ReferenceImage[], options?: VideoMediaOptions): Promise<VideoGenerationTask> {
    if (!config.baseUrl.trim()) throw new Error(apiText("baseUrlRequired"));
    if (!config.apiKey.trim()) throw new Error(apiText("apiKeyRequired"));
    const refs = await Promise.all(references.map((image) => imageToDataUrl(image)));
    const videos = await Promise.all((options?.videos || []).map((video) => referenceMediaToFile(video, "ref.mp4", "invalidReferenceVideo", options)));
    const audios = await Promise.all((options?.audios || []).map((audio) => referenceMediaToFile(audio, "ref.mp3", "invalidReferenceAudio", options)));
    const result = videoPluginResult(
        await runModelPlugin({
            capability: "video",
            script,
            config,
            prompt,
            images: refs,
            videos,
            audios,
            params: {
                seconds: normalizeVideoSeconds(config.videoSeconds),
                size: normalizeVideoSize(config.size, config.vquality),
                resolution: normalizeVideoResolution(config.vquality),
                ratio: videoAspectRatio(config.size),
                generateAudio: boolConfig(config.videoGenerateAudio, true),
                watermark: boolConfig(config.videoWatermark, false),
                mode: resolveVideoMode(config.videoMode, refs.length),
            },
            signal: options?.signal,
        }),
    );
    const id = nanoid();
    pluginVideoResults.set(id, result);
    return { id, provider: "plugin", model };
}

function videoPluginResult(result: unknown): VideoGenerationResult {
    if (result instanceof Blob) return { blob: result };
    if (typeof result === "string") return { url: result, mimeType: "video/mp4" };
    if (result && typeof result === "object") {
        const record = result as Record<string, unknown>;
        if (record.blob instanceof Blob) return { blob: record.blob };
        const url = [record.url, record.video_url, record.result_url].find((value) => typeof value === "string" && value) as string | undefined;
        if (url) return { url, mimeType: "video/mp4" };
    }
    throw new Error(apiText("scriptNoVideo"));
}

export async function storeGeneratedVideo(result: VideoGenerationResult): Promise<UploadedFile> {
    if (result.blob) return uploadMediaFile(result.blob, "video");
    if (result.url) {
        try {
            return await uploadMediaFile(result.url, "video");
        } catch {
            return { url: result.url, storageKey: "", bytes: 0, mimeType: result.mimeType || "video/mp4" };
        }
    }
    throw new Error(apiText("noPlayableVideo"));
}

async function videoResultFromUrl(url: string, options?: RequestOptions, headers?: Record<string, string>): Promise<VideoGenerationResult> {
    try {
        const response = await axios.get<Blob>(withLocalProxy(url), { headers, responseType: "blob", signal: options?.signal });
        await assertVideoBlob(response.data);
        return { blob: response.data };
    } catch (error) {
        // Header-authenticated URLs cannot play without credentials, so keep the task resumable instead of saving them.
        if (axios.isCancel(error) || options?.signal?.aborted || headers) throw error;
        return { url, mimeType: "video/mp4" };
    }
}

async function createGeminiVideoTask(config: AiConfig, model: string, prompt: string, references: ReferenceImage[], options?: VideoMediaOptions): Promise<VideoGenerationTask> {
    const images = await Promise.all(references.map((image) => imageToDataUrl(image)));
    const videos = await Promise.all((options?.videos || []).map((video) => referenceMediaToFile(video, "ref.mp4", "invalidReferenceVideo", options)));
    const audios = await Promise.all((options?.audios || []).map((audio) => referenceMediaToFile(audio, "ref.mp3", "invalidReferenceAudio", options)));
    const mode = resolveVideoMode(config.videoMode, images.length);
    const instance: Record<string, unknown> = { prompt };
    if (mode === "frames") {
        if (images[0]) instance.image = parseDataUrlInline(images[0]);
        if (images[1]) instance.lastFrame = parseDataUrlInline(images[1]);
    } else {
        instance.referenceImages = images.map((dataUrl) => ({ image: parseDataUrlInline(dataUrl), referenceType: "asset" }));
    }
    if (videos[0]) instance.video = await fileToGeminiInline(videos[0]);
    if (audios[0]) instance.audio = await fileToGeminiInline(audios[0]);
    const params = videoParams(config, mode === "reference" && images.length > 0);
    try {
        const created = unwrapEnvelope((await axios.post<ApiEnvelope<GeminiVideoOperation>>(geminiVideoUrl(config, model, "predictLongRunning"), {
            instances: [instance],
            parameters: {
                aspectRatio: params.ratio === "auto" ? "16:9" : params.ratio,
                durationSeconds: params.seconds,
                resolution: params.resolution,
                // Veo on the Gemini API always generates audio and has no watermark switch.
                ...(params.family.audio ? { generateAudio: boolConfig(config.videoGenerateAudio, true) } : {}),
                ...(params.family.watermark ? { addWatermark: boolConfig(config.videoWatermark, false) } : {}),
            },
        }, { headers: geminiVideoHeaders(config), signal: options?.signal })).data, apiText("noVideoTask"));
        if (!created.name) throw new Error(apiText("noVideoTaskId"));
        return { id: created.name, provider: "gemini", model };
    } catch (error) {
        throw new Error(readAxiosError(error, apiText("videoTaskCreateFailed")));
    }
}

async function pollGeminiVideoTask(config: AiConfig, task: VideoGenerationTask, options?: RequestOptions): Promise<VideoGenerationTaskState> {
    try {
        const state = unwrapEnvelope((await axios.get<ApiEnvelope<GeminiVideoOperation>>(geminiOperationUrl(config, task.id), { headers: geminiVideoHeaders(config), signal: options?.signal })).data, apiText("videoTaskQueryFailed"));
        if (state.error) return { status: "failed", error: readApiErrorMessage(state.error.message) || apiText("videoGenerationFailed") };
        if (!state.done) return { status: "pending" };
        const uri = state.response?.generateVideoResponse?.generatedSamples?.[0]?.video?.uri;
        if (!uri) return { status: "failed", error: apiText("noPlayableVideo") };
        return { status: "completed", result: await videoResultFromUrl(uri, options, { "x-goog-api-key": config.apiKey }) };
    } catch (error) {
        throw new Error(readAxiosError(error, apiText("videoTaskQueryFailed")));
    }
}

function arkTasksUrl(config: Pick<AiConfig, "baseUrl">, id = "") {
    const base = config.baseUrl.trim().replace(/\/+$/, "");
    return withLocalProxy(`${/\/api\/v3$/i.test(base) ? base : `${base}/api/v3`}/contents/generations/tasks${id ? `/${encodeURIComponent(id)}` : ""}`);
}

/** Seedance only accepts URLs for reference video/audio; local images are sent as data URLs. */
function publicMediaUrl(item: { url?: string }) {
    if (!isPublicMediaUrl(item.url || "")) throw new Error(apiText("arkVideoMediaUrl"));
    return item.url!;
}

async function createArkVideoTask(config: AiConfig, model: string, endpointId: string | undefined, prompt: string, references: ReferenceImage[], options?: VideoMediaOptions): Promise<VideoGenerationTask> {
    const images = await Promise.all(references.map((image) => imageToDataUrl(image)));
    const mode = resolveVideoMode(config.videoMode, images.length);
    const params = videoParams(config, mode === "reference");
    const content = [
        ...(prompt.trim() ? [{ type: "text", text: prompt }] : []),
        ...images.map((url, index) => ({ type: "image_url", image_url: { url }, role: mode === "frames" ? (index === 0 ? "first_frame" : "last_frame") : "reference_image" })),
        ...(options?.videos || []).map((video) => ({ type: "video_url", video_url: { url: publicMediaUrl(video) }, role: "reference_video" })),
        ...(options?.audios || []).map((audio) => ({ type: "audio_url", audio_url: { url: publicMediaUrl(audio) }, role: "reference_audio" })),
    ];
    try {
        const created = (await axios.post<{ id?: string }>(arkTasksUrl(config), {
            model: endpointId?.trim() || config.model,
            content,
            ratio: params.ratio === "auto" ? "adaptive" : params.ratio,
            duration: params.seconds,
            resolution: params.resolution,
            ...(params.family.audio ? { generate_audio: boolConfig(config.videoGenerateAudio, true) } : {}),
            watermark: boolConfig(config.videoWatermark, false),
        }, { headers: aiHeaders(config, "application/json"), signal: options?.signal })).data;
        if (!created.id) throw new Error(apiText("noVideoTaskId"));
        return { id: created.id, provider: "ark", model };
    } catch (error) {
        throw new Error(readAxiosError(error, apiText("videoTaskCreateFailed")));
    }
}

async function pollArkVideoTask(config: AiConfig, task: VideoGenerationTask, options?: RequestOptions): Promise<VideoGenerationTaskState> {
    try {
        const state = (await axios.get<{ status?: string; content?: { video_url?: string }; error?: { message?: string } }>(arkTasksUrl(config, task.id), { headers: aiHeaders(config), signal: options?.signal })).data;
        if (state.status === "failed" || state.status === "expired" || state.status === "cancelled") return { status: "failed", error: readApiErrorMessage(state.error?.message) || apiText("videoGenerationFailed") };
        if (state.status !== "succeeded" && state.status !== "completed") return { status: "pending" };
        const url = state.content?.video_url;
        return url ? { status: "completed", result: await videoResultFromUrl(url, options) } : { status: "failed", error: apiText("noPlayableVideo") };
    } catch (error) {
        throw new Error(readAxiosError(error, apiText("videoTaskQueryFailed")));
    }
}

async function createXaiVideoTask(config: AiConfig, model: string, prompt: string, references: ReferenceImage[], options?: VideoMediaOptions): Promise<VideoGenerationTask> {
    const images = await Promise.all(references.map((image) => imageToDataUrl(image)));
    const mode = resolveVideoMode(config.videoMode, images.length);
    const params = videoParams(config, mode === "reference");
    try {
        const created = (await axios.post<{ request_id?: string }>(aiApiUrl(config, "/videos/generations"), {
            model: config.model,
            prompt,
            duration: params.seconds,
            aspect_ratio: params.ratio === "auto" ? "16:9" : params.ratio,
            resolution: params.resolution,
            ...(mode === "frames" && images[0] ? { image: { url: images[0] } } : {}),
            ...(mode === "reference" && images.length ? { reference_images: images.map((url) => ({ url })) } : {}),
        }, { headers: aiHeaders(config, "application/json"), signal: options?.signal })).data;
        if (!created.request_id) throw new Error(apiText("noVideoTaskId"));
        return { id: created.request_id, provider: "xai", model };
    } catch (error) {
        throw new Error(readAxiosError(error, apiText("videoTaskCreateFailed")));
    }
}

async function pollXaiVideoTask(config: AiConfig, task: VideoGenerationTask, options?: RequestOptions): Promise<VideoGenerationTaskState> {
    try {
        const state = (await axios.get<{ status?: string; video?: { url?: string | null }; error?: { message?: string } }>(aiApiUrl(config, `/videos/${encodeURIComponent(task.id)}`), { headers: aiHeaders(config), signal: options?.signal })).data;
        if (state.status === "failed" || state.status === "expired") return { status: "failed", error: readApiErrorMessage(state.error?.message) || apiText("videoGenerationFailed") };
        if (state.status !== "done") return { status: "pending" };
        const url = state.video?.url;
        return url ? { status: "completed", result: await videoResultFromUrl(url, options) } : { status: "failed", error: apiText("noPlayableVideo") };
    } catch (error) {
        throw new Error(readAxiosError(error, apiText("videoTaskQueryFailed")));
    }
}

function assertVideoConfig(config: AiConfig, model: string) {
    if (!model) throw new Error(apiText("videoModelRequired"));
    if (!config.baseUrl.trim()) throw new Error(apiText("baseUrlRequired"));
    if (!config.apiKey.trim()) throw new Error(apiText("apiKeyRequired"));
}

function geminiVideoBaseUrl(config: Pick<AiConfig, "baseUrl">) {
    const normalizedBaseUrl = config.baseUrl.trim().replace(/\/+$/, "");
    const lowerBaseUrl = normalizedBaseUrl.toLowerCase();
    return lowerBaseUrl.endsWith("/v1") || lowerBaseUrl.endsWith("/v1beta") ? normalizedBaseUrl : `${normalizedBaseUrl}/v1beta`;
}

function geminiVideoUrl(config: Pick<AiConfig, "baseUrl">, model: string, action: string) {
    return withLocalProxy(`${geminiVideoBaseUrl(config)}/models/${encodeURIComponent(modelOptionName(model).replace(/^models\//, ""))}:${action}`);
}

function geminiOperationUrl(config: Pick<AiConfig, "baseUrl">, name: string) {
    return withLocalProxy(`${geminiVideoBaseUrl(config)}/${name.replace(/^\//, "")}`);
}

function geminiVideoHeaders(config: Pick<AiConfig, "apiKey">) {
    return { "x-goog-api-key": config.apiKey, "Content-Type": "application/json" };
}

function videoAspectRatio(size: string, fallback = "16:9") {
    const ratio = inferVideoRatio(size);
    return ratio === "auto" ? fallback : ratio;
}

function parseDataUrlInline(dataUrl: string, fallbackType = "image/png"): GeminiInlineData {
    const match = dataUrl.match(/^data:([^;]+);base64,(.*)$/);
    return { bytesBase64Encoded: match?.[2] || "", mimeType: match?.[1] || fallbackType };
}

async function fileToGeminiInline(file: File): Promise<GeminiInlineData> {
    return parseDataUrlInline(await readFileAsDataUrl(file), file.type || "application/octet-stream");
}

async function referenceMediaToFile(item: { name: string; type?: string; url?: string; storageKey?: string }, fallbackName: string, errorKey: "invalidReferenceVideo" | "invalidReferenceAudio", options?: RequestOptions) {
    let blob = item.storageKey ? await getMediaBlob(item.storageKey) : null;
    if (!blob) {
        const url = item.storageKey ? await resolveMediaUrl(item.storageKey, item.url || "") : item.url || "";
        if (!url) throw new Error(apiText(errorKey));
        try {
            blob = await (await fetch(url, { signal: options?.signal })).blob();
        } catch (error) {
            if (error instanceof DOMException && error.name === "AbortError") throw error;
            throw new Error(apiText(errorKey));
        }
    }
    if (!blob.size) throw new Error(apiText(errorKey));
    return new File([blob], item.name || fallbackName, { type: item.type || blob.type || "application/octet-stream" });
}

function normalizeVideoSeconds(value: string) {
    return clampVideoSeconds(value);
}

function resolveVideoMode(mode: string | undefined, imageCount: number) {
    if (mode === "reference" || imageCount > 2) return "reference";
    return "frames";
}

function normalizeVideoSize(value: string, resolution?: string) {
    if (value === "auto") return null;
    if (/^\d+x\d+$/.test(value || "")) return value;
    const ratio = inferVideoRatio(value || "16:9");
    if (ratio === "auto") return null;
    return computeVideoSize(resolution || "720", ratio);
}

function normalizeVideoResolution(value: string) {
    if (value === "low") return "480p";
    if (value === "auto" || value === "high" || value === "medium") return "720p";
    const resolution = value.replace(/p$/i, "") || "720";
    return `${resolution}p`;
}

function unwrapEnvelope<T>(payload: ApiEnvelope<T>, emptyMessage: string): T {
    if (!payload) throw new Error(emptyMessage);
    if (typeof payload === "object" && "code" in payload && payload.code !== undefined) {
        if (payload.code !== 0 && payload.code !== "0") throw new Error(readApiErrorMessage(payload) || apiText("requestFailed"));
        if (!payload.data) throw new Error(emptyMessage);
        return payload.data;
    }
    return payload as T;
}

function readApiErrorMessage(value: unknown): string {
    if (!value) return "";
    if (typeof value === "string") {
        try {
            const parsed = JSON.parse(value);
            const inner = readApiErrorMessage(parsed) || value;
            if (inner === value && typeof parsed === "object" && Object.keys(parsed).length === 0) return "";
            return inner;
        } catch {
            if (/<[a-z][\s\S]*>/i.test(value)) return apiText("htmlError", { preview: `${value.slice(0, 80)}...` });
            return value;
        }
    }
    if (typeof value !== "object") return "";
    const payload = value as { msg?: unknown; message?: unknown; error?: unknown; detail?: unknown };
    // error may be a string or an object containing a message.
    const errorMsg =
        typeof payload.error === "string"
            ? payload.error
            : (payload.error as { message?: unknown })?.message;
    return (
        readApiErrorMessage(payload.msg) ||
        readApiErrorMessage(payload.message) ||
        readApiErrorMessage(errorMsg) ||
        readApiErrorMessage(payload.detail) ||
        ""
    );
}

function readAxiosError(error: unknown, fallback: string) {
    if (axios.isCancel(error)) return apiText("requestCanceled");
    if (axios.isAxiosError<{ error?: { message?: string }; msg?: string; message?: string; code?: number | string }>(error)) {
        if (!error.response && error.code === "ERR_NETWORK") return apiText("requestFailed");
        const responseData = error.response?.data;
        return readApiErrorMessage(responseData) || statusMessage(error.response?.status, fallback);
    }
    if (error instanceof DOMException && error.name === "AbortError") return apiText("requestCanceled");
    return error instanceof Error ? readApiErrorMessage(error.message) || error.message : fallback;
}

function statusMessage(status: number | undefined, fallback: string) {
    if (status === 401 || status === 403) return apiText("authenticationFailed");
    if (status === 429) return apiText("rateLimited");
    return status ? `${fallback}（${status}）` : fallback;
}

async function assertVideoBlob(blob: Blob) {
    if (!blob.type.includes("json")) return;
    let payload: { code?: number; msg?: string; error?: { message?: string } };
    try {
        payload = JSON.parse(await blob.text()) as { code?: number; msg?: string; error?: { message?: string } };
    } catch {
        return;
    }
    if (typeof payload.code === "number" && payload.code !== 0) throw new Error(readApiErrorMessage(payload) || apiText("videoDownloadFailed"));
    if (payload.error?.message) throw new Error(readApiErrorMessage(payload.error.message) || payload.error.message);
}

function isPublicMediaUrl(value: string) {
    return /^https?:\/\//i.test(value || "");
}

function delay(ms: number, signal?: AbortSignal) {
    return new Promise<void>((resolve, reject) => {
        if (signal?.aborted) {
            reject(new DOMException("Aborted", "AbortError"));
            return;
        }
        const timer = setTimeout(resolve, ms);
        signal?.addEventListener(
            "abort",
            () => {
                clearTimeout(timer);
                reject(new DOMException("Aborted", "AbortError"));
            },
            { once: true },
        );
    });
}
