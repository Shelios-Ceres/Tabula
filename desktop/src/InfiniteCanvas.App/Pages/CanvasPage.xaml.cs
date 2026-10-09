using System.Numerics;
using InfiniteCanvas.App.Board;
using InfiniteCanvas.Core.Canvas;
using InfiniteCanvas.Core.Settings;
using InfiniteCanvas.Core.Storage;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace InfiniteCanvas.App.Pages;

public sealed partial class CanvasPage : Page
{
    private CanvasDocument _doc = new();
    private DispatcherQueueTimer? _saveTimer;
    private bool _dirty, _confirmingClose;

    public CanvasPage()
    {
        InitializeComponent();
        // Glass capsules float above the canvas with a soft system shadow cast onto it.
        var shadow = new ThemeShadow();
        shadow.Receivers.Add(Board);
        foreach (var capsule in new UIElement[] { TitleCapsule, ToolCapsule, ZoomCapsule })
        {
            capsule.Shadow = shadow;
            capsule.Translation = new Vector3(0, 0, 24);
        }
        Library.Target = Board;
        Library.CastShadowOn(Board);
        LibraryToggle.IsChecked = SettingsStore.Current.LibraryOpen;
        Library.Visibility = SettingsStore.Current.LibraryOpen ? Visibility.Visible : Visibility.Collapsed;
        Map.Attach(Board);
        MiniMapToggle.IsChecked = SettingsStore.Current.MiniMapOpen;
        Map.Visibility = SettingsStore.Current.MiniMapOpen ? Visibility.Visible : Visibility.Collapsed;
        Agent.Attach(Board);
        Agent.CastShadowOn(Board);
        AgentToggle.IsChecked = SettingsStore.Current.AgentOpen;
        Agent.Visibility = SettingsStore.Current.AgentOpen ? Visibility.Visible : Visibility.Collapsed;
        // Side panels and the map slide out of their own edge when toggled.
        Motion.ShowHide(Library, 0, 0.5f, 0.97f);
        Motion.ShowHide(Agent, 1, 0.5f, 0.97f);
        Motion.ShowHide(Map, 0, 1, 0.94f);
        QuotaButton.Flyout = new QuotaFlyout();
        TitleCapsule.SizeChanged += (_, _) => KeepToolsClear();
        ToolCapsule.SizeChanged += (_, _) => KeepToolsClear();
        ApplySafeArea();
    }

    private void LibraryToggle_Click(object sender, RoutedEventArgs e)
    {
        var open = LibraryToggle.IsChecked == true;
        Library.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SettingsStore.Current.LibraryOpen = open;
        SettingsStore.Save();
        ApplySafeArea();
    }

    private void AgentToggle_Click(object sender, RoutedEventArgs e)
    {
        var open = AgentToggle.IsChecked == true;
        Agent.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SettingsStore.Current.AgentOpen = open;
        SettingsStore.Save();
        ApplySafeArea();
    }

    private void MiniMapToggle_Click(object sender, RoutedEventArgs e)
    {
        var open = MiniMapToggle.IsChecked == true;
        Map.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        Map.Refresh();
        SettingsStore.Current.MiniMapOpen = open;
        SettingsStore.Save();
    }

    /// <summary>Tells the canvas which screen margins the side panels, top capsules and zoom capsule cover; the map sits beside the library.</summary>
    private void ApplySafeArea()
    {
        var library = Library.Visibility == Visibility.Visible ? Library.Margin.Left + Library.Width + 12 : 0;
        Map.Margin = new Thickness(Math.Max(16, library), 0, 0, 16);
        Board.SafeArea = new Thickness(library, 68, Agent.Visibility == Visibility.Visible ? Agent.Margin.Right + Agent.Width + 12 : 0, 72);
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Narrow windows collapse the toolbars to icons; every button keeps its tooltip and automation name.
        var compact = e.NewSize.Width < 1440;
        foreach (var label in new[] { TextLabel, ImageLabel, VideoLabel, AudioLabel, ConfigLabel, LibraryLabel, AgentLabel, QuotaLabel }) label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        TitleText.MaxWidth = compact ? 160 : 200;
        KeepToolsClear();
    }

