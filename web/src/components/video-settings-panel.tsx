import { type ReactNode } from "react";
import { Slider } from "antd";
import { useTranslation } from "react-i18next";

import i18n from "@/i18n";
import { ImageSettingsTheme } from "@/components/image-settings-panel";
import { type CanvasTheme } from "@/lib/canvas-theme";
import { computeVideoSize, inferVideoRatio, parseAspectRatio, parseVideoResolution, VIDEO_SECONDS_MAX, VIDEO_SECONDS_MIN, videoRatioOptions } from "@/lib/media-size";
import { getVideoFamily, snapVideoRatio, snapVideoResolution, snapVideoSeconds } from "@/lib/model-families";
import { type AiConfig } from "@/stores/use-config-store";

const resolutionOptions = [
    { value: "480", label: "480p" },
    { value: "720", label: "720p" },
    { value: "1080", label: "1080p" },
];
const videoModeOptions = [
    { value: "frames", labelKey: "frames" },
    { value: "reference", labelKey: "reference" },
];

export const videoResolutionOptions = resolutionOptions.map((item) => ({ value: item.value, label: item.label }));
export const videoSizeOptions = videoRatioOptions.map((item) => ({ value: item.value, get label() { return item.value === "auto" ? i18n.t("settingsPanels.common.auto") : item.value; } }));
export const videoSecondsRange = { min: VIDEO_SECONDS_MIN, max: VIDEO_SECONDS_MAX };

type VideoSettingsPanelProps = {
    config: AiConfig;
    /** Video model used to pick the family options; defaults to config.model. */
    model?: string;
    onConfigChange: (key: "vquality" | "size" | "videoSeconds" | "videoGenerateAudio" | "videoWatermark" | "videoMode", value: string) => void;
    theme: CanvasTheme;
    showTitle?: boolean;
    className?: string;
};

export function VideoSettingsPanel({ config, model, onConfigChange, theme, showTitle = true, className = "w-[320px] space-y-4 rounded-2xl px-1 py-0.5" }: VideoSettingsPanelProps) {
    const { t } = useTranslation();
    const family = getVideoFamily(model || config.model);
    const seconds = snapVideoSeconds(family, config.videoSeconds || "6");
    const secondOptions = Array.isArray(family.seconds) ? family.seconds : undefined;
    const secondRange = (secondOptions ? undefined : family.seconds) as { min: number; max: number } | undefined;
    const modeOptions = videoModeOptions.filter((item) => (item.value === "frames" ? family.frames : family.maxReferences > 0));
    const videoMode = modeOptions.some((item) => item.value === config.videoMode) ? normalizeVideoModeValue(config.videoMode) : modeOptions[0]?.value || "frames";
    const resolution = snapVideoResolution(family, config.vquality);
    const selectedRatio = snapVideoRatio(family, inferVideoRatio(config.size || "auto"));
    const ratioOptions = family.ratios.map((value) => ({ value, ...(parseAspectRatio(value) || { width: 0, height: 0 }) }));
    const veoLocked = family.protocol === "gemini" && (resolution !== "720" || videoMode === "reference");
    const applySize = (nextResolution: string, ratio: string) => {
        onConfigChange("vquality", nextResolution);
        onConfigChange("size", computeVideoSize(nextResolution, ratio));
    };
    const selectResolution = (nextResolution: string) => {
        if (selectedRatio === "auto") onConfigChange("vquality", nextResolution);
        else applySize(nextResolution, selectedRatio);
    };

    return (
        <ImageSettingsTheme theme={theme}>
            <div className={className} style={{ color: theme.node.text }} onMouseDown={(event) => event.stopPropagation()}>
                {showTitle ? <div className="text-lg font-semibold">{t("settingsPanels.video.title")}</div> : null}
                <SettingGroup title={t("settingsPanels.video.quality")} color={theme.node.muted}>
                    <div className="grid grid-cols-4 gap-2.5">
                        {family.resolutions.map((value) => (
                            <OptionPill key={value} selected={resolution === value} theme={theme} onClick={() => selectResolution(value)}>
                                {videoResolutionLabel(value)}
                            </OptionPill>
                        ))}
                    </div>
                </SettingGroup>
                <SettingGroup title={t("settingsPanels.video.ratio")} color={theme.node.muted}>
                    <div className="grid grid-cols-4 gap-2.5">
                        {ratioOptions.map((item) => (
                            <button
                                key={item.value}
                                type="button"
                                className="flex h-[72px] cursor-pointer flex-col items-center justify-center gap-1.5 rounded-xl border bg-transparent text-sm transition hover:opacity-80"
                                style={{ borderColor: selectedRatio === item.value ? theme.node.text : theme.node.stroke, color: theme.node.text }}
                                onMouseDown={(event) => event.stopPropagation()}
                                onClick={() => applySize(resolution, item.value)}
                            >
                                <SizePreview width={item.width} height={item.height} color={theme.node.text} />
                                <span>{item.value === "auto" ? t(family.protocol === "ark" ? "settingsPanels.video.adaptive" : "settingsPanels.common.auto") : item.value}</span>
                            </button>
                        ))}
                    </div>
                </SettingGroup>
                <SettingGroup title={t("settingsPanels.video.seconds")} color={theme.node.muted}>
                    {secondOptions ? (
                        <div className="grid grid-cols-4 gap-2.5">
                            {secondOptions.map((value) => (
                                <OptionPill key={value} selected={(veoLocked ? 8 : seconds) === value} disabled={veoLocked && value !== 8} theme={theme} onClick={() => onConfigChange("videoSeconds", String(value))}>
                                    {value}s
                                </OptionPill>
                            ))}
                        </div>
                    ) : (
                        <div className="flex items-center gap-3" onMouseDown={(event) => event.stopPropagation()}>
                            <Slider className="min-w-0 flex-1" min={secondRange?.min ?? VIDEO_SECONDS_MIN} max={secondRange?.max ?? VIDEO_SECONDS_MAX} step={1} value={seconds} onChange={(value) => onConfigChange("videoSeconds", String(Array.isArray(value) ? value[0] : value))} />
                            <SecondsInput value={seconds} min={secondRange?.min} max={secondRange?.max} theme={theme} onCommit={(value) => onConfigChange("videoSeconds", String(value))} />
                            <span className="shrink-0 text-sm" style={{ color: theme.node.muted }}>s</span>
                        </div>
                    )}
                    {veoLocked ? <div className="text-xs" style={{ color: theme.node.muted }}>{t("settingsPanels.video.veoEightSeconds")}</div> : null}
                </SettingGroup>
                {modeOptions.length > 1 ? <SettingGroup title={t("settingsPanels.video.mode")} color={theme.node.muted}>
                    <div className="grid grid-cols-2 gap-2.5">
                        {modeOptions.map((item) => (
                            <OptionPill key={item.value} selected={videoMode === item.value} theme={theme} onClick={() => onConfigChange("videoMode", item.value)}>
                                {t(`settingsPanels.video.modes.${item.labelKey}`)}
                            </OptionPill>
                        ))}
                    </div>
                </SettingGroup> : null}
            </div>
        </ImageSettingsTheme>
    );
}

