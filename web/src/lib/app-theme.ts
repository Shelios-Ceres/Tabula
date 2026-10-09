import type { ThemeConfig } from "antd";
import { theme as antdTheme } from "antd";

const FONT_FAMILY = '-apple-system, BlinkMacSystemFont, "SF Pro Text", "SF Pro Display", "PingFang SC", "Helvetica Neue", "Hiragino Sans GB", "Microsoft YaHei UI", "Microsoft YaHei", system-ui, sans-serif';

// Apple 系统色：冷灰中性色 + 系统蓝主色，弹层底色半透明以配合全局 Liquid Glass 材质。
const palette = {
    light: {
        primary: "#0071e3",
        primaryHover: "#0077ed",
        primaryText: "#ffffff",
        text: "#1d1d1f",
        bgBase: "#ffffff",
        bgContainer: "#ffffff",
        bgLayout: "#f5f5f7",
        border: "#d2d2d7",
        borderSecondary: "#e8e8ed",
        success: "#34c759",
        warning: "#ff9500",
        error: "#ff3b30",
        elevatedBg: "rgba(255, 255, 255, 0.72)",
        itemHoverBg: "rgba(120, 120, 128, 0.12)",
        itemSelectedBg: "rgba(0, 113, 227, 0.1)",
        itemSelectedHoverBg: "rgba(0, 113, 227, 0.14)",
        itemText: "#1d1d1f",
        tableSelectedBg: "rgba(0, 113, 227, 0.06)",
        tableSelectedHoverBg: "rgba(0, 113, 227, 0.1)",
    },
    dark: {
        primary: "#0a84ff",
        primaryHover: "#409cff",
        primaryText: "#ffffff",
        text: "#f5f5f7",
        bgBase: "#000000",
        bgContainer: "#1c1c1e",
        bgLayout: "#000000",
        border: "#38383a",
        borderSecondary: "#2c2c2e",
        success: "#30d158",
        warning: "#ff9f0a",
        error: "#ff453a",
        elevatedBg: "rgba(36, 36, 38, 0.72)",
        itemHoverBg: "rgba(120, 120, 128, 0.24)",
        itemSelectedBg: "rgba(10, 132, 255, 0.2)",
        itemSelectedHoverBg: "rgba(10, 132, 255, 0.26)",
        itemText: "#f5f5f7",
        tableSelectedBg: "rgba(10, 132, 255, 0.12)",
        tableSelectedHoverBg: "rgba(10, 132, 255, 0.18)",
    },
};

export function getAntThemeConfig(dark: boolean): ThemeConfig {
    const color = dark ? palette.dark : palette.light;

    return {
        algorithm: dark ? antdTheme.darkAlgorithm : antdTheme.defaultAlgorithm,
        cssVar: { key: dark ? "infinite-canvas-dark" : "infinite-canvas-light" },
        token: {
            fontFamily: FONT_FAMILY,
            colorPrimary: color.primary,
            colorInfo: color.primary,
            // antd 的全局 a 样式不在 CSS layer 中，会压过 Tailwind 文字色；链接保持正文色，避免导航/Logo/图标链接全部变蓝。
            colorLink: color.text,
            colorLinkHover: color.text,
            colorLinkActive: color.text,
            colorSuccess: color.success,
            colorWarning: color.warning,
            colorError: color.error,
            colorTextBase: color.text,
            colorTextLightSolid: color.primaryText,
            colorBgBase: color.bgBase,
            colorBgContainer: color.bgContainer,
            colorBgLayout: color.bgLayout,
            colorBorder: color.border,
            colorBorderSecondary: color.borderSecondary,
            colorBgElevated: color.elevatedBg,
            controlItemBgHover: color.itemHoverBg,
            controlItemBgActive: color.itemSelectedBg,
            controlItemBgActiveHover: color.itemSelectedHoverBg,
            borderRadius: 10,
            borderRadiusSM: 8,
            borderRadiusLG: 16,
        },
        components: {
            Modal: {
                headerBg: "transparent",
                borderRadiusLG: 24,
            },
            Button: {
                primaryShadow: "none",
                defaultShadow: "none",
                dangerShadow: "none",
                borderRadius: 999,
                borderRadiusSM: 999,
                borderRadiusLG: 999,
                fontWeight: 500,
            },
            Switch: {
                colorPrimary: color.success,
                colorPrimaryHover: color.success,
            },
            Segmented: {
                borderRadius: 999,
                borderRadiusSM: 999,
                borderRadiusLG: 999,
            },
            Dropdown: {
                colorBgElevated: color.elevatedBg,
                colorText: color.itemText,
                controlItemBgHover: color.itemHoverBg,
                controlItemBgActive: color.itemSelectedBg,
                controlItemBgActiveHover: color.itemSelectedHoverBg,
            },
            Menu: {
                popupBg: color.elevatedBg,
                itemActiveBg: color.itemSelectedBg,
                itemHoverBg: color.itemHoverBg,
                itemSelectedBg: color.itemSelectedBg,
                itemSelectedColor: color.itemText,
                darkPopupBg: palette.dark.elevatedBg,
                darkItemHoverBg: palette.dark.itemHoverBg,
                darkItemSelectedBg: palette.dark.itemSelectedBg,
                darkItemSelectedColor: palette.dark.itemText,
            },
            Select: {
                optionActiveBg: color.itemHoverBg,
                optionSelectedBg: color.itemSelectedBg,
                optionSelectedColor: color.itemText,
            },
            Table: {
                rowSelectedBg: color.tableSelectedBg,
                rowSelectedHoverBg: color.tableSelectedHoverBg,
            },
        },
    };
}
