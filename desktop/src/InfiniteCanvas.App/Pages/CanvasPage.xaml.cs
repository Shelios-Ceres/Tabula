using System.Diagnostics;
using System.Numerics;
using InfiniteCanvas.App.Board;
using InfiniteCanvas.Core.Canvas;
using InfiniteCanvas.Core.Models;
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
    private Flyout _help = null!;
    private DispatcherQueueTimer? _toastTimer;
    private int _toastVersion;
    /// <summary>How long the delete toast stays: a UI timing, not a limit on anything.</summary>
    private static readonly TimeSpan ToastTime = TimeSpan.FromSeconds(6);

    public CanvasPage()
    {
        InitializeComponent();
        // Glass capsules float above the canvas with a soft system shadow cast onto it.
        var shadow = new ThemeShadow();
        shadow.Receivers.Add(Board);
        foreach (var capsule in new UIElement[] { TitleCapsule, RightCapsule, StatusCapsule, ToastCapsule })
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
        Motion.ShowHide(Map, 1, 1, 0.94f);
        var quota = new QuotaFlyout();
        quota.Opening += (_, _) => UpdateSpend();
        QuotaButton.Flyout = quota;
        BuildMainMenu();
        _help = BuildHelp();
        HelpButton.Flyout = _help;
        Palette.Attach(Board.Context, RunCommandAsync);
        Palette.Closed += Board.FocusCanvas;
        Palette.CastShadowOn(Board);
        Motion.ShowHide(ToastHost, 0.5f, 1, 0.97f);
        Board.NodesDeleted += Board_NodesDeleted;
        Board.ComposerMoved += () => PlaceMap();
        // The composer's 添加 → 从资料库选…: show the library panel. The composer sits above the status bar when they would meet, so it needs its width.
        Board.LibraryRequested += () => { if (Library.Visibility != Visibility.Visible) { LibraryToggle.IsChecked = true; LibraryToggle_Click(LibraryToggle, new RoutedEventArgs()); } };
        StatusHost.SizeChanged += (_, _) => ApplySafeArea();
        Board.Changed += HideToastWhenStale;
        // Liquid glass experiment: scaled glass panels, the tuning panel and its shortcut.
        GlassScale.Wrap(Library);
        GlassScale.Wrap(Agent);
        Motion.ShowHide(GlassPanel, 1, 0.5f, 0.97f);
        GlassPanel.CloseRequested += () => ShowGlassPanel(false);
        AddAccelerator(Windows.System.VirtualKey.L, Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift, () => ShowGlassPanel(GlassPanel.Visibility != Visibility.Visible));
        AddAccelerator(Windows.System.VirtualKey.K, Windows.System.VirtualKeyModifiers.Control, Palette.Open);
        AddAccelerator(Windows.System.VirtualKey.F11, Windows.System.VirtualKeyModifiers.None, () => SetFullScreen(App.Window.AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen));
        KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        Board.Changed += UpdateUndo;
        Board.Changed += UpdateCount;
        Board.SelectionChanged += UpdateCount;
        // Each node's status change raises Changed, so a batch's count follows its pictures one by one, not only when the whole run starts or ends.
        Board.Changed += UpdateRunning;
        Board.RunningChanged += UpdateRunning;
        Board.RunningChanged += UpdateSpend;
        GlassSettings.Changed += GlassSettings_Changed;
        Unloaded += (_, _) => GlassSettings.Changed -= GlassSettings_Changed;
        ApplyGlassScale();
    }

    private void AddAccelerator(Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) => { e.Handled = true; action(); };
        KeyboardAccelerators.Add(accelerator);
    }

    private void GlassSettings_Changed(bool scaleChanged)
    {
        if (scaleChanged) ApplyGlassScale();
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

    private void ShowGlassPanel(bool open)
    {
        // The right dock holds one panel at a time.
        if (open && Agent.Visibility == Visibility.Visible) SetAgentOpen(false);
        GlassPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
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

    private void AgentToggle_Click(object sender, RoutedEventArgs e) => SetAgentOpen(AgentToggle.IsChecked == true);

    private void SetAgentOpen(bool open)
    {
        if (open && GlassPanel.Visibility == Visibility.Visible) GlassPanel.Visibility = Visibility.Collapsed;
        AgentToggle.IsChecked = open;
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

    /// <summary>The panel in the right dock, if one is open.</summary>
    private FrameworkElement? RightDock => Agent.Visibility == Visibility.Visible ? Agent : GlassPanel.Visibility == Visibility.Visible ? GlassPanel : null;

    /// <summary>Tells the canvas which screen margins the side panels, top capsules and status bar cover; the map sits above the status bar, beside the right dock.</summary>
    private void ApplySafeArea()
    {
        var s = GlassSettings.Current.UiScale;
        var library = Library.Visibility == Visibility.Visible ? Library.Margin.Left + Library.Width + 12 : 0;
        var dock = RightDock is { } panel ? panel.Margin.Right + panel.Width + 12 : 0;
        PlaceMap(dock);
        Board.StatusWidth = StatusHost.ActualWidth + 16;
        Board.SafeArea = new Thickness(library, 12 + 56 * s, dock, 16 + 56 * s);
    }

    /// <summary>The map sits above the status bar, beside the right dock; when the composer reaches it sideways, it rides above the composer instead.</summary>
    private void PlaceMap(double? dock = null)
    {
        var s = GlassSettings.Current.UiScale;
        var right = Math.Max(16, dock ?? (RightDock is { } panel ? panel.Margin.Right + panel.Width + 12 : 0));
        var bottom = 16 + 56 * s + 8;
        if (Board.ComposerClearance > 0 && Board.ComposerSpan.Right + 12 > ActualWidth - right - (Map.ActualWidth > 0 ? Map.ActualWidth : 240))
            bottom = Math.Max(bottom, Board.ComposerClearance + 12);
        Map.Margin = new Thickness(0, 0, right, bottom);
    }

    /// <summary>Narrow windows drop the spend label to its icon; every button keeps its tooltip and automation name. The labelled pills need about 1250 DIPs at scale 1 and grow with the glass UI scale.</summary>
    private void Page_SizeChanged(object sender, SizeChangedEventArgs? e)
    {
        var compact = ActualWidth < 1250 * GlassSettings.Current.UiScale;
        QuotaLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        TitleText.MaxWidth = compact ? 140 : 200;
    }

    private void UpdateUndo()
    {
        UndoButton.IsEnabled = Board.History?.CanUndo == true;
        RedoButton.IsEnabled = Board.History?.CanRedo == true;
    }

    private void UpdateCount()
    {
        var total = Board.Doc.Nodes.Count(n => n.Kind != NodeKind.Group);
        var selected = Board.Selection.Count(id => Board.Doc.Find(id) is { Kind: not NodeKind.Group });
        CountText.Text = selected > 0 ? $"已选 {selected} / {total}" : $"节点 {total}";
    }

    private void UpdateRunning()
    {
        var running = Board.RunningNodes().Count;
        JobsButton.Visibility = JobsDivider.Visibility = running > 0 ? Visibility.Visible : Visibility.Collapsed;
        JobsText.Text = $"{running} 个生成中";
    }

    /// <summary>Today's estimated spend from the local ledger, per currency; the 额度 flyout has the detail.</summary>
    private void UpdateSpend()
    {
        var spent = SettingsStore.Current.Channels.GroupBy(UsageReport.CurrencyOf).Select(g => (Currency: g.Key, Amount: g.Sum(c => Prices.Estimate(c, Labels.Today)))).Where(x => x.Amount > 0).ToList();
        QuotaLabel.Text = "今天 " + (spent.Count == 0 ? "¥0.00" : string.Join(" · ", spent.Select(x => Prices.Money(x.Amount, x.Currency))));
    }

    private void Undo_Click(object sender, RoutedEventArgs e) { Board.Undo(); UpdateUndo(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { Board.Redo(); UpdateUndo(); }
    private void Jobs_Click(object sender, RoutedEventArgs e) => Board.LocateRunning();

    /// <summary>The ≡ menu: the canvas-level commands of the table (new nodes, import, paste, select all) and the places to go.</summary>
    private void BuildMainMenu()
    {
        var menu = new MenuFlyout();
        foreach (var id in new[] { "new-text", "new-image", "new-video", "new-audio", "new-config" }) menu.Items.Add(CommandItem(id));
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var id in new[] { "import", "paste", "select-all" }) menu.Items.Add(CommandItem(id));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CanvasView.Item("返回画布列表", "", () => BackAsync()));
        menu.Items.Add(CanvasView.Item("操作说明", "", () => _help.ShowAt(MenuButton)));
        menu.Items.Add(CanvasView.Item("设置", "", () => OpenSettingsAsync()));
        MenuButton.Flyout = menu;
    }

    private MenuFlyoutItem CommandItem(string id) => CanvasView.Item(id, () => Board.RunAsync(id));

    private void Search_Click(object sender, RoutedEventArgs e) => Palette.Open();

    /// <summary>Run a palette command: the page's own panels and places here, every other command on the canvas.</summary>
    private async Task RunCommandAsync(string id)
    {
        switch (id)
        {
            case "open-library": LibraryToggle.IsChecked = Library.Visibility != Visibility.Visible; LibraryToggle_Click(LibraryToggle, new RoutedEventArgs()); break;
            case "open-agent": SetAgentOpen(Agent.Visibility != Visibility.Visible); break;
            case "open-quota": QuotaButton.Flyout.ShowAt(QuotaButton); break;
            case "minimap": MiniMapToggle.IsChecked = Map.Visibility != Visibility.Visible; MiniMapToggle_Click(MiniMapToggle, new RoutedEventArgs()); break;
            case "fullscreen": SetFullScreen(App.Window.AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen); break;
            case "help": _help.ShowAt(HelpButton); break;
            case "settings": await OpenSettingsAsync(); break;
            case "home": await BackAsync(); break;
            default: await Board.RunAsync(id); break;
        }
    }

    private void Board_NodesDeleted(int count)
    {
        _toastVersion = Board.History?.Version ?? 0;
        ToastText.Text = $"已删除 {count} 个节点";
        // Above the composer when it is still up.
        ToastHost.Margin = new Thickness(0, 0, 0, Math.Max(76, Board.ComposerClearance + 12));
        ToastHost.Visibility = Visibility.Visible;
        _toastTimer ??= DispatcherQueue.CreateTimer();
        _toastTimer.Interval = ToastTime;
        _toastTimer.IsRepeating = false;
        _toastTimer.Tick -= ToastTimer_Tick;
        _toastTimer.Tick += ToastTimer_Tick;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void ToastTimer_Tick(DispatcherQueueTimer sender, object args) => HideToast();

    private void HideToast()
    {
        _toastTimer?.Stop();
        ToastHost.Visibility = Visibility.Collapsed;
    }

    /// <summary>Any other edit after the delete means 撤销 would undo something else: the toast goes.</summary>
    private void HideToastWhenStale()
    {
        if (ToastHost.Visibility == Visibility.Visible && (Board.History?.Version ?? 0) != _toastVersion) HideToast();
    }

    private void ToastUndo_Click(object sender, RoutedEventArgs e)
    {
        if ((Board.History?.Version ?? 0) == _toastVersion) { Board.Undo(); UpdateUndo(); }
        HideToast();
    }

    private async void Settings_Click(object sender, RoutedEventArgs e) => await OpenSettingsAsync();

    /// <summary>Settings is its own page: the canvas is saved and closed first, like going back, and Back returns to it.</summary>
    private async Task OpenSettingsAsync()
    {
        if (!await PrepareLeaveAsync()) return;
        Frame.Navigate(typeof(SettingsPage), null, new Microsoft.UI.Xaml.Media.Animation.DrillInNavigationTransitionInfo());
    }

    private void SetFullScreen(bool on)
    {
        App.Window.AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        FullScreenToggle.IsChecked = on;
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => SetFullScreen(FullScreenToggle.IsChecked == true);

    private void RevealFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(ProjectStore.AssetsDir(_doc.Id))}\"") { UseShellExecute = true });

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
        UpdateCount();
        UpdateRunning();
        UpdateSpend();
        // Opened from 设置 › 外观 to tune the liquid glass.
        if (Skin.OpenTuning) { Skin.OpenTuning = false; ShowGlassPanel(true); }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        App.Window.Closed -= Window_Closed;
        App.Window.AppWindow.Closing -= Window_Closing;
        Board.Changed -= Board_Changed;
        Board.ViewportChanged -= UpdateZoomText;
        App.Window.SetCanvasChrome(false);
        if (App.Window.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) SetFullScreen(false);
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

    private void UpdateZoomText() => ZoomText.Text = $"{Math.Round(Board.Zoom * 100)}%";

    private async void Back_Click(object sender, RoutedEventArgs e) => await BackAsync();

    private async Task BackAsync()
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

    /// <summary>The help flyout: gestures, then every shortcut of the command table, so it cannot drift from the menus.</summary>
    private Flyout BuildHelp()
    {
        // Rows are generated from the command table (Core/Canvas/CanvasCommands.cs): gestures first, then every shortcut.
        var grid = new Grid { ColumnSpacing = 20, RowSpacing = 8, Padding = new Thickness(0, 0, 12, 0), ColumnDefinitions = { new() { Width = GridLength.Auto }, new() { Width = new GridLength(1, GridUnitType.Star) } } };
        void Section(string heading, IEnumerable<(string Title, string Text)> rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var title = new TextBlock { Text = heading, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, grid.RowDefinitions.Count > 1 ? 8 : 0, 0, 0) };
            Grid.SetRow(title, grid.RowDefinitions.Count - 1);
            Grid.SetColumnSpan(title, 2);
            grid.Children.Add(title);
            foreach (var (name, text) in rows)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var row = grid.RowDefinitions.Count - 1;
                var left = new TextBlock { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
                var right = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
                Grid.SetRow(left, row);
                Grid.SetRow(right, row);
                Grid.SetColumn(right, 1);
                grid.Children.Add(left);
                grid.Children.Add(right);
            }
        }
        Section("手势", CanvasCommands.Gestures);
        Section("快捷键", CanvasCommands.Shortcuts());
        return new Flyout { Content = new ScrollViewer { MaxHeight = 560, Width = 400, Content = grid } };
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Board.ZoomBy(1.2);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Board.ZoomBy(1 / 1.2);
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => Board.ResetZoom();
    private void Fit_Click(object sender, RoutedEventArgs e) => Board.FitToContent();
}
