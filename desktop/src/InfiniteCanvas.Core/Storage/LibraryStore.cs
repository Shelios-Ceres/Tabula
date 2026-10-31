using InfiniteCanvas.Core.Canvas;
using Microsoft.VisualBasic.FileIO;

namespace InfiniteCanvas.Core.Storage;

public sealed record LibraryItem(string Path, string Name, NodeKind Kind, DateTime AddedAt);

/// <summary>Reusable media of one canvas: plain files in that canvas's library folder, newest first.</summary>
public static class LibraryStore
{
    public static event Action? Changed;

    public static List<LibraryItem> List(string projectId)
    {
        var dir = ProjectStore.LibraryDir(projectId);
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

    /// <summary>Removed items go to the recycle bin; cancelling the shell dialog returns silently.</summary>
    public static void Remove(string path)
    {
        if (File.Exists(path)) FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.DoNothing);
        Changed?.Invoke();
    }
}
