export type CanvasColorTheme = "light" | "dark";
export type CanvasBackgroundMode = "dots" | "lines" | "blank";

export const canvasThemes = {
    light: {
        canvas: {
            background: "#f5f5f7",
            dot: "rgba(60,60,67,.26)",
            line: "rgba(60,60,67,.10)",
            selectionStroke: "#0071e3",
            selectionFill: "rgba(0,113,227,.08)",
        },
        node: {
            label: "#6e6e73",
            fill: "#e8e8ed",
            panel: "#ffffff",
            stroke: "#d2d2d7",
            activeStroke: "#0071e3",
            placeholder: "#8e8e93",
            text: "#1d1d1f",
            muted: "#6e6e73",
            faint: "#aeaeb2",
        },
        toolbar: {
            panel: "rgba(255,255,255,.9)",
            border: "rgba(60,60,67,.14)",
            item: "#3a3a3c",
            itemHover: "rgba(120,120,128,.14)",
            activeBg: "rgba(120,120,128,.2)",
            activeText: "#1d1d1f",
        },
    },
    dark: {
        canvas: {
            background: "#0f0f11",
            dot: "rgba(235,235,245,.22)",
            line: "rgba(235,235,245,.08)",
            selectionStroke: "#0a84ff",
            selectionFill: "rgba(10,132,255,.14)",
        },
        node: {
            label: "#aeaeb2",
            fill: "#2c2c2e",
            panel: "#1c1c1e",
            stroke: "#38383a",
            activeStroke: "#0a84ff",
            placeholder: "#8e8e93",
            text: "#f5f5f7",
            muted: "#aeaeb2",
            faint: "#636366",
        },
        toolbar: {
            panel: "rgba(30,30,32,.9)",
            border: "rgba(255,255,255,.12)",
            item: "#d1d1d6",
            itemHover: "rgba(120,120,128,.24)",
            activeBg: "rgba(120,120,128,.32)",
            activeText: "#f5f5f7",
        },
    },
} as const;

export type CanvasTheme = (typeof canvasThemes)[CanvasColorTheme];
