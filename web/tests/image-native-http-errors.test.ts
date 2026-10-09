import { describe, expect, mock, test } from "bun:test";

mock.module("@/services/image-storage", () => ({ imageToDataUrl: async () => "" }));
mock.module("@/stores/use-config-store", () => ({ withLocalProxy: (url: string) => url }));

const { checkResponse } = await import("@/services/api/image-native/common");

describe("native image HTTP errors", () => {
    test("preserves the proxy's string error and top-level socket code", async () => {
        const response = new Response(JSON.stringify({ error: "fetch failed | UND_ERR_SOCKET: other side closed", code: "UND_ERR_SOCKET" }), { status: 502, headers: { "x-request-id": "proxy-request" } });
        const error = await checkResponse(response).catch((error) => error);
        expect(error).toMatchObject({ message: "fetch failed | UND_ERR_SOCKET: other side closed", code: "UND_ERR_SOCKET", status: 502, requestId: "proxy-request" });
    });

    test("preserves an official nested error and prefers its code", async () => {
        const response = new Response(JSON.stringify({ error: { message: "Organization verification required", code: "permission_denied" }, code: "outer" }), { status: 403, headers: { "x-request-id": "official-request" } });
        const error = await checkResponse(response).catch((error) => error);
        expect(error).toMatchObject({ message: "Organization verification required", code: "permission_denied", status: 403, requestId: "official-request" });
    });

    test("uses the existing status fallback when the response is not JSON", async () => {
        const error = await checkResponse(new Response("upstream unavailable", { status: 503, headers: { "x-tt-logid": "fallback-request" } })).catch((error) => error);
        expect(error).toMatchObject({ message: "图片请求失败（HTTP 503）", status: 503, requestId: "fallback-request" });
        expect(error.code).toBeUndefined();
    });

    test("keeps the HTTP status when the error body is JSON null", async () => {
        const error = await checkResponse(new Response("null", { status: 500, headers: { "x-request-id": "null-body" } })).catch((error) => error);
        expect(error).toMatchObject({ message: "图片请求失败（HTTP 500）", status: 500, requestId: "null-body" });
    });
});
