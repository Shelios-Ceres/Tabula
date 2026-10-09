using InfiniteCanvas.Core.Settings;

namespace InfiniteCanvas.Providers;

public sealed record MediaInput(byte[] Bytes, string MimeType)
{
    public string Base64 => Convert.ToBase64String(Bytes);
    public string DataUrl => $"data:{MimeType.ToLowerInvariant()};base64,{Base64}";
    /// <summary>A vendor-side address for the same media (a Seedance result URL, a Veo video URI), when it is still valid.</summary>
    public string? Url { get; init; }
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
    /// <summary>DashScope: "direct" or "agent" prompt rewriting (agent is text-to-image only).</summary>
    public string? PromptExtendMode { get; init; }
    /// <summary>DashScope: thinking while rewriting the prompt; null keeps the vendor default.</summary>
    public bool? EnableThinking { get; init; }
    public long? Seed { get; init; }
}

/// <param name="Selections">Ark interactive editing: points / boxes on the references, in 0–1 fractions.</param>
/// <param name="PreviousId">Continue a stored conversation (OpenAI response ID / Gemini interaction ID).</param>
public sealed record ImageRequest(string Model, string Prompt, IReadOnlyList<MediaInput> References, MediaInput? Mask, int Count, ImageOptions Options, string? EndpointId = null,
    IReadOnlyList<Core.Canvas.ImageSelection>? Selections = null, string? PreviousId = null);

/// <summary>Layer metadata from Ark layer decomposition; Box is [left, top, right, bottom] in 0–1000 of the base image.</summary>
public sealed record ImageLayer(int ZIndex, string? Name, string? Description, double[]? Box);

public sealed record GeneratedImage(byte[] Bytes, string MimeType, ImageLayer? Layer = null);

public sealed record ImageResult(List<GeneratedImage> Images, List<string> Texts, List<string> Errors, string? ConversationId = null);

/// <summary>Live progress: a preview frame, a finished image at an index, streamed text, or the ID of a background task the vendor accepted (kept so polling can resume later).</summary>
public sealed record ImageEvent(GeneratedImage? Preview = null, int Index = -1, GeneratedImage? Output = null, string? Text = null, string? TaskId = null);

public enum VideoMode { Frames, Reference }

/// <summary>
/// One video task. Seconds of -1 lets the model pick the length (Seedance). Videos / Audios are reference clips;
/// Task is "edit" or "extend" when the first reference video is the one to change, otherwise a plain generation.
/// </summary>
public sealed record VideoRequest(string Model, string Prompt, IReadOnlyList<MediaInput> Images, VideoMode Mode, int Seconds, string Resolution, string Ratio, bool GenerateAudio, bool Watermark, string? EndpointId = null)
{
    public IReadOnlyList<MediaInput> Videos { get; init; } = [];
    public IReadOnlyList<MediaInput> Audios { get; init; } = [];
    public string? Task { get; init; }
    /// <summary>Seedance 2.5 draft (样片): a 480p preview whose task ID later yields the final video.</summary>
    public bool Draft { get; init; }
    public string? DraftTaskId { get; init; }
    public bool ReturnLastFrame { get; init; }
    /// <summary>Grok Imagine Video 1.5 preset voices (voice_id), referenced in the prompt as &lt;AUDIO_0&gt;…</summary>
    public IReadOnlyList<string> Voices { get; init; } = [];
    /// <summary>Grok Imagine Video 1.5 images pinned inside the clip at the given second.</summary>
    public IReadOnlyList<(MediaInput Image, double Seconds)> Keyframes { get; init; } = [];
    /// <summary>Gemini Omni: continue a stored interaction instead of resending the video.</summary>
    public string? PreviousId { get; init; }
}

public enum VideoTaskStatus { Pending, Completed, Failed }

/// <summary>
/// RemoteUrl is the vendor's address of the result, usable as a later reference until RemoteUntil; LastFrame is the final frame
/// when it was requested; ConversationId continues a conversational model (Gemini Omni).
/// </summary>
public sealed record VideoTaskState(VideoTaskStatus Status, byte[]? Video = null, string? MimeType = null, string? Error = null, int Progress = -1)
{
    public string? RemoteUrl { get; init; }
    public DateTime RemoteUntil { get; init; }
    public byte[]? LastFrame { get; init; }
    public string? ConversationId { get; init; }
}

public sealed record TextRequest(string Model, string Prompt, IReadOnlyList<MediaInput> Images, string? System = null, string? Reasoning = null);

public sealed record SpeechRequest(string Model, string Input, string Voice, string Format, double Speed, string? Instructions);

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

    public virtual Task<ImageResult> GenerateImageAsync(ImageRequest request, IProgress<ImageEvent>? progress, CancellationToken ct) => throw Unsupported("生成图片");
    /// <summary>Wait for a background image task reported through <see cref="ImageEvent.TaskId"/>, e.g. after the app was closed mid-generation.</summary>
    public virtual Task<ImageResult> ResumeImageTaskAsync(string taskId, bool store, CancellationToken ct) => throw Unsupported("续取图片任务");
    /// <summary>Best-effort cancel of a background task the user stopped; vendors without a cancel endpoint simply let it finish.</summary>
    public virtual Task CancelTaskAsync(string taskId) => Task.CompletedTask;
    public virtual Task<string> CreateVideoTaskAsync(VideoRequest request, CancellationToken ct) => throw Unsupported("生成视频");
    public virtual Task<VideoTaskState> PollVideoTaskAsync(string taskId, CancellationToken ct) => throw Unsupported("生成视频");
    public virtual Task<string> GenerateTextAsync(TextRequest request, Action<string>? onDelta, CancellationToken ct) => throw Unsupported("生成文本");
    public virtual Task<GeneratedImage> SpeechAsync(SpeechRequest request, CancellationToken ct) => throw Unsupported("生成音频");
    public virtual Task<List<string>> ListModelsAsync(CancellationToken ct) => Task.FromResult(Core.Models.ModelFamilies.Recommended(Channel.Vendor).ToList());

    public static AiProvider For(ModelChannel channel, HttpClient http) => channel.Vendor switch
    {
        Vendor.OpenAI => new OpenAIProvider(channel, http),
        Vendor.Google => new GoogleProvider(channel, http),
        Vendor.Ark => new ArkProvider(channel, http),
        Vendor.XAI => new XaiProvider(channel, http),
        _ => new QwenProvider(channel, http),
    };
}
