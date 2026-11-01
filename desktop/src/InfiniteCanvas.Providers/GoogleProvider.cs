using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using InfiniteCanvas.Core.Models;
using InfiniteCanvas.Core.Settings;

namespace InfiniteCanvas.Providers;

/// <summary>Google Gemini API: Interactions API for images, text and speech.</summary>
public sealed class GoogleProvider(ModelChannel channel, HttpClient http) : AiProvider(channel, http)
{
    private Action<HttpRequestHeaders> Auth => headers => headers.Add("x-goog-api-key", Key);

    private static JsonArray Blocks(string prompt, IEnumerable<MediaInput> images)
    {
        var blocks = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = prompt } };
        foreach (var image in images) blocks.Add(new JsonObject { ["type"] = "image", ["mime_type"] = image.MimeType, ["data"] = image.Base64 });
        return blocks;
    }

    private static JsonArray UserInput(string prompt, IEnumerable<MediaInput> images) => new() { new JsonObject { ["type"] = "user_input", ["content"] = Blocks(prompt, images) } };

    /// <summary>
    /// Interaction usage: thinking is billed as text output, and image / audio output tokens have their own price, so they are split out by modality.
    /// Without a modality breakdown, an image or speech request's output is taken as media.
    /// </summary>
    private void CountUsage(JsonNode? usage, bool media)
    {
        if (usage is not JsonObject) return;
        var output = (long)Http.Decimal(usage["total_output_tokens"]);
        var byModality = usage["output_tokens_by_modality"].Items().ToList();
        var mediaTokens = byModality.Count > 0
            ? byModality.Where(m => Http.Str(m["modality"]) is "image" or "audio" or "video").Sum(m => (long)Http.Decimal(m["tokens"]))
            : media ? output : 0;
        Count((long)Http.Decimal(usage["total_input_tokens"]), output - mediaTokens + (long)Http.Decimal(usage["total_thought_tokens"]), mediaTokens);
    }

    public override async Task<ImageResult> GenerateImageAsync(ImageRequest request, IProgress<ImageEvent>? progress, CancellationToken ct)
    {
        var options = request.Options;
        var family = ModelFamilies.FindImage(Vendor.Google, request.Model) ?? throw new ApiException($"Google Gemini 暂不支持型号 {request.Model}");
        if (request.Count != 1) throw new ApiException("Google Interactions API 每次只生成 1 张图片");
        if (request.Mask is not null) throw new ApiException("Google Interactions API 不支持蒙版编辑");
        if (request.References.Count > family.MaxReferences) throw new ApiException($"{request.Model} 最多支持 {family.MaxReferences} 张参考图");

        var format = new JsonObject { ["type"] = "image", ["mime_type"] = "image/jpeg" }
            .Set("aspect_ratio", family.Ratios.Contains(options.Ratio) ? options.Ratio : null)
            .Set("image_size", family.Tiers.Contains(options.Tier) ? options.Tier : null);
        var searchTypes = new JsonArray();
        if (options.WebSearch && family.WebSearch) searchTypes.Add("web_search");
        if (options.ImageSearch && family.ImageSearch) searchTypes.Add("image_search");
        var thinking = options.Thinking is { } level && level != "auto" && family.ThinkingLevels.Contains(level) ? level : null;
        // A stored interaction carries the earlier turns, so a continuation only sends the new instruction.
        var body = new JsonObject { ["model"] = request.Model, ["input"] = UserInput(request.Prompt, request.References), ["store"] = options.Store, ["response_format"] = format }
            .Set("previous_interaction_id", request.PreviousId)
            .Set("tools", searchTypes.Count > 0 ? new JsonArray { new JsonObject { ["type"] = "google_search", ["search_types"] = searchTypes } } : null)
            .Set("generation_config", thinking is null ? null : new JsonObject { ["thinking_level"] = thinking });

        var payload = await Http.PostJsonAsync($"{Base}/interactions", Auth, body, ct).ConfigureAwait(false);
        var interaction = payload["interaction"] as JsonObject ?? payload.AsObject();
        if (Http.Str(interaction["status"]) is { } status && status != "completed") throw new ApiException(Http.Message(interaction) ?? $"Interactions 状态为 {status}");
        CountUsage(interaction["usage"], media: true);
        var images = new List<GeneratedImage>();
        var texts = new List<string>();
        foreach (var step in interaction["steps"].Items().Where(step => Http.Str(step["type"]) == "model_output"))
        foreach (var content in step["content"].Items())
        {
            if (Http.Str(content["type"]) == "text" && Http.Str(content["text"]) is { } text) texts.Add(text);
            if (Http.Str(content["type"]) == "image" && Http.Str(content["data"]) is { } data)
            {
                var image = data.StartsWith("data:") ? Http.DecodeDataUrl(data) : (Convert.FromBase64String(data), Http.Str(content["mime_type"]) ?? "image/jpeg");
                images.Add(new(image.Item1, image.Item2));
                progress?.Report(new ImageEvent(Index: images.Count - 1, Output: images[^1]));
            }
        }
        return images.Count > 0 ? new(images, texts, [], options.Store ? Http.Str(interaction["id"]) : null) : throw new ApiException(texts.FirstOrDefault() ?? "Google Interactions 未返回图片");
    }

    public override async Task<string> GenerateTextAsync(TextRequest request, Action<string>? onDelta, CancellationToken ct)
    {
        var body = new JsonObject { ["model"] = request.Model, ["input"] = UserInput(request.Prompt, request.Images), ["store"] = false, ["stream"] = true }
            .Set("system_instruction", string.IsNullOrWhiteSpace(request.System) ? null : request.System)
            .Set("generation_config", request.Reasoning is null or "" or "auto" ? null : new JsonObject { ["thinking_level"] = request.Reasoning == "xhigh" ? "high" : request.Reasoning });
        using var response = await Http.PostStreamAsync($"{Base}/interactions", Auth, Http.Content(body), ct).ConfigureAwait(false);
        var text = new StringBuilder();
        void Append(string? delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            text.Append(delta);
            onDelta?.Invoke(text.ToString());
        }
        await foreach (var item in Http.ReadSse(response, ct).ConfigureAwait(false))
        {
            if (item.Data == "[DONE]") break;
            var data = JsonNode.Parse(item.Data)!;
            var type = Http.Str(data["event_type"]) ?? item.Event;
            if (type is "error" or "interaction.failed") throw new ApiException(Http.Message(data) ?? Http.Message(data["interaction"]) ?? "文本生成失败");
            if (type == "step.start" && Http.Str(data["step"]?["type"]) == "model_output")
                foreach (var content in data["step"]!["content"].Items().Where(c => Http.Str(c["type"]) == "text")) Append(Http.Str(content["text"]));
            if (type == "step.delta" && Http.Str(data["delta"]?["type"]) == "text") Append(Http.Str(data["delta"]!["text"]));
            if (type == "interaction.completed")
            {
                CountUsage(data["interaction"]?["usage"], media: false);
                if (Http.Str(data["interaction"]?["status"]) != "completed")
                    throw new ApiException(Http.Message(data["interaction"]) ?? "文本生成未完整结束，已保留收到的部分内容");
                return text.ToString();
            }
        }
        throw new ApiException("文本响应在完成前中断，已保留收到的部分内容，请重试");
    }

    /// <summary>
    /// Gemini TTS through Interactions: the text is read verbatim, delivery instructions travel as speech_metadata.style,
    /// and the audio comes back as a 24 kHz mono WAV.
    /// </summary>
    public override async Task<GeneratedImage> SpeechAsync(SpeechRequest request, CancellationToken ct)
    {
        var text = new JsonObject { ["type"] = "text", ["text"] = request.Input }
            .Set("annotations", string.IsNullOrWhiteSpace(request.Instructions) ? null : new JsonArray { new JsonObject { ["type"] = "speech_metadata", ["style"] = request.Instructions } });
        var voice = ModelFamilies.GeminiVoices.FirstOrDefault(v => v.Equals(request.Voice, StringComparison.OrdinalIgnoreCase)) ?? ModelFamilies.GeminiVoices[0];
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["input"] = new JsonArray { new JsonObject { ["type"] = "user_input", ["content"] = new JsonArray { text } } },
            ["store"] = false,
            ["response_format"] = new JsonObject { ["type"] = "audio", ["mime_type"] = "audio/wav" },
            ["generation_config"] = new JsonObject { ["speech_config"] = new JsonArray { new JsonObject { ["voice"] = voice } } },
        };
        var payload = await Http.PostJsonAsync($"{Base}/interactions", Auth, body, ct).ConfigureAwait(false);
        var interaction = payload["interaction"] ?? payload;
        CountUsage(interaction["usage"], media: true);
        var audio = interaction["steps"].Items().Where(s => Http.Str(s["type"]) == "model_output").SelectMany(s => s["content"].Items())
            .FirstOrDefault(c => Http.Str(c["type"]) == "audio" && Http.Str(c["data"]) is not null);
        if (audio is null) throw new ApiException(Http.Message(interaction) ?? "Gemini 未返回音频");
        return new(Convert.FromBase64String(Http.Str(audio["data"])!), Http.Str(audio["mime_type"]) ?? "audio/wav");
    }

    public override async Task<List<string>> ListModelsAsync(CancellationToken ct)
    {
        var payload = await Http.QueryJsonAsync($"{Base}/models?pageSize=1000", Auth, ct).ConfigureAwait(false);
        return payload["models"].Items().Select(item => Http.Str(item["name"])?.Replace("models/", "")).OfType<string>().Order().ToList();
    }
}
