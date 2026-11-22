using System.Globalization;
using Tabula.Core.Canvas;
using Tabula.Core.Settings;
using Tabula.Core.Storage;

namespace Tabula.Core.Models;

/// <summary>Tokens a vendor reported: input, text output (thinking included) and media output (image / audio tokens, which Gemini prices separately).</summary>
public readonly record struct TokenUsage(long Input, long Output, long Media = 0)
{
    public long Total => Input + Output + Media;
    public static TokenUsage operator +(TokenUsage a, TokenUsage b) => new(a.Input + b.Input, a.Output + b.Output, a.Media + b.Media);
    public TokenUsage Per(int count) => new(Input / count, Output / count, Media / count);
}

/// <summary>Per 1M tokens in the channel's billing currency (USD, or CNY for mainland MiniMax): input, text output (thinking included), media output (image / audio tokens).</summary>
public sealed record TokenPrice(decimal Input, decimal Output, decimal Media);

/// <summary>What an image run is expected to cost before it starts: the request total in the vendor's billing currency, and how it was worked out.</summary>
public sealed record ImageEstimate(decimal Amount, string Currency, string Basis)
{
    public string Text => $"{Prices.Money(Amount, Currency)}（{Basis}）";
}

/// <summary>
/// Local cost estimates. Token prices: MiniMax's H3 Image pages, the Gemini API paid tier (ai.google.dev/gemini-api/docs/pricing, standard rates through 2026-12-31)
/// and OpenAI's GPT Image rates, matched by model prefix, longest first; each model's price can be overridden in settings.
/// Free tier, batch, cached input and the >200k-token tier are not modelled.
/// </summary>
public static class Prices
{
    private static readonly (string Prefix, TokenPrice Price)[] Gemini =
    [
        ("gemini-3.8-flash", new(0.75m, 3.75m, 0)),
        ("gemini-3.7-flash", new(0.75m, 3.75m, 0)),
        ("gemini-3.6-flash", new(0.75m, 3.75m, 0)),
        ("gemini-3.5-flash", new(1.50m, 9.00m, 0)),
        ("gemini-3.5-flash-lite", new(0.30m, 2.50m, 0)),
        ("gemini-3.1-flash-lite", new(0.25m, 1.50m, 0)),
        ("gemini-3-flash", new(0.50m, 3.00m, 0)),
        ("gemini-3.1-pro", new(2.00m, 12.00m, 0)),
        ("gemini-2.5-pro", new(1.25m, 10.00m, 0)),
        ("gemini-2.5-flash", new(0.30m, 2.50m, 0)),
        ("gemini-2.5-flash-image", new(0.30m, 2.50m, 30.00m)),
        ("gemini-2.5-flash-lite", new(0.10m, 0.40m, 0)),
        ("gemini-nano-banana-2.1", new(1.50m, 7.50m, 30.00m)),
        ("gemini-3.1-flash-image", new(0.50m, 3.00m, 60.00m)),
        ("gemini-3.1-flash-lite-image", new(0.25m, 1.50m, 30.00m)),
        ("gemini-3-pro-image", new(2.00m, 12.00m, 120.00m)),
        ("gemini-3.8-flash-tts", new(0.50m, 0, 9.00m)),
        ("gemini-3.8-flash-lite-tts", new(0.50m, 0, 6.00m)),
        ("gemini-3.1-flash-tts", new(1.00m, 0, 20.00m)),
        ("gemini-2.5-flash-preview-tts", new(0.50m, 0, 10.00m)),
        ("gemini-2.5-pro-preview-tts", new(1.00m, 0, 20.00m)),
    ];

    // GPT Image: text input, then image output (the Image API's usage.output_tokens). Image input ($8 / 1M at most) is billed as text input here, so edits come out a little low.
    private static readonly (string Prefix, TokenPrice Price)[] OpenAIImage =
    [
        ("gpt-image-2.6", new(5, 30, 0)),
        ("gpt-image-2", new(5, 30, 0)),
        ("gpt-image-1.5", new(5, 32, 0)),
        ("gpt-image-1-mini", new(2, 8, 0)),
        ("gpt-image-1", new(5, 40, 0)),
    ];

