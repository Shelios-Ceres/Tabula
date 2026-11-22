using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Tabula.Core.Models;
using Tabula.Core.Settings;

namespace Tabula.Providers;

/// <summary>MiniMax H3 Image: text-to-image (<c>/images/generations</c>) and picture-guided edits (<c>/images/edits</c>), both synchronous, answered with short-lived URLs.</summary>
public sealed class MiniMaxProvider(ModelChannel channel, HttpClient http) : AiProvider(channel, http)
{
    private Action<HttpRequestHeaders> Auth => Http.Bearer(Key);

    public override async Task<ImageResult> GenerateImageAsync(ImageRequest request, IProgress<ImageEvent>? progress, CancellationToken ct)
    {
        var options = request.Options;
        var family = ModelFamilies.FindImage(Vendor.MiniMax, request.Model) ?? throw new ApiException($"MiniMax 暂不支持型号 {request.Model}，请使用 MiniMax-H3-Image 系列");
        if (request.Mask is not null || request.Markup is not null) throw new ApiException("MiniMax 图片接口不支持蒙版或标注编辑");
        if (request.Count < 1 || request.Count > family.MaxImages) throw new ApiException($"{request.Model} 单次最多生成 {family.MaxImages} 张");

        // The auto tier sends neither area nor ratio (the model decides, 4K by default); a concrete tier sends area, and a concrete ratio sends aspect_ratio.
        var tier = string.IsNullOrEmpty(options.Tier) ? family.Tiers[0] : options.Tier;
        var area = tier is "1K" or "2K" or "4K" && family.Tiers.Contains(tier) ? tier : null;
        var ratio = area is not null && options.Ratio is { } r && r != "auto" && family.Ratios.Contains(r) ? r : null;
        var edit = request.References.Count > 0;
        // Edits always use prompt enhancement (the edits endpoint has no switch for it), so only text-to-image can turn it off, and then it needs a fixed ratio.
        var enhanced = edit || options.PromptExtend;
        if (!enhanced && ratio is null) throw new ApiException("关闭提示词增强时必须指定分辨率档位和比例，不能选「自动」");

        var format = family.OutputFormats.Contains(options.OutputFormat) ? options.OutputFormat : family.OutputFormats[0];
        var body = new JsonObject { ["model"] = request.Model, ["prompt"] = request.Prompt, ["n"] = request.Count, ["output_format"] = format, ["response_format"] = "url" }
            .Set("area", area)
            .Set("aspect_ratio", ratio)
            .Set("disable_prompt_enhanced", enhanced ? null : true)
            // References travel as data URIs in the JSON body, in order: the prompt addresses them as picture 1, 2...
            .Set("images", edit ? new JsonArray(request.References.Select(r => (JsonNode)new JsonObject { ["image_url"] = r.DataUrl }).ToArray()) : null);
        var payload = await Http.PostJsonAsync($"{Base}/images/{(edit ? "edits" : "generations")}", Auth, body, ct).ConfigureAwait(false);

        // Only successful pictures are billed; input is the text and reference tokens, output the image tokens (priced apart, as for Gemini).
        if (payload["usage"] is JsonObject usage)
            Count((long)Http.Decimal(usage["input_tokens"]), 0, (long)Http.Decimal(usage["output_tokens_details"]?["image_tokens"] ?? usage["output_tokens"]));

        // A 200 can mix pictures and failures: the pictures fill the first nodes and the failures' own messages the rest.
        var mime = $"image/{Http.Str(payload["output_format"]) ?? format}";
        var images = new List<GeneratedImage>();
        var errors = new List<string>();
        foreach (var item in payload["data"].Items())
        {
            if (item["error"] is JsonNode error) errors.Add(Failure(error));
            else if (Http.Str(item["url"]) is { } url)
            {
                try
                {
                    // The link expires after 24 hours, so every picture is fetched right away; one that cannot be fetched fails only its own node.
                    var (bytes, _) = await Http.DownloadAsync(url, null, ct).ConfigureAwait(false);
                    images.Add(new(bytes, mime));
                    progress?.Report(new ImageEvent(Index: images.Count - 1, Output: images[^1]));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add($"图片已生成但下载失败：{ex.Message}"); }
            }
        }
        return images.Count > 0 || errors.Count > 0 ? new(images, [], errors) : throw new ApiException("MiniMax 响应未返回图片");
    }

    /// <summary>A failed item: its message ends with the internal code, and the code alone is kept when there is no message.</summary>
    private static string Failure(JsonNode error) => Http.Message(error) is { Length: > 0 } message ? message : error["code"]?.ToString() is { Length: > 0 } code ? $"单张图片生成失败（{code}）" : "单张图片生成失败";

    public override Task<Balance> BalanceAsync(CancellationToken ct) => throw new NotSupportedException("MiniMax 没有查询余额的接口，请在 MiniMax 开放平台的账户管理里查看");
}
