import { Button, Input, InputNumber, Segmented, Select, Switch } from "antd";
import { useEffect, type ReactNode } from "react";
import { useTranslation } from "react-i18next";

import type { CanvasTheme } from "@/lib/canvas-theme";
import { arkImageRatios, arkImageSizePresets, findArkImageSizePreset, getNativeImageCapabilities, resolveNativeImageOutputFormat } from "@/lib/image-model-capabilities";
import { modelOptionName, resolveImageProvider, resolveSameChannelMainModel, sameChannelTextModels, snapshotImageNativeOptions, type AiConfig } from "@/stores/use-config-store";
import type { NativeImageOptions } from "@/types/image-generation";
import type { ImageOutputFormat } from "@/types/image";

export function NativeImageSettings({ config, model, theme, onChange, onOutputFormatChange }: { config: AiConfig; model: string; theme: CanvasTheme; onChange: (value: NativeImageOptions) => void; onOutputFormatChange: (value: ImageOutputFormat) => void }) {
    const { t } = useTranslation();
    const provider = resolveImageProvider(config, model);
    const capabilities = getNativeImageCapabilities(provider, model);
    const native = config.imageNative;
    const textModels = provider === "openai" ? sameChannelTextModels(config, model) : [];
    const googleSize = capabilities.sizes.includes(native.google?.imageSize || "") ? native.google?.imageSize : capabilities.sizes[0];
    const googleRatio = capabilities.ratios.includes(native.google?.aspectRatio || "") ? native.google?.aspectRatio : capabilities.ratios[0];
    const googleThinking = capabilities.thinkingLevels.includes(native.google?.thinkingLevel || "") ? native.google?.thinkingLevel : "auto";
    const arkPreset = findArkImageSizePreset(native.ark?.size || "");
    const arkResolution = arkPreset ? (capabilities.sizes.includes(arkPreset.resolution) ? arkPreset.resolution : capabilities.sizes[0]) : capabilities.sizes.includes(native.ark?.size || "") ? native.ark?.size : /^\d+(?:\.\d+)?K$/i.test(native.ark?.size || "") ? capabilities.sizes[0] : undefined;
    const arkSize = arkPreset && arkResolution ? arkImageSizePresets[arkResolution][arkPreset.ratio] : arkResolution || native.ark?.size || capabilities.sizes[0];
    const arkRatioResolution = arkResolution || "2K";
    const arkRatio = arkPreset?.ratio || (arkResolution || !native.ark?.size ? "auto" : "custom");
    const arkEndpoint = provider === "ark" ? snapshotImageNativeOptions(config, model, native).ark?.endpoint : undefined;
    const outputFormat = resolveNativeImageOutputFormat(provider, model, native, config.outputFormat);

    useEffect(() => {
        if (outputFormat !== config.outputFormat) onOutputFormatChange(outputFormat);
    }, [config.outputFormat, onOutputFormatChange, outputFormat]);
    const patch = (value: Partial<NativeImageOptions>) => onChange({ ...native, ...value });
    const patchOpenAi = (value: Partial<NonNullable<NativeImageOptions["openai"]>>) => patch({ openai: { ...native.openai, ...value } });
    const patchGoogle = (value: Partial<NonNullable<NativeImageOptions["google"]>>) => patch({ google: { ...native.google, ...value } });
    const patchArk = (value: Partial<NonNullable<NativeImageOptions["ark"]>>) => patch({ ark: { ...native.ark, ...value } });
    const patchXai = (value: Partial<NonNullable<NativeImageOptions["xai"]>>) => patch({ xai: { ...native.xai, ...value } });

    if (!capabilities.supported) return null;

    return (
        <div className="space-y-4 border-t pt-4" style={{ borderColor: theme.node.stroke }}>
            <div>
                <div className="text-sm font-semibold">{t("nativeImage.title")}</div>
                <div className="mt-1 text-xs leading-5" style={{ color: theme.node.muted }}>{t(`nativeImage.providers.${provider}`)}</div>
            </div>

            {provider === "openai" ? (
                <>
                    <Field label={t("nativeImage.openai.api")}>
                        <Segmented block size="small" value={native.openai?.api || "images"} options={[{ value: "images", label: t("nativeImage.openai.images") }, { value: "responses", label: t("nativeImage.openai.responses") }]} onChange={(value) => { const api = value as "images" | "responses"; patchOpenAi({ api, ...(api === "responses" ? { mainModel: resolveSameChannelMainModel(config, model, native.openai?.mainModel) } : {}) }); }} />
                    </Field>
                    {native.openai?.api === "responses" ? (
                        <>
                            <Field label={t("nativeImage.openai.mainModel")} hint={t("nativeImage.openai.mainModelHint")}>
                                <Select className="w-full" value={resolveSameChannelMainModel(config, model, native.openai.mainModel)} options={textModels.map((value) => ({ value, label: value }))} onChange={(mainModel) => patchOpenAi({ mainModel })} />
                                {!textModels.length ? <span className="block text-xs text-red-500">{t("nativeImage.openai.mainModelMissing")}</span> : null}
                            </Field>
                            <Toggle label={t("nativeImage.store")} hint={t("nativeImage.storeHint")} checked={Boolean(native.openai.store)} onChange={(store) => patchOpenAi({ store })} />
                            <div className="text-xs leading-5" style={{ color: theme.node.muted }}>{t("nativeImage.openai.responsesSingleRequest")}</div>
                        </>
                    ) : null}
                    {capabilities.supportsStream ? <Toggle label={t("nativeImage.stream")} hint={t("nativeImage.streamHint")} checked={Boolean(native.stream)} onChange={(stream) => patch({ stream })} /> : null}
                    {native.openai?.api !== "responses" && native.stream ? (
                        <Field label={t("nativeImage.openai.partialImages")} hint={t("nativeImage.openai.partialImagesHint")}>
                            <InputNumber className="w-full" min={0} max={3} value={native.openai?.partialImages || 0} onChange={(partialImages) => patchOpenAi({ partialImages: partialImages || 0 })} />
                        </Field>
                    ) : null}
                </>
            ) : null}

            {provider === "google" ? (
                <>
                    <OutputFormatField values={["jpeg"]} value={outputFormat} hint={t("nativeImage.google.jpegOnly")} onChange={onOutputFormatChange} />
                    <ChoiceField label={t("nativeImage.imageSize")} values={capabilities.sizes} value={googleSize} onChange={(imageSize) => patchGoogle({ imageSize: imageSize as NonNullable<NativeImageOptions["google"]>["imageSize"] })} />
                    <Field label={t("nativeImage.aspectRatio")}>
                        <Select className="w-full" value={googleRatio} options={capabilities.ratios.map((value) => ({ value, label: value }))} onChange={(aspectRatio) => patchGoogle({ aspectRatio })} />
                    </Field>
                    {capabilities.supportsThinking && capabilities.thinkingLevels.length > 1 ? (
                        <Field label={t("nativeImage.google.thinking")}>
                            <Select className="w-full" value={googleThinking} options={capabilities.thinkingLevels.map((value) => ({ value, label: t(`nativeImage.thinking.${value}`) }))} onChange={(thinkingLevel) => patchGoogle({ thinkingLevel: thinkingLevel as NonNullable<NativeImageOptions["google"]>["thinkingLevel"] })} />
                        </Field>
                    ) : capabilities.supportsThinking ? (
                        <div className="space-y-1 text-xs">
                            <div className="font-medium">{t("nativeImage.google.thinkingFixed")}</div>
                            <div style={{ color: theme.node.muted }}>{t("nativeImage.google.thinkingFixedHint")}</div>
                        </div>
                    ) : null}
                    {capabilities.supportsWebSearch ? <Toggle label={t("nativeImage.webSearch")} checked={Boolean(native.google?.webSearch)} onChange={(webSearch) => patchGoogle({ webSearch })} /> : null}
                    {capabilities.supportsImageSearch ? <Toggle label={t("nativeImage.imageSearch")} checked={Boolean(native.google?.imageSearch)} onChange={(imageSearch) => patchGoogle({ imageSearch })} /> : null}
                    <Toggle label={t("nativeImage.google.includeText")} hint={t("nativeImage.google.includeTextHint")} checked={Boolean(native.google?.includeText)} onChange={(includeText) => patchGoogle({ includeText })} />
                    {capabilities.supportsStream ? <Toggle label={t("nativeImage.stream")} hint={t("nativeImage.streamHint")} checked={Boolean(native.stream)} onChange={(stream) => patch({ stream })} /> : null}
                    <Toggle label={t("nativeImage.store")} hint={t("nativeImage.storeHint")} checked={Boolean(native.google?.store)} onChange={(store) => patchGoogle({ store })} />
                    <div className="text-xs leading-5" style={{ color: theme.node.muted }}>{t("nativeImage.google.singleRequest")}</div>
                </>
            ) : null}

            {provider === "ark" ? (
                <>
                    <div className="flex items-start justify-between gap-3">
                        <div className="min-w-0">
                            <div className="text-xs font-medium">{t("nativeImage.ark.endpoint")}</div>
                            {arkEndpoint?.id ? (
                                <code className="mt-1 block truncate text-xs" style={{ color: theme.node.muted }} title={arkEndpoint.id}>{arkEndpoint.id}</code>
                            ) : (
                                <div className="mt-1 text-xs" style={{ color: theme.node.muted }}>{t("nativeImage.ark.officialModelId")}</div>
                            )}
                        </div>
                        {native.ark?.endpoint ? <Button type="link" size="small" className="h-auto shrink-0 p-0" onClick={() => patchArk({ endpoint: undefined })}>{t("nativeImage.ark.useChannelEndpoint")}</Button> : null}
                    </div>
                    <OutputFormatField values={capabilities.outputFormats} value={outputFormat} disableJpeg={Boolean(native.ark?.transparent)} onChange={onOutputFormatChange} />
                    <Field label={t("nativeImage.imageSize")} hint={t("nativeImage.ark.sizeHint")}>
                        <Segmented block size="small" value={arkResolution || ""} options={capabilities.sizes.map((item) => ({ value: item, label: item }))} onChange={(size) => patchArk({ size: arkPreset ? arkImageSizePresets[String(size)][arkPreset.ratio] : String(size) })} />
                        <Input value={arkSize} placeholder="2048x2048" onChange={(event) => patchArk({ size: event.target.value })} />
                    </Field>
                    <Field label={t("nativeImage.ark.ratioPreset")} hint={t("nativeImage.ark.ratioPresetHint")}>
                        <Select className="w-full" value={arkRatio} options={[
                            { value: "auto", label: t("nativeImage.ark.autoRatio", { resolution: arkRatioResolution }) },
                            ...arkImageRatios.map((ratio) => ({ value: ratio, label: `${ratio} · ${arkImageSizePresets[arkRatioResolution][ratio].replace("x", " × ")}` })),
                            ...(arkRatio === "custom" ? [{ value: "custom", label: t("nativeImage.ark.customSize"), disabled: true }] : []),
                        ]} onChange={(ratio) => patchArk({ size: ratio === "auto" ? arkRatioResolution : arkImageSizePresets[arkRatioResolution][ratio] })} />
                    </Field>
                    <Toggle label={t("nativeImage.ark.watermark")} hint={t("nativeImage.ark.watermarkHint")} checked={native.ark?.watermark === true} onChange={(watermark) => patchArk({ watermark })} />
                    <Field label={t("nativeImage.ark.optimizePrompt")}>
                        <Segmented block size="small" value={capabilities.supportsSequential ? "standard" : native.ark?.optimizePrompt || "standard"} options={(capabilities.supportsSequential ? ["standard"] : ["standard", "fast"]).map((value) => ({ value, label: t(`nativeImage.ark.${value}`) }))} onChange={(optimizePrompt) => patchArk({ optimizePrompt: optimizePrompt as "standard" | "fast" })} />
                    </Field>
                    {capabilities.supportsSequential ? <Toggle label={t("nativeImage.ark.sequential")} hint={t("nativeImage.ark.sequentialHint")} checked={Boolean(native.ark?.sequential)} onChange={(sequential) => patchArk({ sequential })} /> : null}
                    {capabilities.supportsWebSearch ? <Toggle label={t("nativeImage.webSearch")} checked={Boolean(native.ark?.webSearch)} onChange={(webSearch) => patchArk({ webSearch })} /> : null}
                    {capabilities.supportsStream ? <Toggle label={t("nativeImage.stream")} hint={t("nativeImage.streamHint")} checked={Boolean(native.stream)} onChange={(stream) => patch({ stream })} /> : null}
                    {capabilities.supportsLayers ? (
                        <>
                            <Toggle label={t("nativeImage.ark.layers")} hint={t("nativeImage.ark.layersHint")} checked={Boolean(native.ark?.layerDecomposition)} onChange={(layerDecomposition) => patchArk({ layerDecomposition })} />
                            <Toggle label={t("nativeImage.ark.transparentLayers")} hint={t("nativeImage.ark.transparentLayersHint")} checked={Boolean(native.ark?.transparent)} onChange={(transparent) => patchArk({ transparent })} />
                        </>
                    ) : null}
                    {capabilities.maxImages === 1 ? <div className="text-xs leading-5" style={{ color: theme.node.muted }}>{t("nativeImage.ark.proSingleRequest")}</div> : null}
                </>
            ) : null}

            {provider === "xai" ? (
                <>
                    <ChoiceField label={t("nativeImage.imageSize")} values={capabilities.sizes} value={native.xai?.resolution || capabilities.sizes[0]} onChange={(resolution) => patchXai({ resolution: resolution as NonNullable<NativeImageOptions["xai"]>["resolution"] })} />
                    <Field label={t("nativeImage.aspectRatio")}>
                        <Select className="w-full" value={native.xai?.aspectRatio || "auto"} options={[{ value: "auto", label: t("settingsPanels.common.auto") }, ...capabilities.ratios.map((value) => ({ value, label: value }))]} onChange={(aspectRatio) => patchXai({ aspectRatio })} />
                    </Field>
                </>
            ) : null}
        </div>
    );
}