/** Summary using the values actually sent for the model family. */
export function videoSettingsSummary(config: AiConfig, model = config.model) {
    const family = getVideoFamily(model);
    const resolution = snapVideoResolution(family, config.vquality);
    const ratio = snapVideoRatio(family, inferVideoRatio(config.size || "auto"));
    const locked = family.protocol === "gemini" && (resolution !== "720" || config.videoMode === "reference");
    return [videoResolutionLabel(resolution), ratio === "auto" ? i18n.t("settingsPanels.video.adaptive") : ratio, `${locked ? 8 : snapVideoSeconds(family, config.videoSeconds || "6")}s`, videoModeLabel(config.videoMode)].join(" · ");
}

export function videoResolutionLabel(value: string) {
    const resolution = parseVideoResolution(value);
    return resolution === "2160" ? "4K" : `${resolution}p`;
}

export function videoSizeLabel(value: string) {
    const ratio = inferVideoRatio(value);
    return ratio === "auto" ? i18n.t("settingsPanels.video.adaptive") : ratio;
}

export function videoSecondsLabel(value: string) {
    if (String(value).trim() === "-1") return i18n.t("settingsPanels.video.smart");
    return `${value || "6"}s`;
}

export function videoModeLabel(value: string) {
    return i18n.t(`settingsPanels.video.modes.${normalizeVideoModeValue(value)}`);
}

export function normalizeVideoModeValue(value: string | undefined) {
    return value === "reference" ? "reference" : "frames";
}

export function normalizeVideoSizeValue(value: string, resolution = "720") {
    if (value === "auto") return "auto";
    if (/^\d+x\d+$/.test(value || "")) return value;
    const ratio = inferVideoRatio(value);
    return ratio === "auto" ? "auto" : computeVideoSize(resolution, ratio);
}

export function normalizeVideoResolutionValue(value: string) {
    return parseVideoResolution(value);
}

function OptionPill({ selected, disabled = false, theme, onClick, children }: { selected: boolean; disabled?: boolean; theme: CanvasTheme; onClick: () => void; children: ReactNode }) {
    return (
        <button type="button" disabled={disabled} className="h-9 cursor-pointer rounded-full border px-2 text-sm transition hover:opacity-80 disabled:cursor-not-allowed disabled:opacity-35" style={{ background: "transparent", borderColor: selected ? theme.node.text : theme.node.stroke, color: theme.node.text }} onMouseDown={(event) => event.stopPropagation()} onClick={onClick}>
            {children}
        </button>
    );
}

function SettingGroup({ title, color, children }: { title: string; color: string; children: ReactNode }) {
    return (
        <div className="space-y-2.5">
            <div className="text-xs font-medium" style={{ color }}>
                {title}
            </div>
            {children}
        </div>
    );
}

function SecondsInput({ value, min = VIDEO_SECONDS_MIN, max = VIDEO_SECONDS_MAX, theme, onCommit }: { value: number; min?: number; max?: number; theme: CanvasTheme; onCommit: (value: number) => void }) {
    const commit = (input: HTMLInputElement) => {
        const next = Math.max(min, Math.min(max, Math.floor(Number(input.value) || value)));
        input.value = String(next);
        onCommit(next);
    };

    return (
        <label className="flex h-9 w-[68px] shrink-0 overflow-hidden rounded-xl text-sm" style={{ background: theme.node.fill, color: theme.node.text }}>
            <input
                type="number"
                min={min}
                max={max}
                className="min-w-0 flex-1 bg-transparent px-2 text-center outline-none [appearance:textfield] [&::-webkit-inner-spin-button]:appearance-none [&::-webkit-outer-spin-button]:appearance-none"
                defaultValue={value}
                key={value}
                onBlur={(event) => commit(event.currentTarget)}
                onKeyDown={(event) => {
                    if (event.key === "Enter") event.currentTarget.blur();
                }}
                onMouseDown={(event) => event.stopPropagation()}
            />
        </label>
    );
}

function SizePreview({ width, height, color }: { width: number; height: number; color: string }) {
    if (!width || !height) return null;
    const longSide = Math.max(width, height);
    const previewWidth = Math.max(10, Math.round((width / longSide) * 26));
    const previewHeight = Math.max(10, Math.round((height / longSide) * 26));
    return <span className="rounded-[3px] border-2" style={{ width: previewWidth, height: previewHeight, borderColor: color }} />;
}
