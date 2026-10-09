import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { getImageModelCapabilities } from "@/lib/image-model-capabilities";
import type { ImageResponseMetadata } from "@/types/image";
import type { ImageLayer } from "@/types/image-generation";

export function ImageResponseDetails({ response, model, role, layer, showProviderMetadata = true }: { response?: ImageResponseMetadata; model?: string; role?: "image" | "base" | "layer"; layer?: ImageLayer; showProviderMetadata?: boolean }) {
    const { t } = useTranslation();
    const modelLabelKey = model ? getImageModelCapabilities(model).labelKey : undefined;
    const usage = response?.usage;
    const items = [
        { label: t("imageWorkbench.modelRole"), value: modelLabelKey ? t(modelLabelKey) : undefined },
        { label: t("nativeImage.provider"), value: response?.provider ? t(`nativeImage.providers.${response.provider}`) : undefined },
        { label: t("nativeImage.protocol"), value: response?.protocol },
        { label: t("workbench.model"), value: response?.model || model },
        { label: t("imageWorkbench.actualQuality"), value: response?.quality },
        { label: t("imageWorkbench.actualSize"), value: response?.size },
        { label: t("imageWorkbench.actualBackground"), value: response?.background },
        { label: t("imageWorkbench.actualFormat"), value: response?.outputFormat?.toUpperCase() },
        { label: t("imageWorkbench.inputTokens"), value: finiteNumber(usage?.input_tokens) },
        { label: t("imageWorkbench.inputImageTokens"), value: finiteNumber(usage?.input_tokens_details?.image_tokens) },
        { label: t("imageWorkbench.inputTextTokens"), value: finiteNumber(usage?.input_tokens_details?.text_tokens) },
        { label: t("imageWorkbench.outputTokens"), value: finiteNumber(usage?.output_tokens) },
        { label: t("imageWorkbench.outputImageTokens"), value: finiteNumber(usage?.output_tokens_details?.image_tokens) },
        { label: t("imageWorkbench.outputTextTokens"), value: finiteNumber(usage?.output_tokens_details?.text_tokens) },
        { label: t("imageWorkbench.totalTokens"), value: finiteNumber(usage?.total_tokens) },
        { label: t("imageWorkbench.requestId"), value: response?.requestId },
        { label: t("nativeImage.resultRole"), value: role ? t(`nativeImage.roles.${role}`) : undefined },
        { label: t("nativeImage.layerName"), value: layer?.name },
        { label: t("nativeImage.layerOrder"), value: finiteNumber(layer?.zIndex) },
        { label: t("nativeImage.layerBounds"), value: formatBounds(layer) },
    ].filter((item): item is { label: string; value: string | number } => item.value !== undefined && item.value !== "");

    if (!items.length && !(showProviderMetadata && response?.providerMetadata)) return null;
    return (
        <div className="space-y-2 text-xs text-current">
            {items.length ? (
                <details className="opacity-70">
                    <summary className="w-fit cursor-pointer rounded py-1 outline-none focus-visible:ring-2 focus-visible:ring-ring/30">{t("imageWorkbench.actualDetails")}</summary>
                    <dl className="mt-1 grid grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-1 rounded-md border border-current/10 p-2">
                        {items.map((item) => (
                            <div key={item.label} className="contents">
                                <dt>{item.label}</dt>
                                <dd className="min-w-0 break-all text-right font-medium tabular-nums opacity-90">{item.value}</dd>
                            </div>
                        ))}
                    </dl>
                    {layer?.description ? <p className="mt-2 leading-5">{layer.description}</p> : null}
                    {response?.rawUsage ? <div className="mt-2"><div className="mb-1 font-medium">{t("nativeImage.rawUsage")}</div><pre className="max-h-40 overflow-auto whitespace-pre-wrap break-words rounded-md border border-current/10 p-2 text-[11px]">{JSON.stringify(response.rawUsage, null, 2)}</pre></div> : null}
                </details>
            ) : null}
            {showProviderMetadata ? <ImageProviderMetadata metadata={response?.providerMetadata} /> : null}
        </div>
    );
}

