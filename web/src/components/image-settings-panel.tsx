import { type ReactNode, useState } from "react";
import { ConfigProvider, Slider, Switch } from "antd";
import { ChevronDown } from "lucide-react";
import { useTranslation } from "react-i18next";

import i18n from "@/i18n";
import { NativeImageSettings } from "@/components/native-image-settings";
import { type CanvasTheme } from "@/lib/canvas-theme";
import { getImageModelCapabilities, getNativeImageCapabilities, imageBackgrounds, imageOutputFormats, resolveNativeImageCount } from "@/lib/image-model-capabilities";
import { computeMediaSize, inferMediaRatio, inferMediaScale, mediaRatioOptions, mediaScaleOptions, readMediaDimensions } from "@/lib/media-size";
import { resolveImageProvider, type AiConfig } from "@/stores/use-config-store";
import type { NativeImageOptions } from "@/types/image-generation";

const DIMENSION_STEP = 16;

export const imageQualityOptions = ["auto", "low", "medium", "high", "xhigh", "max"].map((value) => ({ value, get label() { return i18n.t(`settingsPanels.common.${value}`); } }));
export const imageAspectOptions = mediaRatioOptions.map((item) => ({ value: item.value, label: item.value === "auto" ? i18n.t("settingsPanels.common.auto") : item.value }));
export const imageScaleOptions = mediaScaleOptions.map((value) => ({ value, label: value === "auto" ? i18n.t("settingsPanels.common.auto") : value }));

export type ImageSettingKey = "quality" | "size" | "count" | "background" | "outputFormat" | "outputCompression" | "moderation" | "imageNative";
export const imageSettingKeys: readonly ImageSettingKey[] = ["quality", "size", "count", "background", "outputFormat", "outputCompression", "moderation", "imageNative"];
export type ImageSettingsChange = <K extends ImageSettingKey>(key: K, value: AiConfig[K]) => void;

type ImageSettingsPanelProps = {
    config: AiConfig;
    onConfigChange: ImageSettingsChange;
    theme: CanvasTheme;
    model?: string;
    onNativeChange?: (value: NativeImageOptions) => void;
    referenceCount?: number;
    showTitle?: boolean;
    className?: string;
    maxCount?: number;
    quickCount?: number;
};

