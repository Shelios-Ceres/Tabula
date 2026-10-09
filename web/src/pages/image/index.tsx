import { ArrowLeft, ArrowRight, BookOpen, CheckSquare, ClipboardPaste, Download, FolderPlus, History, ImagePlus, LoaderCircle, PenLine, Plus, RotateCcw, SlidersHorizontal, Sparkles, Trash2, Upload } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { App, Button, Checkbox, Drawer, Empty, Image, Input, Modal, Tag, Tooltip, Typography } from "antd";
import localforage from "localforage";
import { saveAs } from "file-saver";
import { useTranslation } from "react-i18next";
import type { TFunction } from "i18next";

import { ImageSettingsPanel, validateImageUiOptions } from "@/components/image-settings-panel";
import { ImageProviderMetadata, ImageResponseDetails } from "@/components/image-response-details";
import { ModelPicker } from "@/components/model-picker";
import { PromptSelectDialog } from "@/components/prompts/prompt-select-dialog";
import { AssetPickerModal, type InsertAssetPayload } from "@/components/canvas/asset-picker-modal";
import { canvasThemes } from "@/lib/canvas-theme";
import { imageReferenceLabel } from "@/lib/image-reference-prompt";
import { getImageModelCapabilities, getNativeImageCapabilities, resolveNativeImageCount, resolveNativeImageOptions, resolveNativeImageOutputFormat } from "@/lib/image-model-capabilities";
import { defaultConfig, modelOptionLabel, resolveImageProvider, resolveSameChannelMainModel, snapshotImageNativeOptions, useConfigStore, useEffectiveConfig, type AiConfig } from "@/stores/use-config-store";
import { useThemeStore } from "@/stores/use-theme-store";
import { nanoid } from "nanoid";
import { formatBytes, formatDuration } from "@/lib/image-utils";
import { runImageJob } from "@/services/api/image-jobs";
import { cleanupUnusedImages, collectImageStorageKeys, resolveImageUrl, uploadImage } from "@/services/image-storage";
import { useAssetStore } from "@/stores/use-asset-store";
import { useCanvasStore } from "@/stores/canvas/use-canvas-store";
import { useWorkbenchAgentStore } from "@/stores/use-workbench-agent-store";
import type { ImageGenerationResult, ReferenceImage } from "@/types/image";
import type { ImageConversation, ImageJobResult, ImageOutputError, ImageProvider } from "@/types/image-generation";
import i18n from "@/i18n";
import { buildWorkbenchRetry, failWorkbenchResults, finishWorkbenchResults, mergeWorkbenchOutputOrder, mergeWorkbenchRetry, requiresWholeJobRetry, upsertWorkbenchResult, workbenchRequestError, type GeneratedImage, type GenerationLogConfig, type GenerationResult, type ImageJobSnapshot, type WorkbenchRetry } from "./image-workbench-results";

type GenerationLog = {
    id: string;
    createdAt: number;
    title: string;
    prompt: string;
    time: string;
    model: string;
    config: GenerationLogConfig;
    references: ReferenceImage[];
    durationMs: number;
    successCount: number;
    failCount: number;
    imageCount: number;
    size: string;
    quality: string;
    status: "success" | "failed";
    images: GeneratedImage[];
    thumbnails: string[];
    texts: string[];
    errors: ImageOutputError[];
    conversation?: ImageConversation;
    providerMetadata?: Record<string, unknown>;
    outputOrder?: ImageJobResult["outputOrder"];
    provider?: ImageProvider;
};

type UpdateAiConfig = <K extends keyof AiConfig>(key: K, value: AiConfig[K]) => void;

const RESULT_ACTION_BUTTON_CLASS = "min-w-0 px-1.5 [&_.ant-btn-icon]:shrink-0 [&>span:last-child]:min-w-0 [&>span:last-child]:truncate";
const CHECKERBOARD_STYLE = { backgroundColor: "#f5f5f4", backgroundImage: "linear-gradient(45deg, rgba(120,113,108,.13) 25%, transparent 25%), linear-gradient(-45deg, rgba(120,113,108,.13) 25%, transparent 25%), linear-gradient(45deg, transparent 75%, rgba(120,113,108,.13) 75%), linear-gradient(-45deg, transparent 75%, rgba(120,113,108,.13) 75%)", backgroundPosition: "0 0, 0 8px, 8px -8px, -8px 0", backgroundSize: "16px 16px" } as const;
const logStore = localforage.createInstance({ name: "infinite-canvas", storeName: "image_generation_logs" });

