import { describe, expect, mock, test } from "bun:test";

Object.defineProperty(globalThis, "localStorage", { value: { getItem: () => null, setItem: () => undefined, removeItem: () => undefined }, configurable: true });
mock.module("@/lib/canvas/node-registry", () => ({ getNodeDefinition: () => undefined }));
mock.module("@/services/image-storage", () => ({ imageToDataUrl: async () => "" }));

const { buildNodeMentionReferences, getGenerationResourceNodes } = await import("@/lib/canvas/canvas-resource-references");
const { default: i18n } = await import("@/i18n");
await i18n.changeLanguage("zh-CN");
const image = (id: string, content = "saved-image", groupId?: string) => ({ id, type: "image", title: id, position: { x: 0, y: 0 }, width: 100, height: 100, metadata: { content, groupId } } as any);
const edge = (fromNodeId: string, toNodeId: string) => ({ id: `${fromNodeId}-${toNodeId}`, fromNodeId, toNodeId });

describe("image reference numbering matches request order", () => {
    test("an existing source image occupies image 1 before connected images", () => {
        const a = image("a");
        const b = image("b");
        const references = buildNodeMentionReferences(a, [a, b], [edge("b", "a")]);
        expect(references.map(({ nodeId, label }) => [nodeId, label])).toEqual([["a", "图片1"], ["b", "图片2"]]);
        expect(getGenerationResourceNodes("a", [a, b], [edge("b", "a")]).map((node) => node.id)).toEqual(["b"]);
    });

    test("empty output images do not consume a reference number", () => {
        const a = image("a", "");
        const b = image("b");
        expect(buildNodeMentionReferences(a, [a, b], [edge("b", "a")]).map(({ nodeId, label }) => [nodeId, label])).toEqual([["b", "图片1"]]);
    });

    test("connected configuration inputs retain the same self-first numbering", () => {
        const a = image("a");
        const b = image("b");
        const config = { ...image("config", ""), type: "config" };
        const edges = [edge("a", "config"), edge("b", "config")];
        expect(buildNodeMentionReferences(a, [a, b, config], edges).map(({ nodeId, label }) => [nodeId, label])).toEqual([["a", "图片1"], ["b", "图片2"]]);
    });

    test("a source also present inside a reference group is only numbered once", () => {
        const a = image("a", "saved-image", "group");
        const b = image("b", "saved-image", "group");
        const group = { ...image("group", ""), type: "group" };
        expect(buildNodeMentionReferences(a, [a, b, group], [edge("group", "a")]).map(({ nodeId, label }) => [nodeId, label])).toEqual([["a", "图片1"], ["b", "图片2"]]);
    });
});
