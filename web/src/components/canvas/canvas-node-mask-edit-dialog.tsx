import { useCallback, useEffect, useRef, useState, type PointerEvent as ReactPointerEvent } from "react";
import { createPortal } from "react-dom";
import { Button, Input, Modal, Slider, Tooltip } from "antd";
import { Brush, ChevronDown, Eraser, ImagePlus, MousePointer2, Redo2, RotateCcw, SquareDashed, Undo2, WandSparkles, ZoomIn, ZoomOut } from "lucide-react";
import { useTranslation } from "react-i18next";

import { readImageMeta } from "@/lib/image-utils";
import { useImageEditorViewport } from "@/components/canvas/use-image-editor-viewport";
import { ImageSettingsPanel, type ImageSettingKey } from "@/components/image-settings-panel";
import { ModelPicker } from "@/components/model-picker";
import { canvasThemes } from "@/lib/canvas-theme";
import { clientPointToImage, pointSelection, rectangleSelection, selectionToImageBounds, type ImagePoint } from "@/lib/canvas/image-selections";
import { getNativeImageCapabilities } from "@/lib/image-model-capabilities";
import { buildGenerationConfig } from "@/lib/canvas/canvas-generation-helpers";
import { resolveImageProvider, resolveModelScript, type AiConfig } from "@/stores/use-config-store";
import { useThemeStore } from "@/stores/use-theme-store";
import type { ImageSelection } from "@/types/image-generation";

export type CanvasImageMaskEditPayload = {
    prompt: string;
    maskDataUrl: string;
    maskPreviewDataUrl: string;
    selections?: ImageSelection[];
    hasPaint?: boolean;
    generate: boolean;
    config: AiConfig;
};

type DrawMode = "paint" | "erase";
type EditMode = DrawMode | "point" | "rectangle";
type MaskStroke = { mode: DrawMode; size: number; points: ImagePoint[] };
type SelectionEdit = { selection: ImageSelection; pointRadius: number };
type EditAction = { kind: "stroke"; stroke: MaskStroke } | { kind: "selection"; edit: SelectionEdit };
type BrushPreview = { x: number; y: number; size: number; adjusting: boolean };
type RectangleDraft = { pointerId: number; start: ImagePoint; current: ImagePoint; startClientX: number; startClientY: number };

const defaultBrushSize = 100;
const maskOverlayColor = "#2563eb";
const maskOverlayFill = "rgba(37, 99, 235, .4)";