function finiteNumber(value?: number) {
    return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function formatBounds(layer?: ImageLayer) {
    const value = layer?.boundingBox?.absolute || layer?.boundingBox?.normalized;
    return value?.length === 4 ? value.join(", ") : undefined;
}

export function ImageProviderMetadata({ metadata }: { metadata?: Record<string, unknown> }) {
    const { t } = useTranslation();
    const snippets = useMemo(() => readSuggestions(metadata || {}).map(sanitizeSearchHtml).filter(Boolean), [metadata]);
    const citations = useMemo(() => readCitations(metadata || {}), [metadata]);
    if (!metadata || !Object.keys(metadata).length) return null;
    return (
        <div className="space-y-3 rounded-lg border border-current/10 p-3 text-xs text-current">
            {snippets.length ? (
                <section aria-label={t("nativeImage.searchSuggestions")}>
                    <div className="mb-2 font-medium">{t("nativeImage.searchSuggestions")}</div>
                    <div className="space-y-2">
                        {snippets.map((srcDoc, index) => <iframe key={`${index}-${srcDoc.length}`} title={t("nativeImage.searchSuggestionFrame", { index: index + 1 })} className="h-56 w-full rounded-md border border-current/10 bg-white" sandbox="allow-popups allow-popups-to-escape-sandbox" referrerPolicy="no-referrer" srcDoc={srcDoc} />)}
                    </div>
                </section>
            ) : null}
            {citations.length ? (
                <section aria-label={t("nativeImage.searchSources")}>
                    <div className="mb-2 font-medium">{t("nativeImage.searchSources")}</div>
                    <div className="flex flex-wrap gap-2">{citations.map((item) => <a key={item.url} href={item.url} target="_blank" rel="noopener noreferrer" referrerPolicy="no-referrer" className="rounded-full border border-current/15 px-2.5 py-1 underline decoration-current/30 underline-offset-2 hover:decoration-current">{item.title}</a>)}</div>
                </section>
            ) : null}
            <details className="opacity-70">
                <summary className="cursor-pointer font-medium">{t("nativeImage.providerMetadata")}</summary>
                <pre className="thin-scrollbar mt-2 max-h-48 overflow-auto whitespace-pre-wrap break-words rounded-md border border-current/10 p-2 text-[11px]">{JSON.stringify(metadata, null, 2)}</pre>
            </details>
        </div>
    );
}

function readSuggestions(metadata: Record<string, unknown>) {
    const value = metadata.searchSuggestions || metadata.search_suggestions || metadata.suggestions;
    return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : [];
}

function readCitations(metadata: Record<string, unknown>) {
    const value = metadata.citations || metadata.sources;
    if (!Array.isArray(value)) return [];
    const seen = new Set<string>();
    return value.flatMap((item, index) => {
        if (typeof item === "string") {
            const url = safeHttpUrl(item);
            if (!url || seen.has(url)) return [];
            seen.add(url);
            return [{ url, title: new URL(url).hostname }];
        }
        if (!item || typeof item !== "object") return [];
        const record = item as Record<string, unknown>;
        const url = safeHttpUrl(String(record.url || record.uri || record.link || ""));
        if (!url || seen.has(url)) return [];
        seen.add(url);
        return [{ url, title: String(record.title || record.name || record.source || new URL(url).hostname || index + 1) }];
    });
}

function sanitizeSearchHtml(value: string) {
    if (typeof DOMParser === "undefined") return "";
    const document = new DOMParser().parseFromString(value, "text/html");
    document.querySelectorAll("script,meta,base,iframe,frame,object,embed,form,link").forEach((node) => node.remove());
    document.querySelectorAll("*").forEach((element) => {
        for (const attribute of Array.from(element.attributes)) {
            const name = attribute.name.toLowerCase();
            if (name.startsWith("on") || name === "action" || name === "formaction" || name === "ping") element.removeAttribute(attribute.name);
            if (name === "href" || name === "xlink:href") {
                const href = safeHttpUrl(attribute.value);
                if (!href) element.removeAttribute(attribute.name);
                else {
                    element.removeAttribute(attribute.name);
                    element.setAttribute("href", href);
                    element.setAttribute("target", "_blank");
                    element.setAttribute("rel", "noopener noreferrer");
                }
            }
            if ((name === "src" || name === "poster" || name === "data" || name === "background") && element.tagName !== "IMG") element.removeAttribute(attribute.name);
            if (name === "src" && element.tagName === "IMG" && !safeImageUrl(attribute.value)) element.removeAttribute(attribute.name);
            if (name === "srcset") element.removeAttribute(attribute.name);
        }
    });
    const contentSecurityPolicy = "default-src 'none'; script-src 'none'; style-src 'unsafe-inline'; img-src data: https://www.gstatic.com https://www.google.com; connect-src 'none'; frame-src 'none'; media-src 'none'; font-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";
    return `<!doctype html><html><head><meta http-equiv="Content-Security-Policy" content="${contentSecurityPolicy}"><meta name="referrer" content="no-referrer"><style>html{color-scheme:light}body{margin:0;padding:12px;color:#292524;background:#fff;font:13px/1.55 system-ui,sans-serif;overflow:auto}a{color:#1d4ed8;text-underline-offset:2px}img{max-width:100%;height:auto}</style></head><body>${document.body.innerHTML}</body></html>`;
}

function safeHttpUrl(value: string) {
    try {
        const url = new URL(value);
        return url.protocol === "https:" || url.protocol === "http:" ? url.href : "";
    } catch {
        return "";
    }
}

function safeImageUrl(value: string) {
    if (/^data:image\/(?:png|jpeg|jpg|gif|webp);/i.test(value)) return true;
    try {
        const url = new URL(value);
        return url.protocol === "https:" && (url.hostname === "www.gstatic.com" || url.hostname.endsWith(".gstatic.com") || url.hostname === "www.google.com" || url.hostname.endsWith(".google.com"));
    } catch {
        return false;
    }
}
