using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InfiniteCanvas.Core.Canvas;
using InfiniteCanvas.Core.Models;
using InfiniteCanvas.Core.Storage;

namespace InfiniteCanvas.Core.Settings;

/// <summary>The vendor decides every protocol a channel uses: image, video, text, audio and model listing.</summary>
public enum Vendor { OpenAI, Google, Ark, XAI, Qwen }

public sealed class ChannelModel
{
    public string Name { get; set; } = "";
    /// <summary>The user's own display name; requests always use <see cref="Name"/>.</summary>
    public string? Alias { get; set; }
    public Capability Capability { get; set; }
    /// <summary>Ark only: an ep- endpoint ID used instead of the model ID.</summary>
    public string? EndpointId { get; set; }
    /// <summary>The user's price for local cost estimates; null uses the built-in one (<see cref="Prices"/>).</summary>
    public TokenPrice? Price { get; set; }
}

public sealed class ModelChannel
{
    public string Id { get; set; } = Ids.New("channel");
    public Vendor Vendor { get; set; }
    /// <summary>Host only; the version path is added by <see cref="ApiBase"/>.</summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>Google only: new models usually land on v1beta first.</summary>
    public string ApiVersion { get; set; } = "v1beta";
    /// <summary>DPAPI-encrypted for the current Windows user; never written in plain text.</summary>
    public string? ProtectedKey { get; set; }
    /// <summary>Optional billing credential, separate from the API key (which cannot read billing): OpenAI Admin Key, xAI Management Key, or the secret of a Volcengine / Alibaba Cloud AccessKey pair. Encrypted like the API key.</summary>
    public string? ProtectedBillingKey { get; set; }
    /// <summary>The AccessKey ID that goes with <see cref="BillingKey"/> (Volcengine / Alibaba Cloud only); not a secret.</summary>
    public string? BillingId { get; set; }
    public List<ChannelModel> Models { get; set; } = [];

    [JsonIgnore]
    public string ApiKey { get => Unprotect(ProtectedKey); set => ProtectedKey = Protect(value); }

    [JsonIgnore]
    public string BillingKey { get => Unprotect(ProtectedBillingKey); set => ProtectedBillingKey = Protect(value); }

    /// <summary>Volcengine and Alibaba Cloud sign billing requests with an AccessKey pair, so they also need the ID.</summary>
    public static bool BillingNeedsId(Vendor vendor) => vendor is Vendor.Ark or Vendor.Qwen;

    /// <summary>Whether the vendor's balance can be queried: Google has no billing API reachable with a key.</summary>
    [JsonIgnore]
    public bool HasBilling => Vendor != Vendor.Google && BillingKey.Length > 0 && (!BillingNeedsId(Vendor) || !string.IsNullOrWhiteSpace(BillingId));

    // A key encrypted by another Windows user or machine, or a damaged value, cannot be decrypted here: treat it as missing so it is re-entered.
    private static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return ""; }
    }

    private static string? Protect(string value) => string.IsNullOrEmpty(value) ? null : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    public static string DefaultBaseUrl(Vendor vendor) => vendor switch
    {
        Vendor.OpenAI => "https://api.openai.com",
        Vendor.Google => "https://generativelanguage.googleapis.com",
        Vendor.Ark => "https://ark.cn-beijing.volces.com",
        Vendor.XAI => "https://api.x.ai",
        _ => "https://dashscope.aliyuncs.com",
    };

    public static string VendorName(Vendor vendor) => vendor switch
    {
        Vendor.OpenAI => "OpenAI",
        Vendor.Google => "Google Gemini",
        Vendor.Ark => "火山方舟",
        Vendor.XAI => "xAI",
        _ => "千问",
    };

    /// <summary>Request base: OpenAI/xAI add /v1, Google adds its version, Ark /api/v3, DashScope /api/v1.</summary>
    public string ApiBase()
    {
        var host = (string.IsNullOrWhiteSpace(BaseUrl) ? DefaultBaseUrl(Vendor) : BaseUrl).Trim().TrimEnd('/');
        if (!host.Contains("://")) host = "https://" + host;
        var suffix = Vendor switch
        {
            Vendor.Google => "/" + ApiVersion,
            Vendor.Ark => "/api/v3",
            Vendor.Qwen => "/api/v1",
            _ => "/v1",
        };
        return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? host : host + suffix;
    }
}

/// <summary>A local agent started as an ACP stdio subprocess.</summary>
public sealed class AgentConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Full command line that starts the agent in ACP stdio mode.</summary>
    public string Command { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Each vendor's documented ACP entry point.</summary>
    public static List<AgentConfig> Defaults() =>
    [
        new() { Id = "claude", Name = "Claude Code", Command = "npx -y @agentclientprotocol/claude-agent-acp" },
        new() { Id = "kimi", Name = "Kimi Code", Command = "kimi acp" },
        new() { Id = "grok", Name = "Grok Build", Command = "grok agent stdio" },
        new() { Id = "deepseek", Name = "DeepSeek Harness", Command = "npx -y @deepseek-ai/dsh --profile acp" },
    ];
}

