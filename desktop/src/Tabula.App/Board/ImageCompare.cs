using System.Numerics;
using Tabula.Core.Canvas;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Rect = Windows.Foundation.Rect;

namespace Tabula.App.Board;

public enum CompareMode { Slider, Overlay, Difference, Flicker, Side }

/// <summary>
/// The full-screen compare view: two pictures A and B drawn into one Win2D canvas as a slider, an overlay, a difference (with gain and an optional heat map)
/// or a flicker. B is scaled to A's width and centred; the arrow keys or a drag move B by whole pixels so the two can be lined up (offset only).
/// "并排" shows two to four pictures in a grid, each with its title and pixel size, and zooms and pans them together: the same zoom factor and the same
/// point of each picture (as a fraction of its own size) in the middle of its cell.
/// </summary>
public sealed class CompareSurface : Grid, IDisposable
{
    private readonly CanvasBitmap _a, _b;
    private readonly IReadOnlyList<CanvasBitmap> _bitmaps;
    private readonly IReadOnlyList<CanvasNode> _nodes;
    private readonly Action _close;
    private readonly CanvasControl _canvas = new() { ClearColor = Colors.Transparent };
    private CompareMode _mode;
    private double _split = 0.5, _opacity = 0.5, _gain = 1;
    private bool _heat, _showB;
    private Vector2 _offset;
    private Point? _last;
    private double _zoom = 1;
    private Vector2 _center = new(0.5f, 0.5f);
    private int _dragCell;
    private const float CaptionHeight = 26, CellGap = 12, Pad = 16;
    private readonly Button _reset;
    private readonly Microsoft.UI.Xaml.DispatcherTimer _flicker = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly Dictionary<CompareMode, Button> _modeButtons = [];
    private readonly Slider _amount = new() { Minimum = 0, Maximum = 100, Value = 50, Width = 160, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleSwitch _heatSwitch = new() { OnContent = "热力", OffContent = "灰度", VerticalAlignment = VerticalAlignment.Center };

    /// <summary>The first <see cref="CompareLayout.MaxPictures"/> pictures, left to right; <paramref name="selected"/> is how many were selected, for the notice.</summary>
    public CompareSurface(IReadOnlyList<CanvasNode> nodes, IReadOnlyList<CanvasBitmap> bitmaps, int selected, double width, double height, Action onClose)
    {
        (_nodes, _bitmaps) = (nodes, bitmaps);
        (_a, _b) = (bitmaps[0], bitmaps[1]);
        _close = onClose;
        (Width, Height) = (width, height);
        Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["GlassThickBrush"];
        RowSpacing = 8;
        Padding = new Thickness(0, 0, 0, 16);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        AutomationProperties.SetName(this, "图片对比");

        string Caption(string name, CanvasNode node, CanvasBitmap bitmap) => $"{name}：{node.Title} · {bitmap.SizeInPixels.Width} × {bitmap.SizeInPixels.Height}";
        var count = nodes.Count;
        var titleText = count == 2 ? $"{Caption("A", nodes[0], bitmaps[0])}    {Caption("B", nodes[1], bitmaps[1])}"
            : $"并排 · {count} 张" + (selected > count ? $" · 已选 {selected} 张，只对比从左到右的前 {count} 张" : "");
        var titles = new TextBlock { Text = titleText, VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(20, 12, 12, 0) };
        // Two pictures keep the four ways of comparing a pair; three or four only make sense side by side.
        var modes = count == 2 ? new[] { (CompareMode.Slider, "滑块"), (CompareMode.Overlay, "叠加"), (CompareMode.Difference, "差异"), (CompareMode.Flicker, "闪烁"), (CompareMode.Side, "并排") } : new[] { (CompareMode.Side, "并排") };
        foreach (var (mode, label) in modes)
        {
            var button = new Button { Content = label, Style = (Style)Application.Current.Resources["ToolbarButton"] };
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) => SetMode(mode);
            _modeButtons[mode] = button;
            bar.Children.Add(button);
        }
        AutomationProperties.SetName(_amount, "叠加透明度或差异增益");
        AutomationProperties.SetName(_heatSwitch, "差异显示方式");
        _amount.ValueChanged += (_, _) => { if (_mode == CompareMode.Overlay) _opacity = _amount.Value / 100; else if (_mode == CompareMode.Difference) _gain = 1 + _amount.Value / 100 * 7; _canvas.Invalidate(); };
        _heatSwitch.Toggled += (_, _) => { _heat = _heatSwitch.IsOn; _canvas.Invalidate(); };
        bar.Children.Add(_amount);
        bar.Children.Add(_heatSwitch);
        _reset = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Content = new FontIcon { Glyph = "", FontSize = 14 } };
        _reset.Click += (_, _) => { _offset = Vector2.Zero; (_zoom, _center) = (1, new(0.5f, 0.5f)); _canvas.Invalidate(); };
        bar.Children.Add(_reset);
        var close = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Content = new FontIcon { Glyph = "", FontSize = 14 }, HorizontalAlignment = HorizontalAlignment.Right, AllowFocusOnInteraction = true };
        ToolTipService.SetToolTip(close, "关闭 (Esc)");
        AutomationProperties.SetName(close, "关闭对比");
        close.Click += (_, _) => _close();
        var top = new Grid { Padding = new Thickness(0, 0, 12, 0) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(titles, 1);
        SetColumn(close, 2);
        titles.Margin = new Thickness(16, 12, 16, 0);
        top.Children.Add(bar);
        top.Children.Add(titles);
        top.Children.Add(close);
        Children.Add(top);

        _canvas.Draw += Draw;
        _canvas.PointerPressed += (_, e) => { _last = e.GetCurrentPoint(_canvas).Position; _canvas.CapturePointer(e.Pointer); MoveBy(e, 0); };
        _canvas.PointerMoved += (_, e) => MoveBy(e, 1);
        _canvas.PointerReleased += (_, e) => { _last = null; _canvas.ReleasePointerCapture(e.Pointer); };
        _canvas.PointerWheelChanged += (_, e) =>
        {
            if (_mode != CompareMode.Side || !e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)) return;
            e.Handled = true;
            ZoomAt(e.GetCurrentPoint(_canvas).Position, Math.Pow(1.1, e.GetCurrentPoint(_canvas).Properties.MouseWheelDelta / 120d));
        };
        ActualThemeChanged += (_, _) => _canvas.Invalidate();
        SetRow(_canvas, 1);
        Children.Add(_canvas);
        _flicker.Tick += (_, _) => { _showB = !_showB; _canvas.Invalidate(); };
        // Tunnelling, so the arrow keys move B instead of moving focus between the buttons.
        PreviewKeyDown += (_, e) =>
        {
            var step = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) ? 10 : 1;
            var move = e.Key switch { VirtualKey.Left => new Vector2(-step, 0), VirtualKey.Right => new Vector2(step, 0), VirtualKey.Up => new Vector2(0, -step), VirtualKey.Down => new Vector2(0, step), _ => (Vector2?)null };
            if (e.Key == VirtualKey.Escape) { e.Handled = true; _close(); }
            else if (move is { } by) { _offset += by; e.Handled = true; _canvas.Invalidate(); }
        };
        Loaded += (_, _) => close.Focus(FocusState.Programmatic);
        SetMode(count == 2 ? CompareMode.Slider : CompareMode.Side);
    }

    private void SetMode(CompareMode mode)
    {
        _mode = mode;
        _showB = false;
        if (mode == CompareMode.Flicker) _flicker.Start(); else _flicker.Stop();
        foreach (var (key, button) in _modeButtons) button.Style = (Style)Application.Current.Resources[key == mode ? "AccentActionButton" : "ToolbarButton"];
        _amount.Visibility = mode is CompareMode.Overlay or CompareMode.Difference ? Visibility.Visible : Visibility.Collapsed;
        _heatSwitch.Visibility = mode == CompareMode.Difference ? Visibility.Visible : Visibility.Collapsed;
        var side = mode == CompareMode.Side;
        AutomationProperties.SetName(_reset, side ? "重置缩放和位置" : "重置偏移");
        ToolTipService.SetToolTip(_reset, side ? "重置缩放和位置（Ctrl + 滚轮缩放，拖动平移，几张一起动）" : "重置偏移（方向键移动 B，Shift 一次 10 像素）");
        AutomationProperties.SetName(_canvas, side ? "并排对比：" + string.Join("；", _nodes.Select((n, i) => $"{n.Title} {_bitmaps[i].SizeInPixels.Width} × {_bitmaps[i].SizeInPixels.Height}")) : "对比画面");
        _amount.Value = mode == CompareMode.Overlay ? _opacity * 100 : (_gain - 1) / 7 * 100;
        _canvas.Invalidate();
    }

    /// <summary>The scale from A's pixels to the canvas and where A starts on it, so the whole of A fits.</summary>
    private (float Scale, Vector2 Origin) View()
    {
        var (w, h) = (_canvas.ActualWidth, _canvas.ActualHeight);
        var scale = (float)Math.Max(0.01, Math.Min((w - 32) / _a.SizeInPixels.Width, (h - 32) / _a.SizeInPixels.Height));
        return (scale, new((float)(w - _a.SizeInPixels.Width * scale) / 2, (float)(h - _a.SizeInPixels.Height * scale) / 2));
    }

    /// <summary>B scaled to A's width and centred on A, moved by the offset (A's pixels).</summary>
    private (float Scale, Rect Box) BPlacement()
    {
        var scale = (float)_a.SizeInPixels.Width / _b.SizeInPixels.Width;
        var height = _b.SizeInPixels.Height * scale;
        return (scale, new Rect(_offset.X, (_a.SizeInPixels.Height - height) / 2 + _offset.Y, _a.SizeInPixels.Width, height));
    }

    /// <summary>Where each picture sits in the side-by-side grid: the area its picture is fitted into, and that fit's scale.</summary>
    private List<(Rect Area, float Fit)> SideCells()
    {
        var sizes = _bitmaps.Select(b => ((double)b.SizeInPixels.Width, (double)b.SizeInPixels.Height)).ToList();
        var (w, h) = (_canvas.ActualWidth - 2 * Pad, _canvas.ActualHeight - 2 * Pad);
        var (columns, rows) = CompareLayout.Choose(w, h, sizes, CaptionHeight);
        var (cellW, cellH) = ((w - (columns - 1) * CellGap) / columns, (h - (rows - 1) * CellGap) / rows - CaptionHeight);
        var cells = new List<(Rect, float)>();
        for (var i = 0; i < sizes.Count; i++)
        {
            int row = i / columns, inRow = Math.Min(columns, sizes.Count - row * columns);
            var x = Pad + (columns - inRow) * (cellW + CellGap) / 2 + i % columns * (cellW + CellGap);
            var y = Pad + row * (cellH + CaptionHeight + CellGap);
            cells.Add((new Rect(x, y, Math.Max(1, cellW), Math.Max(1, cellH)), (float)Math.Max(0.001, Math.Min(cellW / sizes[i].Item1, cellH / sizes[i].Item2))));
        }
        return cells;
    }

    /// <summary>The cell a point is over (picture or caption); the nearest one when it is in a gap.</summary>
    private static int CellAt(List<(Rect Area, float Fit)> cells, Point point) =>
        Enumerable.Range(0, cells.Count).MinBy(i => Math.Max(Math.Max(cells[i].Area.Left - point.X, point.X - cells[i].Area.Right), Math.Max(cells[i].Area.Top - point.Y, point.Y - cells[i].Area.Bottom - CaptionHeight)));

    /// <summary>Zoom every picture by <paramref name="factor"/>, keeping the point under the cursor where it is in the cell it is over.</summary>
    private void ZoomAt(Point cursor, double factor)
    {
        var cells = SideCells();
        var i = CellAt(cells, cursor);
        var (size, area) = (_bitmaps[i].SizeInPixels, cells[i].Area);
        var (cx, cy) = (area.X + area.Width / 2, area.Y + area.Height / 2);
        double Shown(double zoom) => cells[i].Fit * zoom;
        // The point of the picture (0-1) under the cursor, before and after.
        var (nx, ny) = (_center.X + (cursor.X - cx) / (size.Width * Shown(_zoom)), _center.Y + (cursor.Y - cy) / (size.Height * Shown(_zoom)));
        var next = Math.Clamp(_zoom * factor, 1, Math.Max(2, 16 / cells.Min(c => c.Fit)));
        _zoom = next;
        _center = Vector2.Clamp(new((float)(nx - (cursor.X - cx) / (size.Width * Shown(next))), (float)(ny - (cursor.Y - cy) / (size.Height * Shown(next)))), Vector2.Zero, Vector2.One);
        _canvas.Invalidate();
    }

    private void MoveBy(PointerRoutedEventArgs e, int phase)
    {
        var point = e.GetCurrentPoint(_canvas).Position;
        if (_mode == CompareMode.Side)
        {
            if (_last is not { } from || phase == 1 && !e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed) return;
            var cells = SideCells();
            if (phase == 0) _dragCell = CellAt(cells, point);
            else
            {
                var (size, shown) = (_bitmaps[_dragCell].SizeInPixels, cells[_dragCell].Fit * _zoom);
                _center = Vector2.Clamp(_center - new Vector2((float)((point.X - from.X) / (size.Width * shown)), (float)((point.Y - from.Y) / (size.Height * shown))), Vector2.Zero, Vector2.One);
            }
            _last = point;
            _canvas.Invalidate();
            return;
        }
        var (scale, origin) = View();
        if (_last is not { } last || phase == 1 && !e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed) return;
        if (_mode == CompareMode.Slider) _split = Math.Clamp((point.X - origin.X) / (_a.SizeInPixels.Width * scale), 0, 1);
        else if (phase == 1) _offset += new Vector2((float)((point.X - last.X) / scale), (float)((point.Y - last.Y) / scale));
        _last = point;
        _canvas.Invalidate();
    }

    private static float[] Ramp(Func<double, double> channel) => Enumerable.Range(0, 64).Select(i => (float)Math.Clamp(channel(i / 63d), 0, 1)).ToArray();

    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        if (_mode == CompareMode.Side) { DrawSide(ds); return; }
        var (scale, origin) = View();
        var (aw, ah) = ((float)_a.SizeInPixels.Width, (float)_a.SizeInPixels.Height);
        var (bScale, b) = BPlacement();
        var bFull = new Rect(0, 0, _b.SizeInPixels.Width, _b.SizeInPixels.Height);
        ds.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(origin);
        string? note = null;
        switch (_mode)
        {
            case CompareMode.Slider:
                ds.DrawImage(_a, 0, 0);
                var x = (float)(_split * aw);
                using (ds.CreateLayer(1f, new Rect(x, 0, aw - x, ah))) ds.DrawImage(_b, b, bFull, 1f, CanvasImageInterpolation.HighQualityCubic);
                ds.DrawLine(x, 0, x, ah, Colors.White, 2 / scale);
                ds.FillCircle(x, ah / 2, 12 / scale, Colors.White);
                break;
            case CompareMode.Overlay:
                ds.DrawImage(_a, 0, 0);
                ds.DrawImage(_b, b, bFull, (float)_opacity, CanvasImageInterpolation.HighQualityCubic);
                note = $"B {_opacity:P0}";
                break;
            case CompareMode.Difference:
                var moved = new Transform2DEffect { Source = _b, TransformMatrix = Matrix3x2.CreateScale(bScale) * Matrix3x2.CreateTranslation((float)b.X, (float)b.Y), InterpolationMode = CanvasImageInterpolation.HighQualityCubic };
                var diff = new BlendEffect { Background = _a, Foreground = moved, Mode = BlendEffectMode.Difference };
                var g = (float)_gain;
                ICanvasImage image = _heat
                    ? new TableTransferEffect
                    {
                        Source = new ColorMatrixEffect { Source = diff, ColorMatrix = new Matrix5x4 { M11 = .3f * g, M12 = .3f * g, M13 = .3f * g, M21 = .59f * g, M22 = .59f * g, M23 = .59f * g, M31 = .11f * g, M32 = .11f * g, M33 = .11f * g, M44 = 1 } },
                        RedTable = Ramp(t => 1.5 * t - 0.25 + 0.5 * t * t), GreenTable = Ramp(t => 1 - Math.Abs(2 * t - 1) * 1.4 + 0.2), BlueTable = Ramp(t => 1 - 2.2 * t), ClampOutput = true,
                    }
                    : new ColorMatrixEffect { Source = diff, ColorMatrix = new Matrix5x4 { M11 = g, M22 = g, M33 = g, M44 = 1 } };
                ds.DrawImage(image);
                note = $"差异 · 增益 ×{_gain:0.0}" + (Math.Abs(bScale - 1) > 0.001 ? " · B 已缩放" : "");
                break;
            default:
                if (_showB) ds.DrawImage(_b, b, bFull, 1f, CanvasImageInterpolation.HighQualityCubic); else ds.DrawImage(_a, 0, 0);
                note = _showB ? "B" : "A";
                break;
        }
        ds.Transform = Matrix3x2.Identity;
        var labels = new List<string>();
        if (note is not null) labels.Add(note);
        if (_offset != Vector2.Zero) labels.Add($"偏移 {_offset.X:0}, {_offset.Y:0} 像素");
        if (_mode != CompareMode.Difference && Math.Abs(bScale - 1) > 0.001) labels.Add("B 已缩放到 A 的宽度");
        for (var i = 0; i < labels.Count; i++)
        {
            ds.DrawText(labels[i], 21, 11 + 20 * i, Color.FromArgb(200, 0, 0, 0));
            ds.DrawText(labels[i], 20, 10 + 20 * i, Colors.White);
        }
    }

    private void DrawSide(CanvasDrawingSession ds)
    {
        var ink = ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
        using var format = new CanvasTextFormat { FontSize = 13, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis, HorizontalAlignment = CanvasHorizontalAlignment.Center };
        var cells = SideCells();
        for (var i = 0; i < cells.Count; i++)
        {
            var (area, fit) = cells[i];
            var bitmap = _bitmaps[i];
            var (pw, ph, shown) = ((float)bitmap.SizeInPixels.Width, (float)bitmap.SizeInPixels.Height, fit * (float)_zoom);
            var dest = new Rect(area.X + area.Width / 2 - _center.X * pw * shown, area.Y + area.Height / 2 - _center.Y * ph * shown, pw * shown, ph * shown);
            using (ds.CreateLayer(1f, area)) ds.DrawImage(bitmap, dest, new Rect(0, 0, pw, ph), 1f, shown >= 3 ? CanvasImageInterpolation.NearestNeighbor : CanvasImageInterpolation.HighQualityCubic);
            ds.DrawRectangle(area, Color.FromArgb(60, ink.R, ink.G, ink.B), 1);
            ds.DrawText($"{_nodes[i].Title} · {bitmap.SizeInPixels.Width} × {bitmap.SizeInPixels.Height}", new Rect(area.X, area.Bottom + 4, area.Width, CaptionHeight - 4), ink, format);
        }
        if (_zoom > 1) ds.DrawText($"缩放 ×{_zoom:0.0}", 20, 0, Color.FromArgb(220, ink.R, ink.G, ink.B));
    }

    public void Dispose()
    {
        _flicker.Stop();
        _canvas.RemoveFromVisualTree();
        foreach (var bitmap in _bitmaps) bitmap.Dispose();
    }
}