export function ImageSettingsPanel({ config, onConfigChange, onNativeChange, referenceCount = 0, theme, model, showTitle = true, className = "w-[320px] space-y-4 rounded-2xl px-1 py-0.5", maxCount, quickCount = 10 }: ImageSettingsPanelProps) {
    const { t } = useTranslation();
    const [snapDimensionToStep, setSnapDimensionToStep] = useState(true);
    const [advancedOpen, setAdvancedOpen] = useState(false);
    const selectedModel = model || config.model || config.imageModel;
    const capabilities = getImageModelCapabilities(selectedModel);
    const provider = resolveImageProvider(config, selectedModel);
    const nativeCapabilities = getNativeImageCapabilities(provider, selectedModel);
    const nativeMode = Boolean(onNativeChange && nativeCapabilities.supported);
    const showGenericImageOptions = !nativeMode || provider === "openai";
    const providerMaxCount = nativeMode ? resolveNativeImageCount(provider, selectedModel, config.imageNative, Number.MAX_SAFE_INTEGER, referenceCount) : capabilities.maxImages;
    const showCount = !nativeMode || providerMaxCount > 1;
    const optionError = validateImageUiOptions(config, selectedModel);
    const alignToStep = capabilities.isGptImage || snapDimensionToStep;
    const qualityOptions = capabilities.qualities;
    const resolvedMaxCount = Math.min(maxCount ?? providerMaxCount, providerMaxCount);
    const quality = config.quality || "auto";
    const count = Math.max(1, Math.min(resolvedMaxCount, Math.floor(Math.abs(Number(config.count)) || 1)));
    const activeSize = config.size || "auto";
    const selectedScale = inferMediaScale(activeSize);
    const selectedRatio = inferMediaRatio(activeSize);
    const dimensions = readMediaDimensions(activeSize, selectedScale, selectedRatio);
    const applySize = (scale: string, ratio: string) => onConfigChange("size", computeMediaSize(scale, ratio));
    const selectScale = (scale: string) => applySize(scale, selectedRatio === "auto" ? "1:1" : selectedRatio);
    const selectRatio = (ratio: string) => applySize(selectedScale, ratio);
    const updateDimension = (key: "width" | "height", value: number | null) => {
        const next = Math.max(1, Math.floor(value || dimensions[key] || 1024));
        const width = key === "width" ? next : dimensions.width;
        const height = key === "height" ? next : dimensions.height;
        onConfigChange("size", `${alignDimension(width, alignToStep)}x${alignDimension(height, alignToStep)}`);
    };

    return (
        <ImageSettingsTheme theme={theme}>
            <div
                className={className}
                style={{ color: theme.node.text }}
                onMouseDown={(event) => {
                    event.stopPropagation();
                    if (event.target instanceof HTMLInputElement) return;
                    if (document.activeElement instanceof HTMLInputElement && event.currentTarget.contains(document.activeElement)) document.activeElement.blur();
                }}
            >
                {showTitle ? <div className="text-lg font-semibold">{t("settingsPanels.image.title")}</div> : null}
                {showGenericImageOptions ? <div className="space-y-2.5">
                    <SettingTitle color={theme.node.muted}>{t("settingsPanels.image.quality")}</SettingTitle>
                    <div className={`grid gap-2.5 ${qualityOptions.length > 4 ? "grid-cols-3" : "grid-cols-4"}`} role="radiogroup" aria-label={t("settingsPanels.image.quality")}>
                        {qualityOptions.map((value) => (
                            <OptionPill key={value} selected={quality === value} theme={theme} onClick={() => onConfigChange("quality", value)}>
                                {t(`settingsPanels.common.${value}`)}
                            </OptionPill>
                        ))}
                    </div>
                </div> : null}
                {showGenericImageOptions ? <div className="space-y-2.5">
                    <div className="flex items-center justify-between gap-3">
                        <SettingTitle color={theme.node.muted}>{t("settingsPanels.image.size")}</SettingTitle>
                        <div className="flex items-center gap-2">
                            <span className="text-xs font-medium" style={{ color: theme.node.muted }}>
                                {t("settingsPanels.image.align16")}
                            </span>
                            <span title={t(capabilities.isGptImage ? "settingsPanels.image.align16Required" : "settingsPanels.image.align16Hint")} onMouseDown={(event) => event.stopPropagation()}>
                                <Switch size="small" checked={alignToStep} disabled={capabilities.isGptImage} onChange={setSnapDimensionToStep} />
                            </span>
                        </div>
                    </div>
                    <div className="grid grid-cols-[1fr_auto_1fr] items-center gap-2.5">
                        <DimensionInput prefix="W" value={dimensions.width} disabled={selectedRatio === "auto"} theme={theme} alignToStep={alignToStep} onChange={(value) => updateDimension("width", value)} />
                        <span className="text-lg opacity-45">↔</span>
                        <DimensionInput prefix="H" value={dimensions.height} disabled={selectedRatio === "auto"} theme={theme} alignToStep={alignToStep} onChange={(value) => updateDimension("height", value)} />
                    </div>
                </div> : null}
                {showGenericImageOptions ? <div className="space-y-2.5">
                    <SettingTitle color={theme.node.muted}>{t("settingsPanels.image.resolution")}</SettingTitle>
                    <div className="grid grid-cols-4 gap-2.5">
                        {mediaScaleOptions.map((value) => (
                            <OptionPill key={value} selected={selectedScale === value} theme={theme} onClick={() => selectScale(value)}>
                                {value === "auto" ? t("settingsPanels.common.auto") : value}
                            </OptionPill>
                        ))}
                    </div>
                </div> : null}
                {showGenericImageOptions ? <div className="space-y-2.5">
                    <SettingTitle color={theme.node.muted}>{t("settingsPanels.image.aspectRatio")}</SettingTitle>
                    <div className="grid grid-cols-4 gap-2.5">
                        {mediaRatioOptions.map((item) => (
                            <button
                                key={item.value}
                                type="button"
                                className="flex h-[72px] cursor-pointer flex-col items-center justify-center gap-1.5 rounded-xl border bg-transparent text-sm transition hover:opacity-80"
                                style={{ borderColor: selectedRatio === item.value ? theme.node.text : theme.node.stroke, background: "transparent", color: theme.node.text }}
                                onMouseDown={(event) => event.stopPropagation()}
                                onClick={() => selectRatio(item.value)}
                            >
                                <AspectIcon width={item.width} height={item.height} color={theme.node.text} />
                                <span>{item.value === "auto" ? t("settingsPanels.common.auto") : item.value}</span>
                            </button>
                        ))}
                    </div>
                </div> : null}
                {capabilities.isGptImage ? (
                    <div className="space-y-2.5">
                        <SettingTitle color={theme.node.muted}>{t("settingsPanels.image.background")}</SettingTitle>
                        <div className="grid grid-cols-3 gap-2.5" role="radiogroup" aria-label={t("settingsPanels.image.background")}>
                            {imageBackgrounds.map((value) => (
                                <OptionPill key={value} selected={(config.background || "auto") === value} disabled={value === "transparent" && config.outputFormat === "jpeg"} title={value === "transparent" && config.outputFormat === "jpeg" ? t("settingsPanels.image.transparentJpegConflict") : undefined} theme={theme} onClick={() => onConfigChange("background", value)}>
                                    {t(`settingsPanels.image.backgrounds.${value}`)}
                                </OptionPill>
                            ))}
                        </div>
                        {config.outputFormat === "jpeg" ? <div className="text-xs" style={{ color: theme.node.muted }}>{t("settingsPanels.image.transparentJpegConflict")}</div> : null}
                    </div>
                ) : null}
                {showCount ? <div className="space-y-2.5">
                    <SettingTitle color={theme.node.muted}>{t("settingsPanels.image.count")}</SettingTitle>
                    <div className="grid grid-cols-4 gap-2.5">
                        {Array.from({ length: Math.min(quickCount, resolvedMaxCount) }, (_, index) => index + 1).map((value) => (
                            <OptionPill key={value} selected={count === value} theme={theme} onClick={() => onConfigChange("count", String(value))}>
                                {t("settingsPanels.image.images", { count: value })}
                            </OptionPill>
                        ))}
                        <CountInput value={count} max={resolvedMaxCount} theme={theme} onChange={(value) => onConfigChange("count", String(value || 1))} />
                    </div>
                    {nativeMode && provider === "ark" ? <div className="text-xs" style={{ color: theme.node.muted }}>{t("nativeImage.ark.countRemaining", { count: resolvedMaxCount, references: referenceCount })}</div> : null}
                </div> : null}
                {capabilities.isGptImage ? (
                    <div className="border-t pt-3" style={{ borderColor: theme.node.stroke }}>
                        <button
                            type="button"
                            className="flex h-9 w-full items-center justify-between rounded-lg bg-transparent px-1 text-sm font-medium transition hover:opacity-75"
                            style={{ color: theme.node.text }}
                            aria-expanded={advancedOpen}
                            onMouseDown={(event) => event.stopPropagation()}
                            onClick={() => setAdvancedOpen((value) => !value)}
                        >
                            <span>{t("settingsPanels.image.advanced")}</span>
                            <ChevronDown className={`size-4 transition-transform ${advancedOpen ? "rotate-180" : ""}`} />
                        </button>
                        {advancedOpen ? (
                            <div className="mt-3 space-y-4">
                                <div className="space-y-2.5">
                                    <SettingTitle color={theme.node.muted}>{t("settingsPanels.image.outputFormat")}</SettingTitle>
                                    <div className="grid grid-cols-3 gap-2.5" role="radiogroup" aria-label={t("settingsPanels.image.outputFormat")}>
                                        {imageOutputFormats.map((value) => (
                                            <OptionPill key={value} selected={config.outputFormat === value} disabled={value === "jpeg" && config.background === "transparent"} title={value === "jpeg" && config.background === "transparent" ? t("settingsPanels.image.transparentJpegConflict") : undefined} theme={theme} onClick={() => onConfigChange("outputFormat", value)}>
                                                {value.toUpperCase()}
                                            </OptionPill>
                                        ))}
                                    </div>
                                    {config.background === "transparent" ? <div className="text-xs" style={{ color: theme.node.muted }}>{t("settingsPanels.image.transparentJpegConflict")}</div> : null}
                                </div>
                                {config.outputFormat !== "png" ? (
                                    <div className="space-y-2">
                                        <div className="flex items-center justify-between gap-3">
                                            <SettingTitle color={theme.node.muted}>{t("settingsPanels.image.compression")}</SettingTitle>
                                            <span className="text-xs tabular-nums" style={{ color: theme.node.muted }}>{config.outputCompression}</span>
                                        </div>
                                        <Slider min={0} max={100} value={config.outputCompression} aria-label={t("settingsPanels.image.compression")} onChange={(value) => onConfigChange("outputCompression", value)} />
                                    </div>
                                ) : null}
                                <div className="space-y-2.5">
                                    <SettingTitle color={theme.node.muted}>{t("settingsPanels.image.moderation")}</SettingTitle>
                                    <div className="grid grid-cols-2 gap-2.5" role="radiogroup" aria-label={t("settingsPanels.image.moderation")}>
                                        {(["auto", "low"] as const).map((value) => (
                                            <OptionPill key={value} selected={config.moderation === value} theme={theme} onClick={() => onConfigChange("moderation", value)}>
                                                {t(`settingsPanels.image.moderations.${value}`)}
                                            </OptionPill>
                                        ))}
                                    </div>
                                </div>
                            </div>
                        ) : null}
                    </div>
                ) : null}
                {optionError ? <div role="alert" className="rounded-lg border px-3 py-2 text-xs font-medium" style={{ borderColor: theme.node.stroke, color: theme.node.text }}>{optionError}</div> : null}
                {nativeMode && onNativeChange ? <NativeImageSettings config={config} model={selectedModel} theme={theme} onChange={onNativeChange} onOutputFormatChange={(outputFormat) => onConfigChange("outputFormat", outputFormat)} /> : null}
            </div>
        </ImageSettingsTheme>
    );
}

