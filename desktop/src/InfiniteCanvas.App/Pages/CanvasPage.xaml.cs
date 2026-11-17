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
    private bool _dirty, _confirmingClose, _preparedToLeave;

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
        TitleHost.SizeChanged += (_, _) => KeepToolsClear();
        ToolHost.SizeChanged += (_, _) => KeepToolsClear();
        // Liquid glass experiment: scaled glass panels, the tuning panel and its shortcut.
        GlassScale.Wrap(Library);
        GlassScale.Wrap(Agent);
        Motion.ShowHide(GlassPanel, 1, 0.5f, 0.97f);
        GlassPanel.CloseRequested += () => ShowGlassPanel(false);
        var accelerator = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.L, Modifiers = Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift };
        accelerator.Invoked += (_, e) => { e.Handled = true; ShowGlassPanel(GlassPanel.Visibility != Visibility.Visible); };
        KeyboardAccelerators.Add(accelerator);
        KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        GlassSettings.Changed += GlassSettings_Changed;
        Unloaded += (_, _) => GlassSettings.Changed -= GlassSettings_Changed;
        Loaded += (_, _) => ApplyGlassTheme();
        ApplyGlassScale();
    }

    private void GlassSettings_Changed(bool scaleChanged)
    {
        if (scaleChanged) ApplyGlassScale();
        ApplyGlassTheme();
    }

    /// <summary>The tuning panel's theme switch applies to the whole window, so the canvas, the backdrop and the glass change together.</summary>
    private static void ApplyGlassTheme()
    {
        if (App.Window.Content is not FrameworkElement root) return;
        var theme = GlassSettings.Current.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        if (root.RequestedTheme != theme) root.RequestedTheme = theme;
    }

    /// <summary>Panel widths and the margins below the top capsules follow the glass UI scale.</summary>
    private void ApplyGlassScale()
    {
        var s = GlassSettings.Current.UiScale;
        Library.Width = 300 * s;
        Agent.Width = 380 * s;
        var top = 12 + 60 * s;
        Library.Margin = new Thickness(16, top, 0, 16);
        Agent.Margin = new Thickness(0, top, 16, 16 + 56 * s);
        GlassPanel.Margin = new Thickness(0, top, 16, 16 + 56 * s);
        ApplySafeArea();
        if (ActualWidth > 0) Page_SizeChanged(this, null);
    }

    private void GlassToggle_Click(object sender, RoutedEventArgs e) => ShowGlassPanel(GlassToggle.IsChecked == true);

    private void ShowGlassPanel(bool open)
    {
        GlassPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        GlassToggle.IsChecked = open;
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
        var s = GlassSettings.Current.UiScale;
        Board.SafeArea = new Thickness(library, 12 + 56 * s, Agent.Visibility == Visibility.Visible ? Agent.Margin.Right + Agent.Width + 12 : 0, 16 + 56 * s);
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs? e)
    {
        // Narrow windows collapse the toolbars to icons; every button keeps its tooltip and automation name.
        // The labelled toolbars need about 1250 DIPs at scale 1 (the old 1440 left slack); they grow with the glass UI scale.
        var compact = ActualWidth < 1250 * GlassSettings.Current.UiScale;
        foreach (var label in new[] { TextLabel, ImageLabel, VideoLabel, AudioLabel, ConfigLabel, LibraryLabel, AgentLabel, QuotaLabel }) label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        TitleText.MaxWidth = compact ? 160 : 200;
        KeepToolsClear();
    }

    /// <summary>The tool capsule stays centred but slides right instead of covering the title capsule when the window is narrow or the title is long.</summary>
    private void KeepToolsClear()
    {
        var overlap = TitleHost.Margin.Left + TitleHost.ActualWidth + 12 - (ActualWidth - ToolHost.ActualWidth) / 2;
        ToolHost.Margin = new Thickness(Math.Max(0, overlap) * 2, 12, 0, 0);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _doc = (e.Parameter as string is { } id ? ProjectStore.TryLoad(id) : null) ?? ProjectStore.Create();
        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(800);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => SaveNow();
        Board.Load(_doc);
        Library.Refresh();
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
        if (_preparedToLeave) return;
        args.Cancel = true;
        // Repeated close clicks while the dialog is open must not queue more copies; a minimized window shows it only once restored.
        if (_confirmingClose) return;
        if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        if (await PrepareLeaveAsync()) App.Window.Close();
    }

    /// <summary>Keep the page and its save subscription alive until all writers settle and the final save succeeds.</summary>
    public async Task<bool> PrepareLeaveAsync()
    {
        if (_preparedToLeave) return true;
        if (_confirmingClose) return false;
        _confirmingClose = true;
        try
        {
            if (!SaveNow()) { await ShowSaveFailureAsync(); return false; }
            if (!await Board.ConfirmLeaveAsync()) return false;
            IsEnabled = false;
            _saveTimer?.Stop();
            SaveState.Text = "正在保存并结束当前操作…";
            SharedCanvasHost.Open = null;
            await Task.WhenAll(Board.PrepareLeaveAsync(), Agent.ShutdownAsync());
            if (!SaveNow()) { await ShowSaveFailureAsync(); return false; }
            _preparedToLeave = true;
            return true;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            await Dialogs.MessageAsync("暂时无法离开画布", ex.Message);
            return false;
        }
        finally
        {
            if (!_preparedToLeave)
            {
                IsEnabled = true;
                if (Board.IsClosing)
                {
                    Board.ResumeAfterCancelledLeave();
                    Agent.ResumeAfterCancelledLeave();
                }
                SharedCanvasHost.Open = new CanvasAgentHost(Board);
            }
            _confirmingClose = false;
        }
    }

    private static Task ShowSaveFailureAsync() => Dialogs.MessageAsync("画布尚未保存", "保存失败，画布仍为你保留。请检查磁盘空间和文件权限，然后点击保存状态重试；保存成功后再离开。");

    /// <summary>Only prune masks after the final save and all pending writers, including imports, have finished.</summary>
    private void Leave()
    {
        _saveTimer?.Stop();
        SharedCanvasHost.Open = null;
        if (_preparedToLeave && _doc.Nodes.All(n => n.TaskId is null)) ProjectStore.PruneMasks(_doc);
    }

    private void Board_Changed()
    {
        _dirty = true;
        SaveState.Text = "未保存";
        _saveTimer?.Stop();
        if (!Board.IsClosing) _saveTimer?.Start();
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
        if (!await PrepareLeaveAsync()) return;
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(ProjectsPage));
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveNow()) await ShowSaveFailureAsync();
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
