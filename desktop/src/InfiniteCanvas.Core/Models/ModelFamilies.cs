using System.Globalization;
using System.Text.RegularExpressions;
using InfiniteCanvas.Core.Canvas;
using InfiniteCanvas.Core.Settings;

namespace InfiniteCanvas.Core.Models;

/// <summary>
/// Capabilities are declared per model family (matched by regex on a normalized ID), never by exact model name,
/// so snapshot dates, -preview/-latest suffixes and vendor prefixes resolve to the same family.
/// Values follow each vendor's official API reference.
/// </summary>
public sealed record ImageFamily(string Id, Vendor Vendor, string Pattern)
{
    public Regex Match { get; } = new(Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// <summary>Resolution tiers shown in the UI ("auto", "1K", "1.5k"...).</summary>
    public string[] Tiers { get; init; } = [];
    public string[] Ratios { get; init; } = [];
    public int MaxReferences { get; init; }
    public int MaxImages { get; init; } = 1;
    public string[] OutputFormats { get; init; } = ["png"];
    public string[] Qualities { get; init; } = [];
    public string[] ThinkingLevels { get; init; } = [];
    public bool Stream { get; init; }
    public bool WebSearch { get; init; }
    public bool ImageSearch { get; init; }
    public bool Sequential { get; init; }
    public bool Layers { get; init; }
    /// <summary>Ark Seedream 5.0 pro / flash: points and boxes on the reference steer local edits; transparent output from an alpha input.</summary>
    public bool Interactive { get; init; }
    public bool FastPrompt { get; init; }
    public bool Mask { get; init; }
    public bool Background { get; init; }
    public bool NegativePrompt { get; init; }
    /// <summary>Ark only: documented total-pixel ceiling for explicit WIDTHxHEIGHT sizes; the top tier uses all of it.</summary>
    public long MaxPixels { get; init; }
}

public sealed record VideoFamily(string Id, Vendor Vendor, string Pattern)
{
    public Regex Match { get; } = new(Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public int[] Seconds { get; init; } = [];
    public (int Min, int Max)? SecondRange { get; init; }
    /// <summary>Vertical pixel tiers; "2160" is sent as 4k.</summary>
    public string[] Resolutions { get; init; } = [];
    public string[] Ratios { get; init; } = [];
    public int MaxReferences { get; init; }
    public bool Audio { get; init; }
    public bool Watermark { get; init; }
    /// <summary>Seedance 2.5 keeps the first frame's aspect ratio, so frame tasks only accept adaptive.</summary>
    public bool FramesAdaptiveOnly { get; init; }
    /// <summary>Seedance 2.5: a 480p draft first, then the 1080p final from its task ID.</summary>
    public bool Draft { get; init; }
    /// <summary>Seedance: the task can return its final frame, to start the next clip from.</summary>
    public bool LastFrame { get; init; }
    /// <summary>Seedance 2.0 / 2.5: duration -1 lets the model choose the length.</summary>
    public bool SmartDuration { get; init; }
    /// <summary>Reference clips the model accepts (Seedance only by public URL, i.e. its own recent results).</summary>
    public int MaxVideos { get; init; }
    public int MaxAudios { get; init; }
    /// <summary>Operations on an input video besides generating: "edit" and / or "extend".</summary>
    public string[] Tasks { get; init; } = [];
    /// <summary>Grok Imagine Video 1.5: preset voices and keyframes pinned inside the clip.</summary>
    public int MaxVoices { get; init; }
    public int MaxKeyframes { get; init; }
    /// <summary>Gemini Omni: a stored interaction can be refined turn by turn.</summary>
    public bool Conversational { get; init; }
}

public static class ModelFamilies
{
    private static readonly string[] StandardRatios = ["1:1", "2:3", "3:2", "3:4", "4:3", "4:5", "5:4", "9:16", "16:9", "21:9"];
    private static readonly string[] TableRatios = ["1:1", "2:3", "3:2", "3:4", "4:3", "4:5", "5:4", "16:9", "9:16", "21:9", "9:21"];
    private static readonly string[] ArkRatios = ["auto", "1:1", "4:3", "3:4", "16:9", "9:16", "3:2", "2:3", "21:9"];
    private static readonly string[] XaiRatios = ["auto", "1:1", "16:9", "9:16", "4:3", "3:4", "3:2", "2:3", "2:1", "1:2", "19.5:9", "9:19.5", "20:9", "9:20", "21:9", "5:2"];
    private static readonly string[] SeedanceRatios = ["adaptive", "16:9", "4:3", "1:1", "3:4", "9:16", "21:9"];
    private static readonly string[] GrokVideoRatios = ["16:9", "9:16", "1:1", "4:3", "3:4", "3:2", "2:3"];
    private static readonly string[] WanRatios = ["16:9", "9:16", "1:1", "4:3", "3:4"];