export function ImageSettingsTheme({ theme, children }: { theme: CanvasTheme; children: ReactNode }) {
    return (
        <ConfigProvider
            theme={{
                token: { colorBgContainer: theme.toolbar.panel, colorBgElevated: theme.toolbar.panel, colorBorder: theme.node.stroke, colorPrimary: theme.node.activeStroke, colorText: theme.node.text, colorTextLightSolid: theme.node.panel },
                components: {
                    Button: { defaultBg: theme.toolbar.panel, defaultBorderColor: theme.node.stroke, defaultColor: theme.node.text },
                    Slider: { railBg: theme.node.stroke, railHoverBg: theme.node.stroke, trackBg: theme.node.activeStroke, handleColor: theme.node.text, handleActiveColor: theme.node.text },
                },
            }}
        >
            {children}
        </ConfigProvider>
    );
}

export function imageQualityLabel(value: string) {
    return (["auto", "low", "medium", "high", "xhigh", "max"].includes(value) ? i18n.t(`settingsPanels.common.${value}`) : value);
}

export function imageSizeLabel(size: string) {
    const scale = inferMediaScale(size);
    const ratio = inferMediaRatio(size);
    if (ratio === "auto" || size === "auto") return i18n.t("settingsPanels.common.auto");
    if (scale === "auto") return ratio;
    return `${scale} · ${ratio}`;
}