    /// <summary>The tool capsule stays centred but slides right instead of covering the title capsule when the window is narrow or the title is long.</summary>
    private void KeepToolsClear()
    {
        var overlap = TitleCapsule.Margin.Left + TitleCapsule.ActualWidth + 12 - (ActualWidth - ToolCapsule.ActualWidth) / 2;
        ToolCapsule.Margin = new Thickness(Math.Max(0, overlap) * 2, 12, 0, 0);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _doc = (e.Parameter as string is { } id ? ProjectStore.TryLoad(id) : null) ?? ProjectStore.Create();
        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(800);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => SaveNow();
        Board.Load(_doc);
        SharedCanvasHost.Open = new CanvasAgentHost(Board);
        // Subscribe after loading so merely opening a canvas does not rewrite it.
        Board.Changed += Board_Changed;
        Board.ViewportChanged += UpdateZoomText;
        App.Window.Closed += Window_Closed;
        App.Window.AppWindow.Closing += Window_Closing;
        App.Window.SetCanvasChrome(true);
        TitleText.Text = _doc.Title;
        App.Window.SetCaption(_doc.Title);
        SaveState.Text = "已保存";
        UpdateUndo();
        UpdateZoomText();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        App.Window.Closed -= Window_Closed;
        App.Window.AppWindow.Closing -= Window_Closing;
        Board.Changed -= Board_Changed;
        Board.ViewportChanged -= UpdateZoomText;
        App.Window.SetCanvasChrome(false);
        Leave();
    }

    private void Window_Closed(object sender, WindowEventArgs args) => Leave();

    private async void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (Board.Generator.RunningCount == 0) return;
        args.Cancel = true;
        // Repeated close clicks while the dialog is open must not queue more copies; a minimized window shows it only once restored.
        if (_confirmingClose) return;
        _confirmingClose = true;
        if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        try { if (!await Board.ConfirmLeaveAsync()) return; }
        finally { _confirmingClose = false; }
        sender.Closing -= Window_Closing;
        App.Window.Close();
    }

    /// <summary>Stop generations and save; then, only when nothing can still write a result, drop asset files no node uses — the undo history that could bring them back ends here.</summary>
    private void Leave()
    {
        _saveTimer?.Stop();
        // Ends the agent process tree synchronously before the first await, so no agent outlives the canvas or the app.
        _ = Agent.ShutdownAsync();
        SharedCanvasHost.Open = null;
        Board.Generator.Shutdown();
        if (SaveNow() && Board.Generator.RunningCount == 0 && _doc.Nodes.All(n => n.TaskId is null)) ProjectStore.PruneAssets(_doc);
    }

    private void Board_Changed()
    {
        _dirty = true;
        SaveState.Text = "未保存";
        _saveTimer?.Stop();
        _saveTimer?.Start();
        UpdateUndo();
    }

    private bool SaveNow()
    {
        if (!_dirty) return true;
        try
        {
            ProjectStore.Save(_doc);
            _dirty = false;
            SaveState.Text = "已保存";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Disk full or file locked: keep _dirty so the next edit retries; never log to the same failing disk.
            SaveState.Text = "保存失败";
            return false;
        }
    }

    private void UpdateUndo()
    {
        UndoButton.IsEnabled = Board.History?.CanUndo == true;
        RedoButton.IsEnabled = Board.History?.CanRedo == true;
    }

    private void UpdateZoomText() => ZoomText.Text = $"{Math.Round(Board.Zoom * 100)}%";

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (!await Board.ConfirmLeaveAsync()) return;
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(ProjectsPage));
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (await Dialogs.InputAsync("重命名画布", _doc.Title) is not { Length: > 0 } title) return;
        _doc.Title = title;
        TitleText.Text = title;
        App.Window.SetCaption(title);
        Board_Changed();
    }

    private void AddText_Click(object sender, RoutedEventArgs e) => Board.AddNodeAtCenter(NodeKind.Text);
    private void AddImage_Click(object sender, RoutedEventArgs e) => Board.AddNodeAtCenter(NodeKind.Image);
    private void AddVideo_Click(object sender, RoutedEventArgs e) => Board.AddNodeAtCenter(NodeKind.Video);
    private void AddAudio_Click(object sender, RoutedEventArgs e) => Board.AddNodeAtCenter(NodeKind.Audio);
    private void AddConfig_Click(object sender, RoutedEventArgs e) => Board.AddNodeAtCenter(NodeKind.Config);
    private async void Import_Click(object sender, RoutedEventArgs e) => await Board.ImportWithPickerAsync();
    private void Undo_Click(object sender, RoutedEventArgs e) { Board.Undo(); UpdateUndo(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { Board.Redo(); UpdateUndo(); }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Board.ZoomBy(1.2);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Board.ZoomBy(1 / 1.2);
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => Board.ResetZoom();
    private void Fit_Click(object sender, RoutedEventArgs e) => Board.FitToContent();
}
