using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using InfiniteCanvas.Core.Models;
using InfiniteCanvas.Core.Settings;

namespace InfiniteCanvas.Providers;

/// <summary>xAI: Grok Imagine images (aspect_ratio + resolution) and the Responses API.</summary>
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

    public override Task<string> GenerateTextAsync(TextRequest request, Action<string>? onDelta, CancellationToken ct) =>
        ResponsesApi.StreamTextAsync(Http, $"{Base}/responses", Auth, request with { Reasoning = null }, onDelta, ct);

    public override async Task<List<string>> ListModelsAsync(CancellationToken ct)
    {
        var payload = await Http.QueryJsonAsync($"{Base}/models", Auth, ct).ConfigureAwait(false);
        return payload["data"].Items().Select(item => Http.Str(item["id"])).OfType<string>().Order().ToList();
    }
}