    // OpenAI's published per-image output prices (USD) for low / medium / high at 1024x1024, 1024x1536, 1536x1024; GPT Image 2.5 and 2.6 have no published table.
    private static readonly string[] OpenAISizes = ["1024x1024", "1024x1536", "1536x1024"];
    private static readonly string[] OpenAIQualities = ["low", "medium", "high"];
    private static readonly (string Prefix, decimal[][] Table)[] OpenAIPerImage =
    [
        ("gpt-image-2.6", []),
        ("gpt-image-2.5", []),
        ("gpt-image-2", [[0.006m, 0.005m, 0.005m], [0.053m, 0.041m, 0.041m], [0.211m, 0.165m, 0.165m]]),
        ("gpt-image-1.5", [[0.009m, 0.013m, 0.013m], [0.034m, 0.05m, 0.05m], [0.133m, 0.2m, 0.2m]]),
        ("gpt-image-1-mini", [[0.005m, 0.006m, 0.006m], [0.011m, 0.015m, 0.015m], [0.036m, 0.052m, 0.052m]]),
        ("gpt-image-1", [[0.011m, 0.016m, 0.016m], [0.042m, 0.063m, 0.063m], [0.167m, 0.25m, 0.25m]]),
    ];

    // Gemini image output tokens per picture by tier, from the pricing page; an input picture counts 560 tokens (documented for Gemini 3 Pro Image).
    private static readonly Dictionary<string, Dictionary<string, int>> GeminiImageTokens = new()
    {
        ["gemini-nano-banana-2.1"] = new() { ["1K"] = 1120, ["2K"] = 1680, ["4K"] = 3780 },
        ["gemini-3.1-flash-image"] = new() { ["512"] = 747, ["1K"] = 1120, ["2K"] = 1680, ["4K"] = 2520 },
        ["gemini-3.1-flash-lite-image"] = new() { ["1K"] = 1120 },
        ["gemini-3-pro-image"] = new() { ["1K"] = 1120, ["2K"] = 1120, ["4K"] = 2000 },
        ["gemini-2.5-flash-image"] = new() { ["1K"] = 1290 },
    };
    private const int GeminiInputImageTokens = 560;

    // MiniMax H3 Image, per 1M tokens: text input, no text output, image output (platform.minimax.io/docs/pricing/overview and platform.minimax.cn/docs/pricing/overview).
    // Reference-picture input tokens are billed at their own rate (standard 1.50 / ¥10, Highspeed 3.00 / ¥20) but counted as input here, so edits come out a little off.
    // The limited 4K half-price offer is not modelled.
    private static readonly (string Prefix, TokenPrice Price)[] MiniMaxUsd =
    [
        ("minimax-h3-image-highspeed", new(2.30m, 0, 7.60m)),
        ("minimax-h3-image", new(2.30m, 0, 3.80m)),
    ];

    private static readonly (string Prefix, TokenPrice Price)[] MiniMaxCny =
    [
        ("minimax-h3-image-highspeed", new(15, 0, 50)),
        ("minimax-h3-image", new(15, 0, 25)),
    ];

    // MiniMax's per-picture reference costs (the "参考任务总费用" tables on the same pricing pages), regular prices: the 4K half-price offer is not modelled.
    // Rows per tier 1K / 2K / 4K: text-to-image, edit with 1 reference, edit with 3 references (1K references). Actual cost follows token use.
    private static readonly string[] MiniMaxTiers = ["1K", "2K", "4K"];
    private static readonly (string Prefix, decimal[][] Rows)[] MiniMaxUsdPerImage =
    [
        ("minimax-h3-image-highspeed", [[0.03529m, 0.03622m, 0.13001m], [0.04525m, 0.04576m, 0.13673m], [0.06037m, 0.06069m, 0.15407m]]),
        ("minimax-h3-image", [[0.02179m, 0.02333m, 0.06879m], [0.02654m, 0.02658m, 0.07312m], [0.03563m, 0.04177m, 0.07914m]]),
    ];
    private static readonly (string Prefix, decimal[][] Rows)[] MiniMaxCnyPerImage =
    [
        ("minimax-h3-image-highspeed", [[0.2319m, 0.2380m, 0.8550m], [0.2978m, 0.3011m, 0.8998m], [0.3982m, 0.4003m, 1.0146m]]),
        ("minimax-h3-image", [[0.1430m, 0.1531m, 0.4522m], [0.1744m, 0.1747m, 0.4809m], [0.2346m, 0.2746m, 0.5211m]]),
    ];

    private static T? Longest<T>(IEnumerable<(string Prefix, T Value)> table, string name) =>
        table.Where(p => name.StartsWith(p.Prefix, StringComparison.OrdinalIgnoreCase)).OrderByDescending(p => p.Prefix.Length).Select(p => p.Value).FirstOrDefault();

    public static TokenPrice? Default(Vendor vendor, string model) => vendor switch
    {
        Vendor.Google => Longest(Gemini, ModelFamilies.Normalize(model)),
        Vendor.OpenAI => Longest(OpenAIImage, ModelFamilies.Normalize(model)),
        _ => null,
    };

