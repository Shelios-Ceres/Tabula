import { Button, Drawer, Input, Segmented, Select, Space } from "antd";
import { ListPlus, Sparkles, Trash2 } from "lucide-react";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { guessCapability, normalizeChannelModels, vendorBaseUrls, type ChannelModel, type ChannelVendor, type GoogleApiVersion, type ModelCapability, type ModelChannel } from "@/stores/use-config-store";
import { findImageFamily, getVideoFamily, vendorRecommendedModels } from "@/lib/model-families";
import { ModelScriptEditor } from "./model-script-editor";
import { ModelSelectModal } from "./model-select-modal";

type ScriptTarget = { name: string; capability: ModelCapability; value: string };

const vendors: ChannelVendor[] = ["openai", "google", "ark", "xai"];
const apiVersions: GoogleApiVersion[] = ["v1beta", "v1"];

export function ChannelEditorDrawer({ open, channel, onSave, onClose }: { open: boolean; channel: ModelChannel | null; onSave: (channel: ModelChannel) => void; onClose: () => void }) {
    const { t } = useTranslation();
    const [draft, setDraft] = useState<ModelChannel | null>(channel);
    const [selectOpen, setSelectOpen] = useState(false);
    const [scriptTarget, setScriptTarget] = useState<ScriptTarget | null>(null);
    const capabilityOptions: Array<{ label: string; value: ModelCapability }> = ["image", "video", "text", "audio"].map((value) => ({ label: t(`config.channelEditor.capabilities.${value}`), value: value as ModelCapability }));

    useEffect(() => {
        if (open && channel) setDraft(channel);
    }, [open, channel]);

    if (!draft) return null;

    const patch = (value: Partial<ModelChannel>) => setDraft((current) => (current ? { ...current, ...value } : current));
    const setModels = (models: ChannelModel[]) => patch({ models });
    const missingRecommended = vendorRecommendedModels[draft.vendor].filter((name) => !draft.models.some((model) => model.name === name));
    const apiVersion = draft.apiVersion || "v1beta";

    // Keep a hand-typed address; only replace it while it is empty or still the previous vendor's default.
    const changeVendor = (vendor: ChannelVendor) => {
        const baseUrl = !draft.baseUrl.trim() || draft.baseUrl.trim() === vendorBaseUrls[draft.vendor] ? vendorBaseUrls[vendor] : draft.baseUrl;
        patch({ vendor, baseUrl, apiVersion: vendor === "google" ? apiVersion : undefined });
    };

    const applySelection = (names: string[]) => {
        const map = new Map(draft.models.map((model) => [model.name, model]));
        setModels(names.map((name) => map.get(name) || { name, capability: guessCapability(name) }));
    };

    const setCapability = (name: string, capability: ModelCapability) => setModels(draft.models.map((model) => (model.name === name ? { ...model, capability } : model)));
    const setEndpointId = (name: string, endpointId: string) => setModels(draft.models.map((model) => (model.name === name ? { ...model, endpointId: endpointId || undefined } : model)));
    const setScript = (name: string, script: string) => setModels(draft.models.map((model) => (model.name === name ? { ...model, script: script || undefined } : model)));
    const removeModel = (name: string) => setModels(draft.models.filter((model) => model.name !== name));
    const addRecommendedModels = () => setModels([...draft.models, ...missingRecommended.map((name) => ({ name, capability: guessCapability(name) }))]);

    const save = () => {
        onSave({ ...draft, name: draft.name.trim() || t("config.channels.unnamed"), baseUrl: draft.baseUrl.trim() || vendorBaseUrls[draft.vendor], models: normalizeChannelModels(draft.models) });
        onClose();
    };

    return (
        <Drawer
            open={open}
            size={640}
            title={t("config.channelEditor.title")}
            onClose={onClose}
            styles={{ body: { paddingTop: 16 } }}
            extra={
                <Space>
                    <Button onClick={onClose}>{t("common.cancel")}</Button>
                    <Button type="primary" onClick={save}>
                        {t("common.save")}
                    </Button>
                </Space>
            }
        >
            <div className="grid gap-4 md:grid-cols-2">
                <label className="block">
                    <span className="mb-1 block text-sm font-medium">{t("config.channelEditor.name")}</span>
                    <Input value={draft.name} onChange={(event) => patch({ name: event.target.value })} />
                </label>
                <label className="block">
                    <span className="mb-1 block text-sm font-medium">{t("config.channelEditor.vendor")}</span>
                    <Select className="w-full" value={draft.vendor} options={vendors.map((value) => ({ value, label: t(`config.channelEditor.vendors.${value}`) }))} onChange={changeVendor} />
                </label>
                <span className="-mt-2 block text-xs text-stone-500 md:col-span-2">{t("config.channelEditor.vendorHint")}</span>
                {draft.vendor === "google" ? (
                    <label className="block md:col-span-2">
                        <span className="mb-1 block text-sm font-medium">{t("config.channelEditor.apiVersion")}</span>
                        <Select
                            className="w-full"
                            value={apiVersion}
                            optionLabelProp="label"
                            options={apiVersions.map((value) => ({
                                value,
                                label: t(`config.channelEditor.apiVersions.${value}`),
                                title: t(`config.channelEditor.apiVersionHints.${value}`),
                            }))}
                            optionRender={(option) => (
                                <div className="py-0.5">
                                    <div>{option.label}</div>
                                    <div className="whitespace-normal text-xs text-stone-500">{option.data.title}</div>
                                </div>
                            )}
                            onChange={(value) => patch({ apiVersion: value })}
                        />
                        <span className="mt-1 block text-xs text-stone-500">{t(`config.channelEditor.apiVersionHints.${apiVersion}`)}</span>
                    </label>
                ) : null}
                <label className="block md:col-span-2">
                    <span className="mb-1 block text-sm font-medium">{t("config.channelEditor.baseUrl")}</span>
                    <Input value={draft.baseUrl} onChange={(event) => patch({ baseUrl: event.target.value })} placeholder={vendorBaseUrls[draft.vendor]} />
                    <span className="mt-1 block text-xs text-stone-500">{t("config.channelEditor.baseUrlHint")}</span>
                </label>
                <label className="block md:col-span-2">
                    <span className="mb-1 block text-sm font-medium">API Key</span>
                    <Input.Password value={draft.apiKey} onChange={(event) => patch({ apiKey: event.target.value })} placeholder="sk-..." />
                </label>
            </div>

            <div className="mt-6 mb-3 flex flex-wrap items-center justify-between gap-2">
                <div>
                    <div className="text-sm font-semibold">{t("config.channelEditor.models")}</div>
                    <div className="mt-0.5 text-xs text-stone-500">{t("config.channelEditor.modelDescription", { count: draft.models.length })}</div>
                </div>
                <div className="flex flex-wrap justify-end gap-2">
                    {missingRecommended.length ? (
                        <Button icon={<Sparkles className="size-4" />} onClick={addRecommendedModels}>{t("config.channelEditor.addRecommendedModels")}</Button>
                    ) : null}
                    <Button type="primary" icon={<ListPlus className="size-4" />} onClick={() => setSelectOpen(true)}>
                        {t("config.channelEditor.selectModels")}
                    </Button>
                </div>
            </div>

            <div className="space-y-2 rounded-lg border border-stone-200 p-2 dark:border-stone-800">
                {draft.models.length ? (
                    draft.models.map((model) => (
                        <div key={model.name} className="flex flex-wrap items-center gap-3 rounded-md px-2 py-1.5 hover:bg-stone-50 dark:hover:bg-stone-900/40">
                            <span className="min-w-0 flex-1 truncate text-sm" title={model.name}>
                                {model.name}
                            </span>
                            <div className="flex shrink-0 items-center gap-2">
                                <Segmented size="small" value={model.capability} options={capabilityOptions} onChange={(value) => setCapability(model.name, value as ModelCapability)} />
                                <Button size="small" type={model.script ? "primary" : "default"} ghost={Boolean(model.script)} onClick={() => setScriptTarget({ name: model.name, capability: model.capability, value: model.script || "" })}>
                                    {t(model.script ? "config.channelEditor.scriptReady" : "config.channelEditor.script")}
                                </Button>
                                <Button size="small" danger type="text" icon={<Trash2 className="size-3.5" />} onClick={() => removeModel(model.name)} />
                            </div>
                            {draft.vendor === "ark" && (model.capability === "image" ? findImageFamily("ark", model.name) : model.capability === "video" && getVideoFamily(model.name).protocol === "ark") ? (
                                <label className="grid basis-full gap-1 pt-1 sm:grid-cols-[9rem_minmax(0,1fr)] sm:items-start">
                                    <span className="text-xs font-medium text-stone-600 dark:text-stone-300">{t("config.channelEditor.arkEndpointId")}</span>
                                    <span className="block min-w-0">
                                        <Input size="small" allowClear value={model.endpointId || ""} onChange={(event) => setEndpointId(model.name, event.target.value)} placeholder={t("config.channelEditor.arkEndpointIdPlaceholder")} />
                                        <span className="mt-1 block text-xs leading-5 text-stone-500 dark:text-stone-400">{t("config.channelEditor.arkEndpointIdHint")}</span>
                                    </span>
                                </label>
                            ) : null}
                        </div>
                    ))
                ) : (
                    <div className="px-2 py-8 text-center text-sm text-stone-500">{t("config.channelEditor.empty")}</div>
                )}
            </div>

            <ModelSelectModal open={selectOpen} channel={draft} selectedNames={draft.models.map((model) => model.name)} onConfirm={applySelection} onClose={() => setSelectOpen(false)} />

            <ModelScriptEditor
                open={Boolean(scriptTarget)}
                capability={scriptTarget?.capability || "text"}
                modelName={scriptTarget?.name || ""}
                value={scriptTarget?.value || ""}
                onSave={(script) => scriptTarget && setScript(scriptTarget.name, script)}
                onClose={() => setScriptTarget(null)}
            />
        </Drawer>
    );
}
