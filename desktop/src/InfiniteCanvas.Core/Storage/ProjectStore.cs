using System.Text.Json;
using System.Text.Json.Nodes;
using InfiniteCanvas.Core.Canvas;
using Microsoft.VisualBasic.FileIO;

namespace InfiniteCanvas.Core.Storage;

public sealed record ProjectInfo(string Id, string Title, DateTimeOffset UpdatedAt, int NodeCount, int ConnectionCount, string? Cover);

/// <summary>Each canvas is a folder: project.json, an assets folder holding generated and imported media, and its own library folder.</summary>
public static class ProjectStore
{
    /// <summary>INFINITE_CANVAS_DATA points a test instance at a throwaway folder so it never touches real canvases.</summary>
    public static string Root { get; } = Environment.GetEnvironmentVariable("INFINITE_CANVAS_DATA") is { Length: > 0 } custom
        ? Path.GetFullPath(custom)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfiniteCanvas");
    private static string ProjectsDir => Path.Combine(Root, "projects");
    private static string FileOf(string id) => Path.Combine(ProjectsDir, id, "project.json");
    public static string AssetsDir(string id) => Path.Combine(ProjectsDir, id, "assets");
    public static string AssetPath(string id, string name) => Path.Combine(AssetsDir(id), name);
    public static string LibraryDir(string id) => Path.Combine(ProjectsDir, id, "library");

    public static List<ProjectInfo> List()
    {
        if (!Directory.Exists(ProjectsDir)) return [];
        return Directory.EnumerateDirectories(ProjectsDir)
            .Select(dir => TryLoad(Path.GetFileName(dir)))
            .OfType<CanvasDocument>()
            .Select(doc => new ProjectInfo(doc.Id, doc.Title, doc.UpdatedAt, doc.Nodes.Count, doc.Connections.Count, Cover(doc)))
            .OrderByDescending(info => info.UpdatedAt)
            .ToList();
    }

    public static CanvasDocument Create(string title = "未命名画布")
    {
        var doc = new CanvasDocument { Title = title };
        Save(doc);
        return doc;
    }

    /// <summary>The folder name is the canvas id, so a copied or renamed canvas folder opens, saves and deletes on its own.</summary>
    public static CanvasDocument? TryLoad(string id)
    {
        try
        {
            if (!File.Exists(FileOf(id))) return null;
            var json = JsonNode.Parse(File.ReadAllText(FileOf(id)))!;
            json["id"] = id;
            return json.Deserialize<CanvasDocument>(Json.Options);
        }
        catch { return null; }
    }

    public static void Save(CanvasDocument doc)
    {
        doc.UpdatedAt = DateTimeOffset.Now;
        Json.WriteFile(FileOf(doc.Id), doc);
    }

    /// <summary>Deleted canvases go to the recycle bin so they can be restored.</summary>
    public static void Delete(string id)
    {
        var dir = Path.Combine(ProjectsDir, id);
        if (Directory.Exists(dir)) FileSystem.DeleteDirectory(dir, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.DoNothing);
    }

    public static void Rename(string id, string title)
    {
        if (TryLoad(id) is not { } doc) return;
        doc.Title = title;
        Save(doc);
    }

    private static readonly object NameLock = new();

    /// <summary>Generated media and imports are numbered per canvas and kind (图片-001.png, 视频-002.mp4…); <paramref name="prefix"/> overrides the kind, e.g. 蒙版 for masks.</summary>
    public static string SaveAsset(string projectId, byte[] bytes, string mimeType, string? prefix = null) =>
        Numbered(AssetsDir(projectId), prefix ?? Media.Label(mimeType), Media.Extension(mimeType), path =>
        {
            using var file = new FileStream(path, FileMode.CreateNew);
            file.Write(bytes);
        });

    public static string ImportAsset(string projectId, string sourcePath) =>
        Numbered(AssetsDir(projectId), Media.Label(Media.MimeOf(sourcePath)), Path.GetExtension(sourcePath).ToLowerInvariant(), path => File.Copy(sourcePath, path));

    /// <summary>The next free number after the highest one in use. Imports run off the UI thread beside generations, so picking and creating the file happen under one lock, and files are only ever created new.</summary>
    public static string Numbered(string dir, string prefix, string extension, Action<string> create)
    {
        Directory.CreateDirectory(dir);
        lock (NameLock)
        {
            var next = Directory.EnumerateFiles(dir, prefix + "-*")
                .Select(path => int.TryParse(Path.GetFileNameWithoutExtension(path)[(prefix.Length + 1)..], out var n) ? n : 0)
                .DefaultIfEmpty(0).Max() + 1;
            var name = $"{prefix}-{next:000}{extension}";
            create(Path.Combine(dir, name));
            return name;
        }
    }

    /// <summary>A media node shows its file's number as its title until the user names it (the title is still the kind's default or the previous file's number).</summary>
    public static void TitleFromAsset(CanvasNode node, string? previousAsset)
    {
        if (node.Content is not { Length: > 0 } asset) return;
        if (node.Title == NodeSpec.Of(node.Kind).Title || node.Title.Length == 0 || previousAsset is not null && node.Title == Path.GetFileNameWithoutExtension(previousAsset))
            node.Title = Path.GetFileNameWithoutExtension(asset);
    }

    /// <summary>
    /// Delete asset files that no node's content or option references.
    /// Only safe once the canvas is closed: its undo history is gone and nothing still generates into it.
    /// </summary>
    public static void PruneAssets(CanvasDocument doc)
    {
        try
        {
            var dir = AssetsDir(doc.Id);
            if (!Directory.Exists(dir)) return;
            var used = doc.Nodes.SelectMany(n => n.Options.Values.Append(n.Content ?? "")).ToHashSet();
            foreach (var file in Directory.GetFiles(dir).Where(f => !used.Contains(Path.GetFileName(f))))
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Still open elsewhere: the next prune retries. */ }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Unreadable folder: keep everything. */ }
    }

    private static string? Cover(CanvasDocument doc)
    {
        var image = doc.Nodes.FirstOrDefault(node => node.Kind == NodeKind.Image && node.HasContent);
        return image is null ? null : AssetPath(doc.Id, image.Content!);
    }
}

public static class Media
{
    /// <summary>The kind word used in numbered file names and default titles.</summary>
    public static string Label(string mimeType) => mimeType.StartsWith("video/") ? "视频" : mimeType.StartsWith("audio/") ? "音频" : "图片";

    public static string Extension(string mimeType) => mimeType switch
    {
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "video/mp4" => ".mp4",
        "video/quicktime" => ".mov",
        "audio/mpeg" => ".mp3",
        "audio/wav" or "audio/x-wav" => ".wav",
        "audio/aac" => ".aac",
        "audio/flac" => ".flac",
        "audio/opus" => ".opus",
        "audio/pcm" => ".pcm",
        _ when mimeType.StartsWith("video/") => ".mp4",
        _ when mimeType.StartsWith("audio/") => ".mp3",
        _ => ".png",
    };

    public static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".aac" => "audio/aac",
        ".flac" => "audio/flac",
        ".opus" => "audio/opus",
        ".m4a" => "audio/mp4",
        _ => "image/png",
    };

    public static NodeKind? KindOf(string path) => MimeOf(path) switch
    {
        var mime when mime.StartsWith("video/") => NodeKind.Video,
        var mime when mime.StartsWith("audio/") => NodeKind.Audio,
        _ when Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp" => NodeKind.Image,
        _ => null,
    };
}
