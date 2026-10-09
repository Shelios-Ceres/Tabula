import { describe, expect, test } from "bun:test";

Object.defineProperty(globalThis, "localStorage", { value: { getItem: () => null, setItem: () => undefined, removeItem: () => undefined }, configurable: true });

const { readSse } = await import("@/services/api/image-native/common");

function response(chunks: Uint8Array[], init: ResponseInit = {}) {
    const body = new ReadableStream<Uint8Array>({ start(controller) { chunks.forEach((chunk) => controller.enqueue(chunk)); controller.close(); } });
    return new Response(body, { status: 200, ...init });
}

describe("shared SSE framing", () => {
    test("handles chunk boundaries, CRLF, multiline data and unicode", async () => {
        const events: Array<{ event: string; data: string }> = [];
        const encoder = new TextEncoder();
        await readSse(response([encoder.encode("event: output\r\ndata: {\"text\":\"中"), encoder.encode("文\"}\r\ndata: second\r\n\r\n")]), (event) => { events.push(event); });
        expect(events).toEqual([{ event: "output", data: '{"text":"中文"}\nsecond' }]);
    });

    test("rejects missing body and HTTP errors", async () => {
        await expect(readSse(new Response(null, { status: 200 }), () => undefined)).rejects.toThrow();
        await expect(readSse(new Response(JSON.stringify({ error: { message: "bad" } }), { status: 400 }), () => undefined)).rejects.toThrow("bad");
    });
});
