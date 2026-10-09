import { describe, expect, mock, test } from "bun:test";

type SseEvent = { event: string; data: Record<string, unknown> | "[DONE]" };
let streamEvents: SseEvent[] = [];
let axiosHandler: (...args: any[]) => Promise<any> = async () => ({ data: {}, headers: {} });

mock.module("axios", () => ({
    default: { post: (...args: any[]) => axiosHandler(...args), isCancel: () => false, isAxiosError: () => false },
}));
mock.module("@/i18n", () => ({ default: { t: (key: string, options?: Record<string, unknown>) => options?.defaultValue || key } }));
mock.module("@/stores/use-config-store", () => ({
    defaultConfig: {},
    withLocalProxy: (url: string) => url,
    buildApiUrl: (baseUrl: string, path: string) => `${baseUrl.replace(/\/+$/, "")}${path}`,
    resolveModelScript: () => undefined,
    resolveModelRequestConfig: (config: any, value: string) => {
        const [channelId, model] = value.includes("::") ? value.split("::") : [undefined, value];
        const channel = config.channels.find((item: any) => item.id === channelId) || config.channels[0];
        return { ...config, model, baseUrl: channel.baseUrl, apiKey: channel.apiKey, apiFormat: channel.apiFormat };
    },
}));
mock.module("@/services/image-storage", () => ({ imageToDataUrl: async (image: { dataUrl: string }) => image.dataUrl }));
mock.module("@/services/api/image-native/common", () => ({
    withLocalProxy: (url: string) => url,
    imageToDataUrl: async (image: { dataUrl: string }) => image.dataUrl,
    checkResponse: async (response: Response) => { if (!response.ok) throw new Error(`HTTP ${response.status}`); },
    readSse: async (_response: Response, onEvent: (event: { event: string; data: string }) => void | Promise<void>) => {
        for (const item of streamEvents) await onEvent({ event: item.event, data: typeof item.data === "string" ? item.data : JSON.stringify(item.data) });
    },
}));

const { requestOpenAIImageJob } = await import("@/services/api/image-native/openai");
const context = { baseUrl: "https://example.test/v1", apiKey: "secret", channelId: "channel" };
const request = (api: "images" | "responses" = "images", stream = false) => ({
    model: "gpt-image-2.5-flare",
    prompt: "draw",
    references: [],
    count: 1,
    quality: "high",
    size: "1024x1024",
    background: "auto",
    outputFormat: "jpeg" as const,
    outputCompression: 80,
    moderation: "auto" as const,
    native: { stream, openai: { api, mainModel: "gpt-5.5", store: false, partialImages: 2 } },
});

