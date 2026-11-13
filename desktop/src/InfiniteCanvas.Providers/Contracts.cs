using System.Text.Json.Nodes;
using InfiniteCanvas.Core.Settings;

namespace InfiniteCanvas.Providers;

public sealed record MediaInput(byte[] Bytes, string MimeType)
{
    public string Base64 => Convert.ToBase64String(Bytes);
    public string DataUrl => $"data:{MimeType.ToLowerInvariant()};base64,{Base64}";
}

/// <summary>Image settings resolved from the node; adapters read only what their vendor supports.</summary>
public sealed class ImageOptions
{
    public string? Tier { get; init; }
    public string? Ratio { get; init; }
    public string? Quality { get; init; }
    public string Background { get; init; } = "auto";
    public string OutputFormat { get; init; } = "png";
    public int Compression { get; init; } = 100;
    public string Moderation { get; init; } = "auto";
    public bool Stream { get; init; }
    /// <summary>OpenAI streaming previews (0–3); each partial image is billed as extra output tokens.</summary>
    public int PartialImages { get; init; }
    public string? Thinking { get; init; }
    public bool WebSearch { get; init; }
    public bool ImageSearch { get; init; }
    public bool Watermark { get; init; }
    public bool FastPrompt { get; init; }
    public string? NegativePrompt { get; init; }
    public bool PromptExtend { get; init; } = true;
    /// <summary>GPT Image 2.6: how much planning the model does (low / medium / high); null leaves the model default (high).</summary>
    public string? Complexity { get; init; }
    /// <summary>GPT Image 2.6 GIF output: frames (2–64) and milliseconds per frame (20–2000); null keeps the API defaults (9 frames, 200 ms).</summary>
    public int? FrameCount { get; init; }
    public int? FrameDurationMs { get; init; }
    /// <summary>OpenAI only: "images" (Image API) or "responses" (image_generation tool via a mainline model).</summary>
    public string OpenAIApi { get; init; } = "images";
    public string? MainModel { get; init; }
    /// <summary>Responses <c>reasoning.effort</c> for the mainline model; null leaves the field out (non-reasoning models).</summary>
    public string? MainReasoning { get; init; }
    /// <summary>OpenAI Responses / Gemini Interactions: keep the turn on the vendor's servers so the next edit can continue it.</summary>
    public bool Store { get; init; }
    /// <summary>Ark Seedream 5.0 pro / flash: split one image into a base plus transparent layers.</summary>
    public bool LayerDecomposition { get; init; }
    /// <summary>Ark Seedream 5.0 pro / flash: keep the alpha channel of a single transparent input.</summary>
    public bool Transparent { get; init; }
    /// <summary>Ark Seedream 5.5 layer split: <c>max_images</c> (2–100, base included); null leaves the API default (30).</summary>
    public int? LayerMax { get; init; }
    /// <summary>Ark Seedream 5.5 layer split: ask for text recognition on the text layers.</summary>
    public bool TextRecognition { get; init; }
    /// <summary>DashScope: "direct" or "agent" prompt rewriting (agent is text-to-image only).</summary>
    public string? PromptExtendMode { get; init; }
    /// <summary>DashScope: thinking while rewriting the prompt; null keeps the vendor default.</summary>
    public bool? EnableThinking { get; init; }
    public long? Seed { get; init; }
}

/// <param name="Selections">Ark interactive editing: points / boxes on the references, in 0–1 fractions.</param>
/// <param name="PreviousId">Continue a stored conversation (OpenAI response ID / Gemini interaction ID).</param>
/// <param name="Markup">GPT Image markup edits: a transparent annotation layer the size of the first reference.</param>
public sealed record ImageRequest(string Model, string Prompt, IReadOnlyList<MediaInput> References, MediaInput? Mask, int Count, ImageOptions Options, string? EndpointId = null,
    IReadOnlyList<Core.Canvas.ImageSelection>? Selections = null, string? PreviousId = null, MediaInput? Markup = null);

/// <summary>Layer metadata from Ark layer decomposition; Box is [left, top, right, bottom] in 0–1000 of the base image; TextRecognition is the raw <c>text_recognition</c> JSON of a text layer.</summary>
public sealed record ImageLayer(int ZIndex, string? Name, string? Description, double[]? Box, string? TextRecognition = null);

public sealed record GeneratedImage(byte[] Bytes, string MimeType, ImageLayer? Layer = null);

public sealed record ImageResult(List<GeneratedImage> Images, List<string> Texts, List<string> Errors, string? ConversationId = null);