    /// <summary>The built-in price for a channel: MiniMax follows the site it talks to, the other vendors only their vendor.</summary>
    public static TokenPrice? Default(ModelChannel channel, string model) =>
        channel.Vendor == Vendor.MiniMax ? Longest(channel.IsInternationalSite ? MiniMaxUsd : MiniMaxCny, ModelFamilies.Normalize(model)) : Default(channel.Vendor, model);

    /// <summary>The user's override, else the built-in price.</summary>
    public static TokenPrice? Of(ModelChannel channel, string model) => channel.Models.FirstOrDefault(m => m.Name == model)?.Price ?? Default(channel, model);

    public static decimal Cost(TokenUsage tokens, TokenPrice price) => (tokens.Input * price.Input + tokens.Output * price.Output + tokens.Media * price.Media) / 1_000_000m;

    /// <summary>Estimated USD for a channel's ledger from a day on; models without a price are left out.</summary>
    public static decimal Estimate(ModelChannel channel, DateOnly from) =>
        UsageStore.Tokens(channel.Id, from).Sum(pair => Of(channel, pair.Key) is { } price ? Cost(pair.Value, price) : 0);

    public static string Money(decimal amount, string currency) =>
        (currency switch { "CNY" => "¥", "USD" => "$", _ => currency + " " }) + amount.ToString(amount >= 1 ? "N2" : "0.00##", CultureInfo.InvariantCulture);

    /// <summary>Seedream 5.5 layer split: the node's picture limit (base included), the API default 30 when unset or out of range.</summary>
    public static int LayerMax(CanvasNode node) => int.TryParse(node.Option(NodeKeys.LayerMax), out var max) && max is >= NodeKeys.LayerMin and <= NodeKeys.LayerMaxLimit ? max : NodeKeys.LayerMaxDefault;

    /// <summary>Settings that decide an image's cost, so a run can be compared with the last one made the same way.</summary>
    public static string ImageKey(string model, CanvasNode node) => $"{ModelFamilies.Normalize(model)}|{node.Size}|{node.Ratio}|{node.Quality}";

    // Per-picture list prices for vendors billing by the picture: Ark (Volcengine official page), Qwen (Beijing region list price), xAI.
    private static decimal? PerImage(Vendor vendor, ImageFamily? family, string name, string? tier) => vendor switch
    {
        // Seedream 5.5 bills by output pixels: up to 2.61 MP ¥0.13, up to 4.63 MP ¥0.23. Every 1K / 1.5K size is under 2.61 MP and every 2K size over it.
        Vendor.Ark => family?.Id switch { "seedream-5.5" => tier == "2K" ? 0.23m : 0.13m, "seedream-5-pro" => tier == "2K" ? 0.60m : 0.30m, "seedream-5-flash" => 0.12m, "seedream-5-lite" => 0.22m, "seedream-4.5" => 0.25m, "seedream-4.0" => 0.20m, _ => null },
        Vendor.Qwen => name.StartsWith("qwen-image-3.0-pro") ? tier == "2K" ? 0.50m : 0.25m : name.StartsWith("qwen-image-3.0") ? 0.18m : name.StartsWith("qwen-image-2.1-pro") ? 0.25m : null,
        Vendor.XAI => name.StartsWith("grok-imagine-image-quality") ? 0.05m : name.StartsWith("grok-imagine-image-2") ? 0.04m : name.StartsWith("grok-imagine-image") ? 0.02m : null,
        _ => null,
    };

