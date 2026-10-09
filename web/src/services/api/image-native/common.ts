export { imageToDataUrl } from "@/services/image-storage";
export { withLocalProxy } from "@/stores/use-config-store";

export async function checkResponse(response: Response): Promise<void> {
    if (response.ok) return;
    const payload = (await response.json().catch(() => null)) ?? {};
    const error = payload.error || payload;
    const message = typeof error === "string" ? error : typeof error.message === "string" ? error.message : `图片请求失败（HTTP ${response.status}）`;
    throw Object.assign(new Error(message), {
        status: response.status,
        code: error.code ?? payload.code,
        requestId: response.headers.get("x-request-id") || response.headers.get("x-tt-logid") || undefined,
    });
}

/** Shared SSE framing; the provider adapters own JSON and event semantics. */
export async function readSse(response: Response, onEvent: (event: { event: string; data: string }) => void | Promise<void>): Promise<void> {
    await checkResponse(response);
    if (!response.body) throw new Error("接口未返回流式响应内容");
    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";
    let event = "message";
    let data: string[] = [];
    const consumeLine = async (line: string) => {
        if (!line) {
            if (data.length) await onEvent({ event, data: data.join("\n") });
            event = "message";
            data = [];
        } else if (line.startsWith("data:")) data.push(line.slice(5).replace(/^ /, ""));
        else if (line.startsWith("event:")) event = line.slice(6).trim();
    };
    try {
        while (true) {
            const { value, done } = await reader.read();
            buffer += done ? decoder.decode() + "\n\n" : decoder.decode(value, { stream: true });
            let match: RegExpExecArray | null;
            while ((match = /\r\n|\r|\n/.exec(buffer))) {
                if (!done && match[0] === "\r" && match.index === buffer.length - 1) break;
                await consumeLine(buffer.slice(0, match.index));
                buffer = buffer.slice(match.index + match[0].length);
            }
            if (done) break;
        }
    } finally {
        reader.releaseLock();
    }
}
