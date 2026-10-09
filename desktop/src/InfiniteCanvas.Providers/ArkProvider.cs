using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using InfiniteCanvas.Core.Models;
using InfiniteCanvas.Core.Settings;

namespace InfiniteCanvas.Providers;

/// <summary>Volcengine Ark: Seedream image generation, Seedance video tasks and the Chat API.</summary>
public sealed class ArkProvider(ModelChannel channel, HttpClient http) : AiProvider(channel, http)
{
    private Action<HttpRequestHeaders> Auth => Http.Bearer(Key);

    private static string ModelOrEndpoint(string model, string? endpointId)
    {
        var id = endpointId?.Trim();
        if (string.IsNullOrEmpty(id)) return model;
        return id.StartsWith("ep-") ? id : throw new ApiException("推理接入点应填写 ep- 开头的 Endpoint ID");
    }

    public override async Task<ImageResult> GenerateImageAsync(ImageRequest request, IProgress<ImageEvent>? progress, CancellationToken ct)
    {
        var options = request.Options;
        var family = ModelFamilies.FindImage(Vendor.Ark, request.Model) ?? throw new ApiException($"火山方舟暂不支持型号 {request.Model}，请使用 Seedream 4.0 / 4.5 / 5.0 系列");
        if (request.References.Count > family.MaxReferences) throw new ApiException($"{request.Model} 最多支持 {family.MaxReferences} 张参考图");
        if (request.Count > 1 && !family.Sequential) throw new ApiException($"{request.Model} 每次只生成 1 张图片");
        if (request.References.Count + request.Count > family.MaxImages && request.Count > 1) throw new ApiException($"参考图与输出数量之和不能超过 {family.MaxImages}");
        if (request.Mask is not null) throw new ApiException("方舟图片接口不支持蒙版编辑，请改用点选或框选");
        var layers = options.LayerDecomposition;
        if (layers && (!family.Layers || request.References.Count != 1 || request.Count != 1)) throw new ApiException("图层拆分仅支持 Seedream 5.0 pro / flash，且只能输入 1 张图片、输出 1 组");
        if (options.Transparent && (!family.Interactive || request.References.Count != 1)) throw new ApiException("透明背景仅支持 Seedream 5.0 pro / flash，且只能输入 1 张带透明通道的图片");
        if (request.Selections is { Count: > 0 } && !family.Interactive) throw new ApiException($"{request.Model} 不支持点选 / 框选编辑，请使用 Seedream 5.0 pro / flash");

        var format = options.Transparent ? "png" : family.OutputFormats.Contains(options.OutputFormat) ? options.OutputFormat : family.OutputFormats[0];
        var stream = options.Stream && family.Stream;
        var body = new JsonObject
        {
            ["model"] = ModelOrEndpoint(request.Model, request.EndpointId),
            ["prompt"] = PromptWithSelections(request.Prompt, request.Selections, request.References.Count),
            ["response_format"] = "b64_json",
            ["watermark"] = options.Watermark,
            ["optimize_prompt_options"] = new JsonObject { ["mode"] = options.FastPrompt && family.FastPrompt ? "fast" : "standard" },
        }
        // Layer decomposition only takes a resolution tier (or auto); the output keeps the input's aspect.
        .Set("size", layers ? (family.Tiers.Contains(options.Tier) ? options.Tier : "auto") : ModelFamilies.ImageSize(family, options.Tier, options.Ratio))
        // Seedream 4.x has no output_format and always returns JPEG.
        .Set("output_format", family.OutputFormats.Length > 1 ? format : null)
        .Set("layer_decomposition", layers ? true : null)
        .Set("background", options.Transparent ? "transparent" : null)
        .Set("image", request.References.Count switch { 0 => null, 1 => request.References[0].DataUrl, _ => new JsonArray(request.References.Select(r => (JsonNode)r.DataUrl).ToArray()) })
        .Set("tools", options.WebSearch && family.WebSearch ? new JsonArray { new JsonObject { ["type"] = "web_search" } } : null)
        .Set("stream", stream ? true : null);
        if (family.Sequential)
        {
            body["sequential_image_generation"] = request.Count > 1 ? "auto" : "disabled";
            if (request.Count > 1) body["sequential_image_generation_options"] = new JsonObject { ["max_images"] = request.Count };
        }

        var mime = format == "png" ? "image/png" : "image/jpeg";
        var images = new List<GeneratedImage>();
        var errors = new List<string>();
        if (!stream)
        {
            var payload = await Http.PostJsonAsync($"{Base}/images/generations", Auth, body, ct).ConfigureAwait(false);
            foreach (var item in payload["data"].Items())
            {
                if (item["error"] is JsonNode error) errors.Add(Http.Message(error) ?? "单张图片生成失败");
                else if (await ReadImageAsync(item, mime, ct).ConfigureAwait(false) is { } read)
                {
                    // In layer mode the first item is the base image and the rest are transparent PNG layers.
                    var image = layers && images.Count > 0 ? read with { MimeType = "image/png", Layer = LayerOf(item) } : read;
                    images.Add(image);
                    progress?.Report(new ImageEvent(Index: images.Count - 1, Output: image));
                }
            }
        }
        else
        {
            using var response = await Http.PostStreamAsync($"{Base}/images/generations", Auth, Http.Content(body), ct).ConfigureAwait(false);
            var completed = false;
            await foreach (var item in Http.ReadSse(response, ct).ConfigureAwait(false))
            {
                if (item.Data == "[DONE]") break;
                var data = JsonNode.Parse(item.Data)!;
                var type = item.Event != "message" ? item.Event : Http.Str(data["type"]) ?? "";
                if (type == "image_generation.partial_failed") errors.Add(Http.Message(data["error"]) ?? "单张图片生成失败");
                else if (type == "error" || data["error"] is not null) throw new ApiException(Http.Message(data) ?? "方舟图片请求失败");
                else if (type == "image_generation.partial_succeeded" && await ReadImageAsync(data, mime, ct).ConfigureAwait(false) is { } image)
                {
                    images.Add(image);
                    progress?.Report(new ImageEvent(Index: images.Count - 1, Output: image));
                }
                else if (type == "image_generation.completed") completed = true;
            }
            if (!completed) throw new ApiException("方舟流式响应未收到结束事件");
        }
        return images.Count > 0 || errors.Count > 0 ? new(images, [], errors) : throw new ApiException("方舟响应未返回图片");
    }

