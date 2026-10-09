using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using InfiniteCanvas.Core.Models;
using InfiniteCanvas.Core.Settings;

namespace InfiniteCanvas.Providers;

/// <summary>xAI: Grok Imagine images (aspect_ratio + resolution), Grok Imagine video tasks and the Responses API.</summary>
public sealed class XaiProvider(ModelChannel channel, HttpClient http) : AiProvider(channel, http)
{
    private Action<HttpRequestHeaders> Auth => Http.Bearer(Key);

    private static JsonObject ImageUrl(MediaInput image) => new() { ["url"] = image.DataUrl, ["type"] = "image_url" };

    public override async Task<ImageResult> GenerateImageAsync(ImageRequest request, IProgress<ImageEvent>? progress, CancellationToken ct)
    {
        var options = request.Options;
        var family = ModelFamilies.FindImage(Vendor.XAI, request.Model) ?? throw new ApiException($"xAI 暂不支持型号 {request.Model}");
        if (request.Mask is not null) throw new ApiException("xAI 图片接口不支持蒙版编辑");
        if (request.References.Count > family.MaxReferences) throw new ApiException($"{request.Model} 最多支持 {family.MaxReferences} 张参考图");
        if (request.Count < 1 || request.Count > family.MaxImages) throw new ApiException($"{request.Model} 单次最多生成 {family.MaxImages} 张");

        var body = new JsonObject { ["model"] = request.Model, ["prompt"] = request.Prompt, ["n"] = request.Count, ["response_format"] = "b64_json" }
            .Set("resolution", family.Tiers.Contains(options.Tier) ? options.Tier : null)
            .Set("aspect_ratio", options.Ratio is { } ratio && ratio != "auto" && family.Ratios.Contains(ratio) ? ratio : null)
            .Set("quality", options.Quality is { } quality && quality != "auto" && family.Qualities.Contains(quality) ? quality : null)
            // image and images are mutually exclusive; multiple references are addressed as <IMAGE_0>, <IMAGE_1>...
            .Set("image", request.References.Count == 1 ? ImageUrl(request.References[0]) : null)
            .Set("images", request.References.Count > 1 ? new JsonArray(request.References.Select(r => (JsonNode)ImageUrl(r)).ToArray()) : null);
        var edit = request.References.Count > 0;
        var payload = await Http.PostJsonAsync($"{Base}/images/{(edit ? "edits" : "generations")}", Auth, body, ct).ConfigureAwait(false);
        var images = new List<GeneratedImage>();
        foreach (var item in payload["data"].Items())
        {
            var mime = Http.Str(item["mime_type"]) ?? "image/jpeg";
            if (Http.Str(item["b64_json"]) is { } b64) images.Add(new(Convert.FromBase64String(b64), mime));
            else if (Http.Str(item["url"]) is { } url)
            {
                var (bytes, type) = await Http.DownloadAsync(url, null, ct).ConfigureAwait(false);
                images.Add(new(bytes, type));
            }
            else continue;
            progress?.Report(new ImageEvent(Index: images.Count - 1, Output: images[^1]));
        }
        return images.Count > 0 ? new(images, [], []) : throw new ApiException("xAI 响应未返回图片");
    }

