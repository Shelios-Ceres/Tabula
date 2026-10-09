import { beforeEach, describe, expect, mock, test } from "bun:test";

Object.defineProperty(globalThis, "localStorage", {
    value: { getItem: () => null, setItem: () => undefined, removeItem: () => undefined },
    configurable: true,
});

const axiosPost = mock(async () => ({
    data: {
        data: [{ b64_json: "aGVsbG8=" }],
        usage: { input_tokens: 3, output_tokens: 7, total_tokens: 10 },
    },
    headers: { "x-request-id": "req-test-1" },
}));
const axiosRequest = mock(async () => ({ data: { data: [{ b64_json: "aGVsbG8=" }] } }));

mock.module("axios", () => ({
    default: {
        post: axiosPost,
        request: axiosRequest,
        isCancel: (error: any) => error?.code === "ERR_CANCELED",
        isAxiosError: (error: any) => error?.isAxiosError === true,
    },
}));

mock.module("@/services/image-storage", () => ({
    imageToDataUrl: async (image: { dataUrl: string }) => image.dataUrl,
}));

mock.module("@/lib/image-utils", () => ({
    dataUrlToFile: (image: { dataUrl: string; name?: string; type?: string }) => {
        const [, content = ""] = image.dataUrl.split(",", 2);
        return new File([Uint8Array.from(atob(content), (char) => char.charCodeAt(0))], image.name || "reference.png", { type: image.type || "image/png" });
    },
    readImageMeta: async (dataUrl: string) => ({ width: 1024, height: 1024, mimeType: dataUrl.match(/^data:([^;]+)/)?.[1] || "image/png" }),
}));

const { requestEdit, requestGeneration } = await import("@/services/api/image");
const { getPluginTemplates, runModelPlugin } = await import("@/services/api/model-plugin");
const { defaultConfig } = await import("@/stores/use-config-store");

const image = (name = "reference.png", dataUrl = "data:image/png;base64,aGVsbG8=") => ({ id: name, name, type: "image/png", dataUrl });
const config = (overrides: Partial<typeof defaultConfig> = {}) => ({
    ...defaultConfig,
    apiKey: "dummy-key",
    baseUrl: "https://example.invalid",
    model: "gpt-image-2.5-flare",
    imageModel: "gpt-image-2.5-flare",
    size: "1024x1024",
    quality: "xhigh",
    outputFormat: "png" as const,
    outputCompression: 100,
    background: "auto",
    count: "1",
    ...overrides,
});

beforeEach(() => { axiosPost.mockClear(); axiosRequest.mockClear(); });