export default function ImagePage() {
    const { message } = App.useApp();
    const { t } = useTranslation();
    const fileInputRef = useRef<HTMLInputElement>(null);
    const dragDepthRef = useRef(0);
    const effectiveConfig = useEffectiveConfig();
    const updateConfig = useConfigStore((state) => state.updateConfig);
    const isAiConfigReady = useConfigStore((state) => state.isAiConfigReady);
    const openConfigDialog = useConfigStore((state) => state.openConfigDialog);
    const addAsset = useAssetStore((state) => state.addAsset);
    const [prompt, setPrompt] = useState("");
    const [references, setReferences] = useState<ReferenceImage[]>([]);
    const [results, setResults] = useState<GenerationResult[]>([]);
    const [logs, setLogs] = useState<GenerationLog[]>([]);
    const [running, setRunning] = useState(false);
    const [logsOpen, setLogsOpen] = useState(false);
    const [settingsOpen, setSettingsOpen] = useState(false);
    const [promptDialogOpen, setPromptDialogOpen] = useState(false);
    const [assetPickerOpen, setAssetPickerOpen] = useState(false);
    const [startedAt, setStartedAt] = useState(0);
    const [elapsedMs, setElapsedMs] = useState(0);
    const [selectedLogIds, setSelectedLogIds] = useState<string[]>([]);
    const [previewLog, setPreviewLog] = useState<GenerationLog | null>(null);
    const [deleteConfirmOpen, setDeleteConfirmOpen] = useState(false);
    const [isReferenceDragActive, setIsReferenceDragActive] = useState(false);
    const [autoRunToken, setAutoRunToken] = useState(0);
    const [previewImages, setPreviewImages] = useState<ImageGenerationResult[]>([]);
    const [jobTexts, setJobTexts] = useState<string[]>([]);
    const [jobErrors, setJobErrors] = useState<ImageOutputError[]>([]);
    const [providerMetadata, setProviderMetadata] = useState<Record<string, unknown> | undefined>(undefined);
    const [conversation, setConversation] = useState<ImageConversation | undefined>(undefined);
    const [outputOrder, setOutputOrder] = useState<NonNullable<ImageJobResult["outputOrder"]>>([]);
    const imageCommand = useWorkbenchAgentStore((state) => state.imageCommand);
    const clearImageCommand = useWorkbenchAgentStore((state) => state.clearImageCommand);
    const updateAgentTask = useWorkbenchAgentStore((state) => state.updateTask);
    const processedCommandRef = useRef(0);
    const agentTaskIdRef = useRef<string | undefined>(undefined);
    const agentImageConfigRef = useRef<Partial<AiConfig> | undefined>(undefined);
    const activeJobRef = useRef<string | null>(null);

    const model = effectiveConfig.imageModel || effectiveConfig.model;
    const modelCapabilities = getImageModelCapabilities(model);
    const imageProvider = resolveImageProvider(effectiveConfig, model);
    const maxReferences = getNativeImageCapabilities(imageProvider, model).maxReferences;
    const canGenerate = Boolean(prompt.trim());
    const generationCount = resolveNativeImageCount(imageProvider, model, effectiveConfig.imageNative, Number(effectiveConfig.count), references.length);
    const wholeJobRetryIndex = jobErrors.some((error) => error.index < 0) && !results.some((item) => item.status === "failed") ? results.findIndex((item) => item.retry && requiresWholeJobRetry(item.retry, resolveImageProvider(item.retry.config, item.retry.config.model))) : -1;

    useEffect(() => {
        if (!running || !startedAt) return;
        const timer = window.setInterval(() => setElapsedMs(performance.now() - startedAt), 1000);
        return () => window.clearInterval(timer);
    }, [running, startedAt]);

    useEffect(() => {
        void refreshLogs();
    }, []);

    const addReferences = async (files?: FileList | null) => {
        const imageFiles = Array.from(files || []).filter((file) => file.type.startsWith("image/")).slice(0, Math.max(0, maxReferences - references.length));
        if (!imageFiles.length) return message.warning(t("nativeImage.referenceLimit", { count: maxReferences }));
        const nextReferences = await Promise.all(
            imageFiles.map(async (file) => {
                const image = await uploadImage(file);
                return { id: nanoid(), name: file.name, type: image.mimeType, dataUrl: image.url, storageKey: image.storageKey };
            }),
        );
        setReferences((value) => [...value, ...nextReferences]);
    };

    const addReferencesFromClipboard = async () => {
        try {
            const items = await navigator.clipboard.read();
            const blobs = await Promise.all(items.flatMap((item) => item.types.filter((type) => type.startsWith("image/")).map((type) => item.getType(type))));
            if (!blobs.length) {
                message.error(t("imageWorkbench.clipboardEmpty"));
                return;
            }
            const acceptedBlobs = blobs.slice(0, Math.max(0, maxReferences - references.length));
            if (!acceptedBlobs.length) return message.warning(t("nativeImage.referenceLimit", { count: maxReferences }));
            const nextReferences = await Promise.all(
                acceptedBlobs.map(async (blob, index) => {
                    const image = await uploadImage(blob);
                    return { id: nanoid(), name: `clipboard-${index + 1}.png`, type: image.mimeType, dataUrl: image.url, storageKey: image.storageKey };
                }),
            );
            setReferences((value) => [...value, ...nextReferences]);
            message.success(t("imageWorkbench.clipboardAdded", { count: nextReferences.length }));
        } catch {
            message.error(t("imageWorkbench.clipboardEmpty"));
        }
    };

    const generate = async (configOverride?: Partial<AiConfig>) => {
        if (activeJobRef.current) return;
        const agentTaskId = agentTaskIdRef.current;
        agentTaskIdRef.current = undefined;
        const requestedModel = configOverride?.model || model;
        const text = prompt.trim();
        if (!text) {
            message.error(t("imageWorkbench.promptRequired"));
            if (agentTaskId) updateAgentTask(agentTaskId, { status: "failed", error: t("imageWorkbench.promptRequired") });
            return;
        }
        if (!isAiConfigReady({ ...effectiveConfig, ...configOverride, model: requestedModel }, requestedModel)) {
            message.warning(t("workbench.configFirst"));
            openConfigDialog(true);
            if (agentTaskId) updateAgentTask(agentTaskId, { status: "failed", error: t("imageWorkbench.configIncomplete") });
            return;
        }

        const snapshot = buildRequestSnapshot(configOverride);
        if (!snapshot) {
            if (agentTaskId) updateAgentTask(agentTaskId, { status: "failed", error: t("imageWorkbench.invalidParams") });
            return;
        }
        await executeJob(snapshot, agentTaskId);
    };

    const executeJob = async (snapshot: ImageJobSnapshot, agentTaskId?: string, retry?: WorkbenchRetry) => {
        if (activeJobRef.current) return;
        const jobId = nanoid();
        activeJobRef.current = jobId;
        const started = performance.now();
        const expectedCount = Number(snapshot.config.count) || 1;
        const previousResults = retry ? results : [];
        const previousTexts = retry ? jobTexts : [];
        const previousImages = previousResults.flatMap((item) => item.image ? [item.image] : []);
        const previousOrder = retry ? outputOrder : [];
        let attemptResults: GenerationResult[] = Array.from({ length: expectedCount }, (_, outputIndex) => ({ id: `${jobId}:${outputIndex}`, status: "pending", outputIndex, retry: snapshot }));
        let attemptTexts: string[] = [];
        const visibleResults = () => mergeWorkbenchRetry(previousResults, attemptResults, retry);
        const visibleErrors = (items: GenerationResult[]) => items.flatMap((item, index) => item.status === "failed" ? [{ ...item.outputError, index, message: item.error || t("workbench.generationFailed") }] : []);
        const publishResults = () => {
            const items = visibleResults();
            setResults(items);
            setJobErrors(visibleErrors(items));
        };
        const finish = (result: ImageJobResult, requestError?: ImageOutputError) => {
            const durationMs = performance.now() - started;
            const items = visibleResults();
            const images = items.flatMap((item) => item.status === "success" && item.image ? [item.image] : []);
            const errors = visibleErrors(items);
            if (requestError && !errors.some((error) => error.message === requestError.message)) errors.push(requestError);
            const mergedResult = {
                ...result,
                images,
                texts: [...previousTexts, ...attemptTexts],
                errors,
                providerMetadata: result.providerMetadata || (retry ? providerMetadata : undefined),
                conversation: result.conversation || snapshot.conversation,
                outputOrder: mergeWorkbenchOutputOrder(previousOrder, previousImages, previousTexts.length, result, images),
            };
            setResults(items);
            setPreviewImages([]);
            setJobTexts(mergedResult.texts);
            setJobErrors(mergedResult.errors);
            setProviderMetadata(mergedResult.providerMetadata);
            setOutputOrder(mergedResult.outputOrder);
            setConversation(snapshot.conversation ? mergedResult.conversation : undefined);
            const successCount = attemptResults.filter((item) => item.status === "success").length;
            const failCount = attemptResults.filter((item) => item.status === "failed").length;
            const firstError = requestError?.message || result.errors[0]?.message;
            if (agentTaskId) updateAgentTask(agentTaskId, { status: successCount ? "succeeded" : "failed", successCount, failCount, error: successCount ? undefined : firstError || t("workbench.generationFailed") });
            saveLog(buildLog({ prompt: snapshot.text, model: snapshot.config.model, provider: resolveImageProvider(snapshot.config, snapshot.config.model), config: logConfigFromAiConfig(snapshot.config), references: snapshot.references, durationMs, successCount: images.length, failCount: items.filter((item) => item.status === "failed").length, status: images.length ? "success" : "failed", images, result: mergedResult }));
            if (successCount && requestError) message.warning(requestError.message);
            else if (successCount && failCount) message.warning(t("imageWorkbench.partialGenerated"));
            else if (successCount) message.success(t("imageWorkbench.generated"));
            else message.error(firstError || t("workbench.generationFailed"));
        };
        setElapsedMs(0);
        setStartedAt(started);
        setRunning(true);
        setPreviewLog(null);
        setPreviewImages([]);
        setJobTexts(previousTexts);
        if (!retry) setProviderMetadata(undefined);
        setOutputOrder(previousOrder);
        publishResults();
        if (agentTaskId) updateAgentTask(agentTaskId, { status: "running", error: undefined });
        try {
            const result = await runImageJob(snapshot.config, snapshot.text, snapshot.references, {
                conversation: snapshot.conversation,
                onEvent: (event) => {
                    if (activeJobRef.current !== jobId) return;
                    if (event.type === "preview" && event.image) setPreviewImages((current) => upsertPreview(current, event.index, event.image!));
                    if (event.type === "output" && event.image) {
                        const image = toGeneratedImage(event.image, performance.now() - started, snapshot);
                        attemptResults = upsertWorkbenchResult(attemptResults, event.index, { id: image.id, status: "success", image, retry: snapshot });
                        publishResults();
                    }
                    if (event.type === "output-error" && event.error) {
                        attemptResults = upsertWorkbenchResult(attemptResults, event.index ?? event.error.index, { id: `${jobId}:error:${event.error.index}`, status: "failed", error: event.error.message, outputError: event.error, retry: snapshot });
                        publishResults();
                    }
                    if (event.type === "text" && event.text) {
                        attemptTexts = [...attemptTexts, event.text];
                        setJobTexts([...previousTexts, ...attemptTexts]);
                    }
                },
            });
            attemptResults = finishWorkbenchResults(attemptResults, result.images.map((image) => toGeneratedImage(image, performance.now() - started, snapshot)), result.errors, snapshot, jobId);
            attemptTexts = result.texts.length ? result.texts : attemptTexts;
            finish(result);
        } catch (error) {
            const requestError = workbenchRequestError(error, t("workbench.generationFailed"));
            attemptResults = failWorkbenchResults(attemptResults, requestError, snapshot);
            finish({ images: attemptResults.flatMap((item) => item.image ? [item.image] : []), texts: attemptTexts, errors: visibleErrors(attemptResults) }, requestError);
        } finally {
            activeJobRef.current = null;
            setRunning(false);
        }
    };

    // Handle image-generation commands from the Agent panel by setting the prompt and optionally starting generation.
    useEffect(() => {
        if (!imageCommand || imageCommand.nonce === processedCommandRef.current) return;
        processedCommandRef.current = imageCommand.nonce;
        clearImageCommand();
        if (imageCommand.run && activeJobRef.current) {
            if (imageCommand.taskId) updateAgentTask(imageCommand.taskId, { status: "failed", error: t("imageWorkbench.busy") });
            return;
        }
        if (typeof imageCommand.prompt === "string") setPrompt(imageCommand.prompt);
        if (imageCommand.imageConfigSnapshot) agentImageConfigRef.current = { ...imageCommand.imageConfigSnapshot, imageModel: imageCommand.imageConfigSnapshot.model };
        if (imageCommand.run) {
            agentTaskIdRef.current = imageCommand.taskId;
            setAutoRunToken((value) => value + 1);
        }
    }, [imageCommand, clearImageCommand, running, updateAgentTask]);

    useEffect(() => {
        if (!autoRunToken) return;
        const snapshot = agentImageConfigRef.current;
        agentImageConfigRef.current = undefined;
        void generate(snapshot);
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [autoRunToken]);

    const downloadImage = (image: GeneratedImage, index: number) => {
        saveAs(image.dataUrl, `image-${index + 1}.${imageExtension(image.mimeType, image.response?.outputFormat)}`);
    };

    const continueFromResult = (image: GeneratedImage, index: number) => {
        if (activeJobRef.current) return;
        const reference = { id: nanoid(), name: `result-${index + 1}.${imageExtension(image.mimeType, image.response?.outputFormat)}`, type: image.mimeType || "image/png", dataUrl: image.dataUrl, storageKey: image.storageKey };
        setReferences([reference]);
        setConversation(image.response?.conversation);
        setPrompt("");
        setPreviewLog(null);
        message.success(t("imageWorkbench.addedReference"));
    };

    const saveResultToAssets = (image: GeneratedImage, index: number) => {
        addAsset({
            kind: "image",
            title: t("imageWorkbench.resultTitle", { count: index + 1 }),
            coverUrl: image.dataUrl,
            tags: [],
            source: t("imageWorkbench.source"),
            data: { dataUrl: image.dataUrl, storageKey: image.storageKey, width: image.width, height: image.height, bytes: image.bytes, mimeType: image.mimeType || "" },
            metadata: { source: "image-page", prompt: image.requestPrompt || prompt, ...(image.requestConfig ? { generation: image.requestConfig } : {}), ...(image.response ? { imageResponse: image.response } : {}), ...(image.role ? { role: image.role } : {}), ...(image.layer ? { layer: image.layer } : {}) },
        });
        message.success(t("common.addedToAssets"));
    };

    const insertPickedAsset = async (payload: InsertAssetPayload) => {
        if (payload.kind === "text") {
            setPrompt(payload.content);
        } else if (payload.kind === "image") {
            if (references.length >= maxReferences) {
                message.warning(t("nativeImage.referenceLimit", { count: maxReferences }));
                setAssetPickerOpen(false);
                return;
            }
            const stored = await uploadImage(payload.dataUrl);
            setReferences((value) => [...value, { id: nanoid(), name: payload.title, type: stored.mimeType, dataUrl: stored.url, storageKey: stored.storageKey }]);
        } else {
            message.warning(t("imageWorkbench.unsupportedAsset"));
        }
        setAssetPickerOpen(false);
    };

    const createSession = () => {
        if (activeJobRef.current) return;
        setPrompt("");
        setReferences([]);
        setResults([]);
        setPreviewImages([]);
        setJobTexts([]);
        setJobErrors([]);
        setProviderMetadata(undefined);
        setConversation(undefined);
        setOutputOrder([]);
        setElapsedMs(0);
        setStartedAt(0);
        setSelectedLogIds([]);
        setPreviewLog(null);
    };

    const deleteSelectedLogs = () => {
        if (activeJobRef.current) return;
        const imageKeys = collectImageStorageKeys(logs.filter((log) => selectedLogIds.includes(log.id)));
        const retainedResults = previewLog && selectedLogIds.includes(previewLog.id) ? [] : results;
        void Promise.all(selectedLogIds.map((id) => logStore.removeItem(id)))
            .then(() => cleanupUnusedImages({ assets: useAssetStore.getState().assets, projects: useCanvasStore.getState().projects, references, conversation, results: retainedResults }, imageKeys))
            .then(refreshLogs)
            .catch((error) => message.error(error instanceof Error ? error.message : String(error)));
        if (previewLog && selectedLogIds.includes(previewLog.id)) {
            setPreviewLog(null);
            setResults([]);
        }
        setSelectedLogIds([]);
        setDeleteConfirmOpen(false);
    };

    const saveLog = (log: GenerationLog) => {
        void logStore.setItem(log.id, serializeLog(log)).then(refreshLogs).catch((error) => message.error(error instanceof Error ? error.message : String(error)));
    };

    const refreshLogs = async () => setLogs(await readStoredLogs());

    const previewGenerationLog = async (log: GenerationLog) => {
        if (activeJobRef.current) return;
        setPreviewLog(log);
        setLogsOpen(false);
        setResults(log.images.map((image) => ({ id: image.id, status: "success", image })));
        setJobTexts(log.texts || []);
        setJobErrors(log.errors || []);
        setProviderMetadata(log.providerMetadata);
        setOutputOrder(log.outputOrder || []);
    };

    const reuseGenerationLog = (log: GenerationLog) => {
        if (activeJobRef.current) return;
        setPreviewLog(log);
        setLogsOpen(false);
        setPrompt(log.prompt);
        setReferences(log.references || []);
        if (log.config.imageModel || log.model) updateConfig("imageModel", log.config.imageModel || log.model);
        if (log.config.quality) updateConfig("quality", log.config.quality);
        if (log.config.size) updateConfig("size", log.config.size);
        if (log.config.count) updateConfig("count", log.config.count);
        if (log.config.background) updateConfig("background", log.config.background);
        if (log.config.outputFormat) updateConfig("outputFormat", log.config.outputFormat);
        if (typeof log.config.outputCompression === "number") updateConfig("outputCompression", log.config.outputCompression);
        if (log.config.moderation) updateConfig("moderation", log.config.moderation);
        if (log.config.imageNative) updateConfig("imageNative", structuredClone(log.config.imageNative));
        setConversation(log.conversation);
        setJobTexts(log.texts || []);
        setJobErrors(log.errors || []);
        setProviderMetadata(log.providerMetadata);
        setOutputOrder(log.outputOrder || []);
        setResults(log.images.map((image) => ({ id: image.id, status: "success", image })));
    };

    const buildRequestSnapshot = (configOverride?: Partial<AiConfig>): ImageJobSnapshot | null => {
        const text = prompt.trim();
        if (!text) {
            message.error(t("imageWorkbench.promptRequired"));
            return null;
        }
        const requestConfig = { ...effectiveConfig, ...configOverride, model: configOverride?.model || model };
        const optionError = validateImageUiOptions(requestConfig, requestConfig.model);
        if (optionError) {
            message.error(optionError);
            return null;
        }
        if (!isAiConfigReady(requestConfig, requestConfig.model)) {
            message.warning(t("workbench.configFirst"));
            openConfigDialog(true);
            return null;
        }
        const provider = resolveImageProvider(requestConfig, requestConfig.model);
        const capabilities = getNativeImageCapabilities(provider, requestConfig.model);
        if (references.length > capabilities.maxReferences) {
            message.error(t("nativeImage.referenceLimit", { count: capabilities.maxReferences }));
            return null;
        }
        const imageNative = snapshotImageNativeOptions(requestConfig, requestConfig.model, resolveNativeImageOptions(provider, requestConfig.model, requestConfig.imageNative));
        const count = resolveNativeImageCount(provider, requestConfig.model, imageNative, Number(requestConfig.count), references.length);
        const outputFormat = resolveNativeImageOutputFormat(provider, requestConfig.model, imageNative, requestConfig.outputFormat);
        if (provider === "openai" && imageNative.openai?.api === "responses") {
            imageNative.openai.mainModel = resolveSameChannelMainModel(requestConfig, requestConfig.model, imageNative.openai.mainModel);
            if (!imageNative.openai.mainModel) {
                message.error(t("nativeImage.openai.mainModelMissing"));
                return null;
            }
        }
        return { text, config: { ...requestConfig, count: String(count), outputFormat, imageNative }, references: references.map((item) => ({ ...item })), conversation };
    };

    const retryResult = async (index: number) => {
        if (activeJobRef.current) return;
        const snapshot = results[index]?.retry;
        if (!snapshot) return;
        const request = buildWorkbenchRetry(results, index, resolveImageProvider(snapshot.config, snapshot.config.model));
        if (request) await executeJob(request.snapshot, undefined, request.retry);
    };

    return (
        <div className="flex h-full flex-col overflow-hidden bg-stone-50 text-stone-900 dark:bg-stone-950 dark:text-stone-100">
            <main className="grid min-h-0 flex-1 grid-cols-1 gap-3 overflow-y-auto p-3 lg:grid-cols-[300px_minmax(0,1fr)] lg:overflow-hidden xl:grid-cols-[320px_minmax(0,1fr)]">
                <aside className="thin-scrollbar hidden min-h-0 overflow-y-auto liquid-glass liquid-glass-regular liquid-glass-static rounded-2xl border p-4 lg:block">
                    <LogPanel
                        busy={running}
                        logs={logs}
                        selectedLogIds={selectedLogIds}
                        activeLogId={previewLog?.id}
                        onSelectedLogIdsChange={setSelectedLogIds}
                        onCreateSession={createSession}
                        onDeleteSelected={() => setDeleteConfirmOpen(true)}
                        onPreviewLog={(log) => void previewGenerationLog(log)}
                        onReuseLog={reuseGenerationLog}
                    />
                </aside>

                <section className="grid gap-3 lg:min-h-0 lg:overflow-hidden xl:grid-cols-[420px_minmax(0,1fr)]">
                    <div className="thin-scrollbar flex flex-col liquid-glass liquid-glass-regular liquid-glass-static rounded-2xl border p-4 lg:min-h-0 lg:overflow-y-auto">
                        <div>
                            <div className="flex items-start justify-between gap-3">
                                <div className="min-w-0">
                                    <h1 className="text-2xl font-semibold text-stone-950 dark:text-stone-100">{t("imageWorkbench.title")}</h1>
                                    <div className="mt-2 flex flex-wrap items-center gap-2 text-xs text-stone-500 dark:text-stone-400">
                                        <span>{t(references.length ? "imageWorkbench.editMode" : "imageWorkbench.generateMode")}</span>
                                        {conversation ? <Tag className="m-0">{t("nativeImage.branchActive")}</Tag> : null}
                                        {modelCapabilities.labelKey ? <Tag className="m-0">{t(modelCapabilities.labelKey)}</Tag> : <Tag className="m-0">{t(`nativeImage.providers.${imageProvider}`)}</Tag>}
                                    </div>
                                </div>
                                <div className="flex shrink-0 gap-2 lg:hidden">
                                    <Button icon={<History className="size-4" />} onClick={() => setLogsOpen(true)}>
                                        {t("workbench.logs")}
                                    </Button>
                                    <Button icon={<SlidersHorizontal className="size-4" />} onClick={() => setSettingsOpen(true)}>
                                        {t("workbench.settings")}
                                    </Button>
                                </div>
                            </div>
                        </div>

                        <div className="mt-6 space-y-5">
                            <div>
                                <div className="mb-2 flex items-center justify-between gap-3">
                                    <span className="text-base font-semibold">{t("workbench.prompt")}</span>
                                    <div className="flex gap-2">
                                        <Button size="small" icon={<BookOpen className="size-3.5" />} onClick={() => setPromptDialogOpen(true)}>
                                            {t("workbench.viewPrompts")}
                                        </Button>
                                        <Button size="small" icon={<FolderPlus className="size-3.5" />} onClick={() => setAssetPickerOpen(true)}>
                                            {t("workbench.viewAssets")}
                                        </Button>
                                    </div>
                                </div>
                                <Input.TextArea value={prompt} onChange={(event) => setPrompt(event.target.value)} rows={7} placeholder={t("imageWorkbench.promptPlaceholder")} />
                            </div>

                            <div className="min-w-0">
                                <div className="mb-2 flex items-center justify-between gap-3">
                                    <span className="text-base font-semibold">{t("imageWorkbench.references")}</span>
                                    <div className="flex gap-2">
                                        <Button size="small" icon={<ClipboardPaste className="size-3.5" />} onClick={() => void addReferencesFromClipboard()}>
                                            {t("workbench.clipboard")}
                                        </Button>
                                        <Button size="small" icon={<Upload className="size-3.5" />} onClick={() => fileInputRef.current?.click()}>
                                            {t("workbench.upload")}
                                        </Button>
                                    </div>
                                </div>
                                <div
                                    className={`hover-scrollbar hover-scrollbar-hint relative flex min-h-24 w-full min-w-0 max-w-full gap-2 overflow-x-scroll overflow-y-hidden rounded-lg border border-dashed p-2 pb-3 overscroll-x-contain transition-colors ${isReferenceDragActive ? "border-stone-900 bg-stone-100/80 dark:border-stone-100 dark:bg-stone-900/80" : "border-stone-300 dark:border-stone-700"}`}
                                    onDragEnter={(event) => {
                                        event.preventDefault();
                                        dragDepthRef.current += 1;
                                        if (event.dataTransfer.types.includes("Files")) setIsReferenceDragActive(true);
                                    }}
                                    onDragOver={(event) => {
                                        event.preventDefault();
                                        event.dataTransfer.dropEffect = "copy";
                                    }}
                                    onDragLeave={(event) => {
                                        event.preventDefault();
                                        dragDepthRef.current = Math.max(0, dragDepthRef.current - 1);
                                        if (!dragDepthRef.current) setIsReferenceDragActive(false);
                                    }}
                                    onDrop={(event) => {
                                        event.preventDefault();
                                        dragDepthRef.current = 0;
                                        setIsReferenceDragActive(false);
                                        void addReferences(event.dataTransfer.files);
                                    }}
                                    onWheel={(event) => {
                                        if (event.currentTarget.scrollWidth <= event.currentTarget.clientWidth) return;
                                        event.preventDefault();
                                        event.currentTarget.scrollLeft += event.deltaY;
                                    }}
                                >
                                    {references.map((item, index) => (
                                        <div key={item.id} className="group relative size-20 shrink-0 overflow-hidden rounded-md border border-stone-200 dark:border-stone-800">
                                            <img src={item.dataUrl} alt={item.name} className="size-full object-cover" />
                                            <span className="absolute left-1 top-1 rounded bg-black/60 px-1.5 py-0.5 text-[10px] font-medium text-white">{imageReferenceLabel(index)}</span>
                                            <ReferenceOrderButtons index={index} total={references.length} onMove={(offset) => setReferences((value) => moveListItem(value, index, offset))} />
                                            <button
                                                type="button"
                                                className="absolute right-1 top-1 hidden size-6 items-center justify-center rounded bg-black/60 text-white group-hover:flex"
                                                onClick={() => setReferences((value) => value.filter((ref) => ref.id !== item.id))}
                                                aria-label={t("imageWorkbench.removeReference")}
                                            >
                                                <Trash2 className="size-3.5" />
                                            </button>
                                        </div>
                                    ))}
                                    {!references.length ? <div className="flex min-w-full items-center justify-center text-sm text-stone-500">{isReferenceDragActive ? t("imageWorkbench.dropReferences") : t("imageWorkbench.noReferences")}</div> : null}
                                </div>
                            </div>

                            <div className="flex items-center justify-between gap-3 rounded-lg border border-stone-200 bg-stone-50 px-3 py-2 text-sm dark:border-stone-800 dark:bg-stone-900 sm:hidden">
                                <span className="min-w-0 text-stone-500 dark:text-stone-400">
                                    <span className="block truncate">{modelOptionLabel(effectiveConfig, model)} · {t(references.length ? "imageWorkbench.editMode" : "imageWorkbench.generateMode")}</span>
                                    <span className="mt-0.5 block truncate text-xs">{imageSettingsSummary(effectiveConfig, model, generationCount, t)}</span>
                                </span>
                                <Button size="small" type="text" icon={<SlidersHorizontal className="size-4" />} onClick={() => setSettingsOpen(true)}>
                                    {t("workbench.adjust")}
                                </Button>
                            </div>

                            <div className="hidden gap-4 sm:grid sm:grid-cols-2">
                                <GenerationSettings config={effectiveConfig} model={model} referenceCount={references.length} updateConfig={updateConfig} openConfigDialog={openConfigDialog} />
                            </div>
                        </div>

                        <div className="mt-auto pt-6">
                            <Button type="primary" size="large" block icon={<Sparkles className="size-4" />} loading={running} disabled={!canGenerate || running} onClick={() => void generate()}>
                                {t("workbench.generate")}
                            </Button>
                        </div>
                    </div>

                    <div className="thin-scrollbar liquid-glass liquid-glass-regular liquid-glass-static rounded-2xl border p-4 lg:min-h-0 lg:overflow-y-auto lg:p-5">
                        <div className="mb-4 flex items-center justify-between gap-3">
                            <div>
                                <div className="flex flex-wrap items-center gap-2">
                                    <h2 className="text-xl font-semibold">{t("workbench.results")}</h2>
                                    {previewLog ? <Tag className="m-0">{t("imageWorkbench.historyPreview")}</Tag> : null}
                                    {jobErrors.length ? <Tag className="m-0" color="red">{t("workbench.failCount", { count: jobErrors.length })}</Tag> : null}
                                </div>
                            </div>
                            <div className="flex items-center gap-2">
                                {previewLog ? <Button size="small" type="text" disabled={running} icon={<RotateCcw className="size-3.5" />} onClick={() => reuseGenerationLog(previewLog)}>{t("imageWorkbench.reuse")}</Button> : null}
                                {running ? <Tag className="m-0 px-2 py-1">{t("workbench.waiting", { time: formatDuration(elapsedMs) })}</Tag> : null}
                            </div>
                        </div>
                        {previewImages.length ? (
                            <div className="mb-4">
                                <div className="mb-2 text-xs font-medium text-stone-500 dark:text-stone-400">{t("nativeImage.preview")}</div>
                                <div className="grid gap-3 sm:grid-cols-2 2xl:grid-cols-3">
                                    {previewImages.map((image) => <PreviewImageCard key={image.id} image={image} />)}
                                </div>
                            </div>
                        ) : null}
                        {jobTexts.length ? (
                            <div className="mb-4 rounded-lg border border-stone-200 p-3 text-sm leading-6 dark:border-stone-800">
                                <div className="mb-1 text-xs font-medium text-stone-500 dark:text-stone-400">{t("nativeImage.returnedText")}</div>
                                {jobTexts.map((text, index) => <p key={`${index}-${text.slice(0, 20)}`} className="whitespace-pre-wrap">{text}</p>)}
                            </div>
                        ) : null}
                        {outputOrder.length ? <OutputOrderDetails order={outputOrder} /> : null}
                        {providerMetadata ? <div className="mb-4"><ImageProviderMetadata metadata={providerMetadata} /></div> : null}
                        {jobErrors.length ? <div className="mb-4 rounded-lg border border-red-200 bg-red-50 p-3 text-xs text-red-700 dark:border-red-950 dark:bg-red-950/20 dark:text-red-300"><ul className="list-disc space-y-1 pl-4">{jobErrors.map((error) => <li key={`${error.index}-${error.code || error.message}`}>{error.message}</li>)}</ul>{wholeJobRetryIndex >= 0 ? <Button size="small" danger className="mt-2" disabled={running} title={t("imageWorkbench.retryGroupHint")} onClick={() => void retryResult(wholeJobRetryIndex)}>{t("imageWorkbench.retryGroup")}</Button> : null}</div> : null}
                        {results.length ? (
                            <div className="grid gap-4 sm:grid-cols-2 2xl:grid-cols-3">
                                {results.map((result, index) =>
                                    result.status === "success" && result.image ? (
                                        <ResultImageCard key={result.id} image={result.image} index={index} model={result.image.requestConfig?.model || previewLog?.model || model} busy={running} onContinue={continueFromResult} onDownload={downloadImage} onSaveAsset={saveResultToAssets} />
                                    ) : result.status === "failed" ? (
                                        <FailedImageCard key={result.id} error={result.error || t("workbench.generationFailed")} disabled={running || !result.retry} wholeJob={Boolean(result.retry && requiresWholeJobRetry(result.retry, resolveImageProvider(result.retry.config, result.retry.config.model)))} onRetry={() => retryResult(index)} />
                                    ) : (
                                        <PendingImageCard key={result.id} />
                                    ),
                                )}
                            </div>
                        ) : jobTexts.length || jobErrors.length ? null : (
                            <div className="flex min-h-[320px] flex-col items-center justify-center rounded-lg border border-dashed border-stone-300 text-center dark:border-stone-700 lg:min-h-[560px]">
                                <ImagePlus className="mb-4 size-11 text-stone-400" />
                                <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t("imageWorkbench.empty")} />
                            </div>
                        )}
                    </div>
                </section>
            </main>
            <input
                ref={fileInputRef}
                type="file"
                accept="image/*"
                multiple
                className="hidden"
                onChange={(event) => {
                    void addReferences(event.target.files);
                    event.target.value = "";
                }}
            />
            <Drawer title={t("workbench.logs")} placement="bottom" size="large" open={logsOpen} onClose={() => setLogsOpen(false)}>
                <LogPanel
                    busy={running}
                    logs={logs}
                    selectedLogIds={selectedLogIds}
                    activeLogId={previewLog?.id}
                    onSelectedLogIdsChange={setSelectedLogIds}
                    onCreateSession={createSession}
                    onDeleteSelected={() => setDeleteConfirmOpen(true)}
                    onPreviewLog={(log) => void previewGenerationLog(log)}
                    onReuseLog={reuseGenerationLog}
                />
            </Drawer>
            <Drawer title={t("workbench.settings")} placement="bottom" size="82vh" open={settingsOpen} onClose={() => setSettingsOpen(false)}>
                <div className="grid grid-cols-2 gap-3 pb-8">
                    <GenerationSettings config={effectiveConfig} model={model} referenceCount={references.length} updateConfig={updateConfig} openConfigDialog={openConfigDialog} />
                </div>
            </Drawer>
            <PromptSelectDialog open={promptDialogOpen} onOpenChange={setPromptDialogOpen} onSelect={setPrompt} />
            <AssetPickerModal open={assetPickerOpen} defaultTab="my-assets" onInsert={(payload) => void insertPickedAsset(payload)} onClose={() => setAssetPickerOpen(false)} />
            <Modal title={t("workbench.deleteLogs")} open={deleteConfirmOpen} onCancel={() => setDeleteConfirmOpen(false)} onOk={deleteSelectedLogs} okText={t("common.delete")} okButtonProps={{ danger: true }} cancelText={t("common.cancel")}>
                {t("workbench.deleteLogsConfirm", { count: selectedLogIds.length })}
            </Modal>
        </div>
    );
}