export function CanvasNodeMaskEditDialog({ dataUrl, config, open, onClose, onConfirm }: { dataUrl: string; config: AiConfig; open: boolean; onClose: () => void; onConfirm: (payload: CanvasImageMaskEditPayload) => void }) {
    const { t } = useTranslation();
    const theme = canvasThemes[useThemeStore((state) => state.theme)];
    const configRef = useRef(config);
    configRef.current = config;
    const maskCanvasRef = useRef<HTMLCanvasElement>(null);
    const previewCanvasRef = useRef<HTMLCanvasElement>(null);
    const imageRef = useRef<HTMLImageElement>(null);
    const drawingRef = useRef<{ active: boolean; stroke: MaskStroke | null }>({ active: false, stroke: null });
    const rectangleDraftRef = useRef<RectangleDraft | null>(null);
    const brushAdjustRef = useRef<{ active: boolean; pointerId: number; startX: number; startSize: number; previewX: number; previewY: number } | null>(null);
    const historyRef = useRef<EditAction[]>([]);
    const redoRef = useRef<EditAction[]>([]);
    const [image, setImage] = useState<{ width: number; height: number } | null>(null);
    const [prompt, setPrompt] = useState("");
    const [brushSize, setBrushSize] = useState(defaultBrushSize);
    const [mode, setMode] = useState<EditMode>("paint");
    const [error, setError] = useState("");
    const [historySize, setHistorySize] = useState(0);
    const [redoSize, setRedoSize] = useState(0);
    const [brushPreview, setBrushPreview] = useState<BrushPreview | null>(null);
    const [settingsOpen, setSettingsOpen] = useState(false);
    const [draftConfig, setDraftConfig] = useState<AiConfig>(() => imageEditConfig(config));
    const viewport = useImageEditorViewport(image, open);
    const provider = resolveImageProvider(draftConfig, draftConfig.model);
    const nativeCapabilities = getNativeImageCapabilities(provider, draftConfig.model);
    const hasCustomScript = Boolean(resolveModelScript(draftConfig, draftConfig.model));
    const usesNativeMask = nativeCapabilities.supportsMask && !hasCustomScript;
    const usesNativeCoordinates = nativeCapabilities.supportsCoordinates && !hasCustomScript;
    const selectionCount = historyRef.current.filter((action) => action.kind === "selection").length;

    useEffect(() => {
        if (!open) return;
        setPrompt("");
        setBrushSize(defaultBrushSize);
        setMode("paint");
        setError("");
        setHistorySize(0);
        setRedoSize(0);
        setBrushPreview(null);
        setSettingsOpen(false);
        setDraftConfig(imageEditConfig(configRef.current));
        historyRef.current = [];
        redoRef.current = [];
        brushAdjustRef.current = null;
        rectangleDraftRef.current = null;
        drawingRef.current = { active: false, stroke: null };
        void readImageMeta(dataUrl).then(setImage);
    }, [dataUrl, open]);

    const updateDraftConfig = <K extends ImageSettingKey>(key: K, value: AiConfig[K]) => setDraftConfig((current) => ({ ...current, [key]: value }));

    useEffect(() => {
        clearCanvas(maskCanvasRef.current);
        clearCanvas(previewCanvasRef.current);
    }, [image]);

    const commitAction = (action: EditAction) => {
        historyRef.current.push(action);
        redoRef.current = [];
        setHistorySize(historyRef.current.length);
        setRedoSize(0);
        setError("");
        replayEdits(historyRef.current, maskCanvasRef.current, previewCanvasRef.current);
    };

    const cancelRectangle = useCallback(() => {
        rectangleDraftRef.current = null;
        replayEdits(historyRef.current, maskCanvasRef.current, previewCanvasRef.current);
    }, []);

    const draw = (event: ReactPointerEvent<HTMLCanvasElement>) => {
        const point = readCanvasPoint(event.currentTarget, event.clientX, event.clientY);
        const maskCanvas = maskCanvasRef.current;
        const context = maskCanvas?.getContext("2d", { willReadFrequently: true });
        const previewContext = previewCanvasRef.current?.getContext("2d");
        const stroke = drawingRef.current.stroke;
        if (!maskCanvas || !context || !previewContext || !stroke) return;
        configureStrokeContext(context, stroke);
        configurePreviewStrokeContext(previewContext, stroke);
        const last = stroke.points.at(-1);
        drawMaskStroke(context, last || point, point, stroke.size);
        drawMaskStroke(previewContext, last || point, point, stroke.size);
        stroke.points.push(point);
        if (stroke.mode === "paint") {
            setError("");
        }
    };

    const updateBrushPreview = (event: ReactPointerEvent<HTMLCanvasElement>, size = brushSize, adjusting = false) => {
        setBrushPreview({
            x: event.clientX,
            y: event.clientY,
            size,
            adjusting,
        });
    };

    const startDraw = (event: ReactPointerEvent<HTMLCanvasElement>) => {
        if ((event.button === 0 || event.button === 2) && event.altKey && mode !== "rectangle") {
            event.preventDefault();
            event.stopPropagation();
            event.currentTarget.setPointerCapture(event.pointerId);
            brushAdjustRef.current = {
                active: true,
                pointerId: event.pointerId,
                startX: event.clientX,
                startSize: brushSize,
                previewX: event.clientX,
                previewY: event.clientY,
            };
            updateBrushPreview(event, brushSize, true);
            return;
        }
        if (event.button !== 0) return;
        event.preventDefault();
        event.stopPropagation();
        const point = readCanvasPoint(event.currentTarget, event.clientX, event.clientY);
        if (mode === "point") {
            commitAction({ kind: "selection", edit: { selection: pointSelection(point, event.currentTarget.width, event.currentTarget.height), pointRadius: brushSize / 2 } });
            return;
        }
        event.currentTarget.setPointerCapture(event.pointerId);
        if (mode === "rectangle") {
            const draft = { pointerId: event.pointerId, start: point, current: point, startClientX: event.clientX, startClientY: event.clientY };
            rectangleDraftRef.current = draft;
            drawSelectionAnnotation(previewCanvasRef.current, { selection: rectangleSelection(point, point, event.currentTarget.width, event.currentTarget.height), pointRadius: brushSize / 2 }, true);
            return;
        }
        updateBrushPreview(event);
        drawingRef.current = { active: true, stroke: { mode, size: brushSize, points: [] } };
        draw(event);
    };

    const moveDraw = (event: ReactPointerEvent<HTMLCanvasElement>) => {
        const brushAdjust = brushAdjustRef.current;
        if (brushAdjust?.active && event.pointerId === brushAdjust.pointerId) {
            event.preventDefault();
            event.stopPropagation();
            const nextSize = clampBrushSize(brushAdjust.startSize + event.clientX - brushAdjust.startX);
            setBrushSize(nextSize);
            setBrushPreview({
                x: brushAdjust.previewX,
                y: brushAdjust.previewY,
                size: nextSize,
                adjusting: true,
            });
            return;
        }
        const rectangle = rectangleDraftRef.current;
        if (rectangle && event.pointerId === rectangle.pointerId) {
            event.preventDefault();
            event.stopPropagation();
            const next = { ...rectangle, current: readCanvasPoint(event.currentTarget, event.clientX, event.clientY) };
            rectangleDraftRef.current = next;
            replayEdits(historyRef.current, maskCanvasRef.current, previewCanvasRef.current);
            drawSelectionAnnotation(previewCanvasRef.current, { selection: rectangleSelection(next.start, next.current, event.currentTarget.width, event.currentTarget.height), pointRadius: brushSize / 2 }, true);
            return;
        }
        if (mode !== "rectangle") updateBrushPreview(event);
        if (!drawingRef.current.active) return;
        event.preventDefault();
        draw(event);
    };

    const stopDraw = (event: ReactPointerEvent<HTMLCanvasElement>) => {
        const brushAdjust = brushAdjustRef.current;
        if (brushAdjust?.active && event.pointerId === brushAdjust.pointerId) {
            brushAdjustRef.current = null;
            if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId);
            updateBrushPreview(event, brushSize);
            return;
        }
        const rectangle = rectangleDraftRef.current;
        if (rectangle && event.pointerId === rectangle.pointerId) {
            if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId);
            rectangleDraftRef.current = null;
            const moved = Math.hypot(event.clientX - rectangle.startClientX, event.clientY - rectangle.startClientY) >= 3;
            if (moved) {
                const end = readCanvasPoint(event.currentTarget, event.clientX, event.clientY);
                commitAction({ kind: "selection", edit: { selection: rectangleSelection(rectangle.start, end, event.currentTarget.width, event.currentTarget.height), pointRadius: brushSize / 2 } });
            } else {
                replayEdits(historyRef.current, maskCanvasRef.current, previewCanvasRef.current);
            }
            return;
        }
        const stroke = drawingRef.current.stroke;
        drawingRef.current = { active: false, stroke: null };
        if (stroke?.points.length) {
            commitAction({ kind: "stroke", stroke });
        }
    };

    const cancelDraw = (event: ReactPointerEvent<HTMLCanvasElement>) => {
        if (rectangleDraftRef.current?.pointerId === event.pointerId) {
            if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId);
            cancelRectangle();
            return;
        }
        stopDraw(event);
    };

    const undoMask = useCallback(() => {
        if (drawingRef.current.active || rectangleDraftRef.current || !historyRef.current.length) return;
        const action = historyRef.current.pop();
        if (action) redoRef.current.push(action);
        setHistorySize(historyRef.current.length);
        setRedoSize(redoRef.current.length);
        replayEdits(historyRef.current, maskCanvasRef.current, previewCanvasRef.current);
        setError("");
    }, []);

    const redoMask = useCallback(() => {
        if (drawingRef.current.active || rectangleDraftRef.current || !redoRef.current.length) return;
        const action = redoRef.current.pop();
        if (action) historyRef.current.push(action);
        setHistorySize(historyRef.current.length);
        setRedoSize(redoRef.current.length);
        replayEdits(historyRef.current, maskCanvasRef.current, previewCanvasRef.current);
        setError("");
    }, []);

    const resetMask = () => {
        historyRef.current = [];
        redoRef.current = [];
        setHistorySize(0);
        setRedoSize(0);
        rectangleDraftRef.current = null;
        clearCanvas(maskCanvasRef.current);
        clearCanvas(previewCanvasRef.current);
        setError("");
    };

    useEffect(() => {
        if (!open) return;
        const handleKeyDown = (event: KeyboardEvent) => {
            const target = event.target instanceof Element ? event.target : null;
            if (target?.closest("input,textarea,[contenteditable='true']")) return;
            if (event.key === "Escape" && rectangleDraftRef.current) {
                event.preventDefault();
                event.stopPropagation();
                event.stopImmediatePropagation();
                cancelRectangle();
                return;
            }
            const key = event.key.toLowerCase();
            const modifier = (event.metaKey || event.ctrlKey) && !event.altKey;
            const isUndo = modifier && !event.shiftKey && key === "z";
            const isRedo = modifier && ((event.shiftKey && key === "z") || (!event.shiftKey && key === "y"));
            if (!isUndo && !isRedo) return;
            event.preventDefault();
            event.stopPropagation();
            event.stopImmediatePropagation();
            if (isRedo) redoMask();
            else undoMask();
        };
        window.addEventListener("keydown", handleKeyDown, true);
        return () => window.removeEventListener("keydown", handleKeyDown, true);
    }, [cancelRectangle, open, redoMask, undoMask]);

    const selectMode = (nextMode: EditMode) => {
        if (nextMode !== mode) cancelRectangle();
        setMode(nextMode);
        setBrushPreview(null);
    };

    const submit = (generate: boolean) => {
        const nextPrompt = prompt.trim();
        const canvas = maskCanvasRef.current;
        const element = imageRef.current;
        if (!nextPrompt) return setError(t("nativeImage.editPromptRequired", { defaultValue: "请输入编辑要求" }));
        if (!canvas || !element) return;
        const selectionEdits = historyRef.current.flatMap((action) => (action.kind === "selection" ? [action.edit] : []));
        const hasPaint = canvasHasPaint(canvas);
        if (!hasPaint && !selectionEdits.length) return setError(t("nativeImage.editAreaRequired", { defaultValue: "请先涂抹、点选或框选要编辑的区域" }));
        onConfirm({
            prompt: nextPrompt,
            maskDataUrl: buildAlphaMask(canvas, usesNativeMask ? selectionEdits : []),
            maskPreviewDataUrl: buildMaskOverlay(element, previewCanvasRef.current || canvas),
            selections: selectionEdits.length ? selectionEdits.map((edit) => edit.selection) : undefined,
            hasPaint,
            generate,
            config: { ...draftConfig, count: "1" },
        });
    };

    const selectionStrategyLabel = usesNativeMask
        ? t("nativeImage.selectionStrategyMask", { defaultValue: "原生遮罩" })
        : usesNativeCoordinates
          ? t("nativeImage.selectionStrategyCoordinates", { defaultValue: "原生坐标" })
          : t("nativeImage.selectionStrategyAnnotation", { defaultValue: "参考图标注" });
    const selectionHint = usesNativeMask
        ? t("nativeImage.selectionHintMask", { defaultValue: "框选会生成同尺寸透明 Alpha 遮罩；点选使用当前半径。" })
        : usesNativeCoordinates
          ? t("nativeImage.selectionHintCoordinates", { defaultValue: "点选和框选将以原生坐标发送；画笔区域会保留为参考标注。" })
          : t("nativeImage.selectionHintAnnotation", { defaultValue: "点选、框选和画笔区域会标注到参考图，并结合提示词编辑。" });

    return (
        <Modal title={null} open={open && Boolean(dataUrl)} onCancel={onClose} footer={null} width={980} centered destroyOnHidden transitionName="" maskTransitionName="">
            <div className="grid gap-5 lg:grid-cols-[minmax(360px,1fr)_320px]" data-canvas-no-zoom>
                <div
                    ref={viewport.viewportRef}
                    {...viewport.panHandlers}
                    className={`relative h-[min(68vh,720px)] min-h-[360px] rounded-xl border border-black/10 bg-transparent dark:border-white/10 ${viewport.scrollClassName} ${viewport.isPanning ? "cursor-grabbing" : viewport.spacePressed ? "cursor-grab" : ""}`}
                >
                    <div className="relative" style={viewport.contentStyle}>
                        <div ref={viewport.stageRef} className="absolute isolate overflow-hidden rounded-lg bg-transparent select-none [backface-visibility:hidden] [contain:layout_paint] [transform:translateZ(0)]" style={viewport.stageStyle}>
                            {image ? (
                                <>
                                    <canvas ref={maskCanvasRef} width={image.width} height={image.height} className="hidden" />
                                    <div className="absolute left-0 top-0 [backface-visibility:hidden]" style={viewport.mediaStyle}>
                                        <img ref={imageRef} src={dataUrl} alt="" className="absolute inset-0 block h-full w-full bg-transparent object-contain" draggable={false} />
                                        <canvas
                                            ref={previewCanvasRef}
                                            width={image.width}
                                            height={image.height}
                                            className={`absolute inset-0 h-full w-full touch-none ${mode === "rectangle" || mode === "point" ? "cursor-crosshair" : "cursor-none"}`}
                                            role="img"
                                            aria-label={t("nativeImage.selectionCanvasLabel", { defaultValue: "图片编辑选区画布" })}
                                            onPointerDown={startDraw}
                                            onPointerMove={moveDraw}
                                            onPointerUp={stopDraw}
                                            onPointerCancel={cancelDraw}
                                            onPointerEnter={(event) => {
                                                if (mode !== "rectangle") updateBrushPreview(event);
                                            }}
                                            onPointerLeave={() => {
                                                if (!drawingRef.current.active && !brushAdjustRef.current?.active && !rectangleDraftRef.current) setBrushPreview(null);
                                            }}
                                            onContextMenu={(event) => event.preventDefault()}
                                        />
                                    </div>
                                </>
                            ) : null}
                        </div>
                    </div>
                </div>
                {brushPreview
                    ? createPortal(
                          <div
                              className={`pointer-events-none fixed z-[1100] rounded-full border-2 ${brushPreview.adjusting ? "border-[#fbbf24] bg-black/10" : "border-white/90 bg-black/5"} shadow-[0_0_0_1px_rgba(0,0,0,.8)]`}
                              style={{ left: brushPreview.x, top: brushPreview.y, width: Math.max(4, brushPreview.size * viewport.imageScale), aspectRatio: 1, transform: "translate(-50%, -50%)" }}
                          >
                              {brushPreview.adjusting ? <span className="absolute left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 rounded bg-black/75 px-1.5 py-0.5 text-xs font-semibold text-white">{brushSize}px</span> : null}
                          </div>,
                          document.body,
                      )
                    : null}

                <div className="thin-scrollbar flex max-h-[68vh] min-h-[360px] flex-col gap-5 overflow-y-auto pr-1">
                    <div>
                        <h2 className="text-xl font-semibold">{t("canvas.editors.maskTitle")}</h2>
                        <div className="mt-2 text-sm opacity-60">{image ? `${image.width} x ${image.height}px` : t("canvas.editors.loading")}</div>
                        <div className="mt-2 text-xs leading-5 opacity-55">{t("canvas.editors.maskHint")}</div>
                    </div>

                    <div className="grid grid-cols-4 gap-2">
                        <Button className="min-w-0 px-2" type={mode === "paint" ? "primary" : "default"} icon={<Brush className="size-4" />} aria-pressed={mode === "paint"} onClick={() => selectMode("paint")}>
                            {t("canvas.editors.brush")}
                        </Button>
                        <Button className="min-w-0 px-2" type={mode === "erase" ? "primary" : "default"} icon={<Eraser className="size-4" />} aria-pressed={mode === "erase"} onClick={() => selectMode("erase")}>
                            {t("canvas.editors.erase")}
                        </Button>
                        <Button className="min-w-0 px-2" type={mode === "point" ? "primary" : "default"} icon={<MousePointer2 className="size-4" />} aria-pressed={mode === "point"} onClick={() => selectMode("point")}>
                            {t("nativeImage.pointTool", { defaultValue: "点选" })}
                        </Button>
                        <Button className="min-w-0 px-2" type={mode === "rectangle" ? "primary" : "default"} icon={<SquareDashed className="size-4" />} aria-pressed={mode === "rectangle"} onClick={() => selectMode("rectangle")}>
                            {t("nativeImage.rectangleTool", { defaultValue: "框选" })}
                        </Button>
                    </div>

                    <div className="rounded-lg border border-black/10 px-3 py-2 text-xs leading-5 dark:border-white/10">
                        <div className="flex items-center justify-between gap-3 font-medium">
                            <span>{selectionStrategyLabel}</span>
                            {selectionCount ? <span className="tabular-nums opacity-60">{t("nativeImage.selectionCount", { count: selectionCount, defaultValue: "{{count}} 个选区" })}</span> : null}
                        </div>
                        <div className="mt-1 opacity-60">{selectionHint}</div>
                    </div>

                    <div className="flex items-center justify-between rounded-lg border border-black/10 px-2 py-1 dark:border-white/10">
                        <Tooltip title={t("canvas.editors.undoMaskTitle")}>
                            <Button type="text" icon={<Undo2 className="size-4" />} disabled={!historySize} aria-label={t("canvas.editors.undoMask")} onClick={undoMask} />
                        </Tooltip>
                        <Tooltip title={t("canvas.editors.redoMaskTitle")}>
                            <Button type="text" icon={<Redo2 className="size-4" />} disabled={!redoSize} aria-label={t("canvas.editors.redoMask")} onClick={redoMask} />
                        </Tooltip>
                        <div className="flex items-center gap-1">
                            <Tooltip title={t("canvas.editors.zoomOut")}>
                                <Button type="text" icon={<ZoomOut className="size-4" />} disabled={!viewport.canZoomOut} aria-label={t("canvas.editors.zoomOut")} onClick={viewport.zoomOut} />
                            </Tooltip>
                            <button type="button" className="min-w-14 text-center text-xs font-semibold tabular-nums opacity-70" onClick={viewport.resetZoom}>
                                {Math.round(viewport.zoom * 100)}%
                            </button>
                            <Tooltip title={t("canvas.editors.zoomIn")}>
                                <Button type="text" icon={<ZoomIn className="size-4" />} disabled={!viewport.canZoomIn} aria-label={t("canvas.editors.zoomIn")} onClick={viewport.zoomIn} />
                            </Tooltip>
                        </div>
                    </div>

                    {mode !== "rectangle" ? <div className="space-y-2">
                        <div className="flex items-center justify-between text-sm">
                            <span className="font-medium opacity-75">{mode === "point" ? t("nativeImage.pointRadius", { defaultValue: "点选范围" }) : t("canvas.editors.brushSize")}</span>
                            <span className="font-semibold">{brushSize}px</span>
                        </div>
                        <Slider min={8} max={160} step={2} value={brushSize} onChange={setBrushSize} />
                    </div> : null}

                    <div className="space-y-2">
                        <div className="text-sm font-medium opacity-75">{t("canvas.editors.editInstructions")}</div>
                        <Input.TextArea
                            rows={6}
                            value={prompt}
                            status={error && !prompt.trim() ? "error" : undefined}
                            placeholder={t("canvas.editors.maskPlaceholder")}
                            onChange={(event) => {
                                setPrompt(event.target.value);
                                setError("");
                            }}
                        />
                        {error ? <div className="text-xs font-medium text-[#ef4444]">{error}</div> : null}
                    </div>

                    <div className="rounded-xl border border-black/10 dark:border-white/10">
                        <button type="button" className="flex w-full items-center justify-between gap-3 bg-transparent px-3 py-2.5 text-left" aria-expanded={settingsOpen} onClick={() => setSettingsOpen((value) => !value)}>
                            <span className="min-w-0">
                                <span className="block text-sm font-medium">{t("canvas.editors.executionSettings")}</span>
                                <span className="mt-0.5 block truncate text-xs opacity-60">{selectionStrategyLabel} · {draftConfig.quality} · {draftConfig.outputFormat.toUpperCase()}</span>
                            </span>
                            <ChevronDown className={`size-4 shrink-0 transition-transform ${settingsOpen ? "rotate-180" : ""}`} />
                        </button>
                        {settingsOpen ? (
                            <div className="space-y-4 border-t border-black/10 px-3 py-3 dark:border-white/10">
                                <ModelPicker config={draftConfig} value={draftConfig.model} onChange={(model) => setDraftConfig((current) => imageEditConfig({ ...current, model }))} capability="image" fullWidth />
                                <ImageSettingsPanel config={draftConfig} model={draftConfig.model} onConfigChange={updateDraftConfig} onNativeChange={(imageNative) => setDraftConfig((current) => imageEditConfig({ ...current, imageNative }))} referenceCount={1} theme={theme} showTitle={false} className="space-y-4" maxCount={1} quickCount={1} />
                            </div>
                        ) : null}
                    </div>

                    <div className="mt-auto flex items-center justify-between gap-2">
                        <Button icon={<RotateCcw className="size-4" />} onClick={resetMask}>
                            {t("canvas.editors.reset")}
                        </Button>
                        <div className="flex items-center gap-2">
                            <Button icon={<ImagePlus className="size-4" />} onClick={() => submit(false)}>
                                {t("canvas.editors.maskExport")}
                            </Button>
                            <Button type="primary" icon={<WandSparkles className="size-4" />} onClick={() => submit(true)}>
                                {t("canvas.editors.maskGenerate")}
                            </Button>
                        </div>
                    </div>
                </div>
            </div>
        </Modal>
    );
}

