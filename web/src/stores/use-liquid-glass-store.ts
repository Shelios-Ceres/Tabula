import { useMemo } from "react";
import { create } from "zustand";
import { persist } from "zustand/middleware";

// Liquid Glass 材质参数：算法与默认值参照 LiquidGlassWinUI 演示玻璃（DevPage GlassBrush），分两档共用同一套光学参数：
// 通透档（clear*）用于画布胶囊等小控件；常规档（regular*）用于面板、弹窗、菜单等文字较多的地方，靠更强的模糊和染色保证可读。
// 默认值以代码为准（染色与投影由 use-liquid-glass 注入为 CSS 变量），开发环境调参面板只保存改动过的项。
export const LIQUID_GLASS_DEFAULTS = {
    refThickness: 10, // 折射带宽度（px），对应 RefThickness（演示 80px / 400px 玻璃）
    refFactor: 3.37, // 折射率，对应 RefFactor
    refStrength: 0.3, // 折射偏移强度（相对短边一半，最多按 28px 计），决定边缘的放大与翻转程度
    refDispersion: 3.5, // 色散强度，对应 RefDispersion
    dispersionRange: 0.39, // 色散从边缘向内的范围（占折射带比例），对应 DispersionRange
    fresnelRange: 57.84, // 菲涅尔边光范围，对应 RefFresnelRange
    fresnelHardness: 0, // 菲涅尔边光硬度，对应 RefFresnelHardness
    fresnelFactor: 21.96, // 菲涅尔边光强度，对应 RefFresnelFactor
    glareRange: 36.13, // 眩光范围，对应 GlareRange
    glareHardness: 13, // 眩光硬度，对应 GlareHardness
    glareFactor: 71.52, // 眩光强度，对应 GlareFactor
    glareConvergence: 100, // 眩光集中度，对应 GlareConvergence
    glareOpposite: 80, // 对侧眩光强度，对应 GlareOppositeFactor
    glareAngle: -124, // 眩光方向（°），对应 GlareAngle
    exposure: 1.2, // 曝光，对应 Exposure
    contrast: 1, // 对比度，对应 Contrast
    saturate: 1, // 饱和度，对应 Saturation
    clearBlur: 0.76, // 通透档背景模糊（px），对应 BlurAmount
    clearTintLight: 0, // 通透档浅色主题染色透明度（对应 TintA，默认无染色）
    clearTintDark: 0, // 通透档深色主题染色透明度
    clearShadowLight: 0.05, // 通透档浅色主题悬浮投影透明度
    clearShadowDark: 0, // 通透档深色主题悬浮投影透明度
    regularBlur: 14, // 常规档背景模糊（px）
    regularTintLight: 0.62, // 常规档浅色主题染色透明度
    regularTintDark: 0.58, // 常规档深色主题染色透明度
    regularShadowLight: 0.08, // 常规档浅色主题悬浮投影透明度
    regularShadowDark: 0.35, // 常规档深色主题悬浮投影透明度
};

export type LiquidGlassParams = typeof LIQUID_GLASS_DEFAULTS;

type LiquidGlassStore = {
    overrides: Partial<LiquidGlassParams>;
    setParam: (key: keyof LiquidGlassParams, value: number) => void;
    reset: () => void;
};

export const useLiquidGlassStore = create<LiquidGlassStore>()(
    persist(
        (set) => ({
            overrides: {},
            setParam: (key, value) => set((state) => ({ overrides: { ...state.overrides, [key]: value } })),
            reset: () => set({ overrides: {} }),
        }),
        { name: "infinite-canvas:liquid_glass_store" },
    ),
);

export function useLiquidGlassParams(): LiquidGlassParams {
    const overrides = useLiquidGlassStore((state) => state.overrides);
    // 调参面板的改动只在开发环境生效
    return useMemo(() => (import.meta.env.DEV ? { ...LIQUID_GLASS_DEFAULTS, ...overrides } : LIQUID_GLASS_DEFAULTS), [overrides]);
}