function GenerationSettings({ config, model, referenceCount, updateConfig, openConfigDialog }: { config: AiConfig; model: string; referenceCount: number; updateConfig: UpdateAiConfig; openConfigDialog: (shouldPromptContinue?: boolean) => void }) {
    const theme = canvasThemes[useThemeStore((state) => state.theme)];
    const { t } = useTranslation();

    return (
        <>
            <label className="col-span-2 block min-w-0 sm:col-span-1">
                <span className="mb-1.5 block text-sm font-semibold sm:mb-2 sm:text-base">{t("workbench.model")}</span>
                <ModelPicker config={config} value={model} onChange={(value) => updateConfig("imageModel", value)} capability="image" fullWidth onMissingConfig={() => openConfigDialog(false)} />
            </label>
            <div className="col-span-2">
                <ImageSettingsPanel config={config} model={model} referenceCount={referenceCount} onConfigChange={(key, value) => updateConfig(key, value)} onNativeChange={(imageNative) => updateConfig("imageNative", imageNative)} theme={theme} showTitle={false} className="space-y-4" maxCount={15} />
            </div>
        </>
    );
}

function ResultImageCard({
    image,
    index,
    model,
    busy,
    onContinue,
    onDownload,
    onSaveAsset,
}: {
    image: GeneratedImage;
    index: number;
    model: string;
    busy: boolean;
    onContinue: (image: GeneratedImage, index: number) => void;
    onDownload: (image: GeneratedImage, index: number) => void;
    onSaveAsset: (image: GeneratedImage, index: number) => void;
}) {
    const { t } = useTranslation();
    return (
        <div className="overflow-hidden rounded-lg border border-stone-200 bg-background dark:border-stone-800">
            <div className="grid min-h-44 place-items-center overflow-hidden" style={CHECKERBOARD_STYLE}>
                <Image width="100%" src={image.dataUrl} alt={t("imageWorkbench.resultAlt", { count: index + 1 })} className="block max-h-[520px] object-contain" style={{ aspectRatio: image.width && image.height ? `${image.width} / ${image.height}` : "1 / 1" }} />
            </div>
            <div className="space-y-2 border-t border-stone-200 px-3 py-2.5 dark:border-stone-800">
                {image.role === "layer" || image.layer ? <div className="flex flex-wrap items-center gap-2 text-xs font-medium"><Tag className="m-0">{t("nativeImage.roles.layer")}</Tag><span>{image.layer?.name || t("nativeImage.layerFallback", { index: (image.layer?.zIndex ?? index) + 1 })}</span>{typeof image.layer?.zIndex === "number" ? <span className="text-stone-500">z {image.layer.zIndex}</span> : null}</div> : null}
                <div className="flex min-w-0 flex-wrap gap-x-2 gap-y-1 text-xs text-stone-500 dark:text-stone-400">
                    <span>
                        {image.width}x{image.height}
                    </span>
                    {image.mimeType ? <span>{imageExtension(image.mimeType, image.response?.outputFormat).toUpperCase()}</span> : null}
                    <span>{formatBytes(image.bytes)}</span>
                    <span>{formatDuration(image.durationMs)}</span>
                </div>
                <ImageResponseDetails response={image.response} model={model} role={image.role} layer={image.layer} showProviderMetadata={false} />
                <div className="grid min-w-0 grid-cols-3 gap-2">
                    <Tooltip title={t("common.addToAssets")}>
                        <Button className={RESULT_ACTION_BUTTON_CLASS} size="small" icon={<FolderPlus className="size-3.5" />} onClick={() => void onSaveAsset(image, index)}>
                            {t("common.addToAssets")}
                        </Button>
                    </Tooltip>
                    <Tooltip title={t("imageWorkbench.continueEditing")}>
                        <Button className={RESULT_ACTION_BUTTON_CLASS} size="small" disabled={busy} icon={<PenLine className="size-3.5" />} onClick={() => onContinue(image, index)}>
                            {t("imageWorkbench.continueEditing")}
                        </Button>
                    </Tooltip>
                    <Tooltip title={t("common.download")}>
                        <Button className={RESULT_ACTION_BUTTON_CLASS} size="small" icon={<Download className="size-3.5" />} onClick={() => onDownload(image, index)}>
                            {t("common.download")}
                        </Button>
                    </Tooltip>
                </div>
            </div>
        </div>
    );
}

