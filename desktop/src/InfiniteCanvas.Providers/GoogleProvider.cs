using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using InfiniteCanvas.Core.Models;
using InfiniteCanvas.Core.Settings;

namespace InfiniteCanvas.Providers;

/// <summary>Google Gemini API: Interactions API for images and text, Veo predictLongRunning for video.</summary>
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
            if (type == "interaction.completed" && Http.Str(data["interaction"]?["status"]) is { } status && status != "completed")
                throw new ApiException(Http.Message(data["interaction"]) ?? $"Interactions 状态为 {status}");
        }
        return text.ToString();
    }

    public override async Task<string> CreateVideoTaskAsync(VideoRequest request, CancellationToken ct)
    {
        var family = ModelFamilies.FindVideo(Vendor.Google, request.Model);
        if (family?.Conversational == true) return await CreateOmniTaskAsync(request, ct).ConfigureAwait(false);
        static JsonObject Inline(MediaInput image) => new() { ["bytesBase64Encoded"] = image.Base64, ["mimeType"] = image.MimeType };
        var instance = new JsonObject { ["prompt"] = request.Prompt };
        var reference = request.Mode == VideoMode.Reference && request.Images.Count > 0;
        // Extension continues a Veo video from the last two days by 7 s; it is 720p and 8 s only and takes no images.
        if (request.Task == "extend")
        {
            var source = request.Videos.FirstOrDefault() ?? throw new ApiException("延长视频需要输入一个视频");
            instance["video"] = IsGeminiFile(source.Url) ? new JsonObject { ["uri"] = source.Url } : new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = source.MimeType, ["data"] = source.Base64 } };
            return await PredictAsync(request.Model, instance, request.Ratio, 8, "720p", ct).ConfigureAwait(false);
        }
        if (reference)
        {
            if (family is { MaxReferences: 0 }) throw new ApiException($"{request.Model} 不支持参考图生成视频");
            instance["referenceImages"] = new JsonArray(request.Images.Take(family?.MaxReferences ?? 3).Select(image => (JsonNode)new JsonObject { ["image"] = Inline(image), ["referenceType"] = "asset" }).ToArray());
        }
        else
        {
            if (request.Images.Count > 0) instance["image"] = Inline(request.Images[0]);
            if (request.Images.Count > 1) instance["lastFrame"] = Inline(request.Images[1]);
        }
        // Veo only accepts 8 seconds for 1080p, 4k and reference images.
        var seconds = request.Resolution != "720p" || reference ? 8 : request.Seconds;
        return await PredictAsync(request.Model, instance, request.Ratio, seconds, request.Resolution, ct).ConfigureAwait(false);
    }

    private async Task<string> PredictAsync(string model, JsonObject instance, string ratio, int seconds, string resolution, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["instances"] = new JsonArray { instance },
            ["parameters"] = new JsonObject { ["aspectRatio"] = ratio is "16:9" or "9:16" ? ratio : "16:9", ["durationSeconds"] = seconds, ["resolution"] = resolution },
        };
        var id = model.StartsWith("models/") ? model[7..] : model;
        var created = await Http.PostJsonAsync($"{Base}/models/{Uri.EscapeDataString(id)}:predictLongRunning", Auth, body, ct).ConfigureAwait(false);
        return Http.Str(created["name"]) ?? throw new ApiException("接口未返回视频任务 ID");
    }

    /// <summary>A Files API address of this vendor (a Veo or Omni result); only those can be passed by URI.</summary>
    private static bool IsGeminiFile(string? url) => url is not null && url.Contains("generativelanguage.googleapis.com/") && url.Contains("/files/");

    /// <summary>
    /// Gemini Omni through Interactions: a stored background interaction polled until done. Edit and extend work on the first
    /// input video, or on the previous turn when PreviousId continues a stored interaction (no video is resent then).
    /// </summary>
    private async Task<string> CreateOmniTaskAsync(VideoRequest request, CancellationToken ct)
    {
        var blocks = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = request.Prompt } };
        foreach (var image in request.Images) blocks.Add(new JsonObject { ["type"] = "image", ["mime_type"] = image.MimeType, ["data"] = image.Base64 });
        foreach (var video in request.PreviousId is null ? request.Videos : [])
            blocks.Add(IsGeminiFile(video.Url) ? new JsonObject { ["type"] = "video", ["uri"] = video.Url } : new JsonObject { ["type"] = "video", ["mime_type"] = video.MimeType, ["data"] = video.Base64 });
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["input"] = new JsonArray { new JsonObject { ["type"] = "user_input", ["content"] = blocks } },
            // Background needs a stored interaction, which is also what lets the next turn refine this video.
            ["background"] = true,
            ["store"] = true,
            ["response_format"] = new JsonObject { ["type"] = "video", ["aspect_ratio"] = request.Ratio is "9:16" ? "9:16" : "16:9", ["resolution"] = request.Resolution, ["delivery"] = "uri" },
        }
        .Set("previous_interaction_id", request.PreviousId)
        // The task is left to the prompt unless the user asked to edit or extend: naming it adds constraints.
        .Set("generation_config", request.Task is "edit" or "extend" ? new JsonObject { ["video_config"] = new JsonObject { ["task"] = request.Task } } : null);
        var created = await Http.PostJsonAsync($"{Base}/interactions", Auth, body, ct).ConfigureAwait(false);
        var interaction = created["interaction"] ?? created;
        return Http.Str(interaction["id"]) ?? throw new ApiException("接口未返回视频任务 ID");
    }

    public override async Task<VideoTaskState> PollVideoTaskAsync(string taskId, CancellationToken ct)
    {
        // Veo returns operation names ("models/…/operations/…"); anything else is an Omni interaction ID.
        if (!taskId.Contains("/operations/")) return await PollOmniAsync(taskId, ct).ConfigureAwait(false);
        var state = await Http.QueryJsonAsync($"{Base}/{taskId.TrimStart('/')}", Auth, ct).ConfigureAwait(false);
        if (state["error"] is JsonNode error) return new(VideoTaskStatus.Failed, Error: Http.Message(error) ?? "视频生成失败");
        if (state["done"]?.GetValue<bool>() != true) return new(VideoTaskStatus.Pending);
        var result = state["response"]?["generateVideoResponse"];
        var uri = Http.Str(result?["generatedSamples"]?[0]?["video"]?["uri"]);
        if (uri is null) return new(VideoTaskStatus.Failed, Error: result?["raiMediaFilteredReasons"].Items().Select(Http.Str).FirstOrDefault() ?? "未返回可播放的视频");
        var (bytes, mime) = await Http.DownloadAsync(uri, Auth, ct).ConfigureAwait(false);
        // Veo keeps the file for two days, during which it can be extended by URI.
        return new(VideoTaskStatus.Completed, bytes, mime.StartsWith("video/") ? mime : "video/mp4") { RemoteUrl = uri, RemoteUntil = DateTime.UtcNow.AddHours(47) };
    }

    private async Task<VideoTaskState> PollOmniAsync(string id, CancellationToken ct)
    {
        var interaction = await Http.QueryJsonAsync($"{Base}/interactions/{Uri.EscapeDataString(id)}", Auth, ct).ConfigureAwait(false);
        var status = Http.Str(interaction["status"]);
        if (status is "failed" or "cancelled" or "incomplete" or "budget_exceeded") return new(VideoTaskStatus.Failed, Error: Http.Message(interaction) ?? $"Interactions 状态为 {status}");
        if (status != "completed") return new(VideoTaskStatus.Pending);
        var outputs = interaction["steps"].Items().Where(s => Http.Str(s["type"]) == "model_output").SelectMany(s => s["content"].Items()).ToList();
        var video = outputs.FirstOrDefault(c => Http.Str(c["type"]) == "video");
        if (video is null) return new(VideoTaskStatus.Failed, Error: outputs.Select(c => Http.Str(c["text"])).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "未返回可播放的视频");
        var mime = Http.Str(video["mime_type"]) ?? "video/mp4";
        // The stored interaction returns the video inline; a Files API URI is downloaded once the file is ACTIVE.
        if (Http.Str(video["data"]) is { } data) return new(VideoTaskStatus.Completed, Convert.FromBase64String(data), mime) { ConversationId = id };
        if (Http.Str(video["uri"]) is not { } uri || System.Text.RegularExpressions.Regex.Match(uri, "files/([^:/?]+)") is not { Success: true } file)
            return new(VideoTaskStatus.Failed, Error: "未返回可播放的视频");
        var name = file.Groups[1].Value;
        var meta = await Http.QueryJsonAsync($"{Base}/files/{name}", Auth, ct).ConfigureAwait(false);
        if (Http.Str(meta["state"]) == "FAILED") return new(VideoTaskStatus.Failed, Error: Http.Message(meta["error"]) ?? "视频文件处理失败");
        if (Http.Str(meta["state"]) != "ACTIVE") return new(VideoTaskStatus.Pending);
        var (bytes, type) = await Http.DownloadAsync($"{Base}/files/{name}:download?alt=media", Auth, ct).ConfigureAwait(false);
        return new(VideoTaskStatus.Completed, bytes, type.StartsWith("video/") ? type : mime) { ConversationId = id, RemoteUrl = $"{Base}/files/{name}", RemoteUntil = DateTime.UtcNow.AddHours(47) };
    }

    /// <summary>Only Omni interactions can be cancelled; Veo operations run to completion.</summary>
    public override async Task CancelTaskAsync(string taskId)
    {
        if (taskId.Contains("/operations/")) return;
        await Http.PostJsonAsync($"{Base}/interactions/{Uri.EscapeDataString(taskId)}/cancel", Auth, new JsonObject(), CancellationToken.None).ConfigureAwait(false);
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