/// <summary>The measurement behind the image_compare tool.</summary>
internal static class ImageMeasure
{
    /// <summary>
    /// Draw both pictures at A's proportions (long edge at most 1024 pixels, the size canvas_get_image shows) with B scaled to A's width and centred,
    /// and compare the pixels both cover. Failures to decode are an <see cref="InvalidOperationException"/>.
    /// </summary>
    public static async Task<(ImageDifference Difference, bool SizeMismatch)> MeasureAsync(CanvasView view, CanvasNode a, CanvasNode b, double threshold)
    {
        var bitmaps = await ImageTools.LoadInputsAsync(view, [a, b]);
        try
        {
            var (bitmapA, bitmapB) = (bitmaps[0], bitmaps[1]);
            var k = Math.Min(1d, 1024d / Math.Max(bitmapA.SizeInPixels.Width, bitmapA.SizeInPixels.Height));
            var (w, h) = (Math.Max(1, (int)Math.Round(bitmapA.SizeInPixels.Width * k)), Math.Max(1, (int)Math.Round(bitmapA.SizeInPixels.Height * k)));
            var device = CanvasDevice.GetSharedDevice();
            using var targetA = new CanvasRenderTarget(device, w, h, 96);
            using var targetB = new CanvasRenderTarget(device, w, h, 96);
            using (var session = targetA.CreateDrawingSession())
            {
                session.Clear(Colors.Transparent);
                session.DrawImage(bitmapA, new Rect(0, 0, w, h));
            }
            var height = bitmapB.SizeInPixels.Height * (double)w / bitmapB.SizeInPixels.Width;
            using (var session = targetB.CreateDrawingSession())
            {
                session.Clear(Colors.Transparent);
                session.DrawImage(bitmapB, new Rect(0, (h - height) / 2, w, height));
            }
            var mismatch = bitmapA.SizeInPixels.Width != bitmapB.SizeInPixels.Width || bitmapA.SizeInPixels.Height != bitmapB.SizeInPixels.Height;
            return (ImageDifferenceMath.Measure(targetA.GetPixelBytes(), targetB.GetPixelBytes(), w, h, threshold), mismatch);
        }
        finally { foreach (var bitmap in bitmaps) bitmap.Dispose(); }
    }
}
