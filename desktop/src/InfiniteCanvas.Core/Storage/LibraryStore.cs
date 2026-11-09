using InfiniteCanvas.Core.Canvas;

namespace InfiniteCanvas.Core.Storage;

public sealed record LibraryItem(string Path, string Name, NodeKind Kind, DateTime AddedAt);

/// <summary>Reusable media of one canvas: plain files in that canvas's library folder, newest first.</summary>
public static class LibraryStore
{
    public static event Action? Changed;

    public static List<LibraryItem> List(string projectId) => Files(ProjectStore.LibraryDir(projectId));

    /// <summary>Pictures, videos and audio in a folder, newest first.</summary>
    public static List<LibraryItem> Files(string dir)
    {
        if (!Directory.Exists(dir)) return [];
        return new DirectoryInfo(dir).EnumerateFiles()
            .Where(file => Media.KindOf(file.Name) is not null)
            .OrderByDescending(file => file.CreationTime)
            .Select(file => new LibraryItem(file.FullName, System.IO.Path.GetFileNameWithoutExtension(file.Name), Media.KindOf(file.Name)!.Value, file.CreationTime))
            .ToList();
    }

    /// <summary>
    /// Copy files in on the thread pool, keeping their names; a clash gets a numeric suffix instead of overwriting.
    /// A locked or unreadable file is skipped. Changed fires back on the caller's context.
    /// </summary>
    public static async Task<int> AddAsync(string projectId, IReadOnlyList<string> paths)
    {
        var dir = ProjectStore.LibraryDir(projectId);
        var added = await Task.Run(() =>
        {
            Directory.CreateDirectory(dir);
            var count = 0;
            foreach (var path in paths.Where(p => Media.KindOf(p) is not null && File.Exists(p)))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(path);
                var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                var target = System.IO.Path.Combine(dir, name + ext);
                for (var i = 2; File.Exists(target); i++) target = System.IO.Path.Combine(dir, $"{name} ({i}){ext}");
                try { File.Copy(path, target); count++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Locked or unreadable source: skip it. */ }
            }
            return count;
        });
        if (added > 0) Changed?.Invoke();
        return added;
    }

    public static void Remove(string path)
    {
        ProjectStore.Recycle(path);
        Changed?.Invoke();
    }

    // ---------- By name, for agents ----------
    // A library file is only ever reached by looking its name up among the files of this canvas's library folder, so no name can leave it.

    /// <summary>How a file is recycled when the caller passes no function: silently through the shell. Tests point it elsewhere so nothing lands in the machine's Recycle Bin.</summary>
    public static Func<string, bool> Recycle { get; set; } = ProjectStore.RecycleSilently;

    /// <summary>A file name (with or without extension, any case) among this canvas's library files; null when there is none.</summary>
    public static LibraryItem? Find(string projectId, string name)
    {
        var items = List(projectId);
        return items.FirstOrDefault(i => System.IO.Path.GetFileName(i.Path).Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? items.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether another program holds the file open: recycling it then would raise a shell error dialog.</summary>
    private static bool InUse(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// Copy one file in under its own file name. A name already in the library is an error, or with <paramref name="replace"/> the old file goes
    /// to the recycle bin and the new one takes its name. The copy is made first, so a failure (the copy, or the recycling) leaves the old file in place.
    /// Never shows a dialog; <paramref name="recycle"/> is how a file is recycled (silently by default), replaceable by tests. Returns the file name.
    /// </summary>
    public static async Task<string> PutAsync(string projectId, string sourcePath, bool replace, Func<string, bool>? recycle = null)
    {
        recycle ??= Recycle;
        var dir = ProjectStore.LibraryDir(projectId);
        var name = await Task.Run(() =>
        {
            Directory.CreateDirectory(dir);
            var name = System.IO.Path.GetFileNameWithoutExtension(sourcePath) + System.IO.Path.GetExtension(sourcePath).ToLowerInvariant();
            var target = System.IO.Path.Combine(dir, name);
            try
            {
                if (!File.Exists(target)) File.Copy(sourcePath, target);
                else if (!replace) throw new LibraryException($"资料库里已经有「{name}」，要覆盖请用 replace=true（旧文件会移到回收站），或先给节点改名");
                else
                {
                    if (InUse(target)) throw new LibraryException($"「{name}」正被其他程序占用，不能覆盖");
                    var staged = System.IO.Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
                    File.Copy(sourcePath, staged);
                    bool recycled;
                    try { recycled = recycle(target); }
                    catch { File.Delete(staged); throw; }
                    if (!recycled)
                    {
                        File.Delete(staged);
                        throw new LibraryException($"没能把旧的「{name}」移到回收站，已取消覆盖");
                    }
                    File.Move(staged, target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new LibraryException($"「{name}」写入资料库失败：{ex.Message}"); }
            return name;
        });
        Changed?.Invoke();
        return name;
    }

    /// <summary>Change a library file's name but not its extension; <paramref name="newName"/> may repeat the extension. Returns the new file name.</summary>
    public static string Rename(string projectId, string name, string newName)
    {
        var item = Find(projectId, name) ?? throw new LibraryException($"资料库里没有「{name}」，请先用 library_list 查看文件名");
        var extension = System.IO.Path.GetExtension(item.Path);
        var stem = newName;
        if (System.IO.Path.GetExtension(newName).Equals(extension, StringComparison.OrdinalIgnoreCase)) stem = newName[..^extension.Length];
        else if (Media.KindOf(newName) is not null) throw new LibraryException("只能改文件名，不能改扩展名");
        if (stem.Length == 0 || stem != stem.Trim() || stem.EndsWith('.') || stem.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) throw new LibraryException($"「{newName}」不是合法的文件名：不能为空、不能以点或空格结尾，不能含有 \\ / : * ? \" < > | 等字符");
        if (Media.IsReservedName(stem)) throw new LibraryException($"「{stem}」是 Windows 的设备名，不能用作文件名");
        var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(item.Path)!, stem + extension);
        // A change of letter case alone is the same file on Windows, not a clash.
        if (!target.Equals(item.Path, StringComparison.OrdinalIgnoreCase) && File.Exists(target)) throw new LibraryException($"资料库里已经有「{stem + extension}」");
        try { File.Move(item.Path, target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new LibraryException($"改名失败：{ex.Message}"); }
        Changed?.Invoke();
        return stem + extension;
    }

    /// <summary>Move library files to the recycle bin by name, silently. Each name that cannot be removed is reported with its reason and its file stays; the rest are still removed.</summary>
    public static (List<string> Removed, List<(string Name, string Reason)> Failed) RemoveNames(string projectId, IEnumerable<string> names, Func<string, bool>? recycle = null)
    {
        recycle ??= Recycle;
        var removed = new List<string>();
        var failed = new List<(string, string)>();
        foreach (var name in names)
        {
            if (Find(projectId, name) is not { } item) failed.Add((name, "资料库里没有这个文件"));
            else if (InUse(item.Path)) failed.Add((name, "文件正被其他程序占用"));
            else if (recycle(item.Path)) removed.Add(System.IO.Path.GetFileName(item.Path));
            else failed.Add((name, "没能移到回收站"));
        }
        if (removed.Count > 0) Changed?.Invoke();
        return (removed, failed);
    }
}

/// <summary>A library request that cannot be done, with a message the agent can act on.</summary>
public sealed class LibraryException(string message) : Exception(message);
