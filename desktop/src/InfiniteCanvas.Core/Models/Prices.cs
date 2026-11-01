using System.Globalization;
using InfiniteCanvas.Core.Canvas;
using InfiniteCanvas.Core.Settings;
using InfiniteCanvas.Core.Storage;

namespace InfiniteCanvas.Core.Models;

/// <summary>Tokens a vendor reported: input, text output (thinking included) and media output (image / audio tokens, which Gemini prices separately).</summary>
public readonly record struct TokenUsage(long Input, long Output, long Media = 0)
{
    public long Total => Input + Output + Media;
    public static TokenUsage operator +(TokenUsage a, TokenUsage b) => new(a.Input + b.Input, a.Output + b.Output, a.Media + b.Media);
    public TokenUsage Per(int count) => new(Input / count, Output / count, Media / count);
}

/// <summary>USD per 1M tokens: input, text output (thinking included), media output (image / audio tokens).</summary>
public sealed record TokenPrice(decimal Input, decimal Output, decimal Media);

/// <summary>What an image run is expected to cost before it starts: the request total in the vendor's billing currency, and how it was worked out.</summary>
public sealed record ImageEstimate(decimal Amount, string Currency, string Basis)
{
    public string Text => $"{Prices.Money(Amount, Currency)}（{Basis}）";
}

/// <summary>
/// Local cost estimates. Token prices: the Gemini API paid tier (ai.google.dev/gemini-api/docs/pricing, standard rates through 2026-12-31)
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
        ("gpt-image-2", new(5, 30, 0)),
        ("gpt-image-1.5", new(5, 32, 0)),
        ("gpt-image-1-mini", new(2, 8, 0)),
        ("gpt-image-1", new(5, 40, 0)),
    ];

    // OpenAI's published per-image output prices (USD) for low / medium / high at 1024x1024, 1024x1536, 1536x1024; GPT Image 2.5 has no published table.
    private static readonly string[] OpenAISizes = ["1024x1024", "1024x1536", "1536x1024"];
    private static readonly string[] OpenAIQualities = ["low", "medium", "high"];
    private static readonly (string Prefix, decimal[][] Table)[] OpenAIPerImage =
    [
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

    private static T? Longest<T>(IEnumerable<(string Prefix, T Value)> table, string name) =>
        table.Where(p => name.StartsWith(p.Prefix, StringComparison.OrdinalIgnoreCase)).OrderByDescending(p => p.Prefix.Length).Select(p => p.Value).FirstOrDefault();

    public static TokenPrice? Default(Vendor vendor, string model) => vendor switch
    {
        Vendor.Google => Longest(Gemini, ModelFamilies.Normalize(model)),
        Vendor.OpenAI => Longest(OpenAIImage, ModelFamilies.Normalize(model)),
        _ => null,
    };

    /// <summary>The user's override, else the built-in price.</summary>
    public static TokenPrice? Of(ModelChannel channel, string model) => channel.Models.FirstOrDefault(m => m.Name == model)?.Price ?? Default(channel.Vendor, model);

    public static decimal Cost(TokenUsage tokens, TokenPrice price) => (tokens.Input * price.Input + tokens.Output * price.Output + tokens.Media * price.Media) / 1_000_000m;

    /// <summary>Estimated USD for a channel's ledger from a day on; models without a price are left out.</summary>
    public static decimal Estimate(ModelChannel channel, DateOnly from) =>
        UsageStore.Tokens(channel.Id, from).Sum(pair => Of(channel, pair.Key) is { } price ? Cost(pair.Value, price) : 0);

    public static string Money(decimal amount, string currency) =>
        (currency switch { "CNY" => "¥", "USD" => "$", _ => currency + " " }) + amount.ToString(amount >= 1 ? "N2" : "0.00##", CultureInfo.InvariantCulture);

    /// <summary>Settings that decide an image's cost, so a run can be compared with the last one made the same way.</summary>
    public static string ImageKey(string model, CanvasNode node) => $"{ModelFamilies.Normalize(model)}|{node.Size}|{node.Ratio}|{node.Quality}";

    // Per-picture list prices for vendors billing by the picture: Ark (Volcengine official page), Qwen (Beijing region list price), xAI.
    private static decimal? PerImage(Vendor vendor, ImageFamily? family, string name, string? tier) => vendor switch
    {
        Vendor.Ark => family?.Id switch { "seedream-5-pro" => tier == "2K" ? 0.60m : 0.30m, "seedream-5-flash" => 0.12m, "seedream-5-lite" => 0.22m, "seedream-4.5" => 0.25m, "seedream-4.0" => 0.20m, _ => null },
        Vendor.Qwen => name.StartsWith("qwen-image-3.0-pro") ? tier == "2K" ? 0.50m : 0.25m : name.StartsWith("qwen-image-3.0") ? 0.18m : name.StartsWith("qwen-image-2.1-pro") ? 0.25m : null,
        Vendor.XAI => name.StartsWith("grok-imagine-image-quality") ? 0.05m : name.StartsWith("grok-imagine-image-2") ? 0.04m : name.StartsWith("grok-imagine-image") ? 0.02m : null,
        _ => null,
    };

    /// <summary>
    /// Expected cost of an image run from a node before it starts, from the vendor's published prices: per picture (Ark, Qwen, xAI),
    /// per-tier image tokens (Gemini) or the per-size table (OpenAI, 1024 sizes only); otherwise this client's last run of the same model
    /// and settings. Null when nothing is known yet. Thinking tokens and OpenAI reference-image input are not included.
    /// </summary>
    public static ImageEstimate? EstimateImage(ModelChannel channel, string model, CanvasNode node, int references)
    {
        var vendor = channel.Vendor;
        var family = ModelFamilies.FindImage(vendor, model);
        var name = ModelFamilies.Normalize(model);
        var tier = family is not null && family.Tiers.Contains(node.Size) ? node.Size : family?.Tiers.FirstOrDefault();
        var count = Math.Clamp(node.Count, 1, 15);
        // Same batching as the generator: inputs are paid once per request.
        var perRequest = vendor == Vendor.OpenAI && node.Option("openaiApi", "images") == "responses" ? 1 : Math.Max(1, (family?.MaxImages ?? 1) - (family?.Sequential == true ? references : 0));
        var requests = (count + perRequest - 1) / perRequest;
        var prompt = (long)Math.Ceiling((node.Prompt ?? "").Sum(c => c >= 0x2E80 ? 1m : 0.25m));
        var price = Of(channel, model);

        if (PerImage(vendor, family, name, tier) is { } each)
        {
            // Ark Seedream 5.0 pro charges ¥0.02 per reference after the first; Qwen 3.0 ¥0.02 per reference.
            var perReference = family?.Id == "seedream-5-pro" ? Math.Max(0, references - 1) * 0.02m : name.StartsWith("qwen-image-3.0") ? references * 0.02m : 0;
            var currency = vendor == Vendor.XAI ? "USD" : "CNY";
            var basis = $"{count} 张 × {Money(each, currency)}" + (perReference > 0 ? $" + 参考图 {Money(perReference * requests, currency)}" : "")
                + (name.StartsWith("qwen-image-3.0-pro") && tier is "auto" ? "，自动尺寸按 1K 估" : "");
            return new(count * each + perReference * requests, currency, basis);
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
            return new(Cost(last, price) * count, "USD", $"{count} 张，按上次同样设置的实际用量");
        return null;
    }
}