    // Order matters: more specific patterns first.
    public static readonly ImageFamily[] Image =
    [
        new("gpt-image-2.5", Vendor.OpenAI, "^gpt-image-2\\.5") { Tiers = ["auto", "1K", "2K", "4K"], Ratios = TableRatios, MaxReferences = 16, MaxImages = 10, OutputFormats = ["png", "jpeg", "webp"], Qualities = ["auto", "low", "medium", "high", "xhigh", "max"], Stream = true, Mask = true, Background = true },
        new("gpt-image-1", Vendor.OpenAI, "^gpt-image-1") { Tiers = ["auto", "1K"], Ratios = ["1:1", "3:2", "2:3"], MaxReferences = 16, MaxImages = 10, OutputFormats = ["png", "jpeg", "webp"], Qualities = ["auto", "low", "medium", "high"], Stream = true, Mask = true, Background = true },
        new("gpt-image", Vendor.OpenAI, "^gpt-image") { Tiers = ["auto", "1K", "2K", "4K"], Ratios = TableRatios, MaxReferences = 16, MaxImages = 10, OutputFormats = ["png", "jpeg", "webp"], Qualities = ["auto", "low", "medium", "high"], Stream = true, Mask = true, Background = true },
        new("gemini-nano-banana-2.1", Vendor.Google, "^gemini-nano-banana-2\\.1") { Tiers = ["1K", "2K", "4K"], Ratios = [.. StandardRatios, "1:4", "4:1", "1:8", "8:1"], MaxReferences = 14, ThinkingLevels = ["auto", "minimal", "medium", "high"], WebSearch = true, ImageSearch = true, OutputFormats = ["jpeg"] },
        new("gemini-3.1-flash-lite-image", Vendor.Google, "^gemini-3\\.1-flash-lite-image") { Tiers = ["1K"], Ratios = StandardRatios, MaxReferences = 14, ThinkingLevels = ["auto", "minimal", "high"], OutputFormats = ["jpeg"] },
        new("gemini-3.1-flash-image", Vendor.Google, "^gemini-3\\.1-flash-image") { Tiers = ["512", "1K", "2K", "4K"], Ratios = [.. StandardRatios, "1:4", "4:1", "1:8", "8:1"], MaxReferences = 14, ThinkingLevels = ["auto", "minimal", "high"], WebSearch = true, ImageSearch = true, OutputFormats = ["jpeg"] },
        new("gemini-3-pro-image", Vendor.Google, "^gemini-3-pro-image") { Tiers = ["1K", "2K", "4K"], Ratios = StandardRatios, MaxReferences = 14, ThinkingLevels = ["auto"], WebSearch = true, OutputFormats = ["jpeg"] },
        new("gemini-2.5-flash-image", Vendor.Google, "^gemini-2\\.5-flash-image") { Tiers = ["1K"], Ratios = StandardRatios, MaxReferences = 14, OutputFormats = ["jpeg"] },
        new("gemini-image", Vendor.Google, "^gemini-.*(image|banana)") { Tiers = ["1K", "2K", "4K"], Ratios = StandardRatios, MaxReferences = 14, OutputFormats = ["jpeg"] },
        new("seedream-5-pro", Vendor.Ark, "seedream-5-0-pro$") { Tiers = ["1K", "1.5K", "2K"], MaxPixels = 4_624_220, Ratios = ArkRatios, MaxReferences = 10, OutputFormats = ["jpeg", "png"], Layers = true, Interactive = true, FastPrompt = true },
        new("seedream-5-flash", Vendor.Ark, "seedream-5-0-flash$") { Tiers = ["1K", "1.5K", "2K"], MaxPixels = 4_624_220, Ratios = ArkRatios, MaxReferences = 10, OutputFormats = ["jpeg", "png"], Layers = true, Interactive = true },
        new("seedream-5-lite", Vendor.Ark, "seedream-5-0(-lite)?$") { Tiers = ["2K", "3K", "4K"], MaxPixels = 16_777_216, Ratios = ArkRatios, MaxReferences = 14, MaxImages = 15, OutputFormats = ["jpeg", "png"], Stream = true, WebSearch = true, Sequential = true },
        new("seedream-4.5", Vendor.Ark, "seedream-4-5$") { Tiers = ["2K", "4K"], MaxPixels = 16_777_216, Ratios = ArkRatios, MaxReferences = 14, MaxImages = 15, OutputFormats = ["jpeg"], Stream = true, Sequential = true },
        new("seedream-4.0", Vendor.Ark, "seedream-4-0$") { Tiers = ["1K", "2K", "4K"], MaxPixels = 16_777_216, Ratios = ArkRatios, MaxReferences = 14, MaxImages = 15, OutputFormats = ["jpeg"], Stream = true, Sequential = true },
        new("grok-imagine-image-2", Vendor.XAI, "^grok-imagine-image-2\\.") { Tiers = ["1k", "1.5k", "2k"], Ratios = XaiRatios, MaxReferences = 5, MaxImages = 10, Qualities = ["auto", "low", "medium"], OutputFormats = ["jpeg"] },
        new("grok-imagine-image", Vendor.XAI, "^grok-imagine-image") { Tiers = ["1k", "2k"], Ratios = XaiRatios, MaxReferences = 5, MaxImages = 10, OutputFormats = ["jpeg"] },
        new("qwen-image-3", Vendor.Qwen, "^qwen-image-3") { Tiers = ["auto", "1K", "2K"], Ratios = TableRatios, MaxReferences = 3, MaxImages = 6, NegativePrompt = true },
        // max / plus / the original qwen-image: text-to-image only, five fixed sizes and one image per request.
        new("qwen-image-plus", Vendor.Qwen, "^qwen-image(-max|-plus)?$") { Ratios = ["16:9", "4:3", "1:1", "3:4", "9:16"] },
        new("qwen-image", Vendor.Qwen, "^qwen-image") { Tiers = ["auto", "1K", "2K"], Ratios = TableRatios, MaxReferences = 10, MaxImages = 6 },
    ];

