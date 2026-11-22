using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using Tabula.Core.Canvas;
using Tabula.Core.Models;
using Tabula.Core.Settings;
using Tabula.Core.Storage;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Rect = Windows.Foundation.Rect;

namespace Tabula.App.Board;

public enum PictureTool { Adjust, Mark, Crop, Split, Angle, Upscale, Reverse }

/// <summary>
/// Full-screen picture view (R5): replaces the old preview popup and the six separate tool dialogs.
/// Left: upstream sources, batch siblings and newer versions; Centre: zoom/pan stage with direct drawing for
/// local editing, crop and split; Right: tool tabs; Bottom: finishing actions from the shared command table.
/// </summary>
public sealed class PictureView : Grid, IDisposable
{
    private const string BrushTool = "brush", PenTool = "pen", EraserTool = "eraser";
    private static readonly Color Ink = Color.FromArgb(255, 255, 59, 48);

    private static readonly (PictureTool Tool, string Name, string Glyph, bool BitmapOnly)[] Tabs =
    [
        (PictureTool.Adjust, "改图", "\uE945", false),
        (PictureTool.Mark, "局部编辑", "\uE70F", true),
        (PictureTool.Crop, "裁剪", "\uE7A8", true),
        (PictureTool.Split, "拆分", "\uF0E2", true),
        (PictureTool.Angle, "多角度", "\uE89E", true),
        (PictureTool.Upscale, "本地放大", "\uE8A3", true),
        (PictureTool.Reverse, "反推", "\uE8C1", true),
    ];

    private static readonly (string Label, double Aspect)[] CropRatios =
        [("自由", 0), ("原图", -1), ("1:1", 1), ("4:3", 4 / 3d), ("3:4", 3 / 4d), ("16:9", 16 / 9d), ("9:16", 9 / 16d), ("3:2", 1.5), ("2:3", 2 / 3d)];

    private static readonly (string Label, int H, int P, double D, bool Wide)[] AnglePresets =
        [("正面", 0, 0, 5, false), ("左侧 45°", -45, 0, 5, false), ("右侧 45°", 45, 0, 5, false), ("俯视", 0, 30, 5, false), ("仰视", 0, -25, 5, false)];

    private readonly CanvasView _host;
    private readonly Action _close;
    private readonly List<CanvasNode> _watched = [];
    private CanvasNode _node;
    private PictureTool _tool;
    private bool _disposed;
    private bool _space;