function PreviewImageCard({ image }: { image: ImageGenerationResult }) {
    const { t } = useTranslation();
    return (
        <div className="overflow-hidden rounded-lg border border-dashed border-stone-300 dark:border-stone-700">
            <div className="grid min-h-40 place-items-center" style={CHECKERBOARD_STYLE}>
                <img src={image.dataUrl} alt={t("nativeImage.preview")} className="block max-h-80 w-full object-contain" />
            </div>
            <div className="px-3 py-2 text-xs text-stone-500 dark:text-stone-400">{t("nativeImage.previewPending")}</div>
        </div>
    );
}

function OutputOrderDetails({ order }: { order: NonNullable<ImageJobResult["outputOrder"]> }) {
    const { t } = useTranslation();
    return (
        <div className="mb-4 rounded-lg border border-stone-200 px-3 py-2 dark:border-stone-800">
            <div className="mb-2 text-xs font-medium text-stone-500 dark:text-stone-400">{t("nativeImage.outputOrder")}</div>
            <div className="flex flex-wrap items-center gap-1.5">
                {order.map((item, sequence) => <Tag key={`${item.kind}-${item.index}-${sequence}`} className="m-0">{t(item.kind === "image" ? "nativeImage.outputImage" : "nativeImage.outputText", { index: item.index + 1 })}</Tag>)}
            </div>
        </div>
    );
}

