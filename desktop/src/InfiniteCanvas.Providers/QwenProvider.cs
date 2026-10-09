using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using InfiniteCanvas.Core.Models;
using InfiniteCanvas.Core.Settings;

namespace InfiniteCanvas.Providers;

/// <summary>Alibaba Cloud Model Studio via the native DashScope protocol: Qwen-Image generation/editing and Qwen text.</summary>
public sealed class QwenProvider(ModelChannel channel, HttpClient http) : AiProvider(channel, http)
{
    private Action<HttpRequestHeaders> Auth => Http.Bearer(Key);
    private string Multimodal => $"{Base}/services/aigc/multimodal-generation/generation";

    public override async Task<ImageResult> GenerateImageAsync(ImageRequest request, IProgress<ImageEvent>? progress, CancellationToken ct)
    {
        var options = request.Options;
        var family = ModelFamilies.FindImage(Vendor.Qwen, request.Model) ?? throw new ApiException($"千问暂不支持型号 {request.Model}");
        if (request.Mask is not null) throw new ApiException("千问图像接口不支持蒙版编辑");
        if (request.References.Count > family.MaxReferences) throw new ApiException($"{request.Model} 最多支持 {family.MaxReferences} 张参考图");
        if (request.Count < 1 || request.Count > family.MaxImages) throw new ApiException($"{request.Model} 单次可生成 1–{family.MaxImages} 张");

        // Images first in order, then exactly one text item.
        var content = new JsonArray();
        foreach (var image in request.References) content.Add(new JsonObject { ["image"] = image.DataUrl });
        content.Add(new JsonObject { ["text"] = request.Prompt });
        // Agent rewriting is text-to-image only; thinking only applies while the prompt is being rewritten.
        if (options.PromptExtend && options.PromptExtendMode == "agent" && request.References.Count > 0) throw new ApiException("智能体改写仅支持文生图，有参考图时请改用直接增强");
        var parameters = new JsonObject { ["n"] = request.Count, ["prompt_extend"] = options.PromptExtend, ["watermark"] = options.Watermark }
            .Set("size", ModelFamilies.ImageSize(family, options.Tier, options.Ratio))
            .Set("negative_prompt", family.NegativePrompt && !string.IsNullOrWhiteSpace(options.NegativePrompt) ? options.NegativePrompt : null)
            .Set("prompt_extend_mode", options.PromptExtend && options.PromptExtendMode is "direct" or "agent" ? options.PromptExtendMode : null)
            .Set("enable_thinking", options.PromptExtend ? options.EnableThinking : null)
            .Set("seed", options.Seed is >= 0 and <= 2147483647 ? options.Seed : null);
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["input"] = new JsonObject { ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = content } } },
            ["parameters"] = parameters,
        };
        var payload = await Http.PostJsonAsync(Multimodal, Auth, body, ct).ConfigureAwait(false);
        var images = new List<GeneratedImage>();
        foreach (var choice in payload["output"]?["choices"].Items() ?? [])
        foreach (var item in choice["message"]?["content"].Items() ?? [])
        {
            if (Http.Str(item["image"]) is not { } url) continue;
            var (bytes, mime) = await Http.DownloadAsync(url, null, ct).ConfigureAwait(false);
            images.Add(new(bytes, mime.StartsWith("image/") ? mime : "image/png"));
            progress?.Report(new ImageEvent(Index: images.Count - 1, Output: images[^1]));
        }
        return images.Count > 0 ? new(images, [], []) : throw new ApiException(Http.Message(payload) ?? "千问未返回图片");
    }

    /// <summary>
    /// Wan 2.7 video through the asynchronous video-synthesis protocol. Images go inline; audio and video must be URLs,
    /// so a local file is uploaded to DashScope's temporary storage first unless the clip still has a live result URL.
    /// </summary>
    public override async Task<string> CreateVideoTaskAsync(VideoRequest request, CancellationToken ct)
    {
        var family = ModelFamilies.FindVideo(Vendor.Qwen, request.Model) ?? throw new ApiException($"千问暂不支持视频型号 {request.Model}，请使用万相 2.7 系列");
        var oss = false;
        async Task<string> UrlOf(MediaInput input)
        {
            if (input.Url is { } url) return url;
            oss = true;
            return await UploadAsync(request.Model, input, ct).ConfigureAwait(false);
        }
        var media = new JsonArray();
        void Add(string type, string url) => media.Add(new JsonObject { ["type"] = type, ["url"] = url });
        var video = request.Videos.FirstOrDefault();
        var input = new JsonObject { ["prompt"] = request.Prompt };
        var parameters = new JsonObject { ["resolution"] = request.Resolution.ToUpperInvariant(), ["watermark"] = request.Watermark };
        switch (family.Id)
        {
            case "wan-2.7-t2v":
                if (request.Audios.Count > 0) input["audio_url"] = await UrlOf(request.Audios[0]).ConfigureAwait(false);
                parameters["ratio"] = request.Ratio;
                break;
            case "wan-2.7-i2v":
                // Continuing a clip may pin its last frame; otherwise first frame, optional last frame and driving audio.
                if (request.Task == "extend") Add("first_clip", await UrlOf(video ?? throw new ApiException("视频续写需要输入一个视频")).ConfigureAwait(false));
                else Add("first_frame", (request.Images.FirstOrDefault() ?? throw new ApiException("万相图生视频需要输入首帧图片")).DataUrl);
                if (request.Images.Count > (request.Task == "extend" ? 0 : 1)) Add("last_frame", request.Images[^1].DataUrl);
                if (request.Audios.Count > 0 && request.Task != "extend") Add("driving_audio", await UrlOf(request.Audios[0]).ConfigureAwait(false));
                break;
            case "wan-2.7-r2v":
                // Images and videos together are at most 5, referenced in the prompt as 图1 / 视频1 in their own order.
                foreach (var image in request.Images.Take(5)) Add("reference_image", image.DataUrl);
                foreach (var clip in request.Videos.Take(5 - media.Count)) Add("reference_video", await UrlOf(clip).ConfigureAwait(false));
                if (media.Count == 0) throw new ApiException("万相参考生视频需要连接参考图片或参考视频");
                parameters["ratio"] = request.Ratio;
                break;
            default:
                Add("video", await UrlOf(video ?? throw new ApiException("万相视频编辑需要输入一个视频：在已有视频的节点上生成，或连接一个视频节点")).ConfigureAwait(false));
                foreach (var image in request.Images.Take(4)) Add("reference_image", image.DataUrl);
                if (request.Ratio != "adaptive") parameters["ratio"] = request.Ratio;
                break;
        }
        if (media.Count > 0) input["media"] = media;
        // An edit keeps the source's length.
        if (family.Id != "wan-2.7-videoedit") parameters["duration"] = request.Seconds;
        var body = new JsonObject { ["model"] = request.Model, ["input"] = input, ["parameters"] = parameters };
        var created = await Http.PostJsonAsync($"{Base}/services/aigc/video-generation/video-synthesis", headers =>
        {
            Auth(headers);
            headers.Add("X-DashScope-Async", "enable");
            if (oss) headers.Add("X-DashScope-OssResourceResolve", "enable");
        }, body, ct).ConfigureAwait(false);
        return Http.Str(created["output"]?["task_id"]) ?? throw new ApiException(Http.Message(created) ?? "接口未返回视频任务 ID");
    }

    /// <summary>
    /// DashScope temporary upload: a policy for the model, then a form POST to its OSS bucket. The oss:// link only works for the
    /// same model and expires after 48 hours, and requests using it must send X-DashScope-OssResourceResolve.
    /// </summary>
    private async Task<string> UploadAsync(string model, MediaInput input, CancellationToken ct)
    {
        var policy = (await Http.QueryJsonAsync($"{Base}/uploads?action=getPolicy&model={Uri.EscapeDataString(model)}", Auth, ct).ConfigureAwait(false))["data"]
            ?? throw new ApiException("获取千问上传凭证失败");
        var extension = input.MimeType switch { "audio/mpeg" or "audio/mp3" => ".mp3", "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav", "video/quicktime" => ".mov", _ => input.MimeType.StartsWith("video/") ? ".mp4" : "" };
        var key = $"{Http.Str(policy["upload_dir"])}/{Guid.NewGuid():N}{extension}";
        using var form = new MultipartFormDataContent();
        foreach (var (name, field) in new[] { ("OSSAccessKeyId", "oss_access_key_id"), ("Signature", "signature"), ("policy", "policy"), ("x-oss-object-acl", "x_oss_object_acl"), ("x-oss-forbid-overwrite", "x_oss_forbid_overwrite") })
            form.Add(new StringContent(Http.Str(policy[field]) ?? ""), name);
        form.Add(new StringContent(key), "key");
        form.Add(new StringContent("200"), "success_action_status");
        // The file must be the last field.
        form.Add(new ByteArrayContent(input.Bytes) { Headers = { ContentType = new(input.MimeType) } }, "file", Path.GetFileName(key));
        using var _ = await Http.SendAsync(HttpMethod.Post, Http.Str(policy["upload_host"]) ?? throw new ApiException("千问上传凭证缺少上传地址"), null, form, ct).ConfigureAwait(false);
        return $"oss://{key}";
    }

    public override async Task<VideoTaskState> PollVideoTaskAsync(string taskId, CancellationToken ct)
    {
        var state = await Http.QueryJsonAsync($"{Base}/tasks/{Uri.EscapeDataString(taskId)}", Auth, ct).ConfigureAwait(false);
        var output = state["output"];
        var status = Http.Str(output?["task_status"]);
        if (status is "FAILED" or "CANCELED" or "UNKNOWN") return new(VideoTaskStatus.Failed, Error: Http.Message(output) ?? (status == "UNKNOWN" ? "任务不存在或已超过 24 小时" : "视频生成失败"));
        if (status != "SUCCEEDED") return new(VideoTaskStatus.Pending);
        if (Http.Str(output?["video_url"]) is not { } url) return new(VideoTaskStatus.Failed, Error: "未返回可播放的视频");
        var (bytes, mime) = await Http.DownloadAsync(url, null, ct).ConfigureAwait(false);
        // The result URL lasts 24 hours and can feed a later edit or continuation without uploading again.
        return new(VideoTaskStatus.Completed, bytes, mime.StartsWith("video/") ? mime : "video/mp4") { RemoteUrl = url, RemoteUntil = DateTime.UtcNow.AddHours(23) };
    }

    /// <summary>DashScope can only cancel tasks still queued; running ones finish and are simply not collected.</summary>
    public override async Task CancelTaskAsync(string taskId)
    {
        using var _ = await Http.SendAsync(HttpMethod.Post, $"{Base}/tasks/{Uri.EscapeDataString(taskId)}/cancel", Auth, null, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Plain text models use text-generation; VL/Audio/Omni and the qwen3.x line use multimodal-generation with content arrays.</summary>
    public override async Task<string> GenerateTextAsync(TextRequest request, Action<string>? onDelta, CancellationToken ct)
    {
        var multimodal = request.Images.Count > 0 || ModelFamilies.QwenMultimodal(request.Model);
        JsonNode Content(string text, IEnumerable<MediaInput> images)
        {
            if (!multimodal) return text;
            var parts = new JsonArray();
            foreach (var image in images) parts.Add(new JsonObject { ["image"] = image.DataUrl });
            parts.Add(new JsonObject { ["text"] = text });
            return parts;
        }
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.System)) messages.Add(new JsonObject { ["role"] = "system", ["content"] = Content(request.System, []) });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = Content(request.Prompt, request.Images) });
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["input"] = new JsonObject { ["messages"] = messages },
            ["parameters"] = new JsonObject { ["result_format"] = "message", ["incremental_output"] = true },
        };
        var url = multimodal ? Multimodal : $"{Base}/services/aigc/text-generation/generation";
        using var response = await Http.PostStreamAsync(url, headers => { Auth(headers); headers.Add("X-DashScope-SSE", "enable"); }, Http.Content(body), ct).ConfigureAwait(false);
        var text = new StringBuilder();
        await foreach (var item in Http.ReadSse(response, ct).ConfigureAwait(false))
        {
            if (item.Data == "[DONE]") break;
            var data = JsonNode.Parse(item.Data)!;
            var message = data["output"]?["choices"]?[0]?["message"];
            if (message is null && Http.Str(data["code"]) is { Length: > 0 }) throw new ApiException(Http.Message(data) ?? "文本生成失败");
            var delta = message?["content"] switch
            {
                JsonValue value => Http.Str(value),
                JsonArray parts => string.Concat(parts.Select(part => Http.Str(part?["text"]))),
                _ => null,
            };
            if (string.IsNullOrEmpty(delta)) continue;
            text.Append(delta);
            onDelta?.Invoke(text.ToString());
        }
        return text.ToString();
    }
}
