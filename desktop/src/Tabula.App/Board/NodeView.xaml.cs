using System.ComponentModel;
using Tabula.Core.Canvas;
using Tabula.Core.Settings;
using Tabula.Core.Storage;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Canvas = Microsoft.UI.Xaml.Controls.Canvas;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.System;

namespace Tabula.App.Board;

/// <summary>Visual for one node. Lives in the zoomed world layer; the host drives position, selection and zoom.</summary>
public sealed partial class NodeView : UserControl
{
    public CanvasNode Node { get; }
    private readonly CanvasView _host;
    private bool _selected, _hover, _mediaRequested;
    private (int Count, bool Open, int Completed, int Failed) _stack;

    private Image? _image, _previewImage;
    private FrameworkElement? _placeholder;
    private TextBlock? _configPrompt;
    private TextBox? _textBox;
    private MediaPlayerElement? _player;
    private Windows.Media.Playback.MediaPlayer? _media;
    private MediaSource? _mediaSource;
    private string? _mediaFile;
    private int _mediaVersion;
    private bool _playWhenReady;
    private WebView2? _loader;

    public NodeView(CanvasNode node, CanvasView host)
    {
        InitializeComponent();
        Node = node;
        _host = host;
        StackButton.Click += (_, _) => { _host.Select([Node.Id]); _host.ToggleStack(Node); };
        KindIcon.Glyph = Glyph(node.Kind);
        BuildBody();
        SyncAll();
        node.PropertyChanged += Node_PropertyChanged;
        PointerEntered += (_, _) => { _hover = true; RequestMedia(); UpdateChrome(); };
        GotFocus += (_, _) => RequestMedia();
        PointerExited += (_, _) => { _hover = false; UpdateChrome(); };
        ResizeGrip.PointerEntered += (_, _) => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast);
        foreach (var handle in new[] { TargetHandle, SourceHandle }) handle.PointerEntered += (_, _) => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);
        foreach (var element in new UIElement[] { ResizeGrip, TargetHandle, SourceHandle }) element.PointerExited += (_, _) => ProtectedCursor = null;
        // The selection ring fades instead of popping (elements with RenderTransformOrigin, such as the handles, cannot take an OpacityTransition).
        SelectionRing.OpacityTransition = new ScalarTransition { Duration = Motion.Fast };
        ApplyKindColors();
        Loaded += (_, _) => ApplyKindColors();
        ActualThemeChanged += (_, _) => ApplyKindColors();
    }

    private void ApplyKindColors()
    {
        var brush = Palette.Brush(Node.Kind, ActualTheme);
        KindTile.Background = brush;
        PlaceholderIcon.Foreground = brush;
    }

    /// <summary>Batch stack chrome: the deck and member count on a collapsed lead; an opened batch uses the batch frame header instead.</summary>
    public void SetStack(int count, bool open, int completed, int failed)
    {
        // Called on every realize (each pan frame); only real changes touch the visuals.
        if (_stack == (count, open, completed, failed)) return;
        _stack = (count, open, completed, failed);
        var collapsed = count > 1 && !open;
        DeckBack1.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        DeckBack2.Visibility = collapsed && count > 2 ? Visibility.Visible : Visibility.Collapsed;
        StackBadge.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        StackIcon.Glyph = failed > 0 ? "\uE7BA" : "\uE81E";
        StackText.Text = $"{completed}/{count} 完成" + (failed > 0 ? $" · {failed} 失败" : "");
        SyncTitle();
        var tip = $"这一批共 {count} 个结果，已完成 {completed} 个，失败 {failed} 个；点击卡片展开";
        ToolTipService.SetToolTip(StackButton, tip);
        AutomationProperties.SetName(StackButton, tip);
    }

    /// <summary>A collapsed stack shows the batch's name; opened, its lead goes by its own number like the cards beside it.</summary>
    private void SyncTitle() => TitleText.Text = FloatingTitle.Text = _stack is (> 1, true, _, _) ? Stacks.OwnTitle(Node) : Node.Title;

    public static string Glyph(NodeKind kind) => kind switch
    {
        NodeKind.Image => "",
        NodeKind.Text => "",
        NodeKind.Config => "",
        NodeKind.Video => "",
        NodeKind.Audio => "",
        _ => "",
    };

    public bool IsSelected
    {
        get => _selected;
        set { _selected = value; UpdateChrome(); }
    }

    public bool IsAnchor
    {
        set => AnchorChip.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The number of this node among the composer's references (参考 N), or 0 when it is not one.</summary>
    public int RefIndex
    {
        set
        {
            RefChip.Visibility = value > 0 ? Visibility.Visible : Visibility.Collapsed;
            RefText.Text = value > 0 ? $"参考 {value}" : "";
        }
    }

    public void SetZoom(double zoom)
    {
        // Handles, chips, their gaps and the selection ring keep a constant on-screen size whatever the zoom.
        var scale = new ScaleTransform { ScaleX = 1 / zoom, ScaleY = 1 / zoom };
        foreach (var element in new UIElement[] { TargetHandle, SourceHandle, ResizeGrip, FloatingTitle, AnchorChip, RefChip, StatusChip, StackBadge }) element.RenderTransform = scale;
        StackBadge.Margin = new(8 / zoom, 8 / zoom, 0, 0);
        FloatingTitle.Margin = new(6 / zoom, -18 - 6 / zoom, 0, 0);
        AnchorChip.Margin = new(0, -20 - 8 / zoom, 0, 0);
        RefChip.Margin = new(0, 8 / zoom, 8 / zoom, 0);
        if (IsMedia) Header.Padding = new(0, 10 / zoom, 10 / zoom, 0);
        SelectionRing.BorderThickness = new(2 / zoom);
        SelectionRing.Margin = new(-3 / zoom);
        SelectionRing.CornerRadius = new((Node.Kind == NodeKind.Group ? 18 : 14) + 3 / zoom);
        GroupOutline.StrokeThickness = 1.5 / zoom;
    }

    private bool IsMedia => Node.Kind is NodeKind.Image or NodeKind.Video;

    /// <summary>Start a random waiting animation in the body (once per run); false when there is none to show.</summary>
    private bool ShowLoader()
    {
        if (_loader is not null) return true;
        if (Loaders.Pick() is not { } page) return false;
        var view = _loader = new WebView2 { IsHitTestVisible = false, Margin = new Thickness(6), DefaultBackgroundColor = Microsoft.UI.Colors.Transparent };
        Body.Children.Add(view);
        _ = LoadAsync(view, page);
        return true;
    }

    private async Task LoadAsync(WebView2 view, string page)
    {
        try
        {
            await view.EnsureCoreWebView2Async(await Loaders.Environment);
            if (view != _loader) return;
            view.CoreWebView2.SetVirtualHostNameToFolderMapping(Loaders.Host, Loaders.Folder, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            view.CoreWebView2.Settings.AreDevToolsEnabled = false;
            view.Source = new Uri($"https://{Loaders.Host}/{Uri.EscapeDataString(page)}");
        }
        catch (Exception ex) when (view == _loader)
        {
            // No WebView2 runtime or a broken profile: log once and go back to the progress ring for every card.
            App.Log(ex);
            Loaders.Unavailable = true;
            HideLoader();
            SyncStatus();
        }
        catch (Exception) { /* Closed before it finished starting. */ }
    }

    private void HideLoader()
    {
        if (_loader is not { } view) return;
        _loader = null;
        Body.Children.Remove(view);
        view.Close();
    }

    public void Detach()
    {
        HideLoader();
        Node.PropertyChanged -= Node_PropertyChanged;
        SetMedia(null);
    }

    private void UpdateChrome()
    {
        SelectionRing.Opacity = _selected ? 1 : 0;
        TargetHandle.Opacity = SourceHandle.Opacity = _selected || _hover ? 1 : 0;
        ResizeGrip.Opacity = _selected ? 1 : 0;
        // A hidden grip must not catch presses: pressing an unselected node's corner drags it instead of resizing.
        ResizeGrip.IsHitTestVisible = _selected;
        FloatingTitle.Opacity = IsMedia && (_selected || _hover) ? 1 : 0;
    }

    private void BuildBody()
    {
        switch (Node.Kind)
        {
            case NodeKind.Image:
            case NodeKind.Video:
                // Media fills the whole card; only a small glass status chip floats in the corner while busy or failed.
                Grid.SetRow(Body, 0);
                Grid.SetRowSpan(Body, 2);
                Body.CornerRadius = new(13);
                Header.Background = null;
                Header.VerticalAlignment = VerticalAlignment.Top;
                Header.HorizontalAlignment = HorizontalAlignment.Right;
                Canvas.SetZIndex(Header, 1);
                KindTile.Visibility = Visibility.Collapsed;
                TitleText.Visibility = Visibility.Collapsed;
                StatusChip.Style = (Style)Application.Current.Resources["GlassChip"];
                _placeholder = Placeholder(Node.Kind == NodeKind.Image ? "双击导入图片\n或选中后输入提示词生成" : "双击导入视频");
                if (Node.Kind == NodeKind.Image)
                {
                    _image = new Image { Stretch = Stretch.UniformToFill, OpacityTransition = new ScalarTransition { Duration = Motion.Normal } };
                    _image.ImageOpened += (_, _) => _image.Opacity = 1;
                    _previewImage = new Image { Stretch = Stretch.UniformToFill, Opacity = 0.85 };
                    Body.Children.Add(_image);
                    Body.Children.Add(_previewImage);
                }
                else
                {
                    _player = new MediaPlayerElement { AreTransportControlsEnabled = true, Stretch = Stretch.Uniform };
                    _player.TransportControls.IsCompact = true;
                    Body.Children.Add(_player);
                }
                break;
            case NodeKind.Audio:
                _placeholder = Placeholder("双击导入音频，或选中后输入文字生成语音");
                _player = new MediaPlayerElement { AreTransportControlsEnabled = true, VerticalAlignment = VerticalAlignment.Bottom };
                _player.TransportControls.IsCompact = true;
                Body.Children.Add(_player);
                break;
            case NodeKind.Text:
                _textBox = new TextBox
                {
                    AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new(0), Padding = new(12, 4, 12, 12),
                };
                ScrollViewer.SetVerticalScrollBarVisibility(_textBox, ScrollBarVisibility.Auto);
                _textBox.Resources["TextControlBackgroundFocused"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                _textBox.Resources["TextControlBackgroundPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                _textBox.TextChanged += (_, _) =>
                {
                    // Programmatic sets echo back here asynchronously; only real edits differ from the node.
                    if (_textBox.Text == BoxText(Node.Content)) return;
                    _host.History.Record($"{Node.Id}/content");
                    Node.Content = _textBox.Text.Replace('\r', '\n');
                };
                _textBox.PreviewKeyDown += (_, e) => { if (e.Key == VirtualKey.Escape) { e.Handled = true; _host.FocusCanvas(); } };
                Body.Children.Add(_textBox);
                break;
            case NodeKind.Config:
                _configPrompt = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxLines = 6, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(12, 2, 12, 0) };
                Body.Children.Add(_configPrompt);
                ConfigMeta.Visibility = Visibility.Visible;
                break;
            case NodeKind.Group:
                GroupFill.Visibility = Visibility.Visible;
                Card.Background = null;
                Card.BorderThickness = new(0);
                Card.CornerRadius = new(18);
                GroupOutline.Visibility = Visibility.Visible;
                // Groups only feed downstream nodes, so they keep just the source handle.
                TargetHandle.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private FrameworkElement Placeholder(string text)
    {
        PlaceholderIcon.Glyph = Glyph(Node.Kind);
        PlaceholderText.Text = text;
        return PlaceholderPanel;
    }

    private void SyncAll()
    {
        SyncPosition();
        SyncSize();
        SyncTitle();
        SyncStatus();
        SyncContent();
        SyncPreview();
        SyncConfig();
        UpdateChrome();
    }

    private void Node_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CanvasNode.X) or nameof(CanvasNode.Y): SyncPosition(); break;
            case nameof(CanvasNode.Width) or nameof(CanvasNode.Height) or nameof(CanvasNode.FreeResize): SyncSize(); break;
            case nameof(CanvasNode.Title): SyncTitle(); break;
            case nameof(CanvasNode.Status) or nameof(CanvasNode.Error) or nameof(CanvasNode.QueueAhead): SyncStatus(); break;
            case nameof(CanvasNode.Content): SyncContent(); SyncTitle(); UpdateChrome(); break;
            case nameof(CanvasNode.Preview): SyncPreview(); break;
            case nameof(CanvasNode.Prompt) or nameof(CanvasNode.Model) or nameof(CanvasNode.Mode) or nameof(CanvasNode.Count): SyncConfig(); break;
        }
    }

    private void SyncPosition()
    {
        Canvas.SetLeft(this, Node.X);
        Canvas.SetTop(this, Node.Y);
    }

    private void SyncSize()
    {
        Width = Node.Width;
        Height = Node.Height;
        // A freely resized picture is shown whole inside the card instead of being cropped.
        var stretch = Node.FreeResize ? Stretch.Uniform : Stretch.UniformToFill;
        if (_image is not null) _image.Stretch = _previewImage!.Stretch = stretch;
    }

    private void Retry_Click(object sender, RoutedEventArgs e) => _ = _host.Generator.GenerateAsync(Node);

    private void SyncStatus()
    {
        var loading = Node.Status == NodeStatus.Loading;
        var failed = Node.Status == NodeStatus.Error && !string.IsNullOrEmpty(Node.Error);
        var paused = Node.Error is CanvasGenerator.Stopped or CanvasGenerator.Interrupted;
        // An image being generated paints a waiting animation from the local loaders folder, with the small corner chip still spinning.
        // A request still waiting for its channel shows its place in the queue instead.
        var queued = loading && Node.QueueAhead is not null;
        var painting = loading && !queued && Node.Kind == NodeKind.Image && !Node.HasContent && Node.Preview is null && ShowLoader();
        if (!painting) HideLoader();
        // Empty cards explain the state in the middle; cards with content or a preview keep only the small corner chip.
        var center = (loading || failed) && !Node.HasContent && Node.Preview is null && !painting;
        StatusView.Visibility = center ? Visibility.Visible : Visibility.Collapsed;
        BigBusy.IsActive = center && loading;
        BigBusy.Visibility = center && loading ? Visibility.Visible : Visibility.Collapsed;
        FailIcon.Visibility = center && failed && !paused ? Visibility.Visible : Visibility.Collapsed;
        InfoIcon.Visibility = center && failed && paused ? Visibility.Visible : Visibility.Collapsed;
        // A failed or stopped empty node can be retried in place.
        RetryButton.Visibility = center && failed && CanvasGenerator.CanGenerate(Node) ? Visibility.Visible : Visibility.Collapsed;
        InfoIcon.Glyph = "";
        StatusTitle.Text = queued ? "排队中" : loading ? "正在生成…" : paused ? Node.Error! : "生成失败";
        StatusDetail.Text = queued ? (Node.QueueAhead == 0 ? "下一个就是它" : $"前面还有 {Node.QueueAhead} 个请求") : loading || paused ? "" : Node.Error ?? "";
        StatusDetail.Visibility = StatusDetail.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(StatusDetail, failed && !paused ? Node.Error : null);
        Busy.IsActive = loading && !center;
        Busy.Visibility = loading && !center ? Visibility.Visible : Visibility.Collapsed;
        ErrorIcon.Visibility = failed && !center ? Visibility.Visible : Visibility.Collapsed;
        StatusChip.Visibility = (loading || failed) && !center ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(ErrorIcon, Node.Error);
        if (_textBox is not null) _textBox.PlaceholderText = center ? "" : "输入文本，或选中后让模型生成";
        if (_placeholder is not null) _placeholder.Visibility = !Node.HasContent && !loading && !failed && Node.Preview is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private string? AssetPath => Node.HasContent && Node.Kind is not (NodeKind.Text or NodeKind.Config or NodeKind.Group) ? ProjectStore.AssetPath(_host.Doc.Id, Node.Content!) : null;

    private void SyncContent()
    {
        switch (Node.Kind)
        {
            case NodeKind.Image when _image is not null:
                // Always the original file, so zooming in shows exactly the generated pixels.
                // Each new picture fades in once decoded instead of popping in.
                _image.Opacity = 0;
                var image = AssetPath is { } asset && File.Exists(asset) ? asset : null;
                if (Media.IsSvg(image))
                {
                    // Vector pictures report through their own source as well as ImageOpened.
                    var svg = new SvgImageSource(new Uri(image!));
                    svg.Opened += (_, _) => _image.Opacity = 1;
                    _image.Source = svg;
                }
                else _image.Source = image is null ? null : new BitmapImage(new Uri(image));
                break;
            case NodeKind.Video or NodeKind.Audio when _player is not null:
                var file = AssetPath is { } path && File.Exists(path) ? path : null;
                SetMedia(_mediaRequested ? file : null);
                if (!_mediaRequested && file is not null && Node.Kind == NodeKind.Video) LoadPoster(file);
                _player.Visibility = AssetPath is null ? Visibility.Collapsed : Visibility.Visible;
                break;
            case NodeKind.Text when _textBox is not null && _textBox.Text != BoxText(Node.Content):
                _textBox.Text = Node.Content ?? "";
                break;
        }
        SyncStatus();
    }

    /// <summary>TextBox stores every line break as a lone \r and raises TextChanged asynchronously, so compare in its form instead of a sync flag.</summary>
    public static string BoxText(string? text) => (text ?? "").Replace("\r\n", "\r").Replace('\n', '\r');

    public void FocusEditor() => _textBox?.Focus(FocusState.Keyboard);

    /// <summary>The 播放 command: play or pause the card's own player, which opens on first use.</summary>
    public void TogglePlayback()
    {
        RequestMedia();
        if (_media is not { } media) { if (_mediaFile is not null) _playWhenReady = !_playWhenReady; return; }
        if (media.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing) media.Pause();
        else media.Play();
    }

    /// <summary>
    /// Opens, swaps or releases the card's player. The MediaPlayer is created from its own dispatcher callback, not inside a XAML work item:
    /// creating it activates the system media transport controls, which pumps messages, and a second XAML work item running in that pump is
    /// WinUI's fail-fast (0xc000027b). The command manager must stay on: switching it off also disables the card's own play button.
    /// </summary>
    private void SetMedia(string? file)
    {
        if (file == _mediaFile) return;
        _mediaFile = file;
        if (file is null) _playWhenReady = false;
        var version = ++_mediaVersion;
        if (file is null && _media is null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (version != _mediaVersion || _player is null) return;
            if (file is null)
            {
                var old = _media;
                _media = null;
                _player.SetMediaPlayer(null);
                old?.Dispose();
                _mediaSource?.Dispose();
                _mediaSource = null;
                return;
            }
            if (_media is null)
            {
                _media = new Windows.Media.Playback.MediaPlayer();
                _player.SetMediaPlayer(_media);
            }
            var previous = _mediaSource;
            _mediaSource = MediaSource.CreateFromUri(new Uri(file));
            _media.Source = _mediaSource;
            previous?.Dispose();
            if (_playWhenReady) { _playWhenReady = false; _media.Play(); }
        });
    }

    // The media engine is heavy: the player opens on the first hover or keyboard focus (not on selection, so select-all stays cheap) and stays open until Detach.
    private void RequestMedia()
    {
        if (_player is null || _mediaRequested) return;
        _mediaRequested = true;
        SyncContent();
    }

    private async void LoadPoster(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 512);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumb);
            if (_player is not null && AssetPath == path) _player.PosterSource = bitmap;
        }
        catch { /* No poster: the card stays blank until hovering opens the player. */ }
    }

    private async void SyncPreview()
    {
        if (_previewImage is null) return;
        if (Node.Preview is not { } bytes)
        {
            _previewImage.Source = null;
            SyncStatus();
            return;
        }
        var bitmap = new BitmapImage { DecodePixelWidth = 512 };
        try
        {
            using var stream = new MemoryStream(bytes);
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        }
        catch (Exception ex)
        {
            // An undecodable preview is skipped (the final picture still arrives as Content); a throw from async void would end the process.
            App.Log(ex);
            return;
        }
        // A newer partial preview or the final picture may have replaced this one while it decoded.
        if (!ReferenceEquals(Node.Preview, bytes)) return;
        _previewImage.Source = bitmap;
        SyncStatus();
    }

    public void SyncConfig()
    {
        if (_configPrompt is null) return;
        var settings = SettingsStore.Current;
        _configPrompt.Text = string.IsNullOrWhiteSpace(Node.Prompt) ? "选中后输入提示词，连接文本或图片节点作为输入" : Node.Prompt;
        _configPrompt.Opacity = string.IsNullOrWhiteSpace(Node.Prompt) ? 0.6 : 1;
        var inputs = CanvasGraph.UpstreamResources(_host.Doc, Node.Id);
        var summary = string.Join(" · ", new[] { (NodeKind.Image, "张图片"), (NodeKind.Text, "段文本"), (NodeKind.Video, "个视频"), (NodeKind.Audio, "段音频") }
            .Select(pair => (count: inputs.Count(r => r.Type == pair.Item1), pair.Item2)).Where(x => x.count > 0).Select(x => $"{x.count} {x.Item2}"));
        ConfigMeta.Text = $"{Pages.Labels.Name(Node.Mode)} · {settings.Label(settings.ModelFor(Node.Mode, Node.Model))}" + (summary.Length > 0 ? $"\n输入：{summary}" : "");
        TitleText.Text = FloatingTitle.Text = Node.Title;
    }
}