function PendingImageCard() {
    const { t } = useTranslation();
    return (
        <div className="relative aspect-square overflow-hidden rounded-lg border border-dashed border-stone-300 bg-stone-50 dark:border-stone-700 dark:bg-stone-900">
            <div
                className="absolute inset-0 opacity-60"
                style={{
                    backgroundImage: "radial-gradient(circle, rgba(120,113,108,0.35) 1.4px, transparent 1.6px)",
                    backgroundSize: "16px 16px",
                }}
            />
            <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 text-sm text-stone-500 dark:text-stone-400">
                <LoaderCircle className="size-6 animate-spin" />
                <span>{t("workbench.generating")}</span>
            </div>
        </div>
    );
}

function FailedImageCard({ error, disabled, wholeJob, onRetry }: { error: string; disabled: boolean; wholeJob: boolean; onRetry: () => void }) {
    const { t } = useTranslation();
    return (
        <div className="overflow-hidden rounded-lg border border-red-200 bg-red-50 dark:border-red-950 dark:bg-red-950/20">
            <div className="flex aspect-square flex-col items-center justify-center gap-3 p-5 text-center">
                <div className="text-sm font-medium text-red-600 dark:text-red-300">{t("workbench.failed")}</div>
                <Typography.Paragraph ellipsis={{ rows: 4 }} className="!mb-0 !text-xs !text-red-500 dark:!text-red-300">
                    {error}
                </Typography.Paragraph>
            </div>
            <div className="flex justify-end border-t border-red-200 p-3 dark:border-red-950">
                <Button size="small" danger disabled={disabled} title={wholeJob ? t("imageWorkbench.retryGroupHint") : undefined} onClick={onRetry}>
                    {t(wholeJob ? "imageWorkbench.retryGroup" : "workbench.retry")}
                </Button>
            </div>
        </div>
    );
}

