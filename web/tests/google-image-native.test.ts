import { describe, expect, mock, test } from "bun:test";

type SseEvent = { event: string; data: Record<string, unknown> | "[DONE]" };
let streamEvents: SseEvent[] = [];
let interactionCompletedDelivered = false;

mock.module("@/services/api/image-native/common", () => ({
    withLocalProxy: (url: string) => url,
    imageToDataUrl: async (image: { dataUrl: string }) => image.dataUrl,
    checkResponse: async (response: Response) => { if (!response.ok) throw new Error(`HTTP ${response.status}`); },
    readSse: async (_response: Response, onEvent: (event: { event: string; data: string }) => void | Promise<void>) => {
        for (const item of streamEvents) {
            if (item.event === "interaction.completed") interactionCompletedDelivered = true;
            await onEvent({ event: item.event, data: typeof item.data === "string" ? item.data : JSON.stringify(item.data) });
        }
    },
}));

const { requestGoogleImageJob } = await import("@/services/api/image-native/google");
const baseRequest = {
    model: "gemini-3-pro-image",
    prompt: "a tree",
    references: [],
    count: 1,
    quality: "auto",
    size: "1024x1024",
    background: "auto",
    outputFormat: "jpeg" as const,
    outputCompression: 0,
    moderation: "auto" as const,
    native: { stream: false, google: { apiVersion: "v1" as const, imageSize: "1K" as const, aspectRatio: "16:9", store: true } },
};
const context = { baseUrl: "https://generativelanguage.googleapis.com", apiKey: "secret", channelId: "channel-1" };

