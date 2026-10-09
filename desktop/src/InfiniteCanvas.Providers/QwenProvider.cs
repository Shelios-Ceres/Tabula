using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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
        // Every event carries the usage so far, so the last one is the request's total.
        JsonNode? usage = null;
        await foreach (var item in Http.ReadSse(response, ct).ConfigureAwait(false))
        {
            if (item.Data == "[DONE]") break;
            var data = JsonNode.Parse(item.Data)!;
            usage = data["usage"] ?? usage;
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
        Count(usage);
        return text.ToString();
    }

    /// <summary>
    /// Alibaba Cloud account balance (BSS OpenAPI QueryAccountBalance), signed with an AccessKey pair (ACS3-HMAC-SHA256); Model Studio free quotas
    /// have no public API. Accounts on the international site (dashscope-intl or a Singapore workspace) use the Singapore endpoint.
    /// </summary>
    public override async Task<Balance> BalanceAsync(CancellationToken ct)
    {
        var host = Base.Contains("-intl.") || Base.Contains(".ap-southeast-") ? "business.ap-southeast-1.aliyuncs.com" : "business.aliyuncs.com";
        var hash = Convert.ToHexStringLower(SHA256.HashData([]));
        var signedHeaders = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host, ["x-acs-action"] = "QueryAccountBalance", ["x-acs-version"] = "2017-12-14", ["x-acs-content-sha256"] = hash,
            ["x-acs-date"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), ["x-acs-signature-nonce"] = Guid.NewGuid().ToString("N"),
        };
        var names = string.Join(";", signedHeaders.Keys);
        var canonical = $"GET\n/\n\n{string.Concat(signedHeaders.Select(h => $"{h.Key}:{h.Value}\n"))}\n{names}\n{hash}";
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Channel.BillingKey), Encoding.UTF8.GetBytes($"ACS3-HMAC-SHA256\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}")));
        var payload = await Http.QueryJsonAsync($"https://{host}/", headers =>
        {
            foreach (var (name, value) in signedHeaders.Where(h => h.Key != "host")) headers.Add(name, value);
            headers.TryAddWithoutValidation("Authorization", $"ACS3-HMAC-SHA256 Credential={Channel.BillingId?.Trim()},SignedHeaders={names},Signature={signature}");
        }, ct).ConfigureAwait(false);
        if (payload["Success"] is JsonValue success && success.TryGetValue<bool>(out var ok) && !ok) throw new ApiException(Http.Message(payload) ?? "查询余额失败");
        var data = payload["Data"];
        return new("可用额度", Http.Decimal(data?["AvailableAmount"]), Http.Str(data?["Currency"]) ?? "CNY");
    }
}