describe("GPT Image request contract", () => {
    test("sends exact 2.5 model, xhigh/max, explicit size, output options and preserves response metadata", async () => {
        for (const quality of ["xhigh", "max"] as const) {
            axiosPost.mockResolvedValueOnce({
                data: { data: [{ b64_json: "aGVsbG8=" }], usage: { input_tokens: 1, output_tokens: 2, total_tokens: 3 } },
                headers: { "x-request-id": `req-${quality}` },
            });
            const result = await requestGeneration(config({ quality, outputFormat: "webp", outputCompression: 0, background: "transparent", moderation: "low" }), "draw a sun");
            const body = axiosPost.mock.calls.at(-1)?.[1] as Record<string, unknown>;
            expect(body.model).toBe("gpt-image-2.5-flare");
            expect(body.quality).toBe(quality);
            expect(body.size).toBe("1024x1024");
            expect(body.output_format).toBe("webp");
            expect(body.output_compression).toBe(0);
            expect(body.background).toBe("transparent");
            expect(body.moderation).toBe("low");
            expect(body).not.toHaveProperty("response_format");
            expect(result[0].dataUrl.startsWith("data:image/webp;base64,")).toBe(true);
            expect(result[0].response?.usage?.total_tokens).toBe(3);
            expect(result[0].response?.requestId).toBe(`req-${quality}`);
        }
    });

    test("changing xhigh to max does not rewrite an explicit size", async () => {
        await requestGeneration(config({ quality: "xhigh", size: "1536x1024" }), "wide");
        const xhighSize = (axiosPost.mock.calls.at(-1)?.[1] as Record<string, unknown>).size;
        await requestGeneration(config({ quality: "max", size: "1536x1024" }), "wide");
        const maxSize = (axiosPost.mock.calls.at(-1)?.[1] as Record<string, unknown>).size;
        expect(xhighSize).toBe("1536x1024");
        expect(maxSize).toBe("1536x1024");
    });

    test("rejects legacy GPT Image 2 xhigh and max", async () => {
        await expect(requestGeneration(config({ model: "gpt-image-2", imageModel: "gpt-image-2", quality: "xhigh" }), "legacy")).rejects.toThrow();
        await expect(requestGeneration(config({ model: "gpt-image-2", imageModel: "gpt-image-2", quality: "max" }), "legacy")).rejects.toThrow();
        expect(axiosPost).not.toHaveBeenCalled();
    });

    test("rejects invalid official n values instead of coercing them", async () => {
        for (const count of ["0", "-1", "1.5", "11"]) {
            await expect(requestGeneration(config({ count }), "count")).rejects.toThrow();
        }
        expect(axiosPost).not.toHaveBeenCalled();
    });

    test("rejects an overlong final prompt", async () => {
        await expect(requestGeneration(config(), "x".repeat(32001))).rejects.toThrow();
        expect(axiosPost).not.toHaveBeenCalled();
    });

    test("rejects JPEG with transparent background and invalid compression", async () => {
        await expect(requestGeneration(config({ outputFormat: "jpeg", background: "transparent" }), "jpeg")).rejects.toThrow();
        await expect(requestGeneration(config({ outputCompression: -1 }), "compression")).rejects.toThrow();
        await expect(requestGeneration(config({ outputCompression: 101 }), "compression")).rejects.toThrow();
        await expect(requestGeneration(config({ outputCompression: 1.5 }), "compression")).rejects.toThrow();
        await expect(requestGeneration(config({ moderation: "invalid" as "auto" }), "moderation")).rejects.toThrow();
        expect(axiosPost).not.toHaveBeenCalled();
    });

    test("requires an actual transparent pixel in the native mask", async () => {
        let drawImageCalls = 0;
        const installMaskDom = (alpha: number) => {
            class MockImage {
                naturalWidth = 1024;
                naturalHeight = 1024;
                onload?: () => void;
                onerror?: () => void;
                set src(_value: string) { queueMicrotask(() => this.onload?.()); }
            }
            Object.defineProperty(globalThis, "Image", { value: MockImage, configurable: true });
            Object.defineProperty(globalThis, "document", {
                value: {
                    createElement: () => ({
                        width: 0,
                        height: 0,
                        getContext: () => ({
                            drawImage: () => { drawImageCalls += 1; },
                            getImageData: () => ({ data: new Uint8ClampedArray([0, 0, 0, alpha]) }),
                        }),
                    }),
                },
                configurable: true,
            });
        };
        const main = image("main.png");
        const mask = image("mask.png");
        installMaskDom(255);
        await expect(requestEdit(config(), "opaque mask", [main], { mask })).rejects.toThrow();
        installMaskDom(0);
        await requestEdit(config(), "transparent mask", [main], { mask });
        expect(drawImageCalls).toBe(2);
    });

    test("keeps legacy non-GPT response_format behavior", async () => {
        await requestGeneration(config({ model: "custom-image", imageModel: "custom-image", quality: "high" }), "legacy provider");
        const body = axiosPost.mock.calls.at(-1)?.[1] as Record<string, unknown>;
        expect(body.response_format).toBe("b64_json");
    });

    test("preserves a recognizable cancellation error", async () => {
        axiosPost.mockRejectedValueOnce(new DOMException("aborted", "AbortError"));
        const error = await requestGeneration(config(), "cancel", { signal: new AbortController().signal }).catch((error) => error);
        expect(error).toMatchObject({ code: "ERR_CANCELED", originalMessage: "aborted", message: "请求已取消" });
    });

    test("retains code, status and request ID when an HTTP error has no message", async () => {
        axiosPost.mockRejectedValueOnce(Object.assign(new Error("Request failed with status code 400"), {
            isAxiosError: true,
            code: "ERR_BAD_REQUEST",
            response: { status: 400, data: { error: { type: "image_generation_user_error", code: "moderation_blocked" } }, headers: { "x-request-id": "req-no-message" } },
            config: { headers: { Authorization: "Bearer test-only-secret" } },
        }));
        const error = await requestGeneration(config(), "blocked").catch((error) => error);
        expect(error).toMatchObject({ status: 400, code: "moderation_blocked", requestId: "req-no-message" });
        expect(error.message).toContain("moderation_blocked");
        expect(error.message).toContain("req-no-message");
        expect(error).not.toHaveProperty("config");
        expect(error).not.toHaveProperty("cause");
        expect(JSON.stringify(error)).not.toContain("test-only-secret");
    });

    test("preserves a no-response network code with an actionable generic diagnosis", async () => {
        axiosPost.mockRejectedValueOnce(Object.assign(new Error("Network Error"), { isAxiosError: true, code: "ERR_NETWORK" }));
        const error = await requestEdit(config(), "edit", [image("reference.png")]).catch((error) => error);
        expect(error).toMatchObject({ code: "ERR_NETWORK", originalMessage: "Network Error" });
        expect(error.message).toContain("网络连接失败");
        expect(error.message).not.toContain("TLS");
        expect(error).not.toHaveProperty("status");
    });

    test("preserves Axios cancellation code without exposing the request config", async () => {
        axiosPost.mockRejectedValueOnce(Object.assign(new Error("canceled"), { isAxiosError: true, code: "ERR_CANCELED", config: { headers: { Authorization: "Bearer test-only-secret" } } }));
        const error = await requestGeneration(config(), "cancel").catch((error) => error);
        expect(error).toMatchObject({ code: "ERR_CANCELED", originalMessage: "canceled" });
        expect(error.message).toBe("请求已取消");
        expect(error).not.toHaveProperty("config");
    });

    test("uses response metadata and MIME when the API reports the actual result", async () => {
        axiosPost.mockResolvedValueOnce({
            data: { data: [{ b64_json: "aGVsbG8=" }], output_format: "jpeg", quality: "high", size: "1536x1024", background: "opaque" },
            headers: {},
        });
        const result = await requestGeneration(config({ quality: "auto", size: "auto", outputFormat: "png" }), "actual result");
        expect(result[0].dataUrl.startsWith("data:image/jpeg;base64,")).toBe(true);
        expect(result[0].response).toMatchObject({ outputFormat: "jpeg", quality: "high", size: "1536x1024", background: "opaque" });
    });

    test("does not invent actual quality or size when response metadata omits them", async () => {
        axiosPost.mockResolvedValueOnce({ data: { data: [{ b64_json: "aGVsbG8=" }] }, headers: {} });
        const result = await requestGeneration(config({ quality: "auto", size: "auto" }), "missing result metadata");
        expect(result[0].response?.quality).toBeUndefined();
        expect(result[0].response?.size).toBeUndefined();
    });

    test("default OpenAI template omits PNG compression and supports zero JPEG compression", async () => {
        const script = getPluginTemplates().image.find((template) => template.label.includes("OpenAI") || template.label.includes("OpenAI-compatible"))?.script || getPluginTemplates().image[0].script;
        const base = { ...config(), apiKey: "dummy-key", baseUrl: "https://example.invalid" };
        await runModelPlugin({ capability: "image", script, config: { ...base, model: "gpt-image-2.5-flare-2026-09-08" }, prompt: "png", images: [], params: { count: 1, size: undefined, quality: undefined, outputFormat: "png", outputCompression: 0, moderation: "auto" } });
        const pngBody = axiosRequest.mock.calls.at(-1)?.[0] as { data: Record<string, unknown> };
        expect(pngBody.data).not.toHaveProperty("output_compression");
        expect(pngBody.data).not.toHaveProperty("response_format");
        await runModelPlugin({ capability: "image", script, config: { ...base, model: "gpt-image-2.5-flare" }, prompt: "jpeg", images: [], params: { count: 1, size: undefined, quality: undefined, outputFormat: "jpeg", outputCompression: 0, moderation: "auto" } });
        const jpegBody = axiosRequest.mock.calls.at(-1)?.[0] as { data: Record<string, unknown> };
        expect(jpegBody.data.output_compression).toBe(0);
        expect(jpegBody.data).not.toHaveProperty("response_format");
    });
});

describe("GPT Image edit multipart contract", () => {
    test("sends an independent mask field and does not add it to image references", async () => {
        const main = image("main.png");
        const reference = image("reference.png");
        const mask = image("mask.png", "data:image/png;base64,aGVsbG8=");
        await requestEdit(config({ quality: "max" }), "edit locally", [main, reference], { mask });
        const form = axiosPost.mock.calls.at(-1)?.[1] as FormData;
        const entries = [...form.entries()];
        expect(entries.filter(([key]) => key === "image[]")).toHaveLength(2);
        expect(entries.filter(([key]) => key === "mask")).toHaveLength(1);
        expect(entries.find(([key]) => key === "mask")?.[1]).toBeInstanceOf(File);
        expect([...form.keys()].filter((key) => key === "mask")).toHaveLength(1);
    });
});
