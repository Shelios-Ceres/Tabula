using System.Security.Cryptography;
using System.Text;
using InfiniteCanvas.Core.Storage;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace InfiniteCanvas.App;

public partial class App : Application
{
    public static MainWindow Window { get; private set; } = null!;

    /// <summary>One client for every provider. No client-side timeout, like browser fetch: long generations end on the server or when the user stops them.</summary>
    public static HttpClient Http { get; } = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static string CrashLog => Path.Combine(ProjectStore.Root, "crash.log");

    private static readonly object LogLock = new();
    private static bool _launched;

    public App()
    {
        InitializeComponent();
        // Once the window is up, a failed click is logged instead of taking down the canvas and running generations;
        // startup failures still crash so no windowless process is left behind.
        UnhandledException += (_, e) => { e.Handled = _launched; Log(e.Exception); };
        // Faulted fire-and-forget tasks (_ = XxxAsync()) are otherwise dropped silently.
        TaskScheduler.UnobservedTaskException += (_, e) => Log(e.Exception);
    }

    public static void Log(Exception exception)
    {
        lock (LogLock)
        {
            try
            {
                Directory.CreateDirectory(ProjectStore.Root);
                File.AppendAllText(CrashLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}\n\n");
            }
            catch { /* Logging must never crash the app, especially on the finalizer thread. */ }
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // One process per data folder: each keeps canvases and settings in memory and rewrites whole files, so a second process would overwrite the first.
            // The key comes from the data root, so a test instance with its own INFINITE_CANVAS_DATA still runs beside the real one.
            var root = Path.GetFullPath(ProjectStore.Root).TrimEnd('\\', '/').ToLowerInvariant();
            var main = AppInstance.FindOrRegisterForKey("InfiniteCanvas-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..16]);
            if (!main.IsCurrent)
            {
                await main.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
                Exit();
                return;
            }
            Window = new MainWindow();
            main.Activated += (_, _) => Window.DispatcherQueue.TryEnqueue(() =>
            {
                // Restore only when minimized so a maximized window stays maximized.
                if (Window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
                Window.Activate();
            });
            Window.Start();
            Window.Activate();
            _launched = true;
            await Board.SharedCanvasHost.ApplyAsync(Core.Settings.SettingsStore.Current.ExternalAgents);
        }
        catch (Exception ex)
        {
            Log(ex);
            throw;
        }
    }
}