    public static readonly VideoFamily[] Video =
    [
        // Omni has no duration setting; it also edits and extends videos and refines a stored interaction turn by turn.
        new("gemini-omni", Vendor.Google, "^gemini-omni") { Resolutions = ["360", "720", "1080", "2160"], Ratios = ["16:9", "9:16"], MaxReferences = 3, MaxVideos = 3, Tasks = ["edit", "extend"], Conversational = true },
        new("veo-3.1", Vendor.Google, "^veo-3\\.1-generate") { Seconds = [4, 6, 8], Resolutions = ["720", "1080", "2160"], Ratios = ["16:9", "9:16"], MaxReferences = 3, Tasks = ["extend"] },
        new("veo-3.1-fast", Vendor.Google, "^veo-3\\.1-fast") { Seconds = [4, 6, 8], Resolutions = ["720", "1080", "2160"], Ratios = ["16:9", "9:16"], MaxReferences = 3, Tasks = ["extend"] },
        new("veo-3.1-lite", Vendor.Google, "^veo-3\\.1-lite") { Seconds = [4, 6, 8], Resolutions = ["720", "1080"], Ratios = ["16:9", "9:16"] },
        new("veo-3", Vendor.Google, "^veo-3") { Seconds = [4, 6, 8], Resolutions = ["720", "1080", "2160"], Ratios = ["16:9", "9:16"] },
        new("veo", Vendor.Google, "^veo-") { Seconds = [8], Resolutions = ["720", "1080"], Ratios = ["16:9"] },
        new("seedance-2.5", Vendor.Ark, "seedance-2-5") { SecondRange = (4, 30), Resolutions = ["480", "720", "1080"], Ratios = SeedanceRatios, MaxReferences = 30, Audio = true, Watermark = true, FramesAdaptiveOnly = true,
            Draft = true, LastFrame = true, SmartDuration = true, MaxVideos = 10, MaxAudios = 10, Tasks = ["edit", "extend"] },
        new("seedance-2.0-fast", Vendor.Ark, "seedance-2-0-(fast|mini)") { SecondRange = (4, 15), Resolutions = ["480", "720"], Ratios = SeedanceRatios, MaxReferences = 9, Audio = true, Watermark = true, LastFrame = true, SmartDuration = true, MaxVideos = 3, MaxAudios = 3 },
        new("seedance-2.0", Vendor.Ark, "seedance-2-0") { SecondRange = (4, 15), Resolutions = ["480", "720", "1080", "2160"], Ratios = SeedanceRatios, MaxReferences = 9, Audio = true, Watermark = true, LastFrame = true, SmartDuration = true, MaxVideos = 3, MaxAudios = 3 },
        new("seedance-1.0", Vendor.Ark, "seedance") { SecondRange = (2, 12), Resolutions = ["480", "720", "1080"], Ratios = SeedanceRatios, Watermark = true, LastFrame = true },
        new("grok-imagine-video-1.5", Vendor.XAI, "^grok-imagine-video-1\\.5") { SecondRange = (1, 15), Resolutions = ["480", "720", "1080"], Ratios = GrokVideoRatios, MaxReferences = 7, Audio = true, Tasks = ["edit", "extend"], MaxVoices = 3, MaxKeyframes = 4 },
        new("grok-imagine-video", Vendor.XAI, "^grok-imagine-video") { SecondRange = (1, 15), Resolutions = ["480", "720", "1080"], Ratios = GrokVideoRatios, MaxReferences = 7, Audio = true, Tasks = ["edit", "extend"] },
        // Wan 2.7 (new video-synthesis protocol): i2v takes first / last frame, a driving audio or a clip to continue (duration is the total);
        // r2v takes up to 5 reference images and videos together; videoedit always edits one input video (2–10 s) with up to 4 reference images.
        new("wan-2.7-t2v", Vendor.Qwen, "^wan2\\.7-t2v") { SecondRange = (2, 15), Resolutions = ["720", "1080"], Ratios = WanRatios, MaxAudios = 1, Watermark = true },
        new("wan-2.7-i2v", Vendor.Qwen, "^wan2\\.7-i2v") { SecondRange = (2, 15), Resolutions = ["720", "1080"], Ratios = ["adaptive"], MaxReferences = 2, MaxAudios = 1, Tasks = ["extend"], Watermark = true },
        new("wan-2.7-r2v", Vendor.Qwen, "^wan2\\.7-r2v") { SecondRange = (2, 15), Resolutions = ["720", "1080"], Ratios = WanRatios, MaxReferences = 5, MaxVideos = 5, Watermark = true },
        new("wan-2.7-videoedit", Vendor.Qwen, "^wan2\\.7-videoedit") { SecondRange = (2, 10), Resolutions = ["720", "1080"], Ratios = ["adaptive", .. WanRatios], MaxReferences = 4, MaxVideos = 1, Tasks = ["edit"], Watermark = true },
    ];