describe("Google Interactions native image adapter", () => {
    test("sends Flash minimal thinking but rejects an undocumented Pro thinking level", async () => {
        const oldFetch = globalThis.fetch;
        let calls = 0;
        let body: any;
        globalThis.fetch = (async (_url, init) => {
            calls += 1;
            body = JSON.parse(String(init?.body));
            return new Response(JSON.stringify({ id: "thinking-test", steps: [{ type: "model_output", content: [{ type: "image", data: "abc", mime_type: "image/jpeg" }] }] }));
        }) as typeof fetch;
        try {
            await expect(requestGoogleImageJob(context, { ...baseRequest, native: { google: { thinkingLevel: "high" } } })).rejects.toThrow("thinkingLevel");
            expect(calls).toBe(0);
            await requestGoogleImageJob(context, { ...baseRequest, model: "gemini-3.1-flash-image", native: { google: { thinkingLevel: "minimal", aspectRatio: "16:9" } } });
            expect(body.generation_config).toEqual({ thinking_level: "minimal" });
            expect(body.response_format.aspect_ratio).toBe("16:9");
        } finally { globalThis.fetch = oldFetch; }
    });
    test("rejects too many reference images before sending a request", async () => {
        await expect(requestGoogleImageJob(context, { ...baseRequest, references: Array.from({ length: 15 }, (_, index) => ({ id: String(index), name: "ref.png", type: "image/png", dataUrl: "data:image/png;base64,ref" })) })).rejects.toThrow("最多支持 14 张参考图");
    });
    test("sends native image blocks and records the input absent from the POST response", async () => {
        let captured: RequestInit | undefined;
        const oldFetch = globalThis.fetch;
        globalThis.fetch = (async (_input: RequestInfo | URL, init?: RequestInit) => {
            captured = init;
            return new Response(JSON.stringify({
                id: "gi_1",
                model: baseRequest.model,
                usage: { total_tokens: 3 },
                grounding: { sources: [] },
                search_suggestions: ["trees"],
                steps: [{ type: "model_output", content: [{ type: "text", text: "hello" }, { type: "image", data: "abc", mime_type: "image/png" }] }],
            }), { status: 200 });
        }) as typeof fetch;
        try {
            const output = await requestGoogleImageJob(context, baseRequest, {});
            const body = JSON.parse(String(captured?.body));
            expect(body).toMatchObject({
                model: baseRequest.model,
                store: true,
                input: [{ type: "user_input", content: [{ type: "text", text: "a tree" }] }],
                response_format: { type: "image", mime_type: "image/jpeg", aspect_ratio: "16:9", image_size: "1K" },
            });
            expect(body.response_format).not.toBeArray();
            expect(captured?.headers).toMatchObject({ "x-goog-api-key": "secret" });
            expect(output.images).toHaveLength(1);
            expect(output.outputOrder).toEqual([{ kind: "text", index: 0 }, { kind: "image", index: 0 }]);
            expect(output.conversation).toMatchObject({ provider: "google", protocol: "google-interactions", remoteId: "gi_1", stored: true });
            expect(output.conversation?.history).toEqual([
                { type: "user_input", content: [{ type: "text", text: "a tree" }] },
                { type: "model_output", content: [{ type: "text", text: "hello" }, { type: "image", data: "abc", mime_type: "image/png" }] },
            ]);
            expect(output.images[0]?.response?.rawUsage).toEqual({ total_tokens: 3 });
            expect(output.providerMetadata).toMatchObject({ interactionId: "gi_1", interactionModel: baseRequest.model, searchSuggestions: ["trees"] });
        } finally {
            globalThis.fetch = oldFetch;
        }
    });

    test("sends text plus image format only when selected", async () => {
        let captured: RequestInit | undefined;
        const oldFetch = globalThis.fetch;
        globalThis.fetch = (async (_input: RequestInfo | URL, init?: RequestInit) => {
            captured = init;
            return new Response(JSON.stringify({ id: "gi_text", steps: [{ type: "model_output", content: [{ type: "image", data: "abc", mime_type: "image/png" }] }] }), { status: 200 });
        }) as typeof fetch;
        try {
            await requestGoogleImageJob(context, { ...baseRequest, native: { ...baseRequest.native, google: { ...baseRequest.native.google, includeText: true } } }, {});
            expect(JSON.parse(String(captured?.body)).response_format).toEqual([
                { type: "text" },
                { type: "image", mime_type: "image/jpeg", aspect_ratio: "16:9", image_size: "1K" },
            ]);
        } finally {
            globalThis.fetch = oldFetch;
        }
    });

    test("preserves search-result suggestions and inline URL citations without rendering the HTML", async () => {
        const oldFetch = globalThis.fetch;
        globalThis.fetch = (async () => new Response(JSON.stringify({
            id: "gi_grounded",
            model: baseRequest.model,
            steps: [
                {
                    type: "model_output",
                    content: [
                        {
                            type: "text",
                            text: "Weather source",
                            annotations: [{ type: "url_citation", url: "https://weather.example/forecast", title: "Forecast", start_index: 0, end_index: 14 }],
                        },
                        { type: "image", data: "abc", mime_type: "image/png" },
                    ],
                },
                { type: "google_search_result", result: [{ search_suggestions: "<a href=\"https://search.example\">Search weather</a>", queries: ["weather"] }] },
                { type: "google_search_result", result: { search_suggestions: "<a href=\"https://images.example\">Image search</a>", search_queries: ["weather chart"] } },
            ],
        }), { status: 200 })) as typeof fetch;
        try {
            const output = await requestGoogleImageJob(context, baseRequest, {});
            expect(output.images).toHaveLength(1);
            expect(output.providerMetadata).toMatchObject({
                searchSuggestions: ["<a href=\"https://search.example\">Search weather</a>", "<a href=\"https://images.example\">Image search</a>"],
                searchQueries: ["weather", "weather chart"],
                citations: [{ url: "https://weather.example/forecast", title: "Forecast", start_index: 0, end_index: 14 }],
            });
            expect(output.conversation?.history).toContainEqual({ type: "google_search_result", result: { search_suggestions: "<a href=\"https://images.example\">Image search</a>", search_queries: ["weather chart"] } });
        } finally {
            globalThis.fetch = oldFetch;
        }
    });

    test("uses previous interaction only for a stored previous conversation", async () => {
        let captured: RequestInit | undefined;
        const oldFetch = globalThis.fetch;
        globalThis.fetch = (async (_input: RequestInfo | URL, init?: RequestInit) => {
            captured = init;
            return new Response("", { status: 200, headers: { "content-type": "text/event-stream" } });
        }) as typeof fetch;
        streamEvents = [
            { event: "interaction.created", data: { event_type: "interaction.created", interaction: { id: "gi_2", model: baseRequest.model, status: "in_progress" } } },
            { event: "step.start", data: { event_type: "step.start", index: 0, step: { type: "model_output" } } },
            { event: "step.delta", data: { event_type: "step.delta", index: 0, delta: { type: "text", text: "before " } } },
            { event: "step.delta", data: { event_type: "step.delta", index: 0, delta: { type: "image", content_index: 1, mime_type: "image/png", data: "abc" } } },
            { event: "step.delta", data: { event_type: "step.delta", index: 0, delta: { type: "text", text: "after" } } },
            { event: "step.delta", data: { event_type: "step.delta", index: 0, delta: { type: "image", content_index: 1, mime_type: "image/png", data: "def" } } },
            { event: "step.delta", data: { event_type: "step.delta", index: 0, delta: { type: "image", content_index: 2, mime_type: "image/png", data: "ghi" } } },
            { event: "step.stop", data: { event_type: "step.stop", index: 0 } },
            { event: "step.start", data: { event_type: "step.start", index: 1, step: { type: "thought" } } },
            { event: "step.delta", data: { event_type: "step.delta", index: 1, delta: { type: "thought_summary", content: { type: "text", text: "considering" } } } },
            { event: "step.delta", data: { event_type: "step.delta", index: 1, delta: { type: "thought_signature", signature: "signed" } } },
            { event: "step.stop", data: { event_type: "step.stop", index: 1 } },
            { event: "interaction.completed", data: { event_type: "interaction.completed", interaction: { id: "gi_2", model: baseRequest.model, status: "completed", usage: { total_tokens: 7 } } } },
            { event: "done", data: "[DONE]" },
        ];
        try {
            const previousHistory = [{ type: "user_input", content: [{ type: "text", text: "older" }] }];
            const output = await requestGoogleImageJob(context, { ...baseRequest, native: { ...baseRequest.native, stream: true } }, {
                conversation: { provider: "google", protocol: "google-interactions", model: baseRequest.model, remoteId: "previous", stored: true, createdAt: "now", history: previousHistory },
            });
            const body = JSON.parse(String(captured?.body));
            expect(body).toMatchObject({ previous_interaction_id: "previous", input: [{ type: "user_input", content: [{ type: "text", text: "a tree" }] }] });
            expect(body.input).toHaveLength(1);
            expect(output.texts).toEqual(["before after"]);
            expect(output.images.map((image) => image.dataUrl)).toEqual(["data:image/png;base64,abcdef", "data:image/png;base64,ghi"]);
            expect(output.outputOrder).toEqual([{ kind: "text", index: 0 }, { kind: "image", index: 0 }, { kind: "image", index: 1 }]);
            expect(output.conversation?.remoteId).toBe("gi_2");
            expect(output.conversation?.history).toContainEqual({ type: "thought", summary: [{ type: "text", text: "considering" }], signature: "signed" });
        } finally {
            globalThis.fetch = oldFetch;
            streamEvents = [];
        }
    });

    test("emits streamed text and completed-step image previews before interaction completion without duplicating text", async () => {
        const oldFetch = globalThis.fetch;
        const events: Array<{ type: string; text?: string; dataUrl?: string; completedDelivered: boolean }> = [];
        globalThis.fetch = (async () => new Response("", { status: 200, headers: { "content-type": "text/event-stream" } })) as typeof fetch;
        interactionCompletedDelivered = false;
        streamEvents = [
            { event: "interaction.created", data: { event_type: "interaction.created", interaction: { id: "gi_progress", model: baseRequest.model, status: "in_progress" } } },
            { event: "step.start", data: { event_type: "step.start", index: 0, step: { type: "model_output", content: [{ type: "text", text: "already " }] } } },
            { event: "step.delta", data: { event_type: "step.delta", index: 0, delta: { type: "text", text: "streamed" } } },
            { event: "step.delta", data: { event_type: "step.delta", index: 0, delta: { type: "image", mime_type: "image/png", data: "abc" } } },
            { event: "step.delta", data: { event_type: "step.delta", index: 0, delta: { type: "image", mime_type: "image/png", data: "def" } } },
            { event: "step.stop", data: { event_type: "step.stop", index: 0 } },
            { event: "interaction.completed", data: { event_type: "interaction.completed", interaction: { id: "gi_progress", model: baseRequest.model, status: "completed" } } },
        ];
        try {
            const result = await requestGoogleImageJob(context, { ...baseRequest, native: { ...baseRequest.native, stream: true } }, {
                onEvent: (event) => events.push({ type: event.type, text: event.text, dataUrl: event.image?.dataUrl, completedDelivered: interactionCompletedDelivered }),
            });
            expect(events.filter((event) => event.type === "text")).toEqual([
                { type: "text", text: "already ", dataUrl: undefined, completedDelivered: false },
                { type: "text", text: "streamed", dataUrl: undefined, completedDelivered: false },
            ]);
            expect(events.find((event) => event.type === "preview")).toMatchObject({ dataUrl: "data:image/png;base64,abcdef", completedDelivered: false });
            expect(events.filter((event) => event.type === "output")).toHaveLength(1);
            expect(result.texts).toEqual(["already streamed"]);
            expect(result.images.map((image) => image.dataUrl)).toEqual(["data:image/png;base64,abcdef"]);
        } finally {
            globalThis.fetch = oldFetch;
            streamEvents = [];
            interactionCompletedDelivered = false;
        }
    });

    test("resends complete history for stateless conversations", async () => {
        let captured: RequestInit | undefined;
        const oldFetch = globalThis.fetch;
        globalThis.fetch = (async (_input: RequestInfo | URL, init?: RequestInit) => {
            captured = init;
            return new Response(JSON.stringify({ id: "gi_stateless", steps: [{ type: "model_output", content: [{ type: "image", data: "abc", mime_type: "image/png" }] }] }), { status: 200 });
        }) as typeof fetch;
        try {
            const history = [{ type: "user_input", content: [{ type: "text", text: "older" }] }, { type: "thought", signature: "keep-me", summary: [{ type: "text", text: "older reasoning" }] }, { type: "model_output", content: [{ type: "text", text: "older output" }] }];
            await requestGoogleImageJob(context, { ...baseRequest, native: { ...baseRequest.native, google: { ...baseRequest.native.google, store: false } } }, {
                conversation: { provider: "google", protocol: "google-interactions", model: baseRequest.model, remoteId: "not-used", stored: false, createdAt: "now", history },
            });
            const body = JSON.parse(String(captured?.body));
            expect(body).not.toHaveProperty("previous_interaction_id");
            expect(body.input).toEqual([...history, { type: "user_input", content: [{ type: "text", text: "a tree" }] }]);
            expect(body.input.filter((step: { type: string }) => step.type === "user_input")).toHaveLength(2);
            expect(body.input[1]).toEqual(history[1]);
        } finally {
            globalThis.fetch = oldFetch;
        }
    });

    test("rejects a stream without interaction.completed instead of fabricating a result", async () => {
        const oldFetch = globalThis.fetch;
        globalThis.fetch = (async () => new Response("", { status: 200, headers: { "content-type": "text/event-stream" } })) as typeof fetch;
        streamEvents = [{ event: "step.start", data: { event_type: "step.start", index: 0, step: { type: "model_output" } } }];
        try {
            await expect(requestGoogleImageJob(context, { ...baseRequest, native: { ...baseRequest.native, stream: true } }, {})).rejects.toThrow("流式响应未完成");
        } finally {
            globalThis.fetch = oldFetch;
            streamEvents = [];
        }
    });

    test("surfaces a native error event", async () => {
        const oldFetch = globalThis.fetch;
        globalThis.fetch = (async () => new Response("", { status: 200, headers: { "content-type": "text/event-stream" } })) as typeof fetch;
        streamEvents = [{ event: "error", data: { event_type: "error", error: { code: "gateway_timeout", message: "Deadline expired" } } }];
        try {
            await expect(requestGoogleImageJob(context, { ...baseRequest, native: { ...baseRequest.native, stream: true } }, {})).rejects.toThrow("Deadline expired");
        } finally {
            globalThis.fetch = oldFetch;
            streamEvents = [];
        }
    });

    test("rejects counts other than one without making a request", async () => {
        const oldFetch = globalThis.fetch;
        const fetchMock = mock(() => Promise.reject(new Error("must not call fetch")));
        globalThis.fetch = fetchMock as typeof fetch;
        try {
            await expect(requestGoogleImageJob(context, { ...baseRequest, count: 2 }, {})).rejects.toThrow("仅支持图片数量 1");
            expect(fetchMock).not.toHaveBeenCalled();
        } finally {
            globalThis.fetch = oldFetch;
        }
    });

    test("rejects output formats other than JPEG before making a request", async () => {
        const oldFetch = globalThis.fetch;
        const fetchMock = mock(() => Promise.reject(new Error("must not call fetch")));
        globalThis.fetch = fetchMock as typeof fetch;
        try {
            await expect(requestGoogleImageJob(context, { ...baseRequest, outputFormat: "png" }, {})).rejects.toThrow("仅支持 JPEG 输出格式");
            expect(fetchMock).not.toHaveBeenCalled();
        } finally {
            globalThis.fetch = oldFetch;
        }
    });
});
