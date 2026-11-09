using System.Numerics;
using InfiniteCanvas.Core.Canvas;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
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

namespace InfiniteCanvas.App.Board;

public enum CompareMode { Slider, Overlay, Difference, Flicker }

/// <summary>
/// The full-screen compare view: two pictures A and B drawn into one Win2D canvas as a slider, an overlay, a difference (with gain and an optional heat map)
/// or a flicker. B is scaled to A's width and centred; the arrow keys or a drag move B by whole pixels so the two can be lined up (offset only).
/// </summary>
public sealed class CompareSurface : Grid, IDisposable
{
    private readonly CanvasBitmap _a, _b;
    private readonly Action _close;
    private readonly CanvasControl _canvas = new() { ClearColor = Colors.Transparent };
    private CompareMode _mode;
    private double _split = 0.5, _opacity = 0.5, _gain = 1;
    private bool _heat, _showB;
    private Vector2 _offset;
    private Point? _last;
    private readonly Microsoft.UI.Xaml.DispatcherTimer _flicker = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly Dictionary<CompareMode, Button> _modeButtons = [];
    private readonly Slider _amount = new() { Minimum = 0, Maximum = 100, Value = 50, Width = 160, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleSwitch _heatSwitch = new() { OnContent = "热力", OffContent = "灰度", VerticalAlignment = VerticalAlignment.Center };

    public CompareSurface(CanvasNode a, CanvasNode b, CanvasBitmap bitmapA, CanvasBitmap bitmapB, double width, double height, Action onClose)
    {
        _a = bitmapA;
        _b = bitmapB;
        _close = onClose;
        (Width, Height) = (width, height);
        Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["GlassThickBrush"];
        RowSpacing = 8;
        Padding = new Thickness(0, 0, 0, 16);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        AutomationProperties.SetName(this, "图片对比");

        string Caption(string name, CanvasNode node, CanvasBitmap bitmap) => $"{name}：{node.Title} · {bitmap.SizeInPixels.Width} × {bitmap.SizeInPixels.Height}";
        var titles = new TextBlock { Text = $"{Caption("A", a, bitmapA)}    {Caption("B", b, bitmapB)}", VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(20, 12, 12, 0) };
        foreach (var (mode, label) in new[] { (CompareMode.Slider, "滑块"), (CompareMode.Overlay, "叠加"), (CompareMode.Difference, "差异"), (CompareMode.Flicker, "闪烁") })
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
        var reset = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Content = new FontIcon { Glyph = "", FontSize = 14 } };
        AutomationProperties.SetName(reset, "重置偏移");
        ToolTipService.SetToolTip(reset, "重置偏移（方向键移动 B，Shift 一次 10 像素）");
        reset.Click += (_, _) => { _offset = Vector2.Zero; _canvas.Invalidate(); };
        bar.Children.Add(reset);
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
        SetMode(CompareMode.Slider);
    }

    private void SetMode(CompareMode mode)
    {
        _mode = mode;
        _showB = false;
        if (mode == CompareMode.Flicker) _flicker.Start(); else _flicker.Stop();
        foreach (var (key, button) in _modeButtons) button.Style = (Style)Application.Current.Resources[key == mode ? "AccentActionButton" : "ToolbarButton"];
        _amount.Visibility = mode is CompareMode.Overlay or CompareMode.Difference ? Visibility.Visible : Visibility.Collapsed;
        _heatSwitch.Visibility = mode == CompareMode.Difference ? Visibility.Visible : Visibility.Collapsed;
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

    private void MoveBy(PointerRoutedEventArgs e, int phase)
    {
        var point = e.GetCurrentPoint(_canvas).Position;
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

    public void Dispose()
    {
        _flicker.Stop();
        _canvas.RemoveFromVisualTree();
        _a.Dispose();
        _b.Dispose();
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