function OptionPill({ selected, disabled = false, title, theme, onClick, children }: { selected: boolean; disabled?: boolean; title?: string; theme: CanvasTheme; onClick: () => void; children: ReactNode }) {
    return (
        <button
            type="button"
            className="h-9 cursor-pointer rounded-full border px-2 text-sm transition hover:opacity-80 disabled:cursor-not-allowed disabled:opacity-40"
            style={{ background: "transparent", borderColor: selected ? theme.node.text : theme.node.stroke, color: theme.node.text }}
            disabled={disabled}
            title={title}
            onMouseDown={(event) => event.stopPropagation()}
            onClick={onClick}
            role="radio"
            aria-checked={selected}
        >
            {children}
        </button>
    );
}

export function validateImageUiOptions(config: Pick<AiConfig, "background" | "outputFormat" | "quality">, model: string) {
    const capabilities = getImageModelCapabilities(model);
    if (!capabilities.isGptImage) return undefined;
    if (!capabilities.qualities.includes(config.quality as never)) return i18n.t("settingsPanels.image.qualityConflict", { quality: config.quality });
    return config.background === "transparent" && config.outputFormat === "jpeg" ? i18n.t("settingsPanels.image.transparentJpegConflict") : undefined;
}

function DimensionInput({ prefix, value, disabled, theme, alignToStep, onChange }: { prefix: string; value: number; disabled: boolean; theme: CanvasTheme; alignToStep: boolean; onChange: (value: number | null) => void }) {
    const commit = (input: HTMLInputElement) => {
        const next = alignDimension(Math.max(1, Math.floor(Number(input.value) || value || 1024)), alignToStep);
        input.value = String(next);
        onChange(next);
    };

    return (
        <label className="flex h-9 overflow-hidden rounded-xl text-sm" style={{ background: theme.node.fill, color: theme.node.text, opacity: disabled ? 0.55 : 1 }}>
            <span className="grid w-9 place-items-center" style={{ color: theme.node.muted }}>
                {prefix}
            </span>
            <input
                type="number"
                min={1}
                disabled={disabled}
                className="min-w-0 flex-1 bg-transparent px-2 outline-none [appearance:textfield] [&::-webkit-inner-spin-button]:appearance-none [&::-webkit-outer-spin-button]:appearance-none"
                defaultValue={value || ""}
                key={`${prefix}-${value}`}
                onBlur={(event) => commit(event.currentTarget)}
                onKeyDown={(event) => {
                    if (event.key === "Enter") event.currentTarget.blur();
                }}
                onMouseDown={(event) => event.stopPropagation()}
            />
        </label>
    );
}