function imageEditConfig(config: AiConfig): AiConfig {
    const normalized = buildGenerationConfig({ ...config, imageModel: config.model }, undefined, "image");
    return {
        ...normalized,
        count: "1",
        imageNative: {
            ...normalized.imageNative,
            ark: { ...normalized.imageNative.ark, layerDecomposition: false },
        },
    };
}

function readCanvasPoint(canvas: HTMLCanvasElement, clientX: number, clientY: number) {
    return clientPointToImage(clientX, clientY, canvas.getBoundingClientRect(), canvas.width, canvas.height);
}

function clampBrushSize(value: number) {
    return Math.min(160, Math.max(8, Math.round(value / 2) * 2));
}

function clearCanvas(canvas: HTMLCanvasElement | null) {
    const context = canvas?.getContext("2d", { willReadFrequently: true });
    if (!canvas || !context) return;
    context.clearRect(0, 0, canvas.width, canvas.height);
}

function drawMaskStroke(context: CanvasRenderingContext2D, from: { x: number; y: number }, to: { x: number; y: number }, size: number) {
    if (from.x === to.x && from.y === to.y) {
        context.beginPath();
        context.arc(to.x, to.y, size / 2, 0, Math.PI * 2);
        context.fill();
        return;
    }
    context.beginPath();
    context.moveTo(from.x, from.y);
    context.lineTo(to.x, to.y);
    context.stroke();
}