function LogPanel({
    busy,
    logs,
    selectedLogIds,
    activeLogId,
    onSelectedLogIdsChange,
    onCreateSession,
    onDeleteSelected,
    onPreviewLog,
    onReuseLog,
}: {
    busy: boolean;
    logs: GenerationLog[];
    selectedLogIds: string[];
    activeLogId?: string;
    onSelectedLogIdsChange: (ids: string[]) => void;
    onCreateSession: () => void;
    onDeleteSelected: () => void;
    onPreviewLog: (log: GenerationLog) => void;
    onReuseLog: (log: GenerationLog) => void;
}) {
    const { t } = useTranslation();
    const allSelected = Boolean(logs.length) && selectedLogIds.length === logs.length;
    const toggleAll = () => onSelectedLogIdsChange(allSelected ? [] : logs.map((log) => log.id));

    return (
        <>
            <div className="mb-3 flex items-center justify-between gap-3">
                <div>
                    <h2 className="text-base font-semibold">{t("workbench.logs")}</h2>
                </div>
                <Tag className="m-0">{logs.length}</Tag>
            </div>
            <div className="mb-4 flex flex-wrap gap-2">
                <Button size="small" disabled={busy} icon={<Plus className="size-3.5" />} onClick={onCreateSession}>
                    {t("workbench.new")}
                </Button>
                <Button size="small" icon={<CheckSquare className="size-3.5" />} disabled={!logs.length} onClick={toggleAll}>
                    {allSelected ? t("common.cancel") : t("workbench.selectAll")}
                </Button>
                <Button size="small" danger icon={<Trash2 className="size-3.5" />} disabled={busy || !selectedLogIds.length} onClick={onDeleteSelected}>
                    {t("common.delete")}
                </Button>
            </div>
            <div className="space-y-3">
                {logs.map((log) => (
                    <LogCard
                        key={log.id}
                        log={log}
                        busy={busy}
                        selected={selectedLogIds.includes(log.id)}
                        active={activeLogId === log.id}
                        onSelectedChange={(checked) => onSelectedLogIdsChange(checked ? [...selectedLogIds, log.id] : selectedLogIds.filter((id) => id !== log.id))}
                        onClick={() => onPreviewLog(log)}
                        onReuse={() => onReuseLog(log)}
                    />
                ))}
                {!logs.length ? <div className="flex min-h-48 items-center justify-center rounded-lg border border-dashed border-stone-300 text-center text-sm text-stone-500 dark:border-stone-700">{t("workbench.noLogs")}</div> : null}
            </div>
        </>
    );
}