function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
    return <label className="block space-y-2"><span className="block text-xs font-medium">{label}</span>{children}{hint ? <span className="block text-xs opacity-60">{hint}</span> : null}</label>;
}

function ChoiceField({ label, values, value, onChange }: { label: string; values: readonly string[]; value?: string; onChange: (value: string) => void }) {
    return <Field label={label}><Segmented block size="small" value={value} options={values.map((item) => ({ value: item, label: item }))} onChange={(next) => onChange(String(next))} /></Field>;
}

function Toggle({ label, hint, checked, onChange }: { label: string; hint?: string; checked: boolean; onChange: (value: boolean) => void }) {
    return <div className="flex items-start justify-between gap-4"><div><div className="text-xs font-medium">{label}</div>{hint ? <div className="mt-1 text-xs opacity-60">{hint}</div> : null}</div><Switch size="small" checked={checked} onChange={onChange} /></div>;
}

function OutputFormatField({ values, value, disableJpeg = false, hint, onChange }: { values: readonly ImageOutputFormat[]; value: ImageOutputFormat; disableJpeg?: boolean; hint?: string; onChange: (value: ImageOutputFormat) => void }) {
    const { t } = useTranslation();
    return (
        <Field label={t("settingsPanels.image.outputFormat")} hint={disableJpeg ? t("nativeImage.ark.transparentPngOnly") : hint}>
            <Segmented block size="small" value={value} options={values.map((item) => ({ value: item, label: item.toUpperCase(), disabled: disableJpeg && item === "jpeg" }))} onChange={(next) => onChange(next as ImageOutputFormat)} />
        </Field>
    );
}