    /// <summary>
    /// Expected cost of an image run from a node before it starts, from the vendor's published prices: per picture (Ark, Qwen, xAI),
    /// per-tier image tokens (Gemini) or the per-size table (OpenAI, 1024 sizes only); otherwise this client's last run of the same model
    /// and settings, and for MiniMax the pages' per-picture reference costs when there is no such run. Null when nothing is known yet. Thinking tokens and OpenAI reference-image input are not included.
    /// </summary>
    public static ImageEstimate? EstimateImage(ModelChannel channel, string model, CanvasNode node, int references)
    {
        var vendor = channel.Vendor;
        var family = ModelFamilies.FindImage(vendor, model);
        var name = ModelFamilies.Normalize(model);
        var tier = family is not null && family.Tiers.Contains(node.Size) ? node.Size : family?.Tiers.FirstOrDefault();
        var count = Math.Clamp(node.Count, 1, 15);
        // Same batching as the generator: inputs are paid once per request.
        var perRequest = vendor == Vendor.OpenAI && (ModelFamilies.OpenAIApi(node, family) == "responses" || node.Option("outputFormat") == "svg") ? 1 : Math.Max(1, (family?.MaxImages ?? 1) - (family?.Sequential == true ? references : 0));
        var requests = (count + perRequest - 1) / perRequest;
        var prompt = (long)Math.Ceiling((node.Prompt ?? "").Sum(c => c >= 0x2E80 ? 1m : 0.25m));
        var price = Of(channel, model);

        if (PerImage(vendor, family, name, tier) is { } each)
        {
            // Ark Seedream 5.0 pro charges ¥0.02 per reference after the first, Seedream 5.5 ¥0.05 for every input picture; Qwen 3.0 ¥0.02 per reference.
            var perReference = family?.Id == "seedream-5-pro" ? Math.Max(0, references - 1) * 0.02m : family?.Id == "seedream-5.5" ? references * 0.05m : name.StartsWith("qwen-image-3.0") ? references * 0.02m : 0;
            var currency = vendor == Vendor.XAI ? "USD" : "CNY";
            // A 5.5 layer split is one request of at most max_images pictures (base included): the base is billed at the chosen size, each layer by its own size band,
            // and the layers' sizes are unknown here, so they are counted at the lower band; the bill follows the pictures actually returned.
            var split = family?.Id == "seedream-5.5" && node.Option("layers") == "true";
            var layers = split ? LayerMax(node) - 1 : 0;
            if (split) (count, requests) = (1, 1);
            var basis = $"{count} 张 × {Money(each, currency)}" + (layers > 0 ? $" + 最多 {layers} 个图层 × {Money(0.13m, currency)}（实际按返回的张数，各图层按自己的像素档）" : "") + (perReference > 0 ? $" + 参考图 {Money(perReference * requests, currency)}" : "")
                + (name.StartsWith("qwen-image-3.0-pro") && tier is "auto" ? "，自动尺寸按 1K 估" : "");
            return new(count * each + layers * 0.13m + perReference * requests, currency, basis);
        }
        if (vendor == Vendor.Google && family is not null && GeminiImageTokens.TryGetValue(family.Id, out var tiers) && tier is not null && tiers.TryGetValue(tier, out var media) && price is not null)
        {
            var tokens = new TokenUsage(requests * (prompt + references * GeminiInputImageTokens), 0, (long)count * media);
            return new(Cost(tokens, price), "USD", $"{count} 张 × {media} 图片 token，不含思考");
        }
        var (q, s) = (Array.IndexOf(OpenAIQualities, node.Quality), family is null ? -1 : Array.IndexOf(OpenAISizes, ModelFamilies.ImageSize(family, tier, node.Ratio)));
        if (vendor == Vendor.OpenAI && q >= 0 && s >= 0 && Longest(OpenAIPerImage, name) is { Length: > 0 } table)
        {
            var image = table[q][s];
            // Streaming previews add 100 image tokens each per picture.
            var partial = node.Option("stream") == "true" && int.TryParse(node.Option("partialImages"), out var p) ? p * 100L * count : 0;
            var extra = price is null ? 0 : Cost(new TokenUsage(requests * prompt, partial), price);
            return new(count * image + extra, "USD", $"{count} 张 × {Money(image, "USD")}，官方单价");
        }
        if (price is not null && UsageStore.LastImage(ImageKey(model, node)) is { } last)
            return new(Cost(last, price) * count, UsageReport.CurrencyOf(channel), $"{count} 张，按上次同样设置的实际用量");
        if (vendor == Vendor.MiniMax && Longest(channel.IsInternationalSite ? MiniMaxUsdPerImage : MiniMaxCnyPerImage, name) is { } rows)
        {
            // Auto tier renders at MiniMax's default 4K; 2 references use the 3-reference row, more than 3 as well.
            var column = Math.Max(0, Array.IndexOf(MiniMaxTiers, tier is "auto" or null ? "4K" : tier));
            var row = Math.Min(references, 3) switch { 0 => 0, 1 => 1, _ => 2 };
            var unit = rows[row][column];
            var currency = UsageReport.CurrencyOf(channel);
            var shown = tier is "auto" or null ? "自动按 4K" : MiniMaxTiers[column];
            var kind = row == 0 ? "文生图" : row == 1 ? "改图 1 张参考图" : references == 3 ? "改图 3 张参考图" : $"改图 {references} 张参考图按 3 张参考图档";
            return new(count * unit, currency, $"{count} 张 × {Money(unit, currency)}，MiniMax 官方参考价（{shown} {kind}），实际按 token 计费");
        }
        return null;
    }
}
