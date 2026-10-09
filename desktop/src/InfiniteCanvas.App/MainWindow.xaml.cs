using InfiniteCanvas.App.Pages;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InfiniteCanvas.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        // Taskbar and Alt+Tab icon; the exe itself carries the same icon for Explorer and shortcuts.
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        // Keep the title clear of the caption buttons; RightInset is in physical pixels.
        AppTitleBar.Loaded += (_, _) => AppTitleBar.Padding = new(16, 0, AppWindow.TitleBar.RightInset / AppTitleBar.XamlRoot.RasterizationScale, 0);
        // AppWindow sizes are physical pixels, so size the restored window from the work area instead of a fixed number.
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new((int)(area.Width * 0.8), (int)(area.Height * 0.85)));
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        // Minimum size is 1024 x 640 DIP, recomputed when the scale changes and never larger than the current work area.
        void ApplyMinSize()
        {
            if (AppWindow.Presenter is not OverlappedPresenter overlapped) return;
            var scale = RootFrame.XamlRoot.RasterizationScale;
            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            overlapped.PreferredMinimumWidth = Math.Min((int)(1024 * scale), work.Width);
            overlapped.PreferredMinimumHeight = Math.Min((int)(640 * scale), work.Height);
        }
        RootFrame.Loaded += (_, _) =>
        {
            ApplyMinSize();
            RootFrame.XamlRoot.Changed += (_, _) => ApplyMinSize();
        };
    }

    public void Start() => RootFrame.Navigate(typeof(ProjectsPage));

    public Frame Frame => RootFrame;

    public void SetCaption(string? caption) => TitleText.Text = string.IsNullOrEmpty(caption) ? "无限画布" : $"{caption} · 无限画布";

    /// <summary>Canvas pages show the canvas colour under the title bar.</summary>
    public void SetCanvasChrome(bool on) => TitleBackdrop.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Dialogs need the window's XamlRoot.</summary>
    public XamlRoot XamlRoot => Content.XamlRoot;
}