    private static ImageLayer LayerOf(JsonNode item)
    {
        var box = item["bounding_box"]?["normalized"] as JsonArray;
        return new(item["z_index"]?.GetValue<int>() ?? 0, Http.Str(item["name"]), Http.Str(item["description"]),
            box is { Count: 4 } ? box.Select(v => v!.GetValue<double>()).ToArray() : null);
    }

    /// <summary>Seedream interactive editing: coordinates are 0–999 tags after the prompt, prefixed with the image number when several references are sent.</summary>
    private static string PromptWithSelections(string prompt, IReadOnlyList<Core.Canvas.ImageSelection>? selections, int references)
    {
        if (selections is not { Count: > 0 }) return prompt;
        static int C(double value) => Math.Min(999, (int)Math.Round(value * 1000));
        var prefix = references > 1 ? "图 1 " : "";
        var tags = selections.Select(s => s.Kind == Core.Canvas.ImageSelection.Point
            ? $"{prefix}<point>{C(s.X)} {C(s.Y)}</point>"
            : $"{prefix}<bbox>{C(s.X)} {C(s.Y)} {C(s.X2)} {C(s.Y2)}</bbox>");
        return $"{prompt}\n{string.Join(" ", tags)}";
    }

    private async Task<GeneratedImage?> ReadImageAsync(JsonNode item, string mime, CancellationToken ct)
    {
        if (Http.Str(item["b64_json"]) is { } b64) return new(Convert.FromBase64String(b64), Http.Str(item["output_format"]) == "png" ? "image/png" : mime);
        if (Http.Str(item["url"]) is not { } url) return null;
        var (bytes, type) = await Http.DownloadAsync(url, null, ct).ConfigureAwait(false);
        return new(bytes, type);
    }

