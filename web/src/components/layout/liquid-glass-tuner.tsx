import { useState } from "react";
import { Button, Slider } from "antd";
import { Copy, RotateCcw, SlidersHorizontal, X } from "lucide-react";

import { useCopyText } from "@/hooks/use-copy-text";
import { canvasThemes } from "@/lib/canvas-theme";
import { LIQUID_GLASS_DEFAULTS, useLiquidGlassParams, useLiquidGlassStore, type LiquidGlassParams } from "@/stores/use-liquid-glass-store";
import { useThemeStore } from "@/stores/use-theme-store";

// 开发环境专用：全站实时调节 Liquid Glass 参数（括号内为 LiquidGlassWinUI 对应属性），调好后「复制参数」写回 LIQUID_GLASS_DEFAULTS。
type TunerItem = { key: keyof LiquidGlassParams; label: string; min: number; max: number; step: number; unit?: string };

const tierItems = (tier: "clear" | "regular", blurMax: number): TunerItem[] => [
    { key: `${tier}Blur`, label: "背景模糊", min: 0, max: blurMax, step: 0.01, unit: "px" },
    { key: `${tier}TintLight`, label: "浅色染色", min: 0, max: 1, step: 0.01 },
    { key: `${tier}TintDark`, label: "深色染色", min: 0, max: 1, step: 0.01 },
    { key: `${tier}ShadowLight`, label: "浅色投影", min: 0, max: 0.4, step: 0.01 },
    { key: `${tier}ShadowDark`, label: "深色投影", min: 0, max: 0.6, step: 0.01 },
];

const TUNER_GROUPS: { title: string; items: TunerItem[] }[] = [
    {
        title: "折射（RefThickness / RefFactor / RefDispersion）",
        items: [
            { key: "refThickness", label: "折射带宽度", min: 0, max: 30, step: 0.5, unit: "px" },
            { key: "refFactor", label: "折射率", min: 1, max: 4, step: 0.01 },
            { key: "refStrength", label: "偏移强度", min: 0, max: 1, step: 0.01 },
            { key: "refDispersion", label: "色散强度", min: 0, max: 20, step: 0.1 },
            { key: "dispersionRange", label: "色散范围", min: 0, max: 1, step: 0.01 },
        ],
    },
    {
        title: "菲涅尔边光（RefFresnel*）",
        items: [
            { key: "fresnelRange", label: "范围", min: 0, max: 100, step: 0.1 },
            { key: "fresnelHardness", label: "硬度", min: 0, max: 100, step: 1 },
            { key: "fresnelFactor", label: "强度", min: 0, max: 100, step: 0.1 },
        ],
    },
    {
        title: "眩光（Glare*）",
        items: [
            { key: "glareRange", label: "范围", min: 0, max: 100, step: 0.1 },
            { key: "glareHardness", label: "硬度", min: 0, max: 100, step: 1 },
            { key: "glareFactor", label: "强度", min: 0, max: 100, step: 0.1 },
            { key: "glareConvergence", label: "集中度", min: 0, max: 100, step: 1 },
            { key: "glareOpposite", label: "对侧强度", min: 0, max: 100, step: 1 },
            { key: "glareAngle", label: "方向", min: -180, max: 180, step: 1, unit: "°" },
        ],
    },
    {
        title: "背景处理（Exposure / Contrast / Saturation）",
        items: [
            { key: "exposure", label: "曝光", min: 0.5, max: 2, step: 0.01 },
            { key: "contrast", label: "对比度", min: 0, max: 2, step: 0.01 },
            { key: "saturate", label: "饱和度", min: 0, max: 2, step: 0.01 },
        ],
    },
    { title: "通透档（画布胶囊等小控件）", items: tierItems("clear", 10) },
    { title: "常规档（面板、弹窗、菜单）", items: tierItems("regular", 40) },
];

export function LiquidGlassTuner() {
    const [open, setOpen] = useState(false);
    const params = useLiquidGlassParams();
    const setParam = useLiquidGlassStore((state) => state.setParam);
    const reset = useLiquidGlassStore((state) => state.reset);
    const copyText = useCopyText();
    const theme = canvasThemes[useThemeStore((state) => state.theme)];

    return (
        <div data-canvas-no-zoom className="fixed bottom-24 right-4 z-[1100]" onMouseDown={(event) => event.stopPropagation()} onPointerDown={(event) => event.stopPropagation()}>
            {open ? (
                <div className="liquid-glass liquid-glass-regular thin-scrollbar flex max-h-[calc(100vh-140px)] w-72 flex-col gap-3 overflow-y-auto rounded-[22px] border p-4" style={{ color: theme.node.text }}>
                    <div className="flex items-center justify-between">
                        <span className="text-sm font-semibold">玻璃参数</span>
                        <button type="button" className="grid size-7 place-items-center rounded-full transition hover:bg-black/5 dark:hover:bg-white/10" onClick={() => setOpen(false)} aria-label="收起">
                            <X className="size-4" />
                        </button>
                    </div>
                    {TUNER_GROUPS.map((group) => (
                        <div key={group.title} className="flex flex-col gap-1">
                            <span className="text-[11px] font-medium opacity-50">{group.title}</span>
                            {group.items.map((item) => {
                                const value = params[item.key];
                                return (
                                    <div key={item.key}>
                                        <div className="flex items-center justify-between text-xs">
                                            <span className="opacity-75">{item.label}</span>
                                            <span className="tabular-nums" style={{ color: value === LIQUID_GLASS_DEFAULTS[item.key] ? theme.node.muted : theme.node.activeStroke }}>
                                                {value}
                                                {item.unit}
                                            </span>
                                        </div>
                                        <Slider className="!mx-1 !my-1.5" min={item.min} max={item.max} step={item.step} value={value} tooltip={{ formatter: null }} onChange={(next) => setParam(item.key, next)} />
                                    </div>
                                );
                            })}
                        </div>
                    ))}
                    <div className="flex gap-1">
                        <Button type="text" className="flex-1" icon={<Copy className="size-3.5" />} onClick={() => copyText(JSON.stringify(params, null, 4), "已复制玻璃参数")}>
                            复制参数
                        </Button>
                        <Button type="text" className="flex-1" icon={<RotateCcw className="size-3.5" />} onClick={reset}>
                            恢复默认
                        </Button>
                    </div>
                </div>
            ) : (
                <button type="button" className="liquid-glass flex h-9 items-center gap-1.5 rounded-full border px-3 text-xs font-medium" style={{ color: theme.node.text }} onClick={() => setOpen(true)}>
                    <SlidersHorizontal className="size-4" />
                    玻璃参数
                </button>
            )}
        </div>
    );
}