/// <summary>Live progress: a preview frame, a finished image at an index, streamed text, or the ID of a background task the vendor accepted (kept so polling can resume later).</summary>
public sealed record ImageEvent(GeneratedImage? Preview = null, int Index = -1, GeneratedImage? Output = null, string? Text = null, string? TaskId = null);

public sealed record TextRequest(string Model, string Prompt, IReadOnlyList<MediaInput> Images, string? System = null, string? Reasoning = null);

public sealed record SpeechRequest(string Model, string Input, string Voice, string Format, double Speed, string? Instructions);

/// <summary>A money figure from the vendor's billing API: the balance left, or for OpenAI (no balance API) this month's spend.</summary>
public sealed record Balance(string Label, decimal Amount, string Currency)
{
    public string Text => $"{Label} {Currency switch { "CNY" => "¥", "USD" => "$", _ => Currency + " " }}{Amount:N2}";
}

public sealed class ApiException(string message, int status = 0, string? requestId = null) : Exception(message)
{
    public int Status { get; } = status;
    public string? RequestId { get; } = requestId;
}

/// <summary>One adapter per vendor, each speaking that vendor's own native API.</summary>
public abstract class AiProvider(ModelChannel channel, HttpClient http)
{
    protected ModelChannel Channel { get; } = channel;
    protected Http Http { get; } = new(http);
    protected string Base { get; } = channel.ApiBase();
    protected string Key { get; } = channel.ApiKey;

    protected string VendorName => ModelChannel.VendorName(Channel.Vendor);
    protected Exception Unsupported(string what) => new NotSupportedException($"{VendorName} 暂不支持{what}");

    private long _input, _output, _media;

    /// <summary>Tokens the vendor reported for all requests made through this instance (one per generation run, so parallel batch requests add up).</summary>
    public Core.Models.TokenUsage Tokens => new(Interlocked.Read(ref _input), Interlocked.Read(ref _output), Interlocked.Read(ref _media));

    protected void Count(long input, long output, long media = 0)
    {
        Interlocked.Add(ref _input, input);
        Interlocked.Add(ref _output, output);
        Interlocked.Add(ref _media, media);
    }

    /// <summary>Count an OpenAI-style usage object: input_tokens / output_tokens, or prompt_tokens / completion_tokens.</summary>
    protected void Count(JsonNode? usage)
    {
        if (usage is JsonObject)
            Count((long)Http.Decimal(usage["input_tokens"] ?? usage["prompt_tokens"]), (long)Http.Decimal(usage["output_tokens"] ?? usage["completion_tokens"]));
    }

    public virtual Task<ImageResult> GenerateImageAsync(ImageRequest request, IProgress<ImageEvent>? progress, CancellationToken ct) => throw Unsupported("生成图片");
    /// <summary>Wait for a background image task reported through <see cref="ImageEvent.TaskId"/>, e.g. after the app was closed mid-generation.</summary>
    public virtual Task<ImageResult> ResumeImageTaskAsync(string taskId, bool store, CancellationToken ct) => throw Unsupported("续取图片任务");
    /// <summary>Best-effort cancel of a background task the user stopped; vendors without a cancel endpoint simply let it finish.</summary>
    public virtual Task CancelTaskAsync(string taskId) => Task.CompletedTask;
    public virtual Task<string> GenerateTextAsync(TextRequest request, Action<string>? onDelta, CancellationToken ct) => throw Unsupported("生成文本");
    public virtual Task<GeneratedImage> SpeechAsync(SpeechRequest request, CancellationToken ct) => throw Unsupported("生成音频");
    public virtual Task<List<string>> ListModelsAsync(CancellationToken ct) => Task.FromResult(Core.Models.ModelFamilies.Recommended(Channel.Vendor).ToList());
    /// <summary>The vendor's billing figure, read with the channel's billing credential (<see cref="ModelChannel.BillingKey"/>), never the API key.</summary>
    public virtual Task<Balance> BalanceAsync(CancellationToken ct) => throw Unsupported("查询余额");

    public static AiProvider For(ModelChannel channel, HttpClient http) => channel.Vendor switch
    {
        Vendor.OpenAI => new OpenAIProvider(channel, http),
        Vendor.Google => new GoogleProvider(channel, http),
        Vendor.Ark => new ArkProvider(channel, http),
        Vendor.XAI => new XaiProvider(channel, http),
        Vendor.MiniMax => new MiniMaxProvider(channel, http),
        _ => new QwenProvider(channel, http),
    };
}