function LogCard({ log, selected, active, busy, onSelectedChange, onClick, onReuse }: { log: GenerationLog; selected: boolean; active: boolean; busy: boolean; onSelectedChange: (checked: boolean) => void; onClick: () => void; onReuse: () => void }) {
    const { t } = useTranslation();
    const thumbnails = (log.thumbnails || []).filter(Boolean).slice(0, 4);
    const summary = logSettingsSummary(log, t);

    return (
        <div
            role="button"
            tabIndex={busy ? -1 : 0}
            aria-disabled={busy}
            className={`block w-full rounded-lg border p-2 text-left transition ${active ? "border-stone-900 bg-blue-50 dark:border-stone-100 dark:bg-blue-950/20" : "border-stone-200 bg-background hover:bg-stone-50 dark:border-stone-800 dark:hover:bg-stone-900"}`}
            onClick={() => !busy && onClick()}
            onKeyDown={(event) => {
                if (!busy && (event.key === "Enter" || event.key === " ")) {
                    event.preventDefault();
                    onClick();
                }
            }}
        >
            <div className="grid grid-cols-[minmax(128px,1fr)_auto] gap-2">
                <div className="grid min-w-0 grid-cols-[auto_minmax(0,1fr)] items-start gap-2">
                    <Checkbox className="mt-0.5" checked={selected} onClick={(event) => event.stopPropagation()} onChange={(event) => onSelectedChange(event.target.checked)} />
                    <div className="min-w-0">
                        <div className="truncate text-sm font-semibold leading-5">{log.title}</div>
                        <div className="mt-0.5 truncate text-[11px] text-stone-500 dark:text-stone-400">{summary}</div>
                        {thumbnails.length ? (
                            <div className="mt-2 flex gap-1 overflow-hidden">
                                {thumbnails.map((image, index) => (
                                    <img key={`${log.id}-${index}`} src={image} alt="" className="size-8 shrink-0 rounded-md object-cover" />
                                ))}
                            </div>
                        ) : null}
                    </div>
                </div>
                <div className="grid justify-items-end gap-2">
                    <div className="flex gap-1">
                        <Tag className="m-0 flex h-6 items-center rounded-md px-1.5 text-xs leading-none" color="blue">
                            {t("workbench.successCount", { count: log.successCount ?? log.imageCount })}
                        </Tag>
                        {log.failCount ? (
                            <Tag className="m-0 flex h-6 items-center rounded-md px-1.5 text-xs leading-none" color="red">
                                {t("workbench.failCount", { count: log.failCount })}
                            </Tag>
                        ) : null}
                    </div>
                    <div className="flex flex-wrap justify-end gap-1">
                        <Tag className="m-0 flex h-6 items-center rounded-md px-1.5 text-xs leading-none">{t("workbench.itemCount", { count: log.imageCount })}</Tag>
                        <Tag className="m-0 flex h-6 items-center rounded-md px-1.5 text-xs leading-none" color="green">
                            {formatDuration(log.durationMs)}
                        </Tag>
                    </div>
                    <div className="flex justify-end">
                        <Tag className="m-0 flex h-6 items-center rounded-md px-1.5 text-xs leading-none">{log.time}</Tag>
                    </div>
                    <Button size="small" type="text" disabled={busy} className="!h-7 !px-1.5" icon={<RotateCcw className="size-3" />} onClick={(event) => { event.stopPropagation(); onReuse(); }}>{t("imageWorkbench.reuse")}</Button>
                </div>
            </div>
        </div>
    );
}

async function readStoredLogs() {
    if (typeof window === "undefined") return [];
    try {
        const values: GenerationLog[] = [];
        await logStore.iterate<GenerationLog, void>((value) => {
            values.push(value);
        });
        const logs = await Promise.all(values.map(normalizeLog));
        return logs.sort((a, b) => (b.createdAt || 0) - (a.createdAt || 0));
    } catch {
        return [];
    }
}

