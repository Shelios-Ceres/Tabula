import { useEffect, useRef, useState, type RefObject } from "react";
import { createPortal } from "react-dom";
import { Settings2 } from "lucide-react";
import { Button, ConfigProvider } from "antd";
import { useTranslation } from "react-i18next";

import { ImageSettingsPanel, imageQualityLabel, imageSizeLabel, type ImageSettingKey, type ImageSettingsChange } from "@/components/image-settings-panel";
import i18n from "@/i18n";
import { canvasThemes } from "@/lib/canvas-theme";
import { resolveNativeImageCount, resolveNativeImageOptions } from "@/lib/image-model-capabilities";
import { useThemeStore } from "@/stores/use-theme-store";
import { resolveImageProvider, type AiConfig } from "@/stores/use-config-store";
import type { NativeImageOptions } from "@/types/image-generation";

type CanvasImageSettingsPopoverProps = {
    config: AiConfig;
    onConfigChange: ImageSettingsChange;
    onNativeChange?: (value: NativeImageOptions) => void;
    referenceCount?: number;
    overriddenKeys?: readonly ImageSettingKey[];
    onResetOverrides?: () => void;
    onMissingConfig?: () => void;
    onOpenChange?: (open: boolean) => void;
    buttonClassName?: string;
    getPopupContainer?: (triggerNode: HTMLElement) => HTMLElement;
    placement?: "topLeft" | "top" | "topRight" | "bottomLeft" | "bottom" | "bottomRight";
    autoAdjustOverflow?: boolean;
};

export function CanvasImageSettingsPopover({ config, onConfigChange, onNativeChange, referenceCount = 0, overriddenKeys, onResetOverrides, onOpenChange, buttonClassName, placement = "topLeft" }: CanvasImageSettingsPopoverProps) {
    const { t } = useTranslation();
    const theme = canvasThemes[useThemeStore((state) => state.theme)];
    const buttonRef = useRef<HTMLSpanElement>(null);
    const panelRef = useRef<HTMLDivElement>(null);
    const [open, setOpen] = useState(false);
    const [buttonRect, setButtonRect] = useState<DOMRect | null>(null);
    const quality = config.quality || "auto";
    const count = Math.max(1, Math.min(15, Math.floor(Math.abs(Number(config.count)) || 1)));
    const activeSize = config.size || "auto";
    const updateOpen = (nextOpen: boolean) => {
        setOpen(nextOpen);
        onOpenChange?.(nextOpen);
    };

    useEffect(() => {
        if (!open) return;
        const syncPosition = () => setButtonRect(buttonRef.current?.getBoundingClientRect() || null);
        const closeOnOutsidePointer = (event: PointerEvent) => {
            const target = event.target;
            if (!(target instanceof Node)) return;
            if (buttonRef.current?.contains(target) || panelRef.current?.contains(target)) return;
            if (document.activeElement instanceof HTMLElement && panelRef.current?.contains(document.activeElement)) document.activeElement.blur();
            setOpen(false);
            onOpenChange?.(false);
        };

        syncPosition();
        window.addEventListener("resize", syncPosition);
        window.addEventListener("scroll", syncPosition, true);
        window.addEventListener("pointerdown", closeOnOutsidePointer, true);
        return () => {
            window.removeEventListener("resize", syncPosition);
            window.removeEventListener("scroll", syncPosition, true);
            window.removeEventListener("pointerdown", closeOnOutsidePointer, true);
        };
    }, [onOpenChange, open]);

    const provider = resolveImageProvider(config, config.model);
    const native = resolveNativeImageOptions(provider, config.model, config.imageNative);
    const effectiveCount = resolveNativeImageCount(provider, config.model, native, count, referenceCount);
    const summary = provider === "google" ? `${native.google?.imageSize || "1K"} · ${native.google?.aspectRatio || "1:1"}` : provider === "ark" ? `${native.ark?.size || "2K"} · ${t("canvas.controls.images", { count: effectiveCount })}` : provider === "xai" ? `${native.xai?.resolution || "1k"} · ${native.xai?.aspectRatio && native.xai.aspectRatio !== "auto" ? native.xai.aspectRatio : t("settingsPanels.common.auto")} · ${t("canvas.controls.images", { count: effectiveCount })}` : `${imageQualityLabel(quality)} · ${imageSizeLabel(activeSize)} · ${t("canvas.controls.images", { count: effectiveCount })} · ${config.outputFormat.toUpperCase()}`;
    const panel = open && buttonRect ? <ImageSettingsPortal buttonRect={buttonRect} panelRef={panelRef} placement={placement} theme={theme} config={config} referenceCount={referenceCount} overriddenKeys={overriddenKeys} onResetOverrides={onResetOverrides} onConfigChange={onConfigChange} onNativeChange={onNativeChange} /> : null;

    return (
        <>
            <span ref={buttonRef} className="inline-flex min-w-0">
                <Button size="small" type="text" className={buttonClassName || "!h-8 !max-w-[210px] !justify-start !rounded-full !bg-transparent !px-2.5 hover:!bg-black/5 dark:hover:!bg-white/10"} style={{ color: theme.node.text }} icon={<Settings2 className="size-3.5" />} onClick={() => updateOpen(!open)} title={summary} aria-expanded={open}>
                    <span className="truncate">
                        {summary}
                    </span>
                </Button>
            </span>
            {panel}
        </>
    );
}