    // Chrome controls
    private readonly TextBlock _titleText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 };
    private readonly TextBlock _metaText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 340 };
    private readonly StackPanel _navPanel = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _navLabel = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, Margin = new(4, 0, 4, 0) };
    private readonly Button _zoomButton = new() { Style = (Style)Application.Current.Resources["ToolbarButton"], Height = 30, MinHeight = 0, Padding = new(10, 0, 10, 0), CornerRadius = new(15) };
    private readonly StackPanel _stripPanel = new() { Spacing = 8, Padding = new(10, 12, 10, 12) };
    private readonly Grid _stageHost = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly WrapPanel _tabRow = new() { HorizontalSpacing = 4, VerticalSpacing = 4 };
    private readonly StackPanel _toolPane = new() { Spacing = 10 };
    private readonly TextBlock _statusNotice = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Grid _bottomBar = new() { Height = 48, Padding = new(14, 0, 14, 0) };
    private readonly StackPanel _bottomLeft = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _moreButton = new() { Style = (Style)Application.Current.Resources["ToolbarButton"], Height = 32, MinHeight = 0, Padding = new(10, 0, 10, 0), CornerRadius = new(16) };
    private readonly Button _closeButton;

    // Bitmap stage (Win2D) + SVG fallback
    private CanvasControl? _canvas;
    private CanvasBitmap? _bitmap;
    private CanvasRenderTarget? _paint, _markup;
    private Image? _svgImage;
    private int _imgW, _imgH;
    private double _zoom = 1;
    private Vector2 _pan;
    private Point? _panStart;
    private Vector2 _panOrigin;
    private bool _svg;
    private int _loadSeq;
    private bool _marksReady;

    // Tool state
    private string _adjustPrompt = "";
    private string? _adjustModel;
    private int _adjustCount = 2;

    private string _markTool = BrushTool;
    private readonly Dictionary<string, double> _brushSizes = new() { [BrushTool] = 36, [PenTool] = 8, [EraserTool] = 36 };
    private List<ImageSelection> _selections = [];
    private Point? _markLast, _boxStart;
    private Point _pointer = new(-100, -100);
    private bool _painted;
    private string _markPrompt = "";
    private string? _markModel;

    private double _cropAspect;
    private Rect _cropBox;
    private string? _cropDrag;
    private Point _cropStart;
    private Rect _cropStartBox;
    private TextBlock? _cropSizeText;

    private int _splitRows = 2, _splitCols = 2;
    private TextBlock? _splitInfoText;

    private int _angleH = 30, _angleP;
    private double _angleD = 5;
    private bool _angleWide;
    private string? _angleModel;
    private TextBlock? _anglePromptPreview;

    private int _upscaleFactor = 2;
    private string? _upscaleModel;

    private string? _reverseModel;
    private int _reverseStyle;

    private Color _accent = Colors.DodgerBlue;
    private Action? _runCurrentTool;

    public PictureView(CanvasView host, CanvasNode node, PictureTool initialTool, double width, double height, Action onClose)
    {
        _host = host;
        _node = node;
        _close = onClose;
        _svg = Media.IsSvg(node.Content);
        _tool = _svg && Tabs.First(t => t.Tool == initialTool).BitmapOnly ? PictureTool.Adjust : initialTool;
        (Width, Height) = (width, height);
        Background = (Brush)Application.Current.Resources["GlassThickBrush"];
        AutomationProperties.SetName(this, "大图视图");
        ReadThemeColors();

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Top bar
        var topBar = new Grid { Height = 48, Padding = new(12, 0, 12, 0), BorderThickness = new(0, 0, 0, 1), BorderBrush = HairlineBrush() };
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var back = new Button
        {
            Style = (Style)Application.Current.Resources["ToolbarButton"],
            Height = 32, MinHeight = 0, Padding = new(10, 0, 12, 0), CornerRadius = new(16),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new FontIcon { Glyph = "\uE72B", FontSize = 13 }, new TextBlock { Text = "回到画布", FontSize = 13 } } },
        };
        AutomationProperties.SetName(back, "回到画布");
        ToolTipService.SetToolTip(back, "回到画布 (Esc)");
        back.Click += (_, _) => _close();

        var sep = new Border { Width = 1, Height = 18, Background = HairlineBrush(), VerticalAlignment = VerticalAlignment.Center, Margin = new(2, 0, 2, 0) };
        _metaText.Foreground = SecondaryBrush();
        var topLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { back, sep, _titleText, _metaText } };
        topBar.Children.Add(topLeft);

        var prevBtn = IconButton("\uE76B", "上一张", "上一张 (←)", () => StepBatch(-1));
        var nextBtn = IconButton("\uE76C", "下一张", "下一张 (→)", () => StepBatch(1));
        _navLabel.Foreground = SecondaryBrush();
        _navPanel.Children.Add(prevBtn);
        _navPanel.Children.Add(_navLabel);
        _navPanel.Children.Add(nextBtn);

        var zoomOut = IconButton("\uE738", "缩小", "缩小", () => StepZoom(1 / 1.25));
        var zoomIn = IconButton("\uE710", "放大", "放大", () => StepZoom(1.25));
        ToolTipService.SetToolTip(_zoomButton, "适合窗口");
        AutomationProperties.SetName(_zoomButton, "缩放比例");
        _zoomButton.Click += (_, _) => ResetView();

        _closeButton = IconButton("\uE711", "关闭大图", "关闭 (Esc)", _close);
        _closeButton.AllowFocusOnInteraction = true;

        var topRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { _navPanel, zoomOut, _zoomButton, zoomIn, _closeButton } };
        SetColumn(topRight, 1);
        topBar.Children.Add(topRight);
        Children.Add(topBar);

        // Middle 3-column layout: Left strip | Centre stage | Right tool panel
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(108) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        SetRow(body, 1);

        var leftScroll = new ScrollViewer
        {
            Content = _stripPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var leftBorder = new Border { BorderThickness = new(0, 0, 1, 0), BorderBrush = HairlineBrush(), Child = leftScroll };
        body.Children.Add(leftBorder);

        SetColumn(_stageHost, 1);
        _stageHost.SizeChanged += (_, _) => UpdateSvgLayout();
        body.Children.Add(_stageHost);

        var rightGrid = new Grid { Padding = new(12, 12, 12, 12), RowSpacing = 10 };
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightGrid.Children.Add(_tabRow);

        var paneScroll = new ScrollViewer
        {
            Content = _toolPane,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new(0, 0, 4, 0),
        };
        SetRow(paneScroll, 1);
        rightGrid.Children.Add(paneScroll);

        _statusNotice.Foreground = SecondaryBrush();
        SetRow(_statusNotice, 2);
        rightGrid.Children.Add(_statusNotice);

        var rightBorder = new Border { BorderThickness = new(1, 0, 0, 0), BorderBrush = HairlineBrush(), Child = rightGrid };
        SetColumn(rightBorder, 2);
        body.Children.Add(rightBorder);
        Children.Add(body);

        // Bottom finishing actions bar
        _bottomBar.BorderThickness = new(0, 1, 0, 0);
        _bottomBar.BorderBrush = HairlineBrush();
        _bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _bottomBar.Children.Add(_bottomLeft);
        _moreButton.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { new FontIcon { Glyph = "\uE712", FontSize = 13 }, new TextBlock { Text = "更多", FontSize = 13 } } };
        AutomationProperties.SetName(_moreButton, "更多操作");
        ToolTipService.SetToolTip(_moreButton, "更多操作");
        _moreButton.Click += (_, _) => ShowMoreMenu();
        SetColumn(_moreButton, 1);
        _bottomBar.Children.Add(_moreButton);
        SetRow(_bottomBar, 2);
        Children.Add(_bottomBar);

        // Live sync with canvas document & running generations
        _host.Doc.Nodes.CollectionChanged += Doc_NodesChanged;
        _host.Generator.RunningChanged += Generator_RunningChanged;
        ActualThemeChanged += (_, _) => { ReadThemeColors(); _canvas?.Invalidate(); RebuildTabs(); RebuildStrip(); };
        Loaded += (_, _) => _closeButton.Focus(FocusState.Programmatic);
        PreviewKeyDown += OnPreviewKeyDown;
        KeyUp += (_, e) => { if (e.Key == VirtualKey.Space) _space = false; };

        LoadSubject(node, resetTool: false);
    }

    public void Resize(double width, double height)
    {
        (Width, Height) = (width, height);
        _canvas?.Invalidate();
        UpdateSvgLayout();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.Doc.Nodes.CollectionChanged -= Doc_NodesChanged;
        _host.Generator.RunningChanged -= Generator_RunningChanged;
        ClearWatched();
        DisposeStage();
    }

    private void DisposeStage()
    {
        if (_canvas is not null)
        {
            _canvas.RemoveFromVisualTree();
            _canvas = null;
        }
        _svgImage = null;
        _stageHost.Children.Clear();
        _bitmap?.Dispose(); _bitmap = null;
        _paint?.Dispose(); _paint = null;
        _markup?.Dispose(); _markup = null;
        _marksReady = false;
    }

    private void ReadThemeColors()
    {
        if (Application.Current.Resources.TryGetValue("SystemAccentColor", out var accent) && accent is Color color) _accent = color;
    }

    private static Brush HairlineBrush() => (Brush)Application.Current.Resources["CardStrokeBrush"];
    private static Brush SecondaryBrush() => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    private static Brush AccentTextBrush() => (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];

    private static Button IconButton(string glyph, string name, string tip, Action click)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["ToolbarButton"],
            Width = 32, Height = 32, MinHeight = 0, Padding = new(0), CornerRadius = new(16),
            Content = new FontIcon { Glyph = glyph, FontSize = 13 },
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    // ---------- Subject switching & live strip updates ----------

    private void LoadSubject(CanvasNode node, bool resetTool)
    {
        _node = node;
        _svg = Media.IsSvg(node.Content);
        if (resetTool || (_svg && Tabs.First(t => t.Tool == _tool).BitmapOnly))
            _tool = PictureTool.Adjust;
        _zoom = 1;
        _pan = Vector2.Zero;
        _cropDrag = null;
        _cropAspect = 0;
        _markLast = null;
        _boxStart = null;
        _selections = NodeKeys.SelectionsOf(node);
        _painted = node.Option(NodeKeys.Mask).Length > 0 || node.Option(NodeKeys.Markup).Length > 0;
        _statusNotice.Visibility = Visibility.Collapsed;

        // Keep canvas selection in sync so commands and canvas stay aligned.
        _host.Select([node.Id]);

        UpdateHeader();
        RebuildTabs();
        RebuildStrip();
        RebuildBottom();
        RebuildStage();
        RebuildToolPane();
    }

    private void UpdateHeader()
    {
        _titleText.Text = Stacks.OwnTitle(_node);
        var parts = new List<string>();
        if (_node.NaturalWidth > 0 && _node.NaturalHeight > 0) parts.Add($"{_node.NaturalWidth}×{_node.NaturalHeight}");
        if (_node.Content is { Length: > 0 } file) parts.Add(_svg ? "SVG" : Path.GetExtension(file).TrimStart('.').ToUpperInvariant());
        var origin = Composer.OriginOf(_host.Doc, _node);
        if (origin?.Model is { Length: > 0 } model) parts.Add(SettingsStore.Current.Label(model));
        _metaText.Text = string.Join(" · ", parts);

        var siblings = BatchSiblings();
        _navPanel.Visibility = siblings.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (siblings.Count > 1) _navLabel.Text = $"{siblings.IndexOf(_node) + 1} / {siblings.Count}";
        _zoomButton.Content = new TextBlock { Text = $"{Math.Round(_zoom * 100)}%", FontSize = 12.5 };
    }

    private List<CanvasNode> BatchSiblings() =>
        Stacks.Of(_node) is { } stack ? Stacks.Members(_host.Doc, stack).Where(n => n.Kind == NodeKind.Image && n.HasContent).ToList() : [_node];

    private void StepBatch(int delta)
    {
        var siblings = BatchSiblings();
        if (siblings.Count < 2) return;
        var index = siblings.IndexOf(_node);
        var next = siblings[(index + delta + siblings.Count) % siblings.Count];
        LoadSubject(next, resetTool: false);
    }

    private void Doc_NodesChanged(object? sender, NotifyCollectionChangedEventArgs e) => DispatcherQueue.TryEnqueue(SafeRefreshLive);
    private void Generator_RunningChanged() => DispatcherQueue.TryEnqueue(SafeRefreshLive);
    private void Watched_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CanvasNode.Status) or nameof(CanvasNode.Content) or nameof(CanvasNode.Title))
            DispatcherQueue.TryEnqueue(SafeRefreshLive);
    }

    private void SafeRefreshLive()
    {
        if (_disposed) return;
        if (_host.Doc.Find(_node.Id) != _node) { _close(); return; }
        UpdateHeader();
        RebuildStrip();
        RebuildBottom();
    }

    private void ClearWatched()
    {
        foreach (var node in _watched) node.PropertyChanged -= Watched_PropertyChanged;
        _watched.Clear();
    }

    private void Watch(IEnumerable<CanvasNode> nodes)
    {
        foreach (var n in nodes)
            if (!_watched.Contains(n))
            {
                n.PropertyChanged += Watched_PropertyChanged;
                _watched.Add(n);
            }
    }

    private void RebuildStrip()
    {
        ClearWatched();
        _stripPanel.Children.Clear();
        var strip = Composer.PictureStrip(_host.Doc, _node);
        Watch(strip.Sources);
        Watch(strip.Batch);
        Watch(strip.Versions);

        if (strip.Sources.Count > 0) AddStripSection("来源", strip.Sources, numbered: false);
        AddStripSection(strip.Batch.Count > 1 ? "这一批" : "当前", strip.Batch, numbered: strip.Batch.Count > 1);
        if (strip.Versions.Count > 0) AddStripSection("新版本", strip.Versions, numbered: false);
    }

    private void AddStripSection(string header, List<CanvasNode> items, bool numbered)
    {
        _stripPanel.Children.Add(new TextBlock
        {
            Text = header,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = SecondaryBrush(),
            Margin = new(2, _stripPanel.Children.Count == 0 ? 0 : 6, 0, 0),
        });
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var active = item.Id == _node.Id;
            var ready = item.HasContent;
            var grid = new Grid { Width = 80, Height = 80 };
            if (ready && ProjectStore.AssetPath(_host.Doc.Id, item.Content!) is { } path && File.Exists(path))
            {
                ImageSource source = Media.IsSvg(path) ? new SvgImageSource(new Uri(path)) : new BitmapImage { DecodePixelWidth = 160, UriSource = new Uri(path) };
                grid.Children.Add(new Image { Source = source, Stretch = Stretch.UniformToFill });
            }
            else
            {
                var status = item.Status == NodeStatus.Error ? "失败" : "生成中";
                grid.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
                grid.Children.Add(new TextBlock
                {
                    Text = status,
                    FontSize = 11,
                    Foreground = item.Status == NodeStatus.Error ? (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] : SecondaryBrush(),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            if (numbered)
            {
                grid.Children.Add(new Border
                {
                    CornerRadius = new(8),
                    Padding = new(5, 1, 5, 1),
                    Margin = new(4),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Background = new SolidColorBrush(Color.FromArgb(170, 20, 20, 20)),
                    Child = new TextBlock { Text = $"{i + 1}", FontSize = 10.5, Foreground = new SolidColorBrush(Colors.White) },
                });
            }
            var card = new Border
            {
                CornerRadius = new(8),
                BorderThickness = new(active ? 2 : 1),
                BorderBrush = active ? new SolidColorBrush(_accent) : HairlineBrush(),
                Child = grid,
            };
            var button = new Button
            {
                Style = (Style)Application.Current.Resources["ToolbarButton"],
                Padding = new(0), MinHeight = 0, MinWidth = 0, CornerRadius = new(8),
                IsEnabled = ready,
                Content = card,
            };
            AutomationProperties.SetName(button, $"{header}：{Stacks.OwnTitle(item)}");
            ToolTipService.SetToolTip(button, Stacks.OwnTitle(item));
            button.Click += (_, _) => { if (item.HasContent && item.Id != _node.Id) LoadSubject(item, resetTool: false); };
            _stripPanel.Children.Add(button);
        }
    }

    // ---------- Centre Stage (Win2D bitmap + SVG fallback) ----------

    private void RebuildStage()
    {
        DisposeStage();
        var path = ProjectStore.AssetPath(_host.Doc.Id, _node.Content!);
        if (!File.Exists(path)) return;

        if (_svg)
        {
            _svgImage = new Image
            {
                Source = new SvgImageSource(new Uri(path)),
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = new CompositeTransform(),
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            _stageHost.Children.Add(_svgImage);
            _stageHost.PointerWheelChanged += Svg_Wheel;
            UpdateSvgLayout();
            return;
        }

        var seq = ++_loadSeq;
        _canvas = new CanvasControl { ClearColor = Colors.Transparent };
        _canvas.CreateResources += (s, e) => e.TrackAsyncAction(LoadBitmapAsync(s, path, seq).AsAsyncAction());
        _canvas.Draw += Stage_Draw;
        _canvas.PointerPressed += Stage_PointerPressed;
        _canvas.PointerMoved += Stage_PointerMoved;
        _canvas.PointerReleased += Stage_PointerReleased;
        _canvas.PointerExited += (_, _) => { _pointer = new(-100, -100); _canvas?.Invalidate(); };
        _canvas.PointerCaptureLost += (_, _) => { _panStart = null; _cropDrag = null; _markLast = null; _boxStart = null; _canvas?.Invalidate(); };
        _canvas.PointerWheelChanged += Stage_PointerWheelChanged;
        _stageHost.Children.Add(_canvas);
    }

    private void Svg_Wheel(object sender, PointerRoutedEventArgs e)
    {
        if (!_svg) return;
        e.Handled = true;
        var delta = e.GetCurrentPoint(_stageHost).Properties.MouseWheelDelta;
        _zoom = Math.Clamp(_zoom * Math.Pow(1.15, delta / 120d), 0.2, 8);
        UpdateSvgLayout();
        UpdateHeader();
    }

    private void UpdateSvgLayout()
    {
        if (_svgImage?.RenderTransform is CompositeTransform t)
            (t.ScaleX, t.ScaleY, t.TranslateX, t.TranslateY) = (_zoom, _zoom, _pan.X, _pan.Y);
    }

    private async Task LoadBitmapAsync(CanvasControl sender, string path, int seq)
    {
        try
        {
            using var stream = File.OpenRead(path).AsRandomAccessStream();
            var bitmap = await CanvasBitmap.LoadAsync(sender, stream);
            if (_disposed || seq != _loadSeq) { bitmap.Dispose(); return; }
            _bitmap?.Dispose();
            _bitmap = bitmap;
            (_imgW, _imgH) = ((int)bitmap.SizeInPixels.Width, (int)bitmap.SizeInPixels.Height);
            _cropBox = new Rect(0, 0, _imgW, _imgH);

            _paint?.Dispose();
            _markup?.Dispose();
            _paint = new CanvasRenderTarget(sender, _imgW, _imgH, 96);
            using (var ds = _paint.CreateDrawingSession()) ds.Clear(Colors.Transparent);
            _markup = new CanvasRenderTarget(sender, _imgW, _imgH, 96);
            using (var ds = _markup.CreateDrawingSession()) ds.Clear(Colors.Transparent);

            var markupPath = _node.Option(NodeKeys.Markup) is { Length: > 0 } markupAsset ? ProjectStore.AssetPath(_host.Doc.Id, markupAsset) : null;
            if (markupPath is not null && File.Exists(markupPath))
            {
                using var ms = new MemoryStream(await File.ReadAllBytesAsync(markupPath));
                using var layer = await CanvasBitmap.LoadAsync(sender, ms.AsRandomAccessStream());
                using var ds = _markup.CreateDrawingSession();
                ds.DrawImage(layer, new Rect(0, 0, _imgW, _imgH));
            }
            var maskPath = _node.Option(NodeKeys.Mask) is { Length: > 0 } maskAsset ? ProjectStore.AssetPath(_host.Doc.Id, maskAsset) : null;
            if (maskPath is not null && File.Exists(maskPath))
            {
                using var ms = new MemoryStream(await File.ReadAllBytesAsync(maskPath));
                using var mask = await CanvasBitmap.LoadAsync(sender, ms.AsRandomAccessStream());
                if (mask.SizeInPixels.Width == _imgW && mask.SizeInPixels.Height == _imgH)
                {
                    var pixels = mask.GetPixelBytes();
                    var paint = new byte[pixels.Length];
                    for (var i = 0; i < pixels.Length; i += 4)
                        if (pixels[i + 3] < 128) (paint[i], paint[i + 1], paint[i + 2], paint[i + 3]) = (_accent.B, _accent.G, _accent.R, 255);
                    _paint.SetPixelBytes(paint);
                }
            }
            _marksReady = true;
            UpdateCropReadout();
            UpdateSplitReadout();
            sender.Invalidate();
        }
        catch (Exception ex) when (sender.Device?.IsDeviceLost(ex.HResult) != true)
        {
            App.Log(ex);
            ShowNotice("图片无法解码，或尺寸超过显卡支持的最大位图尺寸。");
        }
    }

    private Rect ImageRect()
    {
        if (_canvas is null || _imgW <= 0 || _imgH <= 0) return new(0, 0, 0, 0);
        var (sw, sh) = (_canvas.ActualWidth, _canvas.ActualHeight);
        var pad = 40d;
        var fit = Math.Min(Math.Max(1, sw - pad * 2) / _imgW, Math.Max(1, sh - pad * 2) / _imgH);
        var scale = fit * _zoom;
        var (w, h) = (_imgW * scale, _imgH * scale);
        return new((sw - w) / 2 + _pan.X, (sh - h) / 2 + _pan.Y, w, h);
    }

    private double MaxZoom()
    {
        if (_canvas is null || _imgW <= 0 || _imgH <= 0) return 8;
        var fit = Math.Min(Math.Max(1, _canvas.ActualWidth - 80) / _imgW, Math.Max(1, _canvas.ActualHeight - 80) / _imgH);
        return fit > 0 ? Math.Max(8, 16 / fit) : 8;
    }

    private Point ScreenToImage(Point p, bool clamp = true)
    {
        var r = ImageRect();
        if (r.Width <= 0 || r.Height <= 0) return new(0, 0);
        var ix = (p.X - r.X) / r.Width * _imgW;
        var iy = (p.Y - r.Y) / r.Height * _imgH;
        return clamp ? new(Math.Clamp(ix, 0, _imgW), Math.Clamp(iy, 0, _imgH)) : new(ix, iy);
    }

    private void ResetView()
    {
        _zoom = 1;
        _pan = Vector2.Zero;
        UpdateHeader();
        UpdateSvgLayout();
        _canvas?.Invalidate();
    }

    private void StepZoom(double factor, Point? pivot = null)
    {
        var oldZoom = _zoom;
        var nextZoom = Math.Clamp(oldZoom * factor, 0.2, MaxZoom());
        if (Math.Abs(nextZoom - oldZoom) < 1e-4) return;
        if (pivot is { } p && _canvas is not null)
        {
            var cx = _canvas.ActualWidth / 2 + _pan.X;
            var cy = _canvas.ActualHeight / 2 + _pan.Y;
            var ratio = (float)(nextZoom / oldZoom);
            _pan = new((float)(p.X - (p.X - cx) * ratio - _canvas.ActualWidth / 2), (float)(p.Y - (p.Y - cy) * ratio - _canvas.ActualHeight / 2));
        }
        _zoom = nextZoom;
        UpdateHeader();
        UpdateSvgLayout();
        _canvas?.Invalidate();
    }

    private void Stage_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        var point = e.GetCurrentPoint(_canvas);
        StepZoom(Math.Pow(1.15, point.Properties.MouseWheelDelta / 120d), point.Position);
    }

    private void Stage_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_bitmap is null) return;
        var ds = args.DrawingSession;
        var r = ImageRect();
        if (r.Width <= 0 || r.Height <= 0) return;

        ds.DrawImage(_bitmap, r, new Rect(0, 0, _imgW, _imgH), 1f, _zoom > 2 ? CanvasImageInterpolation.NearestNeighbor : CanvasImageInterpolation.HighQualityCubic);

        if (_tool == PictureTool.Mark && _paint is not null && _markup is not null)
        {
            ds.DrawImage(_paint, r, new Rect(0, 0, _imgW, _imgH), 0.5f);
            ds.DrawImage(_markup, r, new Rect(0, 0, _imgW, _imgH));
            for (var i = 0; i < _selections.Count; i++)
            {
                var s = _selections[i];
                var a = new Point(r.X + s.X * r.Width, r.Y + s.Y * r.Height);
                if (s.Kind == ImageSelection.Point)
                {
                    ds.FillCircle((float)a.X, (float)a.Y, 11, _accent);
                    ds.DrawCircle((float)a.X, (float)a.Y, 11, Colors.White, 2);
                }
                else
                {
                    var rect = new Rect(a, new Point(r.X + s.X2 * r.Width, r.Y + s.Y2 * r.Height));
                    ds.FillRectangle(rect, Color.FromArgb(40, _accent.R, _accent.G, _accent.B));
                    ds.DrawRectangle(rect, _accent, 2);
                    a = new Point(rect.X + 12, rect.Y + 12);
                    ds.FillCircle((float)a.X, (float)a.Y, 11, _accent);
                }
                ds.DrawText($"{i + 1}", (float)a.X, (float)a.Y, Colors.White, new() { FontSize = 12, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center });
            }
            if (_boxStart is { } start)
            {
                using var dash = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
                ds.DrawRectangle(new Rect(start, _pointer), _accent, 2, dash);
            }
            if (_markTool is BrushTool or PenTool or EraserTool && _pointer.X >= 0)
            {
                var rad = (float)(_brushSizes[_markTool] / 2);
                ds.DrawCircle((float)_pointer.X, (float)_pointer.Y, rad, Color.FromArgb(160, 0, 0, 0), 3);
                ds.DrawCircle((float)_pointer.X, (float)_pointer.Y, rad, Colors.White, 1.5f);
            }
        }
        else if (_tool == PictureTool.Crop)
        {
            var sx = r.Width / _imgW;
            var sy = r.Height / _imgH;
            var box = new Rect(r.X + _cropBox.X * sx, r.Y + _cropBox.Y * sy, _cropBox.Width * sx, _cropBox.Height * sy);
            var shade = Color.FromArgb(145, 0, 0, 0);
            ds.FillRectangle(new Rect(r.X, r.Y, r.Width, Math.Max(0, box.Top - r.Y)), shade);
            ds.FillRectangle(new Rect(r.X, box.Bottom, r.Width, Math.Max(0, r.Bottom - box.Bottom)), shade);
            ds.FillRectangle(new Rect(r.X, box.Top, Math.Max(0, box.Left - r.X), box.Height), shade);
            ds.FillRectangle(new Rect(box.Right, box.Top, Math.Max(0, r.Right - box.Right), box.Height), shade);
            ds.DrawRectangle(box, Colors.White, 1.5f);
            for (var i = 1; i < 3; i++)
            {
                ds.DrawLine((float)(box.Left + box.Width * i / 3), (float)box.Top, (float)(box.Left + box.Width * i / 3), (float)box.Bottom, Color.FromArgb(110, 255, 255, 255));
                ds.DrawLine((float)box.Left, (float)(box.Top + box.Height * i / 3), (float)box.Right, (float)(box.Top + box.Height * i / 3), Color.FromArgb(110, 255, 255, 255));
            }
            foreach (var corner in new[] { new Vector2((float)box.Left, (float)box.Top), new((float)box.Right, (float)box.Top), new((float)box.Left, (float)box.Bottom), new((float)box.Right, (float)box.Bottom) })
            {
                ds.FillCircle(corner, 6, Colors.White);
                ds.DrawCircle(corner, 6, _accent, 2);
            }
        }
        else if (_tool == PictureTool.Split)
        {
            var sx = r.Width / _imgW;
            var sy = r.Height / _imgH;
            using var dash = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
            foreach (var tile in ImageGeometry.Split(_imgW, _imgH, _splitRows, _splitCols))
                ds.DrawRectangle(new Rect(r.X + tile.X * sx, r.Y + tile.Y * sy, tile.Width * sx, tile.Height * sy), Colors.White, 1.75f, dash);
        }
    }

    private void Stage_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_canvas is null || _bitmap is null) return;
        var cp = e.GetCurrentPoint(_canvas);
        _pointer = cp.Position;
        var props = cp.Properties;
        if (props.IsMiddleButtonPressed || _space)
        {
            _panStart = _pointer;
            _panOrigin = _pan;
            _canvas.CapturePointer(e.Pointer);
            return;
        }
        if (!props.IsLeftButtonPressed) return;

        if (_tool == PictureTool.Mark && _marksReady)
        {
            var r = ImageRect();
            if (r.Contains(_pointer))
            {
                _canvas.CapturePointer(e.Pointer);
                if (_markTool is BrushTool or PenTool or EraserTool) { _markLast = _pointer; StrokeMark(_pointer); }
                else if (_markTool == ImageSelection.Box) _boxStart = _pointer;
                else if (_markTool == ImageSelection.Point)
                {
                    _selections.Add(new(ImageSelection.Point, Math.Clamp((_pointer.X - r.X) / r.Width, 0, 1), Math.Clamp((_pointer.Y - r.Y) / r.Height, 0, 1)));
                    RebuildToolPane();
                }
                _canvas.Invalidate();
                return;
            }
        }
        else if (_tool == PictureTool.Crop)
        {
            var r = ImageRect();
            if (r.Width > 0 && r.Height > 0)
            {
                var p = ScreenToImage(_pointer);
                var grip = 14 / (r.Width / _imgW);
                Point[] corners = [new(_cropBox.Left, _cropBox.Top), new(_cropBox.Right, _cropBox.Top), new(_cropBox.Left, _cropBox.Bottom), new(_cropBox.Right, _cropBox.Bottom)];
                string[] names = ["nw", "ne", "sw", "se"];
                _cropDrag = corners.Select((c, i) => (c, i)).Where(t => Math.Abs(t.c.X - p.X) <= grip && Math.Abs(t.c.Y - p.Y) <= grip).Select(t => names[t.i]).FirstOrDefault()
                    ?? (_cropBox.Contains(p) ? "move" : "new");
                _cropStart = p;
                _cropStartBox = _cropDrag == "new" ? new Rect(p, p) : _cropBox;
                _canvas.CapturePointer(e.Pointer);
                return;
            }
        }

        // Default left-drag pans the zoomed image.
        _panStart = _pointer;
        _panOrigin = _pan;
        _canvas.CapturePointer(e.Pointer);
    }

    private void Stage_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_canvas is null || _bitmap is null) return;
        _pointer = e.GetCurrentPoint(_canvas).Position;
        if (_panStart is { } origin)
        {
            _pan = new(_panOrigin.X + (float)(_pointer.X - origin.X), _panOrigin.Y + (float)(_pointer.Y - origin.Y));
            _canvas.Invalidate();
            return;
        }
        if (_tool == PictureTool.Mark)
        {
            if (_markLast is not null) StrokeMark(_pointer);
            _canvas.Invalidate();
            return;
        }
        if (_tool == PictureTool.Crop && _cropDrag is not null)
        {
            var p = ScreenToImage(_pointer);
            if (_cropDrag == "move")
            {
                var x = Math.Clamp(_cropStartBox.X + p.X - _cropStart.X, 0, _imgW - _cropStartBox.Width);
                var y = Math.Clamp(_cropStartBox.Y + p.Y - _cropStart.Y, 0, _imgH - _cropStartBox.Height);
                _cropBox = new Rect(x, y, _cropStartBox.Width, _cropStartBox.Height);
            }
            else
            {
                var anchor = _cropDrag switch
                {
                    "nw" => new Point(_cropStartBox.Right, _cropStartBox.Bottom),
                    "ne" => new Point(_cropStartBox.Left, _cropStartBox.Bottom),
                    "sw" => new Point(_cropStartBox.Right, _cropStartBox.Top),
                    "se" => new Point(_cropStartBox.Left, _cropStartBox.Top),
                    _ => _cropStart,
                };
                var (w, h) = (Math.Abs(p.X - anchor.X), Math.Abs(p.Y - anchor.Y));
                if (_cropAspect > 0)
                {
                    if (w / Math.Max(h, 1) > _cropAspect) h = w / _cropAspect; else w = h * _cropAspect;
                    var maxW = p.X < anchor.X ? anchor.X : _imgW - anchor.X;
                    var maxH = p.Y < anchor.Y ? anchor.Y : _imgH - anchor.Y;
                    var fit = Math.Min(1, Math.Min(maxW / Math.Max(w, 1), maxH / Math.Max(h, 1)));
                    (w, h) = (w * fit, h * fit);
                }
                var left = p.X < anchor.X ? anchor.X - w : anchor.X;
                var top = p.Y < anchor.Y ? anchor.Y - h : anchor.Y;
                _cropBox = new Rect(left, top, Math.Max(1, w), Math.Max(1, h));
            }
            UpdateCropReadout();
            _canvas.Invalidate();
        }
    }

    private void Stage_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_canvas is null) return;
        if (_boxStart is { } start && Math.Abs(_pointer.X - start.X) > 6 && Math.Abs(_pointer.Y - start.Y) > 6)
        {
            var r = ImageRect();
            if (r.Width > 0 && r.Height > 0)
            {
                _selections.Add(new(ImageSelection.Box,
                    Math.Clamp((Math.Min(start.X, _pointer.X) - r.X) / r.Width, 0, 1),
                    Math.Clamp((Math.Min(start.Y, _pointer.Y) - r.Y) / r.Height, 0, 1),
                    Math.Clamp((Math.Max(start.X, _pointer.X) - r.X) / r.Width, 0, 1),
                    Math.Clamp((Math.Max(start.Y, _pointer.Y) - r.Y) / r.Height, 0, 1)));
                RebuildToolPane();
            }
        }
        _panStart = null;
        _cropDrag = null;
        _markLast = null;
        _boxStart = null;
        _canvas.ReleasePointerCapture(e.Pointer);
        _canvas.Invalidate();
    }

    private void StrokeMark(Point to)
    {
        if (_paint is null || _markup is null || _markLast is not { } from) return;
        var r = ImageRect();
        if (r.Width <= 0 || r.Height <= 0) return;
        var erase = _markTool == EraserTool;
        var radius = (float)(_brushSizes[_markTool] / 2 / (r.Width / _imgW));
        var a = ScreenToImage(from, clamp: false);
        var b = ScreenToImage(to, clamp: false);
        using var round = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
        (CanvasRenderTarget Layer, Color Color)[] layers = erase ? [(_paint, Colors.Transparent), (_markup, Colors.Transparent)] : [_markTool == PenTool ? (_markup, Ink) : (_paint, _accent)];
        foreach (var (layer, color) in layers)
        {
            using var ds = layer.CreateDrawingSession();
            if (erase) ds.Blend = CanvasBlend.Copy;
            ds.DrawLine((float)a.X, (float)a.Y, (float)b.X, (float)b.Y, color, radius * 2, round);
            ds.FillCircle((float)b.X, (float)b.Y, radius, color);
        }
        _markLast = to;
        if (!erase) _painted = true;
    }

    private void ClearMarks()
    {
        _selections.Clear();
        foreach (var layer in new[] { _paint, _markup }.OfType<CanvasRenderTarget>())
            using (var ds = layer.CreateDrawingSession()) ds.Clear(Colors.Transparent);
        _painted = false;
        _canvas?.Invalidate();
        RebuildToolPane();
    }

    private async Task<byte[]?> ExportMaskAsync()
    {
        if (_paint is null || _canvas is null) return null;
        var pixels = _paint.GetPixelBytes();
        var mask = new byte[pixels.Length];
        var any = false;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] > 0) { any = true; continue; }
            mask[i] = mask[i + 1] = mask[i + 2] = mask[i + 3] = 255;
        }
        if (!any) return null;
        using var bitmap = CanvasBitmap.CreateFromBytes(_canvas.Device, mask, _imgW, _imgH, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var stream = new InMemoryRandomAccessStream();
        await bitmap.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        return await ReadBytesAsync(stream);
    }

    private async Task<byte[]?> ExportMarkupAsync()
    {
        if (_markup is null) return null;
        var pixels = _markup.GetPixelBytes();
        var any = false;
        for (var i = 3; i < pixels.Length && !any; i += 4) any = pixels[i] > 0;
        if (!any) return null;
        using var stream = new InMemoryRandomAccessStream();
        await _markup.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        return await ReadBytesAsync(stream);
    }

    private static async Task<byte[]> ReadBytesAsync(InMemoryRandomAccessStream stream)
    {
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private async Task<bool> SaveMarksOntoNodeAsync()
    {
        if (!_marksReady) { ShowNotice("图片仍在载入，请稍候。"); return false; }
        var (mask, markup) = _painted ? (await ExportMaskAsync(), await ExportMarkupAsync()) : (null, null);
        if (mask is not null && markup is not null)
        {
            ShowNotice("涂抹和标注只能使用其中一种，请先擦除其中一种。");
            return false;
        }
        _host.History.Record();
        _node.SetOption(NodeKeys.Mask, mask is not null ? ProjectStore.SaveAsset(_host.Doc.Id, mask, "image/png", ProjectStore.MaskPrefix) : null);
        _node.SetOption(NodeKeys.Markup, markup is not null ? ProjectStore.SaveAsset(_host.Doc.Id, markup, "image/png", ProjectStore.MarkupPrefix) : null);
        NodeKeys.SetSelections(_node, _selections);
        _host.DraftChanged(true);
        return true;
    }

    // ---------- Right Tool Tabs & Panes ----------

    private void RebuildTabs()
    {
        _tabRow.Children.Clear();
        var activeBg = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        foreach (var (tool, name, glyph, bitmapOnly) in Tabs)
        {
            if (_svg && bitmapOnly) continue;
            var active = tool == _tool;
            var icon = new FontIcon { Glyph = glyph, FontSize = 12.5 };
            if (active) icon.Foreground = AccentTextBrush();
            var label = new TextBlock { Text = name, FontSize = 12.5, FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal };
            if (active) label.Foreground = AccentTextBrush();
            var button = new Button
            {
                Style = (Style)Application.Current.Resources["ToolbarButton"],
                Height = 30, MinHeight = 0, Padding = new(9, 0, 10, 0), CornerRadius = new(15),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { icon, label } },
            };
            if (active) button.Background = activeBg;
            AutomationProperties.SetName(button, name);
            button.Click += (_, _) =>
            {
                _tool = tool;
                _statusNotice.Visibility = Visibility.Collapsed;
                RebuildTabs();
                RebuildToolPane();
                _canvas?.Invalidate();
            };
            _tabRow.Children.Add(button);
        }
    }

    private void RebuildToolPane()
    {
        _toolPane.Children.Clear();
        _runCurrentTool = null;
        _cropSizeText = null;
        _splitInfoText = null;
        _anglePromptPreview = null;

        switch (_tool)
        {
            case PictureTool.Adjust: BuildAdjustPane(); break;
            case PictureTool.Mark: BuildMarkPane(); break;
            case PictureTool.Crop: BuildCropPane(); break;
            case PictureTool.Split: BuildSplitPane(); break;
            case PictureTool.Angle: BuildAnglePane(); break;
            case PictureTool.Upscale: BuildUpscalePane(); break;
            case PictureTool.Reverse: BuildReversePane(); break;
        }
    }

    private static TextBlock Hint(string text) => new() { Text = text, FontSize = 12.5, Foreground = SecondaryBrush(), TextWrapping = TextWrapping.Wrap };

    private static Button PrimaryPill(string label, string glyph, Action click, bool enabled = true)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["ToolbarButton"],
            Height = 32, MinHeight = 0, HorizontalAlignment = HorizontalAlignment.Stretch, CornerRadius = new(16),
            IsEnabled = enabled,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new FontIcon { Glyph = glyph, FontSize = 13, Foreground = AccentTextBrush() },
                    new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = AccentTextBrush() },
                },
            },
        };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => click();
        return button;
    }

    private static Border PrimaryCapsule(Button button) => new()
    {
        Style = (Style)Application.Current.Resources["GlassCapsule"],
        Height = 36, CornerRadius = new(18), Padding = new(2),
        Opacity = button.IsEnabled ? 1 : 0.55,
        Child = button,
    };

    private static TextBox PromptInput(string placeholder, string initial, Action<string> onChange)
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            Text = initial,
            TextWrapping = TextWrapping.Wrap,
            PlaceholderText = placeholder,
            MinHeight = 84, MaxHeight = 180, FontSize = 13,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        box.TextChanged += (_, _) => onChange(box.Text.Replace('\r', '\n'));
        return box;
    }

    private ComboBox ModelCombo(Capability capability, ref string? selected, Action<string>? onChanged = null, Func<string, bool>? filter = null)
    {
        var settings = SettingsStore.Current;
        var all = settings.ModelsFor(capability).ToList();
        var models = filter is null ? all : all.Where(filter).ToList();
        if (models.Count == 0) models = all;
        var current = selected ?? settings.ModelFor(capability, _node.Model);
        if (!models.Contains(current) && models.Count > 0) current = models[0];
        selected = current;
        var box = new ComboBox
        {
            Header = "模型",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = models.Select(settings.Label).ToList(),
            SelectedIndex = models.IndexOf(current),
        };
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0 && box.SelectedIndex < models.Count) onChanged?.Invoke(models[box.SelectedIndex]);
        };
        return box;
    }

    private void BuildAdjustPane()
    {
        _toolPane.Children.Add(Hint("以这张图为参考生成新版本，结果放到画布右侧并同步出现在左侧「新版本」里。"));
        var box = PromptInput("例如：把天空换成黄昏，人物和构图保持不变（Ctrl+Enter 生成）", _adjustPrompt, v => _adjustPrompt = v);
        _toolPane.Children.Add(box);

        if (Composer.OriginOf(_host.Doc, _node) is { Prompt: { Length: > 0 } prev })
        {
            var fill = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Height = 28, MinHeight = 0, Padding = new(8, 0, 8, 0), CornerRadius = new(14), Content = new TextBlock { Text = "填入上次提示词", FontSize = 12 } };
            fill.Click += (_, _) => { _adjustPrompt = prev; box.Text = NodeView.BoxText(prev); };
            _toolPane.Children.Add(fill);
        }

        _toolPane.Children.Add(ModelCombo(Capability.Image, ref _adjustModel, m => _adjustModel = m));

        var countRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var activeBg = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        foreach (var c in new[] { 1, 2, 4 })
        {
            var b = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Height = 30, MinHeight = 0, Padding = new(12, 0, 12, 0), CornerRadius = new(15), Content = new TextBlock { Text = $"{c} 张", FontSize = 12.5, FontWeight = _adjustCount == c ? FontWeights.SemiBold : FontWeights.Normal } };
            if (_adjustCount == c) b.Background = activeBg;
            b.Click += (_, _) => { _adjustCount = c; RebuildToolPane(); };
            countRow.Children.Add(b);
        }
        _toolPane.Children.Add(new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "张数", FontSize = 12.5 }, countRow } });

        void Run()
        {
            if (string.IsNullOrWhiteSpace(_adjustPrompt)) { ShowNotice("请先写下要怎么改。"); box.Focus(FocusState.Programmatic); return; }
            var draft = new CanvasNode { Kind = NodeKind.Config, Mode = Capability.Image, Prompt = _adjustPrompt, Model = _adjustModel, Count = _adjustCount, Ratio = _node.Ratio, Size = _node.Size, Quality = _node.Quality };
            _host.History.Record();
            var generated = Composer.NewGeneration(_host.Doc, draft, [_node], _node.Right + 80, _node.Y);
            _ = _host.Generator.GenerateAsync(generated, recordUndo: false);
            RebuildStrip();
            ShowNotice($"已开始生成 {_adjustCount} 张新版本，结果会落在画布右侧与左侧「新版本」。");
        }
        _runCurrentTool = Run;
        _toolPane.Children.Add(PrimaryCapsule(PrimaryPill("生成新版本", "\uE768", Run)));
    }

    private (bool Mask, bool Points, bool Markup) MarkSupportFor(string? modelKey)
    {
        if (_svg || SettingsStore.Current.Resolve(SettingsStore.Current.ModelFor(Capability.Image, modelKey)) is not { } resolved) return (false, false, false);
        var family = ModelFamilies.FindImage(resolved.Channel.Vendor, resolved.Model.Name);
        var openAI = resolved.Channel.Vendor == Vendor.OpenAI;
        return (openAI && family?.Mask == true, family?.Interactive == true, openAI && family?.Markup == true);
    }

    private void BuildMarkPane()
    {
        var modelCombo = ModelCombo(Capability.Image, ref _markModel, m => { _markModel = m; RebuildToolPane(); },
            m => MarkSupportFor(m) is var (mk, pt, mu) && (mk || pt || mu));
        var (allowMask, allowPoints, allowMarkup) = MarkSupportFor(_markModel);
        _toolPane.Children.Add(modelCombo);

        if (!allowMask && !allowPoints && !allowMarkup)
        {
            _toolPane.Children.Add(Hint("当前模型不支持局部编辑。GPT Image 支持涂抹蒙版和标注，Seedream 5.0 pro / flash / 5.5 支持点选和框选。"));
            return;
        }

        var modes = new List<(string Id, string Name, string Glyph)>();
        if (allowMask) modes.Add((BrushTool, "涂抹", "\uE70F"));
        if (allowMarkup) modes.Add((PenTool, "红笔标注", "\uED63"));
        if (allowMask || allowMarkup) modes.Add((EraserTool, "擦除", "\uE75C"));
        if (allowPoints) { modes.Add((ImageSelection.Point, "点选", "\uE81D")); modes.Add((ImageSelection.Box, "框选", "\uEF20")); }
        if (!modes.Any(m => m.Id == _markTool)) _markTool = modes[0].Id;

        _toolPane.Children.Add(Hint("直接在中间大图上绘制要改的区域，写下修改说明后可直接按标注生成，或先保存标注。"));

        var modeWrap = new WrapPanel { HorizontalSpacing = 4, VerticalSpacing = 4 };
        var activeBg = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        foreach (var (id, name, glyph) in modes)
        {
            var on = id == _markTool;
            var btn = new Button
            {
                Style = (Style)Application.Current.Resources["ToolbarButton"],
                Height = 30, MinHeight = 0, Padding = new(9, 0, 10, 0), CornerRadius = new(15),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { new FontIcon { Glyph = glyph, FontSize = 12 }, new TextBlock { Text = name, FontSize = 12.5, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal } } },
            };
            if (on) btn.Background = activeBg;
            btn.Click += (_, _) => { _markTool = id; RebuildToolPane(); _canvas?.Invalidate(); };
            modeWrap.Children.Add(btn);
        }
        _toolPane.Children.Add(modeWrap);

        if (_brushSizes.TryGetValue(_markTool, out var size))
        {
            var slider = new Slider { Header = "笔刷大小", Minimum = 4, Maximum = 120, Value = size };
            slider.ValueChanged += (_, _) => { _brushSizes[_markTool] = slider.Value; _canvas?.Invalidate(); };
            _toolPane.Children.Add(slider);
        }

        var clearRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (allowPoints && _selections.Count > 0)
        {
            var undoSel = new Button { Style = (Style)Application.Current.Resources["ActionButton"], Content = $"撤销选区 ({_selections.Count})" };
            undoSel.Click += (_, _) => { if (_selections.Count > 0) _selections.RemoveAt(_selections.Count - 1); RebuildToolPane(); _canvas?.Invalidate(); };
            clearRow.Children.Add(undoSel);
        }
        var clear = new Button { Style = (Style)Application.Current.Resources["ActionButton"], Content = "全部清除" };
        clear.Click += (_, _) => ClearMarks();
        clearRow.Children.Add(clear);
        _toolPane.Children.Add(clearRow);

        var prompt = PromptInput("描述要把标注处改成什么（Ctrl+Enter 生成）", _markPrompt, v => _markPrompt = v);
        _toolPane.Children.Add(prompt);

        var saveOnly = new Button { Style = (Style)Application.Current.Resources["ActionButton"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "只保存标注，稍后生成" };
        saveOnly.Click += async (_, _) =>
        {
            if (await SaveMarksOntoNodeAsync()) ShowNotice("已保存局部编辑标注到当前图片。");
        };
        _toolPane.Children.Add(saveOnly);

        async void Run()
        {
            if (string.IsNullOrWhiteSpace(_markPrompt)) { ShowNotice("请先写下要把标注处改成什么。"); prompt.Focus(FocusState.Programmatic); return; }
            if (!await SaveMarksOntoNodeAsync()) return;
            var draft = new CanvasNode { Kind = NodeKind.Config, Mode = Capability.Image, Prompt = _markPrompt, Model = _markModel, Count = 1, Ratio = _node.Ratio, Size = _node.Size, Quality = _node.Quality };
            _host.History.Record();
            var generated = Composer.NewGeneration(_host.Doc, draft, [_node], _node.Right + 80, _node.Y);
            _ = _host.Generator.GenerateAsync(generated, recordUndo: false);
            RebuildStrip();
            ShowNotice("已按标注发起生成，结果会落在画布右侧与左侧「新版本」。");
        }
        _runCurrentTool = Run;
        _toolPane.Children.Add(PrimaryCapsule(PrimaryPill("按标注生成", "\uE768", Run)));
    }

    private void BuildCropPane()
    {
        _toolPane.Children.Add(Hint("拖动大图上的方框移动，拖四角改大小，框外拖动可重新框选。结果落在画布右侧新节点，原图不变。"));
        var wrap = new WrapPanel { HorizontalSpacing = 4, VerticalSpacing = 4 };
        var activeBg = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        foreach (var (label, aspect) in CropRatios)
        {
            var resolved = aspect < 0 ? (_imgH > 0 ? (double)_imgW / _imgH : 1) : aspect;
            var on = Math.Abs(_cropAspect - resolved) < 1e-4;
            var btn = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Height = 28, MinHeight = 0, Padding = new(9, 0, 9, 0), CornerRadius = new(14), Content = new TextBlock { Text = label, FontSize = 12.5, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal } };
            if (on) btn.Background = activeBg;
            btn.Click += (_, _) =>
            {
                _cropAspect = resolved;
                if (_cropAspect > 0 && _imgW > 0 && _imgH > 0)
                {
                    var box = ImageGeometry.CenteredCrop(_imgW, _imgH, _cropAspect);
                    _cropBox = new Rect(box.X, box.Y, box.Width, box.Height);
                }
                RebuildToolPane();
                _canvas?.Invalidate();
            };
            wrap.Children.Add(btn);
        }
        _toolPane.Children.Add(new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "比例", FontSize = 12.5 }, wrap } });

        _cropSizeText = new TextBlock { FontSize = 12.5, Foreground = SecondaryBrush() };
        UpdateCropReadout();
        _toolPane.Children.Add(_cropSizeText);

        async void Run()
        {
            if (_bitmap is null) return;
            var box = ImageGeometry.Clamp(new((int)Math.Round(_cropBox.X), (int)Math.Round(_cropBox.Y), (int)Math.Round(_cropBox.Width), (int)Math.Round(_cropBox.Height)), _imgW, _imgH);
            await ImageTools.ExecuteCropAsync(_host, _node, _bitmap, box, select: false);
            RebuildStrip();
            ShowNotice($"已裁剪 {box.Width}×{box.Height} 到画布右侧新节点，可在左侧「新版本」切换查看。");
        }
        _runCurrentTool = Run;
        _toolPane.Children.Add(PrimaryCapsule(PrimaryPill("裁剪到新节点", "\uE7A8", Run)));
    }

    private void UpdateCropReadout()
    {
        if (_cropSizeText is null || _imgW <= 0 || _imgH <= 0) return;
        var box = ImageGeometry.Clamp(new((int)Math.Round(_cropBox.X), (int)Math.Round(_cropBox.Y), (int)Math.Round(_cropBox.Width), (int)Math.Round(_cropBox.Height)), _imgW, _imgH);
        _cropSizeText.Text = $"裁剪范围：{box.Width} × {box.Height} 像素";
    }

    private void BuildSplitPane()
    {
        _toolPane.Children.Add(Hint("按行列均分切开大图；拆出的图排在画布右侧并自动编组，原图不变。"));
        var rows = new NumberBox { Header = "行数", Value = _splitRows, Minimum = 1, Maximum = 12, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var cols = new NumberBox { Header = "列数", Value = _splitCols, Minimum = 1, Maximum = 12, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        rows.ValueChanged += (_, _) => { _splitRows = double.IsNaN(rows.Value) ? 1 : (int)Math.Clamp(Math.Round(rows.Value), 1, 12); UpdateSplitReadout(); _canvas?.Invalidate(); };
        cols.ValueChanged += (_, _) => { _splitCols = double.IsNaN(cols.Value) ? 1 : (int)Math.Clamp(Math.Round(cols.Value), 1, 12); UpdateSplitReadout(); _canvas?.Invalidate(); };
        _toolPane.Children.Add(rows);
        _toolPane.Children.Add(cols);

        _splitInfoText = new TextBlock { FontSize = 12.5, Foreground = SecondaryBrush() };
        UpdateSplitReadout();
        _toolPane.Children.Add(_splitInfoText);

        async void Run()
        {
            if (_bitmap is null) return;
            if (_splitRows * _splitCols < 2) { ShowNotice("行数和列数至少要拆出 2 张。"); return; }
            var nodes = await ImageTools.ExecuteSplitAsync(_host, _node, _bitmap, _splitRows, _splitCols, select: false);
            RebuildStrip();
            ShowNotice($"已拆分为 {nodes.Count} 张并放入新分组，可在左侧「新版本」查看。");
        }
        _runCurrentTool = Run;
        _toolPane.Children.Add(PrimaryCapsule(PrimaryPill("拆分到新节点", "\uF0E2", Run)));
    }

    private void UpdateSplitReadout()
    {
        if (_splitInfoText is null || _imgW <= 0 || _imgH <= 0) return;
        _splitInfoText.Text = $"共 {_splitRows * _splitCols} 张，每张约 {_imgW / Math.Max(1, _splitCols)} × {_imgH / Math.Max(1, _splitRows)} 像素";
    }

    private void BuildAnglePane()
    {
        _toolPane.Children.Add(Hint("以这张图为参考重画同一主体的新视角，结果放在画布右侧并出现在左侧「新版本」。"));

        var presets = new WrapPanel { HorizontalSpacing = 4, VerticalSpacing = 4 };
        foreach (var (label, h, p, d, w) in AnglePresets)
        {
            var b = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Height = 28, MinHeight = 0, Padding = new(9, 0, 9, 0), CornerRadius = new(14), Content = new TextBlock { Text = label, FontSize = 12.5 } };
            b.Click += (_, _) => { (_angleH, _angleP, _angleD, _angleWide) = (h, p, d, w); RebuildToolPane(); };
            presets.Children.Add(b);
        }
        _toolPane.Children.Add(presets);

        var sh = new Slider { Header = "水平旋转（度，负数向左）", Minimum = -60, Maximum = 60, Value = _angleH, StepFrequency = 1 };
        var sp = new Slider { Header = "俯仰（度，正数俯视、负数仰视）", Minimum = -45, Maximum = 45, Value = _angleP, StepFrequency = 1 };
        var sd = new Slider { Header = "镜头距离（越大越远）", Minimum = 1, Maximum = 10, Value = _angleD, StepFrequency = 0.5 };
        var sw = new ToggleSwitch { Header = "广角镜头", OnContent = "广角", OffContent = "标准", IsOn = _angleWide };
        void RefreshPreview() { if (_anglePromptPreview is not null) _anglePromptPreview.Text = ImageGeometry.AnglePrompt(_angleH, _angleP, _angleD, _angleWide); }
        sh.ValueChanged += (_, _) => { _angleH = (int)sh.Value; RefreshPreview(); };
        sp.ValueChanged += (_, _) => { _angleP = (int)sp.Value; RefreshPreview(); };
        sd.ValueChanged += (_, _) => { _angleD = sd.Value; RefreshPreview(); };
        sw.Toggled += (_, _) => { _angleWide = sw.IsOn; RefreshPreview(); };
        _toolPane.Children.Add(sh);
        _toolPane.Children.Add(sp);
        _toolPane.Children.Add(sd);
        _toolPane.Children.Add(sw);
        _toolPane.Children.Add(ModelCombo(Capability.Image, ref _angleModel, m => _angleModel = m));

        _anglePromptPreview = Hint(ImageGeometry.AnglePrompt(_angleH, _angleP, _angleD, _angleWide));
        _toolPane.Children.Add(_anglePromptPreview);

        void Run()
        {
            ImageTools.ExecuteAngle(_host, _node, _angleH, _angleP, _angleD, _angleWide, _angleModel, select: false);
            RebuildStrip();
            ShowNotice("已开始生成新视角，完成后可在左侧「新版本」切换查看。");
        }
        _runCurrentTool = Run;
        _toolPane.Children.Add(PrimaryCapsule(PrimaryPill("生成新视角", "\uE89E", Run)));
    }

    private void BuildUpscalePane()
    {
        _toolPane.Children.Add(Hint("用 Real-ESRGAN 在本机显卡上放大，图片不上传，结果放在画布右侧新节点。"));
        var models = Upscaler.Models();
        if (models.Count == 0)
        {
            _toolPane.Children.Add(Hint($"尚未检测到本地放大工具。请将 Real-ESRGAN-ncnn-vulkan（realesrgan-ncnn-vulkan.exe 与 models 文件夹）放到：\n{Upscaler.Dir}"));
            var openDir = new Button { Style = (Style)Application.Current.Resources["ActionButton"], Content = "打开数据目录" };
            openDir.Click += (_, _) => { Directory.CreateDirectory(Upscaler.Dir); Process.Start(new ProcessStartInfo(Upscaler.Dir) { UseShellExecute = true }); };
            _toolPane.Children.Add(openDir);
            return;
        }
        _upscaleModel ??= models[0].Name;
        var modelBox = new ComboBox { Header = "放大模型", HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = models.Select(m => m.Label).ToList(), SelectedIndex = Math.Max(0, models.FindIndex(m => m.Name == _upscaleModel)) };
        modelBox.SelectionChanged += (_, _) => { if (modelBox.SelectedIndex >= 0) _upscaleModel = models[modelBox.SelectedIndex].Name; };
        _toolPane.Children.Add(modelBox);

        var factorRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var activeBg = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        foreach (var f in new[] { 2, 4 })
        {
            var b = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Height = 30, MinHeight = 0, Padding = new(14, 0, 14, 0), CornerRadius = new(15), Content = new TextBlock { Text = $"{f}×", FontSize = 12.5, FontWeight = _upscaleFactor == f ? FontWeights.SemiBold : FontWeights.Normal } };
            if (_upscaleFactor == f) b.Background = activeBg;
            b.Click += (_, _) => { _upscaleFactor = f; RebuildToolPane(); };
            factorRow.Children.Add(b);
        }
        _toolPane.Children.Add(new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "倍数", FontSize = 12.5 }, factorRow } });
        if (_node.NaturalWidth > 0 && _node.NaturalHeight > 0)
            _toolPane.Children.Add(Hint($"预计输出：{_node.NaturalWidth}×{_node.NaturalHeight} → {_node.NaturalWidth * _upscaleFactor}×{_node.NaturalHeight * _upscaleFactor} 像素"));

        void Run()
        {
            _ = _host.Generator.UpscaleAsync(_node, _upscaleModel ?? models[0].Name, _upscaleFactor);
            RebuildStrip();
            ShowNotice($"已开始本地 {_upscaleFactor}× 放大，完成后会出现在左侧「新版本」。");
        }
        _runCurrentTool = Run;
        _toolPane.Children.Add(PrimaryCapsule(PrimaryPill($"放大 {_upscaleFactor}× 到新节点", "\uE8A3", Run)));
    }

    private void BuildReversePane()
    {
        _toolPane.Children.Add(Hint("让文本模型看图写出可复用的中文提示词，结果是画布右侧连着这张图的新文本节点。"));
        _toolPane.Children.Add(ModelCombo(Capability.Text, ref _reverseModel, m => _reverseModel = m));

        var styleWrap = new WrapPanel { HorizontalSpacing = 4, VerticalSpacing = 4 };
        var activeBg = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        for (var i = 0; i < ImageTools.ReverseStyles.Length; i++)
        {
            var idx = i;
            var on = _reverseStyle == idx;
            var b = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Height = 28, MinHeight = 0, Padding = new(10, 0, 10, 0), CornerRadius = new(14), Content = new TextBlock { Text = ImageTools.ReverseStyles[idx].Label, FontSize = 12.5, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal } };
            if (on) b.Background = activeBg;
            b.Click += (_, _) => { _reverseStyle = idx; RebuildToolPane(); };
            styleWrap.Children.Add(b);
        }
        _toolPane.Children.Add(new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "写法", FontSize = 12.5 }, styleWrap } });

        void Run()
        {
            var prompt = ImageTools.ReverseStyles[Math.Clamp(_reverseStyle, 0, ImageTools.ReverseStyles.Length - 1)].Prompt;
            ImageTools.ExecuteReversePrompt(_host, _node, _reverseModel, prompt, select: false);
            ShowNotice("正在反推提示词，结果文本节点已创建在画布右侧。");
        }
        _runCurrentTool = Run;
        _toolPane.Children.Add(PrimaryCapsule(PrimaryPill("反推提示词", "\uE8C1", Run)));
    }

    private void ShowNotice(string text)
    {
        _statusNotice.Text = text;
        _statusNotice.Visibility = Visibility.Visible;
    }

    // ---------- Bottom Bar (reads the shared command table) ----------

    private CommandContext SingleContext() => CommandContext.From(_host.Doc, [_node.Id],
        n => _host.MarkSupport(n) is var (m, p, u) && (m || p || u), CanvasGenerator.CanGenerate, _ => false, id => _host.Generator.IsRunning(id));

    private void RebuildBottom()
    {
        _bottomLeft.Children.Clear();
        var ctx = SingleContext();
        foreach (var id in new[] { "keep-one", "promote", "library" })
        {
            var spec = CanvasCommands.Find(id);
            if (!spec.Visible(ctx)) continue;
            var btn = new Button
            {
                Style = (Style)Application.Current.Resources["ToolbarButton"],
                Height = 32, MinHeight = 0, Padding = new(10, 0, 12, 0), CornerRadius = new(16),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new FontIcon { Glyph = spec.Glyph, FontSize = 13 }, new TextBlock { Text = spec.Name, FontSize = 13 } } },
            };
            AutomationProperties.SetName(btn, spec.Name);
            ToolTipService.SetToolTip(btn, spec.Tip);
            btn.Click += async (_, _) => await RunBottomCommandAsync(id);
            _bottomLeft.Children.Add(btn);
        }

        var bitmaps = BatchSiblings().Where(n => !Media.IsSvg(n.Content)).ToList();
        if (bitmaps.Count >= 2)
        {
            var cmp = new Button
            {
                Style = (Style)Application.Current.Resources["ToolbarButton"],
                Height = 32, MinHeight = 0, Padding = new(10, 0, 12, 0), CornerRadius = new(16),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new FontIcon { Glyph = "\uE8AB", FontSize = 13 }, new TextBlock { Text = "和这一批对比", FontSize = 13 } } },
            };
            AutomationProperties.SetName(cmp, "和这一批对比");
            cmp.Click += async (_, _) =>
            {
                var ids = bitmaps.Take(CompareLayout.MaxPictures).Select(n => n.Id).ToList();
                _close();
                _host.Select(ids);
                await _host.RunAsync("compare");
            };
            _bottomLeft.Children.Add(cmp);
        }
    }

    private void ShowMoreMenu()
    {
        var ctx = SingleContext();
        var menu = new MenuFlyout();
        foreach (var id in new[] { "recompose", "replace", "save-as", "open", "reveal", "delete" })
        {
            var spec = CanvasCommands.Find(id);
            if (!spec.Visible(ctx)) continue;
            if (id == "delete" && menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
            var item = new MenuFlyoutItem { Text = spec.Name, Icon = new FontIcon { Glyph = spec.Glyph } };
            if (spec.Shortcut is not null) item.KeyboardAcceleratorTextOverride = spec.Shortcut;
            item.Click += async (_, _) => await RunBottomCommandAsync(id);
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.ShowAt(_moreButton);
    }

    private async Task RunBottomCommandAsync(string id)
    {
        var targetId = _node.Id;
        var closeFirst = id is "keep-one" or "promote" or "replace" or "delete";
        if (closeFirst) _close();
        _host.Select([targetId]);
        await _host.RunAsync(id);
        if (!closeFirst && !_disposed && id == "library") ShowNotice("已加入当前画布的资料库。");
    }

    // ---------- Keyboard ----------

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var typing = e.OriginalSource is TextBox or PasswordBox or RichEditBox or NumberBox;
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            if (typing) _closeButton.Focus(FocusState.Programmatic);
            else _close();
            return;
        }
        if (typing)
        {
            if (e.Key == VirtualKey.Enter && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                e.Handled = true;
                _runCurrentTool?.Invoke();
            }
            return;
        }
        switch (e.Key)
        {
            case VirtualKey.Space: _space = true; break;
            case VirtualKey.Left: e.Handled = true; StepBatch(-1); break;
            case VirtualKey.Right: e.Handled = true; StepBatch(1); break;
            case VirtualKey.Add: e.Handled = true; StepZoom(1.25); break;
            case VirtualKey.Subtract: e.Handled = true; StepZoom(1 / 1.25); break;
            case VirtualKey.Number0 when Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down):
                e.Handled = true; ResetView(); break;
        }
    }
}