async function normalizeLog(log: Partial<GenerationLog>): Promise<GenerationLog> {
    const references = await Promise.all(
        (log.references || []).map(async (item) => ({
            ...item,
            dataUrl: await resolveImageUrl(item.storageKey, item.dataUrl),
        })),
    );
    const images = await Promise.all(
        (log.images || []).map(async (item) => ({
            ...item,
            dataUrl: await resolveImageUrl(item.storageKey, item.dataUrl),
        })),
    );
    const config = normalizeLogConfig(log);
    return {
        id: log.id || nanoid(),
        createdAt: log.createdAt || Date.now(),
        title: log.title || log.model || i18n.t("workbench.untitled"),
        prompt: log.prompt || log.title || "",
        time: log.time || new Date().toLocaleString(i18n.resolvedLanguage, { hour12: false }),
        model: log.model || config.imageModel || "",
        config,
        references,
        durationMs: log.durationMs || 0,
        successCount: log.successCount ?? log.imageCount ?? 0,
        failCount: log.failCount || 0,
        imageCount: log.imageCount || log.successCount || 0,
        size: log.size || config.size || "",
        quality: log.quality || config.quality || "",
        status: log.status || "success",
        images,
        thumbnails: images.map((image) => image.dataUrl).filter(Boolean),
        texts: log.texts || [],
        errors: log.errors || [],
        conversation: log.conversation,
        providerMetadata: log.providerMetadata,
        outputOrder: log.outputOrder || [],
        provider: log.provider || images[0]?.response?.provider,
    };
}

function serializeLog(log: GenerationLog): GenerationLog {
    return {
        ...log,
        references: log.references.map((item) => ({ ...item, dataUrl: item.storageKey ? "" : item.dataUrl })),
        images: log.images.map((image) => ({ ...image, dataUrl: image.storageKey ? "" : image.dataUrl })),
        thumbnails: [],
    };
}

function normalizeLogConfig(log: Partial<GenerationLog>): GenerationLogConfig {
    return {
        model: log.config?.model || log.model || "",
        imageModel: log.config?.imageModel || log.model || "",
        quality: log.config?.quality || log.quality || "",
        size: log.config?.size || log.size || "",
        count: log.config?.count || String(log.imageCount || log.successCount || 1),
        background: log.config?.background || "auto",
        outputFormat: log.config?.outputFormat || "png",
        outputCompression: typeof log.config?.outputCompression === "number" ? log.config.outputCompression : 100,
        moderation: log.config?.moderation || "auto",
        imageNative: structuredClone(log.config?.imageNative || defaultConfig.imageNative),
    };
}

function moveListItem<T>(items: T[], index: number, offset: number) {
    const targetIndex = index + offset;
    if (targetIndex < 0 || targetIndex >= items.length) return items;
    const next = [...items];
    [next[index], next[targetIndex]] = [next[targetIndex], next[index]];
    return next;
}

function ReferenceOrderButtons({ index, total, onMove }: { index: number; total: number; onMove: (offset: number) => void }) {
    if (total <= 1) return null;
    return (
        <div className="absolute inset-x-1 bottom-1 flex justify-between">
            <Button size="small" className="!h-6 !w-6 !min-w-6 !rounded-full !bg-white/85 !p-0 !shadow-sm" icon={<ArrowLeft className="size-3" />} disabled={index <= 0} onClick={() => onMove(-1)} />
            <Button size="small" className="!h-6 !w-6 !min-w-6 !rounded-full !bg-white/85 !p-0 !shadow-sm" icon={<ArrowRight className="size-3" />} disabled={index >= total - 1} onClick={() => onMove(1)} />
        </div>
    );
}

function buildLog({
    prompt,
    model,
    provider,
    config,
    references,
    durationMs,
    successCount,
    failCount,
    status,
    images,
    result,
}: {
    prompt: string;
    model: string;
    provider: ImageProvider;
    config: GenerationLogConfig;
    references: ReferenceImage[];
    durationMs: number;
    successCount: number;
    failCount: number;
    status: GenerationLog["status"];
    images: GeneratedImage[];
    result: ImageJobResult;
}): GenerationLog {
    const logConfig = {
        model: config.model,
        imageModel: config.imageModel,
        quality: config.quality,
        size: config.size,
        count: config.count,
        background: config.background,
        outputFormat: config.outputFormat,
        outputCompression: config.outputCompression,
        moderation: config.moderation,
        imageNative: structuredClone(config.imageNative),
    };
    return {
        id: nanoid(),
        createdAt: Date.now(),
        title: prompt.slice(0, 12) || i18n.t("workbench.untitled"),
        prompt,
        time: new Date().toLocaleString(i18n.resolvedLanguage, { hour12: false }),
        model,
        config: logConfig,
        references,
        durationMs,
        successCount,
        failCount,
        imageCount: images.length,
        size: logConfig.size,
        quality: logConfig.quality,
        status,
        images,
        thumbnails: images.map((image) => image.dataUrl).filter(Boolean),
        texts: result.texts,
        errors: result.errors,
        conversation: result.conversation,
        providerMetadata: result.providerMetadata,
        outputOrder: result.outputOrder,
        provider,
    };
}

function logConfigFromAiConfig(config: AiConfig): GenerationLogConfig {
    return {
        model: config.model,
        imageModel: config.imageModel,
        quality: config.quality,
        size: config.size,
        count: config.count,
        background: config.background,
        outputFormat: config.outputFormat,
        outputCompression: config.outputCompression,
        moderation: config.moderation,
        imageNative: structuredClone(config.imageNative),
    };
}

function imageExtension(mimeType?: string, outputFormat?: string) {
    if (mimeType?.includes("jpeg")) return "jpg";
    if (mimeType?.includes("webp")) return "webp";
    if (mimeType?.includes("png")) return "png";
    return outputFormat === "jpeg" ? "jpg" : outputFormat === "webp" ? "webp" : "png";
}

function toGeneratedImage(image: ImageGenerationResult, durationMs: number, snapshot: ImageJobSnapshot): GeneratedImage {
    return {
        ...image,
        width: image.width || 0,
        height: image.height || 0,
        bytes: image.bytes || 0,
        durationMs,
        requestConfig: logConfigFromAiConfig(snapshot.config),
        requestPrompt: snapshot.text,
    };
}

function upsertPreview(current: ImageGenerationResult[], index: number | undefined, image: ImageGenerationResult) {
    const target = index ?? current.findIndex((item) => item.id === image.id);
    if (target < 0 || target >= current.length) return [...current, image];
    return current.map((item, itemIndex) => (itemIndex === target ? image : item));
}

function imageSettingsSummary(config: AiConfig, model: string, count: number, t: TFunction) {
    const provider = resolveImageProvider(config, model);
    const native = resolveNativeImageOptions(provider, model, config.imageNative);
    if (provider === "google") return `${native.google?.imageSize || "1K"} · ${native.google?.aspectRatio || "1:1"} · ${t("nativeImage.singleRequest")}`;
    if (provider === "ark") return `${native.ark?.size || "2K"} · ${t("settingsPanels.image.images", { count })} · ${native.stream ? t("nativeImage.stream") : t("nativeImage.finalOnly")}`;
    if (provider === "xai") return `${native.xai?.resolution || "1k"} · ${native.xai?.aspectRatio || "auto"} · ${t("settingsPanels.image.images", { count })}`;
    return `${config.quality} · ${config.size} · ${t("settingsPanels.image.images", { count })} · ${config.outputFormat.toUpperCase()}`;
}

function logSettingsSummary(log: GenerationLog, t: TFunction) {
    const provider = log.provider || log.images[0]?.response?.provider;
    if (provider === "google") return `${log.config.imageNative.google?.imageSize || "1K"} · ${log.config.imageNative.google?.aspectRatio || "1:1"} · ${t("workbench.itemCount", { count: log.imageCount })}`;
    if (provider === "ark") return `${log.config.imageNative.ark?.size || "2K"} · ${t("workbench.itemCount", { count: log.imageCount })}`;
    if (provider === "xai") return `${log.config.imageNative.xai?.resolution || "1k"} · ${t("workbench.itemCount", { count: log.imageCount })}`;
    return `${log.quality} · ${log.size} · ${log.config.outputFormat.toUpperCase()}`;
}