function configureStrokeContext(context: CanvasRenderingContext2D, stroke: MaskStroke) {
    context.lineCap = "round";
    context.lineJoin = "round";
    context.lineWidth = stroke.size;
    context.globalCompositeOperation = stroke.mode === "paint" ? "source-over" : "destination-out";
    context.strokeStyle = "#000";
    context.fillStyle = "#000";
}

function configurePreviewStrokeContext(context: CanvasRenderingContext2D, stroke: MaskStroke) {
    context.lineCap = "round";
    context.lineJoin = "round";
    context.lineWidth = stroke.size;
    context.globalCompositeOperation = stroke.mode === "paint" ? "source-over" : "destination-out";
    context.strokeStyle = maskOverlayFill;
    context.fillStyle = maskOverlayFill;
}

function replayEdits(actions: EditAction[], maskCanvas: HTMLCanvasElement | null, previewCanvas: HTMLCanvasElement | null) {
    const context = maskCanvas?.getContext("2d", { willReadFrequently: true });
    const previewContext = previewCanvas?.getContext("2d");
    if (!maskCanvas || !context || !previewCanvas || !previewContext) return;
    context.clearRect(0, 0, maskCanvas.width, maskCanvas.height);
    previewContext.clearRect(0, 0, previewCanvas.width, previewCanvas.height);
    for (const action of actions) {
        if (action.kind !== "stroke") continue;
        const { stroke } = action;
        configureStrokeContext(context, stroke);
        configurePreviewStrokeContext(previewContext, stroke);
        stroke.points.forEach((point, index) => {
            const previous = stroke.points[index - 1] || point;
            drawMaskStroke(context, previous, point, stroke.size);
            drawMaskStroke(previewContext, previous, point, stroke.size);
        });
    }
    for (const action of actions) {
        if (action.kind === "selection") drawSelectionAnnotation(previewCanvas, action.edit);
    }
}

