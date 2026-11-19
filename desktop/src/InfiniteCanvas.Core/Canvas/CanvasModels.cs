using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace InfiniteCanvas.Core.Canvas;

public enum NodeKind { Image, Text, Config, Video, Audio, Group }
public enum NodeStatus { Idle, Loading, Success, Error }
public enum Capability { Image, Video, Text, Audio }

public record struct Viewport(double X, double Y, double K);

public sealed record CanvasConnection(string Id, string From, string To)
{
    public static CanvasConnection Create(string from, string to) => new(Ids.New("edge"), from, to);
}

public static class Ids
{
    public static string New(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)];
}

public partial class CanvasNode : ObservableObject
{
    public string Id { get; init; } = Ids.New("node");
    public NodeKind Kind { get; init; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _width;
    [ObservableProperty] private double _height;
    [ObservableProperty] private NodeStatus _status;
    [ObservableProperty] private string? _error;
    /// <summary>Text node: the text. Media nodes: file name inside the project assets folder.</summary>
    [ObservableProperty] private string? _content;
    [ObservableProperty] private string? _prompt;
    [ObservableProperty] private string? _groupId;

    // Generation settings. Model is "channelId::model"; Mode is only meaningful for config nodes.
    [ObservableProperty] private Capability _mode = Capability.Image;
    [ObservableProperty] private string? _model;
    [ObservableProperty] private int _count = 1;
    [ObservableProperty] private string? _size;
    [ObservableProperty] private string? _ratio;
    [ObservableProperty] private string? _quality;
    [ObservableProperty] private int _seconds;
    [ObservableProperty] private string? _resolution;
    [ObservableProperty] private bool _generateAudio = true;

    /// <summary>Transient streaming preview; never persisted.</summary>
    [ObservableProperty] [property: JsonIgnore] private byte[]? _preview;

    /// <summary>While the request waits for its channel: how many requests are ahead of it (0 = next). Never persisted.</summary>
    [ObservableProperty] [property: JsonIgnore] private int? _queueAhead;

    // Media facts.
    public int NaturalWidth { get; set; }
    public int NaturalHeight { get; set; }
    public string? MimeType { get; set; }
    [ObservableProperty] private bool _freeResize;

    /// <summary>Pending vendor task (a video, or an OpenAI background image) and the model that started it, so polling resumes after a restart.</summary>
    public string? TaskId { get; set; }
    public string? TaskModel { get; set; }

    /// <summary>Vendor-specific switches (background, output format, thinking, voice, ...).</summary>
    public Dictionary<string, string> Options { get; set; } = [];

    [JsonIgnore] public bool HasContent => !string.IsNullOrEmpty(Content);
    [JsonIgnore] public double Right => X + Width;
    [JsonIgnore] public double Bottom => Y + Height;

    public string Option(string key, string fallback = "") => Options.TryGetValue(key, out var value) && value != "" ? value : fallback;

    public void SetOption(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) Options.Remove(key);
        else Options[key] = value;
        OnPropertyChanged(nameof(Options));
    }

    public static CanvasNode Create(NodeKind kind, double centerX, double centerY)
    {
        var spec = NodeSpec.Of(kind);
        return new CanvasNode { Kind = kind, Title = spec.Title, Width = spec.Width, Height = spec.Height, X = centerX - spec.Width / 2, Y = centerY - spec.Height / 2 };
    }

    public CanvasNode Clone(bool newId = false)
    {
        var json = JsonSerializer.SerializeToNode(this, Json.Options)!.AsObject();
        if (newId) json["id"] = Ids.New("node");
        return json.Deserialize<CanvasNode>(Json.Options)!;
    }
}

public sealed record NodeSpec(string Title, double Width, double Height)
{
    public static NodeSpec Of(NodeKind kind) => kind switch
    {
        NodeKind.Image => new("图片", 340, 240),
        NodeKind.Text => new("文本", 340, 240),
        NodeKind.Config => new("配方", 340, 240),
        NodeKind.Video => new("视频", 420, 236),
        NodeKind.Audio => new("音频", 340, 120),
        _ => new("分组", 760, 480),
    };

    /// <summary>What a node generates: a config node follows its mode, a media node its own kind.</summary>
    public static Capability CapabilityOf(CanvasNode node) => node.Kind switch
    {
        NodeKind.Config => node.Mode,
        NodeKind.Image => Capability.Image,
        NodeKind.Video => Capability.Video,
        NodeKind.Audio => Capability.Audio,
        _ => Capability.Text,
    };

    public static NodeKind KindOf(Capability capability) => capability switch
    {
        Capability.Image => NodeKind.Image,
        Capability.Video => NodeKind.Video,
        Capability.Audio => NodeKind.Audio,
        _ => NodeKind.Text,
    };
}

public sealed class CanvasDocument
{
    public string Id { get; init; } = Ids.New("canvas");
    public string Title { get; set; } = "未命名画布";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public Viewport Viewport { get; set; } = new(0, 0, 1);
    public ObservableCollection<CanvasNode> Nodes { get; set; } = [];
    public ObservableCollection<CanvasConnection> Connections { get; set; } = [];

    /// <summary>The composer's unbound draft (prompt, output kind, model and parameters), kept per canvas. A config-kind node that is never part of <see cref="Nodes"/>, so it takes no part in undo.</summary>
    public CanvasNode? Draft { get; set; }

    /// <summary>Nodes the user pinned as references: they stay in the composer while other nodes are selected.</summary>
    public List<string> Pins { get; set; } = [];

    public CanvasNode? Find(string id) => Nodes.FirstOrDefault(node => node.Id == id);
}
