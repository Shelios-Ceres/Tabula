using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Tabula.Core.Canvas;
using Microsoft.VisualBasic.FileIO;

namespace Tabula.Core.Storage;

public sealed record ProjectInfo(string Id, string Title, DateTimeOffset UpdatedAt, int NodeCount, int ConnectionCount, string? Cover);

/// <summary>Each canvas is a folder: project.json, an assets folder holding generated and imported media, and its own library folder.</summary>
public static class ProjectStore
{
    /// <summary>TABULA_DATA points a test instance at a throwaway folder so it never touches real canvases.</summary>
    public static string Root { get; } = Environment.GetEnvironmentVariable("TABULA_DATA") is { Length: > 0 } custom
        ? Path.GetFullPath(custom)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfiniteCanvas");
    private static string ProjectsDir => Path.Combine(Root, "projects");
    private static string FileOf(string id) => Path.Combine(ProjectsDir, id, "project.json");
    public static string AssetsDir(string id) => Path.Combine(ProjectsDir, id, "assets");
    public static string AssetPath(string id, string name) => Path.Combine(AssetsDir(id), name);
    public static string LibraryDir(string id) => Path.Combine(ProjectsDir, id, "library");
    /// <summary>Masks and markup layers are numbered like media (蒙版-001.png, 标注-001.png) but belong to their node's marks, not to the canvas's files.</summary>
    public const string MaskPrefix = "蒙版", MarkupPrefix = "标注";

    public static bool IsMark(string fileName) => fileName.StartsWith(MaskPrefix + "-") || fileName.StartsWith(MarkupPrefix + "-");

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
    /// Delete mask and markup files that no node references any more. Pictures, videos and audio stay after their nodes are deleted from the canvas;
    /// only deleting them from the canvas's files in the side panel removes them. Only safe once the canvas is closed: its undo history is gone.
    /// </summary>
    public static void PruneMasks(CanvasDocument doc)
    {
        try
        {
            var dir = AssetsDir(doc.Id);
            if (!Directory.Exists(dir)) return;
            var used = doc.Nodes.SelectMany(n => n.Options.Values).ToHashSet();
            foreach (var file in Directory.GetFiles(dir).Where(f => IsMark(Path.GetFileName(f)) && !used.Contains(Path.GetFileName(f))))
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Still open elsewhere: the next prune retries. */ }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Unreadable folder: keep everything. */ }
    }

    /// <summary>True only once the file is gone; a cancelled shell dialog leaves its references intact.</summary>
    public static bool Recycle(string path)
    {
        try
        {
            // Unlike File.Exists, this does not mistake an access error for an absent file.
            File.GetAttributes(path);
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return true; }
        catch (OperationCanceledException) { return false; }
    }

    // SHFileOperation as in shellapi.h (the app is x64 / ARM64, where the default packing is the native one).
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOp
    {
        public nint Hwnd;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public nint NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int ShFileOperation(ref ShFileOp op);

    /// <summary>
    /// Move one file to the Recycle Bin without any dialog, for code that must never wait on one (agent tools): progress, confirmation and error UI are all off.
    /// Returns false and leaves the file where it is when the shell cannot do it. Only files on fixed local drives qualify: removable and network drives have no
    /// Recycle Bin, and asking the shell to delete there would destroy the file for good. FOF_WANTNUKEWARNING also keeps a file the bin cannot take (too large,
    /// or the bin switched off for the drive) from being deleted silently; the shell then asks, which is the rare case where this is not dialog-free.
    /// </summary>
    public static bool RecycleSilently(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) return true;
            if (new DriveInfo(full[..1]).DriveType != DriveType.Fixed) return false;
            // ALLOWUNDO | NOCONFIRMATION | SILENT | NOERRORUI | WANTNUKEWARNING
            var op = new ShFileOp { Func = 3, From = full + "\0", Flags = 0x0040 | 0x0010 | 0x0004 | 0x0400 | 0x4000 };
            return ShFileOperation(ref op) == 0 && op.AnyOperationsAborted == 0 && !File.Exists(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
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
        "image/svg+xml" => ".svg",
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
        ".svg" => "image/svg+xml",
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
        _ when Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".svg" or ".bmp" => NodeKind.Image,
        _ => null,
    };

    /// <summary>Windows device names (CON, NUL, COM1…): a file cannot be named after one, with or without an extension.</summary>
    public static bool IsReservedName(string stem)
    {
        var name = stem.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" || name.Length == 4 && (name.StartsWith("COM") || name.StartsWith("LPT")) && name[3] is >= '1' and <= '9';
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';

    /// <summary>
    /// Why a path an agent gave cannot be imported, or null: a drive-letter path on a local drive (every path that starts with a separator is refused,
    /// whatever mix of \ and / it uses: UNC, device and \??\ forms) to an existing file with a supported media extension, with no alternate data stream,
    /// and not a link that leads to a network path. There is no size limit, as with dragging a file in.
    /// </summary>
    public static string? ImportProblem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "路径为空";
        if (IsSeparator(path[0])) return "不接受网络（UNC）路径和设备路径";
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || !IsSeparator(path[2])) return "必须是带盘符的绝对路径";
        if (path.IndexOf(':', 2) >= 0 || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return "路径含有非法字符";
        // Normalising can still change what the path means (. and .. segments), so the result is checked again.
        var full = Path.GetFullPath(path);
        if (full.Length < 3 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || !IsSeparator(full[2])) return "不接受网络（UNC）路径和设备路径";
        if (KindOf(full) is null) return "不支持的文件类型，可用 png、jpg、jpeg、webp、gif、svg、bmp、mp4、mov、webm、mp3、wav、aac、flac、opus、m4a";
        if (IsReservedName(Path.GetFileNameWithoutExtension(full))) return "不能使用设备名作为文件名";
        if (!File.Exists(full)) return "文件不存在";
        try
        {
            if (new DriveInfo(full[..1]).DriveType == DriveType.Network) return "不接受网络驱动器上的文件";
            var target = new FileInfo(full).ResolveLinkTarget(true)?.FullName;
            if (target is not null && (target.Length < 3 || !char.IsAsciiLetter(target[0]) || target[1] != ':' || new DriveInfo(target[..1]).DriveType == DriveType.Network)) return "链接指向了网络或设备路径";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return "无法确认文件位置：" + ex.Message; }
        return null;
    }

    /// <summary>Vector pictures show and export as they are, but pixel tools and raster-only vendors need them drawn first.</summary>
    public static bool IsSvg(string? path) => path is not null && Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pixel size an SVG declares: its width / height in px, else its viewBox; (0, 0) when it is not SVG or says neither.</summary>
    public static (int Width, int Height) SvgSize(byte[] bytes)
    {
        try
        {
            // Files come from vendors and imports: DTDs (and with them entity expansion) are ignored.
            using var reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            var root = XDocument.Load(reader).Root;
            if (root?.Name.LocalName != "svg") return (0, 0);
            static double Px(string? value) => value is not null && double.TryParse(value.Trim().Replace("px", ""), CultureInfo.InvariantCulture, out var number) ? number : 0;
            var (width, height) = (Px((string?)root.Attribute("width")), Px((string?)root.Attribute("height")));
            if ((width <= 0 || height <= 0) && ((string?)root.Attribute("viewBox"))?.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries) is { Length: 4 } box)
                (width, height) = (Px(box[2]), Px(box[3]));
            return width > 0 && height > 0 ? ((int)Math.Round(width), (int)Math.Round(height)) : (0, 0);
        }
        catch (XmlException) { return (0, 0); }
    }
}