    public override async Task<string> CreateVideoTaskAsync(VideoRequest request, CancellationToken ct)
    {
        var family = ModelFamilies.FindVideo(Vendor.XAI, request.Model);
        // Edit keeps the source's length, frame and resolution (capped at 720p); extend only takes the length of the added part (2–10 s).
        if (request.Task is "edit" or "extend")
        {
            var source = request.Videos.FirstOrDefault() ?? throw new ApiException("编辑或延长视频需要输入一个视频");
            var edit = new JsonObject { ["model"] = request.Model, ["prompt"] = request.Prompt, ["video"] = new JsonObject { ["url"] = source.DataUrl } }
                .Set("duration", request.Task == "extend" ? Math.Clamp(request.Seconds, 2, 10) : null);
            var job = await Http.PostJsonAsync($"{Base}/videos/{(request.Task == "edit" ? "edits" : "extensions")}", Auth, edit, ct).ConfigureAwait(false);
            return Http.Str(job["request_id"]) ?? throw new ApiException("接口未返回视频任务 ID");
        }
        var reference = request.Mode == VideoMode.Reference && request.Images.Count > 0;
        var voices = request.Voices.Take(family?.MaxVoices ?? 0).ToList();
        var keyframes = request.Keyframes.Take(family?.MaxKeyframes ?? 0).ToList();
        // Pinned keyframes or voices make it reference-to-video too.
        var capped = reference || voices.Count > 0 || keyframes.Count > 0;
        var body = new JsonObject
        {
            ["model"] = request.Model, ["prompt"] = request.Prompt, ["duration"] = request.Seconds,
            ["aspect_ratio"] = request.Ratio is "auto" or "adaptive" ? "16:9" : request.Ratio,
            // Reference-to-video is capped at 720p; 1080p only applies to text- and image-to-video.
            ["resolution"] = capped && request.Resolution == "1080p" ? "720p" : request.Resolution,
        }
        .Set("generate_audio", family?.Audio == true ? request.GenerateAudio : null)
        .Set("image", !reference && request.Images.Count > 0 ? new JsonObject { ["url"] = request.Images[0].DataUrl } : null)
        .Set("last_frame", !reference && request.Images.Count > 1 ? new JsonObject { ["url"] = request.Images[^1].DataUrl } : null)
        .Set("reference_images", reference ? new JsonArray(request.Images.Take(family?.MaxReferences ?? 7).Select(i => (JsonNode)new JsonObject { ["url"] = i.DataUrl }).ToArray()) : null)
        // Voices are referenced in the prompt as <AUDIO_0>, <AUDIO_1>...
        .Set("reference_audios", voices.Count > 0 ? new JsonArray(voices.Select(v => (JsonNode)new JsonObject { ["voice_id"] = v }).ToArray()) : null)
        // Keyframes sit strictly inside the clip on a 1/3-second grid.
        .Set("keyframes", keyframes.Count > 0 ? new JsonArray(keyframes.Select(k => (JsonNode)new JsonObject
        {
            ["image"] = new JsonObject { ["url"] = k.Image.DataUrl },
            ["timestamp_s"] = Math.Clamp(Math.Round(k.Seconds * 3), 1, request.Seconds * 3 - 1) / 3,
        }).ToArray()) : null);
        var created = await Http.PostJsonAsync($"{Base}/videos/generations", Auth, body, ct).ConfigureAwait(false);
        return Http.Str(created["request_id"]) ?? throw new ApiException("接口未返回视频任务 ID");
    }

    public override async Task<VideoTaskState> PollVideoTaskAsync(string taskId, CancellationToken ct)
    {
        var state = await Http.QueryJsonAsync($"{Base}/videos/{Uri.EscapeDataString(taskId)}", Auth, ct).ConfigureAwait(false);
        var status = Http.Str(state["status"]);
        if (status is "failed" or "expired") return new(VideoTaskStatus.Failed, Error: Http.Message(state["error"]) ?? "视频生成失败");
        if (status != "done") return new(VideoTaskStatus.Pending, Progress: state["progress"]?.GetValue<int>() ?? -1);
        if (Http.Str(state["video"]?["url"]) is not { } url) return new(VideoTaskStatus.Failed, Error: "未返回可播放的视频");
        var (bytes, mime) = await Http.DownloadAsync(url, null, ct).ConfigureAwait(false);
        return new(VideoTaskStatus.Completed, bytes, mime.StartsWith("video/") ? mime : "video/mp4");
    }

    public override Task<string> GenerateTextAsync(TextRequest request, Action<string>? onDelta, CancellationToken ct) =>
        ResponsesApi.StreamTextAsync(Http, $"{Base}/responses", Auth, request with { Reasoning = null }, onDelta, ct);

    public override async Task<List<string>> ListModelsAsync(CancellationToken ct)
    {
        var payload = await Http.QueryJsonAsync($"{Base}/models", Auth, ct).ConfigureAwait(false);
        return payload["data"].Items().Select(item => Http.Str(item["id"])).OfType<string>().Order().ToList();
    }
}