describe("native OpenAI image adapter", () => {
    test("uses the established Images generation validation path with the supplied channel credentials", async () => {
        let captured: any[] = [];
        axiosHandler = async (...args: any[]) => {
            captured = args;
            return { data: { data: [{ b64_json: "aGVsbG8=" }], output_format: "jpeg", usage: { total_tokens: 2 } }, headers: { "x-request-id": "req-1" } };
        };
        const result = await requestOpenAIImageJob(context, request(), {});
        expect(captured[0]).toBe("https://example.test/v1/images/generations");
        expect(captured[1]).toMatchObject({ model: "gpt-image-2.5-flare", prompt: "draw", n: 1, output_format: "jpeg" });
        expect(captured[2].headers.Authorization).toBe("Bearer secret");
        expect(result.images[0]?.dataUrl.startsWith("data:image/jpeg")).toBe(true);
        expect(result.images[0]?.response?.requestId).toBe("req-1");
    });

    test("puts a Responses mask on the image_generation tool and records raw response usage", async () => {
        let captured: RequestInit | undefined;
        const oldFetch = globalThis.fetch;
        globalThis.fetch = (async (_input: RequestInfo | URL, init?: RequestInit) => {
            captured = init;
            return new Response(JSON.stringify({
                id: "resp-1",
                status: "completed",
                model: "gpt-5.5",
                usage: { input_tokens: 3, output_tokens: 4, total_tokens: 7 },
                output: [{ type: "image_generation_call", output_format: "png", result: "aGVsbG8=" }, { type: "message", content: [{ type: "output_text", text: "done" }] }],
            }), { status: 200, headers: { "x-request-id": "resp-request" } });
        }) as typeof fetch;
        try {
            const mask = { id: "mask", name: "mask.png", type: "image/png", dataUrl: "data:image/png;base64,bWFzaw==" };
            const result = await requestOpenAIImageJob(context, request("responses"), { mask });
            const body = JSON.parse(String(captured?.body));
            expect(body).toMatchObject({ model: "gpt-5.5", tool_choice: { type: "image_generation" }, tools: [{ type: "image_generation", model: "gpt-image-2.5-flare", input_image_mask: { image_url: mask.dataUrl } }] });
            expect(body.tools[0]).not.toHaveProperty("n");
            expect(body.input[0].content).not.toContainEqual(expect.objectContaining({ type: "input_image_mask" }));
            expect(result.images[0]?.dataUrl.startsWith("data:image/png")).toBe(true);
            expect(result.images[0]?.response?.rawUsage).toEqual({ input_tokens: 3, output_tokens: 4, total_tokens: 7 });
            expect(result.images[0]?.response?.usage).toBeUndefined();
            expect(result.images[0]?.response?.providerMetadata).toMatchObject({ responseId: "resp-1", responseModel: "gpt-5.5" });
            expect(result.texts).toEqual(["done"]);
            expect(result.conversation?.history).toEqual([
                { role: "user", content: [{ type: "input_text", text: "draw" }] },
                { type: "image_generation_call", output_format: "png", result: "aGVsbG8=" },
                { type: "message", content: [{ type: "output_text", text: "done" }] },
            ]);
        } finally {
            globalThis.fetch = oldFetch;
        }
    });

    test("uses previous_response_id only for a stored prior Response while retaining local history", async () => {
        let captured: RequestInit | undefined;
        const oldFetch = globalThis.fetch;
        globalThis.fetch = (async (_input: RequestInfo | URL, init?: RequestInit) => {
            captured = init;
            return new Response(JSON.stringify({ id: "resp-2", status: "completed", output: [{ type: "image_generation_call", result: "aGVsbG8=" }] }), { status: 200 });
        }) as typeof fetch;
        try {
            const history = [{ role: "user", content: [{ type: "input_text", text: "old" }] }];
            const result = await requestOpenAIImageJob(context, { ...request("responses"), native: { ...request("responses").native, openai: { ...request("responses").native.openai, store: true } } }, {
                conversation: { provider: "openai", protocol: "openai-responses", model: "gpt-5.5", remoteId: "previous", stored: true, createdAt: "now", history },
            });
            const body = JSON.parse(String(captured?.body));
            expect(body.previous_response_id).toBe("previous");
            expect(body.tool_choice).toEqual({ type: "image_generation" });
            expect(body.input).toEqual([{ role: "user", content: [{ type: "input_text", text: "draw" }] }]);
            expect(result.conversation?.history).toEqual([...history, ...body.input, { type: "image_generation_call", result: "aGVsbG8=" }]);
        } finally {
            globalThis.fetch = oldFetch;
        }
    });

    test("accepts final Images events with top-level b64_json and keeps partial images as previews", async () => {
        const oldFetch = globalThis.fetch;
        const events: string[] = [];
        globalThis.fetch = (async () => new Response("", { status: 200, headers: { "content-type": "text/event-stream", "x-request-id": "img-stream" } })) as typeof fetch;
        streamEvents = [
            { event: "image_generation.partial_image", data: { type: "image_generation.partial_image", b64_json: "cHJldmlldw==", output_format: "jpeg" } },
            { event: "step.preview", data: { type: "step.preview", b64_json: "bXVzdC1pZ25vcmU=" } },
            { event: "image_generation.completed", data: { type: "image_generation.completed", b64_json: "ZmluYWw=", output_format: "png" } },
        ];
        try {
            const result = await requestOpenAIImageJob(context, request("images", true), { onEvent: (event) => events.push(event.type) });
            expect(events.filter((event) => event === "preview")).toHaveLength(1);
            expect(result.images).toHaveLength(1);
            expect(result.images[0]?.dataUrl).toBe("data:image/png;base64,ZmluYWw=");
            expect(events.filter((event) => event === "output")).toHaveLength(1);
        } finally {
            globalThis.fetch = oldFetch;
            streamEvents = [];
        }
    });

    test("accepts image_edit stream events for edits and keeps their partial image out of final output", async () => {
        const oldFetch = globalThis.fetch;
        const previews: string[] = [];
        globalThis.fetch = (async (input: RequestInfo | URL) => {
            if (String(input).startsWith("data:image/")) return new Response("reference", { headers: { "content-type": "image/png" } });
            return new Response("", { status: 200, headers: { "content-type": "text/event-stream" } });
        }) as typeof fetch;
        streamEvents = [
            { event: "image_edit.partial_image", data: { type: "image_edit.partial_image", b64_json: "cHJldmlldw==", output_format: "png" } },
            { event: "image_edit.completed", data: { type: "image_edit.completed", b64_json: "ZWRpdGVk", output_format: "webp" } },
        ];
        try {
            const result = await requestOpenAIImageJob(context, { ...request("images", true), references: [{ id: "source", name: "source.png", type: "image/png", dataUrl: "data:image/png;base64,c291cmNl" }] }, {
                onEvent: (event) => { if (event.type === "preview" && event.image) previews.push(event.image.dataUrl); },
            });
            expect(previews).toEqual(["data:image/png;base64,cHJldmlldw=="]);
            expect(result.images.map((image) => image.dataUrl)).toEqual(["data:image/webp;base64,ZWRpdGVk"]);
        } finally {
            globalThis.fetch = oldFetch;
            streamEvents = [];
        }
    });

    test("uses only response.completed as the Responses terminal event and preserves streamed conversation data", async () => {
        const oldFetch = globalThis.fetch;
        const previews: string[] = [];
        globalThis.fetch = (async () => new Response("", { status: 200, headers: { "content-type": "text/event-stream", "x-request-id": "response-stream" } })) as typeof fetch;
        streamEvents = [
            { event: "response.image_generation_call.partial_image", data: { type: "response.image_generation_call.partial_image", partial_image_index: 0, partial_image_b64: "cHJldmlldw==" } },
            { event: "response.image_generation_call.completed", data: { type: "response.image_generation_call.completed", response: { id: "wrong", output: [{ type: "image_generation_call", result: "d3Jvbmc=" }] } } },
            { event: "response.completed", data: { type: "response.completed", response: {
                id: "resp-stream",
                status: "completed",
                model: "gpt-5.5",
                usage: { input_tokens: 1, output_tokens: 2, total_tokens: 3 },
                output: [{ type: "message", content: [{ type: "output_text", text: "caption" }] }, { type: "image_generation_call", output_format: "webp", result: "ZmluYWw=" }],
            } } },
        ];
        try {
            const oldHistory = [{ role: "user", content: [{ type: "input_text", text: "before" }] }];
            const result = await requestOpenAIImageJob(context, request("responses", true), {
                conversation: { provider: "openai", protocol: "openai-responses", model: "gpt-5.5", stored: false, createdAt: "now", history: oldHistory },
                onEvent: (event) => { if (event.type === "preview" && event.image) previews.push(event.image.dataUrl); },
            });
            expect(previews).toEqual(["data:image/jpeg;base64,cHJldmlldw=="]);
            expect(result.images[0]?.dataUrl).toBe("data:image/webp;base64,ZmluYWw=");
            expect(result.texts).toEqual(["caption"]);
            expect(result.outputOrder).toEqual([{ kind: "text", index: 0 }, { kind: "image", index: 0 }]);
            expect(result.conversation).toMatchObject({ remoteId: "resp-stream", model: "gpt-5.5", stored: false });
            expect(result.conversation?.history).toEqual([
                ...oldHistory,
                { role: "user", content: [{ type: "input_text", text: "draw" }] },
                { type: "message", content: [{ type: "output_text", text: "caption" }] },
                { type: "image_generation_call", output_format: "webp", result: "ZmluYWw=" },
            ]);
        } finally {
            globalThis.fetch = oldFetch;
            streamEvents = [];
        }
    });

    test("rejects a Responses stream without response.completed", async () => {
        const oldFetch = globalThis.fetch;
        globalThis.fetch = (async () => new Response("", { status: 200, headers: { "content-type": "text/event-stream" } })) as typeof fetch;
        streamEvents = [{ event: "response.output_item.done", data: { type: "response.output_item.done", item: { type: "image_generation_call", result: "aGVsbG8=" } } }];
        try {
            await expect(requestOpenAIImageJob(context, request("responses", true), {})).rejects.toThrow("response.completed");
        } finally {
            globalThis.fetch = oldFetch;
            streamEvents = [];
        }
    });

    test("forces the image tool in streaming Responses without an Images-only n parameter", async () => {
        const oldFetch = globalThis.fetch;
        let body: any;
        globalThis.fetch = (async (_input: RequestInfo | URL, init?: RequestInit) => {
            body = JSON.parse(String(init?.body));
            return new Response("", { headers: { "content-type": "text/event-stream" } });
        }) as typeof fetch;
        streamEvents = [{ event: "response.completed", data: { response: { status: "completed", output: [{ type: "image_generation_call", result: "aGVsbG8=" }] } } }];
        try {
            await requestOpenAIImageJob(context, request("responses", true));
            expect(body.tool_choice).toEqual({ type: "image_generation" });
            expect(body.tools[0]).not.toHaveProperty("n");
        } finally {
            globalThis.fetch = oldFetch;
            streamEvents = [];
        }
    });

    test("retains provider error code and request ID from stream failure events", async () => {
        const oldFetch = globalThis.fetch;
        globalThis.fetch = (async () => new Response("", { headers: { "x-request-id": "req-blocked" } })) as typeof fetch;
        try {
            for (const api of ["images", "responses"] as const) {
                streamEvents = [{ event: api === "images" ? "error" : "response.failed", data: { error: { code: "moderation_blocked", message: "Blocked by moderation" } } }];
                const error = await requestOpenAIImageJob(context, request(api, true)).catch((error) => error);
                expect(error).toMatchObject({ code: "moderation_blocked", status: 200, requestId: "req-blocked" });
                expect(error.message).toContain("Blocked by moderation");
                expect(error).not.toHaveProperty("cause");
            }
        } finally {
            globalThis.fetch = oldFetch;
            streamEvents = [];
        }
    });

    test("classifies failed fetch as a network failure without asserting a TLS cause", async () => {
        const oldFetch = globalThis.fetch;
        globalThis.fetch = (async () => { throw new TypeError("Failed to fetch"); }) as typeof fetch;
        try {
            const error = await requestOpenAIImageJob(context, request("responses")).catch((error) => error);
            expect(error).toMatchObject({ code: "FETCH_NETWORK_ERROR", originalMessage: "Failed to fetch" });
            expect(error.message).toContain("网络连接失败");
            expect(error.message).not.toContain("TLS");
            expect(error).not.toHaveProperty("cause");
        } finally {
            globalThis.fetch = oldFetch;
        }
    });

    test("normalizes streaming Images generation ratio, pixel and auto sizes without mutating the snapshot", async () => {
        const oldFetch = globalThis.fetch;
        let body: any;
        globalThis.fetch = (async (_input: RequestInfo | URL, init?: RequestInit) => {
            body = JSON.parse(String(init?.body));
            return new Response("", { headers: { "content-type": "text/event-stream" } });
        }) as typeof fetch;
        streamEvents = [{ event: "image_generation.completed", data: { b64_json: "aGVsbG8=" } }];
        try {
            for (const [size, expected] of [["3:4", "768x1024"], ["2480x3312", "2480x3312"], ["auto", "auto"]]) {
                const original = Object.freeze({ ...request("images", true), quality: "max", size });
                await requestOpenAIImageJob(context, original);
                expect(body.size).toBe(expected);
                expect(body.quality).toBe("max");
                expect(original.size).toBe(size);
            }
        } finally {
            globalThis.fetch = oldFetch;
            streamEvents = [];
        }
    });

    test("normalizes streaming Images edit sizes and leaves multipart auto size omitted", async () => {
        const oldFetch = globalThis.fetch;
        let body: FormData;
        globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
            if (String(input).startsWith("data:image/")) return new Response("reference", { headers: { "content-type": "image/png" } });
            body = init?.body as FormData;
            return new Response("", { headers: { "content-type": "text/event-stream" } });
        }) as typeof fetch;
        streamEvents = [{ event: "image_edit.completed", data: { b64_json: "aGVsbG8=" } }];
        try {
            for (const [size, expected] of [["3:4", "768x1024"], ["2480x3312", "2480x3312"], ["auto", null]]) {
                const original = Object.freeze({ ...request("images", true), quality: "max", size: size!, references: [{ id: "source", name: "source.png", type: "image/png", dataUrl: "data:image/png;base64,c291cmNl" }] });
                await requestOpenAIImageJob(context, original);
                expect(body!.get("size")).toBe(expected);
                expect(body!.get("quality")).toBe("max");
                expect(original.size).toBe(size);
            }
        } finally {
            globalThis.fetch = oldFetch;
            streamEvents = [];
        }
    });

    test("normalizes both Responses modes' image tool sizes while preserving source settings", async () => {
        const oldFetch = globalThis.fetch;
        let body: any;
        const completed = { status: "completed", output: [{ type: "image_generation_call", result: "aGVsbG8=" }] };
        globalThis.fetch = (async (_input: RequestInfo | URL, init?: RequestInit) => {
            body = JSON.parse(String(init?.body));
            return new Response(JSON.stringify(completed), { headers: { "content-type": body.stream ? "text/event-stream" : "application/json" } });
        }) as typeof fetch;
        streamEvents = [{ event: "response.completed", data: { response: completed } }];
        try {
            for (const stream of [false, true]) {
                for (const [size, expected] of [["3:4", "768x1024"], ["2480x3312", "2480x3312"], ["auto", "auto"]]) {
                    const original = Object.freeze({ ...request("responses", stream), quality: "max", size });
                    await requestOpenAIImageJob(context, original);
                    expect(body.tools[0].size).toBe(expected);
                    expect(body.tools[0].quality).toBe("max");
                    expect(original.size).toBe(size);
                }
            }
        } finally {
            globalThis.fetch = oldFetch;
            streamEvents = [];
        }
    });
});
