import { expect, mock, test } from "bun:test";

Object.defineProperty(globalThis, "localStorage", { value: { getItem: () => null, setItem: () => undefined, removeItem: () => undefined }, configurable: true });
const stores = new Map<string, Map<string, unknown>>();
mock.module("localforage", () => ({ default: { createInstance: ({ storeName }: { storeName: string }) => {
    const data = stores.get(storeName) || new Map<string, unknown>();
    stores.set(storeName, data);
    return { iterate: async (callback: (value: unknown, key: string) => void) => { for (const [key, value] of data) callback(value, key); }, removeItem: async (key: string) => { data.delete(key); } };
} } }));
const { cleanupUnusedImages, collectImageStorageKeys } = await import("@/services/image-storage");

test("collects native history markers and canvas mask/reference keys", () => {
    const keys = collectImageStorageKeys({ history: [{ data: { __imageStorageKey: "image:history" } }], references: ["image:reference"], maskReference: "image:mask", images: [{ storageKey: "image:output" }] });
    expect([...keys].sort()).toEqual(["image:history", "image:mask", "image:output", "image:reference"]);
});

test("deleting a log preserves images used by later conversations and limits cleanup to its candidates", async () => {
    const files = stores.get("image_files")!;
    files.set("image:shared", new Blob(["shared"]));
    files.set("image:removed", new Blob(["removed"]));
    files.set("image:unrelated", new Blob(["unrelated"]));
    stores.get("image_generation_logs")!.set("later", { conversation: { history: [{ result: { __imageStorageKey: "image:shared", encoding: "base64" } }] } });
    await cleanupUnusedImages({}, ["image:shared", "image:removed"]);
    expect(files.has("image:shared")).toBe(true);
    expect(files.has("image:removed")).toBe(false);
    expect(files.has("image:unrelated")).toBe(true);
});