function drawSelectionAnnotation(canvas: HTMLCanvasElement | null, edit: SelectionEdit, draft = false) {
    const context = canvas?.getContext("2d");
    if (!canvas || !context) return;
    const bounds = selectionToImageBounds(edit.selection, canvas.width, canvas.height);
    const lineWidth = Math.max(2, Math.min(canvas.width, canvas.height) * 0.003);
    context.save();
    context.globalCompositeOperation = "source-over";
    context.lineWidth = lineWidth;
    context.strokeStyle = maskOverlayColor;
    context.fillStyle = "rgba(37, 99, 235, .16)";
    context.setLineDash(draft ? [lineWidth * 3, lineWidth * 2] : []);
    if (edit.selection.kind === "rectangle") {
        const width = bounds.x2 - bounds.x1;
        const height = bounds.y2 - bounds.y1;
        context.fillRect(bounds.x1, bounds.y1, width, height);
        context.strokeRect(bounds.x1 + lineWidth / 2, bounds.y1 + lineWidth / 2, Math.max(0, width - lineWidth), Math.max(0, height - lineWidth));
    } else {
        const radius = Math.max(lineWidth * 3, edit.pointRadius);
        context.beginPath();
        context.arc(bounds.x1, bounds.y1, radius, 0, Math.PI * 2);
        context.fill();
        context.stroke();
        context.setLineDash([]);
        context.fillStyle = maskOverlayColor;
        context.beginPath();
        context.arc(bounds.x1, bounds.y1, lineWidth * 1.5, 0, Math.PI * 2);
        context.fill();
        context.beginPath();
        context.moveTo(bounds.x1 - lineWidth * 4, bounds.y1);
        context.lineTo(bounds.x1 + lineWidth * 4, bounds.y1);
        context.moveTo(bounds.x1, bounds.y1 - lineWidth * 4);
        context.lineTo(bounds.x1, bounds.y1 + lineWidth * 4);
        context.stroke();
    }
    context.restore();
}