    public override async Task<string> CreateVideoTaskAsync(VideoRequest request, CancellationToken ct)
    {
        var family = ModelFamilies.FindVideo(Vendor.Ark, request.Model);
        var model = ModelOrEndpoint(request.Model, request.EndpointId);
        // The final video of a draft reuses the draft's prompt, inputs and settings; only these fields are allowed.
        if (request.DraftTaskId is { } draftId)
            return await CreateTaskAsync(new JsonObject
            {
                ["model"] = model,
                ["content"] = new JsonArray { new JsonObject { ["type"] = "draft_task", ["draft_task"] = new JsonObject { ["id"] = draftId } } },
                ["resolution"] = "1080p",
                ["watermark"] = request.Watermark,
            }.Set("return_last_frame", request.ReturnLastFrame ? true : null), ct).ConfigureAwait(false);

        // Reference clips make it a multimodal reference task, where images are references rather than frames.
        var reference = request.Mode == VideoMode.Reference || request.Videos.Count > 0 || request.Audios.Count > 0;
        var content = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.Prompt)) content.Add(new JsonObject { ["type"] = "text", ["text"] = request.Prompt });
        for (var i = 0; i < request.Images.Count; i++)
        {
            var role = reference ? "reference_image" : i == 0 ? "first_frame" : "last_frame";
            content.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = request.Images[i].DataUrl }, ["role"] = role });
        }
        // Reference videos are taken by public URL or asset ID only, never as uploaded bytes.
        foreach (var video in request.Videos)
            content.Add(new JsonObject
            {
                ["type"] = "video_url",
                ["video_url"] = new JsonObject { ["url"] = video.Url ?? throw new ApiException("方舟只接受公网地址的参考视频，不能上传本地视频文件；可以使用 24 小时内由 Seedance 生成的视频作为参考") },
                ["role"] = "reference_video",
            });
        foreach (var audio in request.Audios)
            content.Add(new JsonObject { ["type"] = "audio_url", ["audio_url"] = new JsonObject { ["url"] = audio.DataUrl }, ["role"] = "reference_audio" });
        var task = request.Task is { } value && family?.Tasks.Contains(value) == true ? value : null;
        // Seedance 2.5 keeps the first frame's aspect ratio in frame tasks, and edit / extend keep the source video's, so only adaptive is accepted there.
        var adaptive = request.Ratio is "auto" or "adaptive" || task is not null || (family?.FramesAdaptiveOnly == true && !reference && request.Images.Count > 0);
        var draft = request.Draft && family?.Draft == true;
        var body = new JsonObject
        {
            ["model"] = model,
            ["content"] = content,
            ["ratio"] = adaptive ? "adaptive" : request.Ratio,
            // An edit keeps the source's length.
            ["duration"] = task == "edit" ? -1 : request.Seconds,
            // Drafts are always 480p previews.
            ["resolution"] = draft ? "480p" : request.Resolution,
            ["watermark"] = request.Watermark,
        }
        .Set("generate_audio", family?.Audio == true ? request.GenerateAudio : null)
        .Set("draft", draft ? true : null)
        .Set("return_last_frame", request.ReturnLastFrame && family?.LastFrame == true ? true : null)
        .Set("omni_reference_task_type", task);
        return await CreateTaskAsync(body, ct).ConfigureAwait(false);
    }

    private async Task<string> CreateTaskAsync(JsonObject body, CancellationToken ct)
    {
        var created = await Http.PostJsonAsync($"{Base}/contents/generations/tasks", Auth, body, ct).ConfigureAwait(false);
        return Http.Str(created["id"]) ?? throw new ApiException("接口未返回视频任务 ID");
    }

    public override async Task<VideoTaskState> PollVideoTaskAsync(string taskId, CancellationToken ct)
    {
        var state = await Http.QueryJsonAsync($"{Base}/contents/generations/tasks/{Uri.EscapeDataString(taskId)}", Auth, ct).ConfigureAwait(false);
        var status = Http.Str(state["status"]);
        if (status is "failed" or "expired" or "cancelled") return new(VideoTaskStatus.Failed, Error: Http.Message(state["error"]) ?? "视频生成失败");
        if (status != "succeeded") return new(VideoTaskStatus.Pending);
        if (Http.Str(state["content"]?["video_url"]) is not { } url) return new(VideoTaskStatus.Failed, Error: "未返回可播放的视频");
        var (bytes, mime) = await Http.DownloadAsync(url, null, ct).ConfigureAwait(false);
        var lastFrame = Http.Str(state["content"]?["last_frame_url"]) is { } frameUrl ? (await Http.DownloadAsync(frameUrl, null, ct).ConfigureAwait(false)).Bytes : null;
        // The result URL stays valid for 24 hours, long enough to reference the video in a follow-up edit or extension.
        return new(VideoTaskStatus.Completed, bytes, mime.StartsWith("video/") ? mime : "video/mp4") { RemoteUrl = url, RemoteUntil = DateTime.UtcNow.AddHours(23), LastFrame = lastFrame };
    }

    /// <summary>Ark Chat API with streamed deltas.</summary>
    public override async Task<string> GenerateTextAsync(TextRequest request, Action<string>? onDelta, CancellationToken ct)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.System)) messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.System });
        JsonNode userContent = request.Prompt;
        if (request.Images.Count > 0)
        {
            var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = request.Prompt } };
            foreach (var image in request.Images) parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = image.DataUrl } });
            userContent = parts;
        }
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = userContent });
        var body = new JsonObject { ["model"] = request.Model, ["messages"] = messages, ["stream"] = true };
        using var response = await Http.PostStreamAsync($"{Base}/chat/completions", Auth, Http.Content(body), ct).ConfigureAwait(false);
        var text = new StringBuilder();
        await foreach (var item in Http.ReadSse(response, ct).ConfigureAwait(false))
        {
            if (item.Data == "[DONE]") break;
            var data = JsonNode.Parse(item.Data)!;
            if (data["error"] is not null) throw new ApiException(Http.Message(data) ?? "文本生成失败");
            if (Http.Str(data["choices"]?[0]?["delta"]?["content"]) is { Length: > 0 } delta)
            {
                text.Append(delta);
                onDelta?.Invoke(text.ToString());
            }
        }
        return text.ToString();
    }
}
