import { describe, expect, mock, test } from "bun:test";
import { renderToStaticMarkup } from "react-dom/server";
import { CanvasNodeType, type CanvasNodeData, type CanvasNodeImage } from "@/types/canvas";

mock.module("@/stores/use-theme-store", () => ({ useThemeStore: (selector: (state: { theme: "light" }) => unknown) => selector({ theme: "light" }) }));
mock.module("react-i18next", () => ({ useTranslation: () => ({ t: (key: string) => key }) }));
mock.module("@/i18n", () => ({ default: { t: (key: string) => key } }));
mock.module("@/lib/canvas/node-registry", () => ({ getNodeDefinition: () => undefined }));
mock.module("@/lib/canvas/plugin-node-context", () => ({ buildNodeContext: () => null }));
mock.module("@/components/canvas/canvas-resource-mention-textarea", () => ({ CanvasResourceMentionTextarea: () => null }));

const { CanvasNode } = await import("@/components/canvas/canvas-node");
const previewUrl = "data:image/png;base64,cHJldmlldw==";
const finalUrl = "data:image/png;base64,ZmluYWw=";
const slot = (id: string, status: CanvasNodeImage["status"], content = ""): CanvasNodeImage => ({ id, status, content, naturalWidth: 800, naturalHeight: 600, bytes: 10, mimeType: "image/png" });
const node = (images: CanvasNodeImage[], content = ""): CanvasNodeData => ({ id: "node", type: CanvasNodeType.Image, title: "test image", position: { x: 0, y: 0 }, width: 400, height: 300, metadata: { status: "loading", images, content } });
const noop = () => {};
const render = (data: CanvasNodeData, imagePreview?: { dataUrl: string; imageId?: string }) => renderToStaticMarkup(<CanvasNode data={data} imagePreview={imagePreview} scale={1} isSelected={false} isRelated={false} isFocusRelated={false} isConnectionTarget={false} isConnecting={false} showPanel={false} showImageInfo={false} batchExpanded onMouseDown={noop} onHoverStart={noop} onHoverEnd={noop} onConnectStart={noop} onResizeStart={noop} onResize={noop} onResizeEnd={noop} onContentChange={noop} onTitleChange={noop} onContextMenu={noop} />);

describe("canvas image preview rendering", () => {
    test("a loading single image shows its transient preview and generation label, then the final image replaces it", () => {
        const pending = node([slot("pending", "loading")]);
        const html = render(pending, { dataUrl: previewUrl });
        expect(html).toContain(`src="${previewUrl}"`);
        expect(html).toContain("canvas.node.generating");
        expect(pending.metadata?.content).toBe("");
        expect(pending.metadata?.images?.[0].content).toBe("");
        const cleared = render(pending);
        expect(cleared).not.toContain(`src="${previewUrl}"`);
        const final = render({ ...pending, metadata: { ...pending.metadata, status: "success", content: finalUrl, images: [slot("saved", "success", finalUrl)] } });
        expect(final).toContain(`src="${finalUrl}"`);
        expect(final).not.toContain(`src="${previewUrl}"`);
        expect(final).not.toContain("canvas.node.generating");
    });

    test("a retry preview appears only in the selected slot while the successful sibling stays visible", () => {
        const siblingUrl = "data:image/png;base64,c2libGluZw==";
        const pending = node([slot("sibling", "success", siblingUrl), slot("retry", "loading")], siblingUrl);
        const html = render(pending, { dataUrl: previewUrl, imageId: "retry" });
        expect(html).toContain(`src="${siblingUrl}"`);
        expect(html).toContain(`src="${previewUrl}"`);
        expect(html).toContain("canvas.node.generating");
        expect(pending.metadata?.content).toBe(siblingUrl);
        expect(pending.metadata?.images?.map((image) => image.content)).toEqual([siblingUrl, ""]);
    });
});