    /// <summary>Strip the channel prefix, "models/", a vendor prefix and trailing snapshot/preview suffixes.</summary>
    public static string Normalize(string model)
    {
        var name = model.Split("::").Last().Trim().ToLowerInvariant();
        name = Regex.Replace(name, "^models/", "");
        name = Regex.Replace(name, "^[a-z0-9_.-]+/", "");
        for (var previous = ""; previous != name;)
        {
            previous = name;
            name = Regex.Replace(name, "-(preview|latest|exp|\\d{4}-\\d{2}-\\d{2}|\\d{6})$", "");
        }
        return name;
    }

    public static ImageFamily? FindImage(Vendor vendor, string model)
    {
        var name = Normalize(model);
        return Image.FirstOrDefault(family => family.Vendor == vendor && family.Match.IsMatch(name));
    }

    public static VideoFamily? FindVideo(Vendor vendor, string model)
    {
        var name = Normalize(model);
        return Video.FirstOrDefault(family => family.Vendor == vendor && family.Match.IsMatch(name));
    }

    /// <summary>
    /// A readable default name for pickers: no vendor prefix, snapshot date or preview suffix, versions written as 5.0, words capitalised
    /// (doubao-seedream-5-0-pro-260628 → Seedream 5.0 Pro, veo-3.1-fast-generate-preview → Veo 3.1 Fast).
    /// </summary>
    public static string ShortName(string model)
    {
        var name = Regex.Replace(Normalize(model), "^doubao-", "");
        name = Regex.Replace(name, @"(?<=^|-)(\d+)-(\d+)(?=-|$)", "$1.$2");
        var words = name.Split('-', StringSplitOptions.RemoveEmptyEntries).Where(word => word != "generate").ToList();
        if (words.Count == 0) return model;
        return string.Join(" ", words.Select(word => word is "gpt" or "tts" || Regex.IsMatch(word, @"^[a-z]\d[a-z]$") ? word.ToUpperInvariant() : char.ToUpperInvariant(word[0]) + word[1..]));
    }

