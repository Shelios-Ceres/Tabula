using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Tabula.Providers;

/// <summary>Responses API text streaming, natively offered by both OpenAI and xAI.</summary>
internal static class ResponsesApi
{
    public static JsonNode Input(string prompt, IReadOnlyList<MediaInput> images) => images.Count == 0 ? prompt : Message(prompt, images);

    public static JsonArray Message(string prompt, IReadOnlyList<MediaInput> images)
    {
        var content = new JsonArray { new JsonObject { ["type"] = "input_text", ["text"] = prompt } };
        foreach (var image in images) content.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = image.DataUrl });
        return new JsonArray { new JsonObject { ["role"] = "user", ["content"] = content } };
    }

    public static async Task<string> StreamTextAsync(Http http, string url, Action<HttpRequestHeaders> auth, TextRequest request, Action<string>? onDelta, CancellationToken ct, Action<JsonNode?>? usage = null)
    {
        var body = new JsonObject { ["model"] = request.Model, ["input"] = Input(request.Prompt, request.Images), ["stream"] = true, ["store"] = false }
            .Set("instructions", string.IsNullOrWhiteSpace(request.System) ? null : request.System)
            .Set("reasoning", request.Reasoning is null or "" or "auto" ? null : new JsonObject { ["effort"] = request.Reasoning });
        using var response = await http.PostStreamAsync(url, auth, Http.Content(body), ct).ConfigureAwait(false);
        var text = new StringBuilder();
        await foreach (var item in Http.ReadSse(response, ct).ConfigureAwait(false))
        {
            if (item.Data == "[DONE]") break;
            var data = JsonNode.Parse(item.Data)!;
            var type = Http.Str(data["type"]) ?? item.Event;
            if (type is "error" or "response.failed" or "response.incomplete") throw new ApiException(Http.Message(data["response"]) ?? Http.Message(data) ?? "文本生成失败");
            if (type == "response.output_text.delta" && Http.Str(data["delta"]) is { } delta)
            {
                text.Append(delta);
                onDelta?.Invoke(text.ToString());
            }
            if (type == "response.output_text.done" && text.Length == 0 && Http.Str(data["text"]) is { } done)
            {
                text.Append(done);
                onDelta?.Invoke(text.ToString());
            }
            if (type == "response.completed")
            {
                usage?.Invoke(data["response"]?["usage"]);
                return text.ToString();
            }
        }
        throw new ApiException("文本响应在完成前中断，已保留收到的部分内容，请重试");
    }
}