function canvasHasPaint(canvas: HTMLCanvasElement) {
    const context = canvas.getContext("2d", { willReadFrequently: true });
    if (!context) return false;
    const data = context.getImageData(0, 0, canvas.width, canvas.height).data;
    for (let index = 3; index < data.length; index += 4) {
        if (data[index] > 0) return true;
    }
    return false;
}

function buildMaskOverlay(image: HTMLImageElement, selectionCanvas: HTMLCanvasElement) {
    const canvas = document.createElement("canvas");
    canvas.width = selectionCanvas.width;
    canvas.height = selectionCanvas.height;
    const context = canvas.getContext("2d");
    if (!context) return selectionCanvas.toDataURL("image/png");
    context.drawImage(image, 0, 0, canvas.width, canvas.height);
    context.drawImage(selectionCanvas, 0, 0);
    return canvas.toDataURL("image/png");
}

function buildAlphaMask(selectionCanvas: HTMLCanvasElement, selections: SelectionEdit[]) {
    const canvas = document.createElement("canvas");
    canvas.width = selectionCanvas.width;
    canvas.height = selectionCanvas.height;
    const context = canvas.getContext("2d");
    if (!context) return selectionCanvas.toDataURL("image/png");
    context.fillStyle = "#000";
    context.fillRect(0, 0, canvas.width, canvas.height);
    context.globalCompositeOperation = "destination-out";
    context.drawImage(selectionCanvas, 0, 0);
    context.fillStyle = "#000";
    for (const edit of selections) {
        const bounds = selectionToImageBounds(edit.selection, canvas.width, canvas.height);
        if (edit.selection.kind === "rectangle") {
            context.fillRect(bounds.x1, bounds.y1, bounds.x2 - bounds.x1, bounds.y2 - bounds.y1);
        } else {
            context.beginPath();
            context.arc(bounds.x1, bounds.y1, edit.pointRadius, 0, Math.PI * 2);
            context.fill();
        }
    }
    return canvas.toDataURL("image/png");
}