    /// <summary>DashScope routes multimodal models (VL/Audio/QVQ/Omni and the versioned qwen3.x line) to multimodal-generation.</summary>
    public static bool QwenMultimodal(string model) => Regex.IsMatch(Normalize(model), "vl|audio|qvq|omni|^qwen3\\.\\d+-");

    /// <summary>
    /// <c>reasoning.effort</c> values an OpenAI mainline model accepts, lowest first, per each model's page;
    /// empty for non-reasoning models (gpt-4o, gpt-4.1, *-chat), which must not receive the field.
    /// </summary>
    public static string[] ReasoningEfforts(string model)
    {
        var id = Normalize(model);
        if (id.Contains("chat") || !Regex.IsMatch(id, @"^(gpt-[56]|o\d)")) return [];
        if (Regex.IsMatch(id, @"^gpt-6(\.\d+)?-luna")) return ["none", "low", "medium", "high", "xhigh", "max"];
        if (id.StartsWith("gpt-6")) return ["low", "medium", "high", "xhigh", "max"];
        if (Regex.IsMatch(id, @"^gpt-5\.1(?!\d)")) return ["none", "low", "medium", "high"];
        if (Regex.IsMatch(id, @"^gpt-5\.\d")) return ["none", "low", "medium", "high", "xhigh"];
        if (id.StartsWith("gpt-5")) return ["minimal", "low", "medium", "high"];
        return ["low", "medium", "high"];
    }

    /// <summary>The chosen effort when the model accepts it, otherwise its lowest; null for non-reasoning models.</summary>
    public static string? SnapReasoning(string model, string? effort) =>
        ReasoningEfforts(model) is { Length: > 0 } efforts ? efforts.Contains(effort) ? effort : efforts[0] : null;

    public static IReadOnlyList<string> Recommended(Vendor vendor) => vendor switch
    {
        Vendor.OpenAI => ["gpt-image-2.5-flare", "gpt-image-2.5-sunburst", "gpt-image-2", "gpt-5.5", "gpt-4o-mini-tts"],
        Vendor.Google => ["gemini-nano-banana-2.1", "gemini-3-pro-image", "gemini-3.1-flash-image", "gemini-3.1-flash-lite-image", "gemini-omni-1.1-flash", "gemini-3.8-flash-tts", "gemini-3.8-flash-lite-tts", "veo-3.1-generate-preview", "veo-3.1-fast-generate-preview", "veo-3.1-lite-generate-preview"],
        Vendor.Ark => ["doubao-seedream-5-0-pro-260628", "doubao-seedream-5-0-flash-260915", "doubao-seedream-5-0-260128", "doubao-seedream-4-5-251128", "doubao-seedream-4-0-250828", "doubao-seedance-2-5-260628", "doubao-seedance-2-0-260128", "doubao-seedance-2-0-fast-260128", "doubao-seedance-2-0-mini-260615"],
        Vendor.XAI => ["grok-imagine-image-2.0", "grok-imagine-video-1.5"],
        _ => ["qwen-image-3.0-pro", "qwen-image-3.0", "qwen-image-2.1-pro", "wan2.7-t2v", "wan2.7-i2v", "wan2.7-r2v", "wan2.7-videoedit", "qwen3.8-max", "qwen-plus"],
    };

    /// <summary>Gemini TTS prebuilt voices, in the order of the speech-generation guide; the first is the fallback.</summary>
    public static readonly string[] GeminiVoices =
    [
        "Kore", "Zephyr", "Puck", "Charon", "Fenrir", "Leda", "Orus", "Aoede", "Callirrhoe", "Autonoe", "Enceladus", "Iapetus", "Umbriel", "Algieba", "Despina",
        "Erinome", "Algenib", "Rasalgethi", "Laomedeia", "Achernar", "Alnilam", "Schedar", "Gacrux", "Pulcherrima", "Achird", "Zubenelgenubi", "Vindemiatrix", "Sadachbia", "Sadaltager", "Sulafat",
    ];