function CountInput({ value, max, theme, onChange }: { value: number; max: number; theme: CanvasTheme; onChange: (value: number | null) => void }) {
    return (
        <label className="col-span-2 flex h-9 overflow-hidden rounded-full border text-sm" style={{ borderColor: theme.node.stroke, color: theme.node.text }}>
            <input
                type="number"
                min={1}
                max={max}
                className="min-w-0 flex-1 bg-transparent px-3 text-center outline-none [appearance:textfield] [&::-webkit-inner-spin-button]:appearance-none [&::-webkit-outer-spin-button]:appearance-none"
                style={{ color: theme.node.text, WebkitTextFillColor: theme.node.text }}
                value={value || ""}
                onChange={(event) => onChange(Number(event.target.value) || null)}
                onMouseDown={(event) => event.stopPropagation()}
            />
        </label>
    );
}

function AspectIcon({ width, height, color }: { width: number; height: number; color: string }) {
    if (!width || !height) return null;
    const ratio = width / height;
    const boxWidth = ratio >= 1 ? 24 : Math.max(10, 24 * ratio);
    const boxHeight = ratio >= 1 ? Math.max(10, 24 / ratio) : 24;
    return (
        <span className="grid h-7 w-9 place-items-center">
            <span className="border-2" style={{ width: boxWidth, height: boxHeight, borderColor: color }} />
        </span>
    );
}

function SettingTitle({ children, color }: { children: string; color: string }) {
    return (
        <div className="text-xs font-medium" style={{ color }}>
            {children}
        </div>
    );
}

function alignDimension(value: number, enabled: boolean) {
    return enabled ? Math.ceil(value / DIMENSION_STEP) * DIMENSION_STEP : value;
}