function ImageSettingsPortal({
    buttonRect,
    panelRef,
    placement,
    theme,
    config,
    referenceCount,
    overriddenKeys,
    onResetOverrides,
    onConfigChange,
    onNativeChange,
}: {
    buttonRect: DOMRect;
    panelRef: RefObject<HTMLDivElement | null>;
    placement: CanvasImageSettingsPopoverProps["placement"];
    theme: (typeof canvasThemes)[keyof typeof canvasThemes];
    config: AiConfig;
    referenceCount: number;
    overriddenKeys?: readonly ImageSettingKey[];
    onResetOverrides?: () => void;
    onConfigChange: ImageSettingsChange;
    onNativeChange?: (value: NativeImageOptions) => void;
}) {
    const width = Math.min(356, window.innerWidth - 24);
    const gap = 8;
    const margin = 12;
    const alignRight = placement?.endsWith("Right");
    const alignCenter = placement === "top" || placement === "bottom";
    const left = alignCenter ? buttonRect.left + buttonRect.width / 2 - width / 2 : alignRight ? buttonRect.right - width : buttonRect.left;
    const topPlacement = placement?.startsWith("top");
    const style = {
        position: "fixed",
        zIndex: 1200,
        width,
        left: Math.max(margin, Math.min(window.innerWidth - width - margin, left)),
        ...(topPlacement ? { bottom: window.innerHeight - buttonRect.top + gap, maxHeight: Math.max(260, buttonRect.top - margin * 2) } : { top: buttonRect.bottom + gap, maxHeight: Math.max(260, window.innerHeight - buttonRect.bottom - margin * 2) }),
        borderRadius: 18,
        color: theme.node.text,
    } as const;

    return createPortal(
        <div
            ref={panelRef}
            className="canvas-image-settings-popover liquid-glass liquid-glass-regular"
            style={style}
            onPointerDown={(event) => event.stopPropagation()}
            onMouseDown={(event) => event.stopPropagation()}
            onClick={(event) => event.stopPropagation()}
        >
            {/* Dropdowns stay inside the dismiss boundary, outside the scroll clipping. */}
            <ConfigProvider getPopupContainer={() => panelRef.current || document.body}>
                <div style={{ maxHeight: style.maxHeight, overflowY: "auto", padding: 18, borderRadius: "inherit" }}>
                    {overriddenKeys ? (
                        <div className="mb-3 flex items-center justify-between gap-3 border-b pb-3 text-xs" style={{ borderColor: theme.node.stroke, color: theme.node.muted }}>
                            <span>{overriddenKeys.length ? i18n.t("settingsPanels.image.nodeOverrides", { count: overriddenKeys.length }) : i18n.t("settingsPanels.image.inheritsGlobal")}</span>
                            {overriddenKeys.length && onResetOverrides ? (
                                <Button size="small" type="text" className="!h-7 !px-1.5" onClick={onResetOverrides}>{i18n.t("settingsPanels.image.resetGlobal")}</Button>
                            ) : null}
                        </div>
                    ) : null}
                    <ImageSettingsPanel config={config} model={config.model} referenceCount={referenceCount} onConfigChange={onConfigChange} onNativeChange={onNativeChange} theme={theme} className="space-y-4" />
                </div>
            </ConfigProvider>
        </div>,
        document.body,
    );
}