public sealed class AppSettings
{
    public List<ModelChannel> Channels { get; set; } = [];
    public string ImageModel { get; set; } = "";
    public string VideoModel { get; set; } = "";
    public string TextModel { get; set; } = "";
    public string AudioModel { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    /// <summary>Whether the canvas page shows the library panel.</summary>
    public bool LibraryOpen { get; set; } = true;
    /// <summary>Local coding agents reached over ACP (Agent Client Protocol).</summary>
    public List<AgentConfig> Agents { get; set; } = AgentConfig.Defaults();
    /// <summary>The agent the assistant panel used last, and whether the panel is open.</summary>
    public string Agent { get; set; } = "claude";
    public bool AgentOpen { get; set; }
    public bool MiniMapOpen { get; set; } = true;
    /// <summary>Serve the canvas tools on the fixed per-user pipe, so agents outside the client (Claude Code, terminal CLIs) can operate the open canvas.</summary>
    public bool ExternalAgents { get; set; } = true;

    public string DefaultModel(Capability capability) => capability switch
    {
        Capability.Image => ImageModel,
        Capability.Video => VideoModel,
        Capability.Audio => AudioModel,
        _ => TextModel,
    };

    public void SetDefaultModel(Capability capability, string value)
    {
        switch (capability)
        {
            case Capability.Image: ImageModel = value; break;
            case Capability.Video: VideoModel = value; break;
            case Capability.Audio: AudioModel = value; break;
            default: TextModel = value; break;
        }
    }

    public IEnumerable<string> ModelsFor(Capability capability) =>
        Channels.SelectMany(channel => channel.Models.Where(model => model.Capability == capability).Select(model => ModelRef.Encode(channel.Id, model.Name)));

    public (ModelChannel Channel, ChannelModel Model)? Resolve(string? value)
    {
        if (ModelRef.Decode(value) is not { } decoded) return null;
        var (channelId, name) = decoded;
        var channel = Channels.FirstOrDefault(item => item.Id == channelId);
        var model = channel?.Models.FirstOrDefault(item => item.Name == name);
        return channel is null || model is null ? null : (channel, model);
    }

    /// <summary>Keep the node's model when it still matches the capability, else fall back to the default.</summary>
    public string ModelFor(Capability capability, string? current)
    {
        if (Resolve(current)?.Model.Capability == capability) return current!;
        var fallback = DefaultModel(capability);
        return Resolve(fallback)?.Model.Capability == capability ? fallback : ModelsFor(capability).FirstOrDefault() ?? "";
    }

    /// <summary>What pickers show: the user's alias as is, otherwise the short name with the vendor.</summary>
    public string Label(string? value)
    {
        if (Resolve(value) is not { } resolved) return ModelRef.Decode(value)?.Model ?? "未选择模型";
        return resolved.Model.Alias is { Length: > 0 } alias ? alias : $"{DisplayName(resolved.Channel, resolved.Model)}（{ChannelName(resolved.Channel)}）";
    }

    /// <summary>The alias, else the short name, else the full ID when another model of the same entry shortens to the same text.</summary>
    public static string DisplayName(ModelChannel channel, ChannelModel model)
    {
        if (model.Alias is { Length: > 0 } alias) return alias;
        var shortName = ModelFamilies.ShortName(model.Name);
        return channel.Models.Count(m => ModelFamilies.ShortName(m.Name) == shortName) > 1 ? model.Name : shortName;
    }

    /// <summary>Every connection is a vendor's native API, so it is shown by the vendor's name; a vendor added more than once gets a number (OpenAI 2).</summary>
    public string ChannelName(ModelChannel channel)
    {
        var same = Channels.Where(c => c.Vendor == channel.Vendor).ToList();
        var name = ModelChannel.VendorName(channel.Vendor);
        return same.Count > 1 && same.IndexOf(channel) > 0 ? $"{name} {same.IndexOf(channel) + 1}" : name;
    }
}

public static class ModelRef
{
    private const string Separator = "::";
    public static string Encode(string channelId, string model) => $"{channelId}{Separator}{model.Trim()}";

    public static (string ChannelId, string Model)? Decode(string? value)
    {
        var index = value?.IndexOf(Separator, StringComparison.Ordinal) ?? -1;
        return index < 0 ? null : (value![..index], value[(index + Separator.Length)..]);
    }
}

public static class SettingsStore
{
    private static string FilePath => Path.Combine(ProjectStore.Root, "settings.json");
    private static AppSettings? _current;

    public static AppSettings Current => _current ??= Load();
    public static event Action? Changed;

    private static AppSettings Load()
    {
        if (!File.Exists(FilePath)) return Defaults();
        try { return Json.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? Defaults(); }
        catch (JsonException)
        {
            // A corrupt file is backed up before defaults are used, so the next save cannot overwrite channels and keys.
            // A file that cannot be read (locked, no permission) throws instead of silently caching defaults.
            File.Copy(FilePath, $"{FilePath}.broken-{DateTime.Now:yyyyMMddHHmmss}", true);
            return Defaults();
        }
    }

    public static void Save()
    {
        try { Json.WriteFile(FilePath, Current); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Settings stay in memory; the next save retries. */ }
        Changed?.Invoke();
    }

    private static AppSettings Defaults()
    {
        var channel = new ModelChannel { Id = "default", Vendor = Vendor.OpenAI, BaseUrl = ModelChannel.DefaultBaseUrl(Vendor.OpenAI) };
        channel.Models.AddRange(ModelFamilies.Recommended(Vendor.OpenAI).Select(name => new ChannelModel { Name = name, Capability = ModelFamilies.GuessCapability(name) }));
        return new AppSettings
        {
            Channels = [channel],
            ImageModel = ModelRef.Encode(channel.Id, "gpt-image-2.5-flare"),
            TextModel = ModelRef.Encode(channel.Id, "gpt-5.5"),
            AudioModel = ModelRef.Encode(channel.Id, "gpt-4o-mini-tts"),
        };
    }
}