    private static readonly string[] VideoKeywords = ["video", "sora", "veo", "seedance", "kling", "wan", "hailuo"];
    private static readonly string[] AudioKeywords = ["audio", "tts", "speech", "voice", "music", "sound"];
    private static readonly string[] ImageKeywords = ["seedream", "gpt-image", "image", "banana", "dall-e", "imagen", "flux", "sdxl", "stable-diffusion", "midjourney"];

    /// <summary>Best-effort default for a fetched model name; the user can change it in settings.</summary>
    public static Capability GuessCapability(string name)
    {
        var normalized = Normalize(name);
        if (Image.Any(family => family.Match.IsMatch(normalized))) return Capability.Image;
        if (Video.Any(family => family.Match.IsMatch(normalized))) return Capability.Video;
        if (VideoKeywords.Any(normalized.Contains)) return Capability.Video;
        if (AudioKeywords.Any(normalized.Contains) && !normalized.Contains("qwen")) return Capability.Audio;
        if (ImageKeywords.Any(normalized.Contains)) return Capability.Image;
        return Capability.Text;
    }

    /// <summary>Families without any duration setting (Gemini Omni) have no length to choose.</summary>
    public static bool HasDuration(VideoFamily family) => family.Seconds.Length > 0 || family.SecondRange is not null;

    public static int SnapSeconds(VideoFamily family, int value)
    {
        if (family.Seconds.Length > 0) return family.Seconds.MinBy(item => Math.Abs(item - value));
        if (value == -1 && family.SmartDuration) return -1;
        var (min, max) = family.SecondRange ?? (4, 15);
        return Math.Clamp(value <= 0 ? 6 : value, min, max);
    }

    public static string SnapResolution(VideoFamily family, string? value)
    {
        var pixels = int.TryParse(value?.TrimEnd('p'), out var parsed) ? parsed : 720;
        return family.Resolutions.MinBy(item => Math.Abs(int.Parse(item) - pixels)) ?? "720";
    }

    public static string SnapRatio(VideoFamily family, string? ratio) => ratio is not null && family.Ratios.Contains(ratio) ? ratio : family.Ratios[0];

    // GPT Image fixed size table by tier and ratio; also used for DashScope (sent as W*H).
    private static readonly Dictionary<string, Dictionary<string, string>> TierTable = new()
    {
        ["1K"] = new() { ["1:1"] = "1024x1024", ["2:3"] = "1024x1536", ["3:2"] = "1536x1024", ["4:3"] = "1024x768", ["3:4"] = "768x1024", ["4:5"] = "1024x1280", ["5:4"] = "1280x1024", ["16:9"] = "1536x864", ["9:16"] = "864x1536", ["21:9"] = "2016x864", ["9:21"] = "864x2016" },
        ["2K"] = new() { ["1:1"] = "2048x2048", ["2:3"] = "1360x2048", ["3:2"] = "2048x1360", ["4:3"] = "2048x1536", ["3:4"] = "1536x2048", ["4:5"] = "1600x2000", ["5:4"] = "2000x1600", ["16:9"] = "2048x1152", ["9:16"] = "1152x2048", ["21:9"] = "2688x1152", ["9:21"] = "1152x2688" },
        // Largest multiples of 16 within 8,294,400 px and a 3840 px edge, each within 0.5% of its ratio; 21:9 is capped by the edge.
        ["4K"] = new() { ["1:1"] = "2880x2880", ["2:3"] = "2352x3520", ["3:2"] = "3520x2352", ["4:3"] = "3312x2496", ["3:4"] = "2496x3312", ["4:5"] = "2576x3216", ["5:4"] = "3216x2576", ["16:9"] = "3840x2160", ["9:16"] = "2160x3840", ["21:9"] = "3840x1648", ["9:21"] = "1648x3840" },
    };

    private static readonly Dictionary<string, string> QwenFixedSizes = new() { ["16:9"] = "1664*928", ["4:3"] = "1472*1104", ["1:1"] = "1328*1328", ["3:4"] = "1104*1472", ["9:16"] = "928*1664" };

