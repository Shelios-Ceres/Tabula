using InfiniteCanvas.Core.Settings;
using InfiniteCanvas.Core.Storage;
using Microsoft.Web.WebView2.Core;

namespace InfiniteCanvas.App.Board;

/// <summary>
/// Waiting animations for image generations: HTML pages in the data folder's loaders/, served to a WebView2 through a virtual host,
/// one picked at random per run. The client's own originals (Assets/Loaders) are copied there the first time the folder is missing;
/// after that the folder is the only source, so deleting a page retires it and an empty folder brings back the progress ring.
/// Pages in subfolders are kept but never picked.
/// </summary>
internal static class Loaders
{
    public const string Host = "loaders.infinite-canvas.example";
    public static string Folder => Path.Combine(ProjectStore.Root, "loaders");
    private static string Builtin => Path.Combine(AppContext.BaseDirectory, "Assets", "Loaders");

    /// <summary>Set once WebView2 fails to start (no runtime), so cards fall back to the progress ring.</summary>
    public static bool Unavailable { get; set; }

    /// <summary>Both opening the folder and playing a page share first-use setup; an existing empty folder stays empty.</summary>
    public static void EnsureInitialized()
    {
        if (Directory.Exists(Folder)) return;
        Directory.CreateDirectory(Folder);
        if (Directory.Exists(Builtin)) foreach (var page in Directory.GetFiles(Builtin, "*.html")) File.Copy(page, Path.Combine(Folder, Path.GetFileName(page)));
    }

    public static string? Pick()
    {
        if (Unavailable || !SettingsStore.Current.WaitingAnimations) return null;
        EnsureInitialized();
        var pages = Directory.GetFiles(Folder, "*.html");
        return pages.Length == 0 ? null : Path.GetFileName(pages[Random.Shared.Next(pages.Length)]);
    }

    private static Task<CoreWebView2Environment>? _environment;

    /// <summary>One browser environment for every card, with its profile in the data folder rather than next to the exe.</summary>
    public static Task<CoreWebView2Environment> Environment =>
        _environment ??= CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(ProjectStore.Root, "webview2"), new CoreWebView2EnvironmentOptions()).AsTask();
}