    // Seedream mapping tables from the Ark image API reference (ratio order: 1:1, 4:3, 3:4, 16:9, 9:16, 3:2, 2:3, 21:9).
    private static readonly string[] ArkTableRatios = ["1:1", "4:3", "3:4", "16:9", "9:16", "3:2", "2:3", "21:9"];
    private static readonly Dictionary<string, string[]> ArkTable = new()
    {
        ["1K"] = ["1024x1024", "1152x864", "864x1152", "1424x800", "800x1424", "1248x832", "832x1248", "1568x672"],
        ["1K-4.0"] = ["1024x1024", "1152x864", "864x1152", "1280x720", "720x1280", "1248x832", "832x1248", "1512x648"],
        ["1.5K"] = ["1536x1536", "1792x1344", "1344x1792", "2048x1152", "1152x2048", "1872x1248", "1248x1872", "2352x1008"],
        ["2K-5"] = ["2048x2048", "2368x1776", "1776x2368", "2816x1584", "1584x2816", "2496x1664", "1664x2496", "3136x1344"],
        ["2K"] = ["2048x2048", "2304x1728", "1728x2304", "2848x1600", "1600x2848", "2496x1664", "1664x2496", "3136x1344"],
        ["3K"] = ["3072x3072", "3456x2592", "2592x3456", "4096x2304", "2304x4096", "3744x2496", "2496x3744", "4704x2016"],
        ["4K"] = ["4096x4096", "4704x3520", "3520x4704", "5504x3040", "3040x5504", "4992x3328", "3328x4992", "6240x2656"],
    };

    /// <summary>The size value each vendor's image API expects for a tier and ratio; null means "let the model decide".</summary>
    public static string? ImageSize(ImageFamily family, string? tier, string? ratio)
    {
        tier = string.IsNullOrEmpty(tier) ? family.Tiers.FirstOrDefault() : tier;
        var hasRatio = !string.IsNullOrEmpty(ratio) && ratio != "auto";
        switch (family.Vendor)
        {
            case Vendor.OpenAI:
                // Tiers or ratios left over from another model fall back instead of sending a size this model rejects.
                if (tier is null or "auto" || !family.Tiers.Contains(tier)) return "auto";
                return TierTable.TryGetValue(tier, out var table) && table.TryGetValue(hasRatio && family.Ratios.Contains(ratio!) ? ratio! : "1:1", out var size) ? size : "auto";
            case Vendor.Qwen:
                // Families without tiers only accept the documented fixed sizes (16:9 is the default).
                if (family.Tiers.Length == 0) return QwenFixedSizes.GetValueOrDefault(hasRatio ? ratio! : "16:9");
                // DashScope bills by area (≤2,250,000 px is the 1K tier) and caps output at 2048×2048,
                // so each tier takes the largest size its billing band allows for the ratio.
                long? area = tier switch { "1K" => 2_250_000, "2K" => 2048L * 2048, _ => null };
                return area is null ? null : MaxAreaSize(AspectOf(hasRatio ? ratio : "1:1"), area.Value).Replace('x', '*');
            case Vendor.Ark:
                if (!hasRatio || tier is null) return tier;
                // The top tier fills the documented pixel ceiling for the ratio; lower tiers follow the official mapping table.
                if (family.MaxPixels > 0 && tier == family.Tiers[^1]) return MaxAreaSize(AspectOf(ratio), family.MaxPixels);
                var key = tier == "1K" && family.Id == "seedream-4.0" ? "1K-4.0" : tier == "2K" && family.Id is "seedream-5-pro" or "seedream-5-flash" ? "2K-5" : tier;
                var index = Array.IndexOf(ArkTableRatios, ratio);
                return ArkTable.TryGetValue(key, out var row) && index >= 0 ? row[index] : tier;
            default:
                return tier;
        }
    }

    /// <summary>Largest WIDTHxHEIGHT (multiples of 16) with the given aspect whose area stays within the cap.</summary>
    public static string MaxAreaSize(double aspect, long maxArea)
    {
        var height = (int)(Math.Sqrt(maxArea / aspect) / 16) * 16;
        var width = (int)Math.Round(height * aspect / 16) * 16;
        while ((long)width * height > maxArea) width -= 16;
        return $"{width}x{height}";
    }

    /// <summary>Approximate output aspect for laying out a placeholder node before the image arrives.</summary>
    public static double AspectOf(string? ratio)
    {
        var parts = ratio?.Split(':');
        return parts is { Length: 2 } && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var w) && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var h) && h > 0 ? w / h : 1;
    }
}
