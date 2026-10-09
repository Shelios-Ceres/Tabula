using System.Numerics;
using InfiniteCanvas.Core.Canvas;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Storage.Streams;
using Windows.UI;
using Rect = Windows.Foundation.Rect;

namespace InfiniteCanvas.App.Board;

/// <summary>
/// Local edit marks on one image: a brush mask (GPT Image inpainting) and / or points and boxes (Seedream interactive editing).
/// The mask is painted at the image's own pixel size so it can be sent as-is.
/// </summary>
public sealed class ImageMarkDialog : ContentDialog
{
    private const string Brush = "brush", Eraser = "eraser";
    private readonly string _imagePath;
    private readonly int _width, _height;
    private readonly double _scale;
    private readonly byte[]? _initialMask;
    private readonly CanvasControl _canvas;
    private readonly Slider _size = new() { Minimum = 6, Maximum = 120, Value = 36, Width = 160, Header = "笔刷大小" };
    private CanvasBitmap? _image;
    private CanvasRenderTarget? _paint;
    private string _tool;
    private Point? _last, _boxStart;
    private Point _pointer;
    private bool _painted;
    private Color _accent = Colors.DodgerBlue;

    public List<ImageSelection> Selections { get; }
    /// <summary>The exported PNG mask, or null when nothing is painted.</summary>
    public byte[]? MaskPng { get; private set; }

    public ImageMarkDialog(string imagePath, int width, int height, byte[]? mask, IEnumerable<ImageSelection> selections, bool allowMask, bool allowMarks)
    {
        _imagePath = imagePath;
        (_width, _height) = (width, height);
        _initialMask = mask;
        _painted = mask is not null;
        Selections = [.. selections];
        // Fit the window: at most 960×620, never below a usable minimum.
        var root = App.Window.XamlRoot.Size;
        _scale = Math.Min(Math.Max(200, Math.Min(960, root.Width - 120)) / width, Math.Max(160, Math.Min(620, root.Height - 320)) / height);
        XamlRoot = App.Window.XamlRoot;
        Title = "局部编辑";
        PrimaryButtonText = "保存";
        CloseButtonText = "取消";
        DefaultButton = ContentDialogButton.Primary;
        IsPrimaryButtonEnabled = false;
        Resources["ContentDialogMaxWidth"] = 1400d;
        Resources["ContentDialogMaxHeight"] = 1100d;
        if (Application.Current.Resources.TryGetValue("SystemAccentColor", out var accent) && accent is Color color) _accent = color;

        var tools = new SelectorBar();
        void Tool(string text, string tag) => tools.Items.Add(new SelectorBarItem { Text = text, Tag = tag });
        if (allowMask) { Tool("涂抹重绘区域", Brush); Tool("擦除", Eraser); }
        if (allowMarks) { Tool("点选", ImageSelection.Point); Tool("框选", ImageSelection.Box); }
        tools.SelectedItem = tools.Items[0];
        _tool = (string)tools.Items[0].Tag;
        tools.SelectionChanged += (s, _) => { _tool = (string)s.SelectedItem.Tag; _size.Visibility = _tool is Brush or Eraser ? Visibility.Visible : Visibility.Collapsed; };
        _size.Visibility = allowMask ? Visibility.Visible : Visibility.Collapsed;

        var pill = (Style)Application.Current.Resources["ActionButton"];
        var undo = new Button { Content = "撤销上一个选区", Style = pill, Visibility = allowMarks ? Visibility.Visible : Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Bottom };
        undo.Click += (_, _) => { if (Selections.Count > 0) Selections.RemoveAt(Selections.Count - 1); _canvas!.Invalidate(); };
        var clear = new Button { Content = "全部清除", Style = pill, VerticalAlignment = VerticalAlignment.Bottom };
        clear.Click += (_, _) => ClearAll();
        var toolbar = new Grid { ColumnSpacing = 12 };
        toolbar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_size);
        actions.Children.Add(undo);
        actions.Children.Add(clear);
        Grid.SetColumn(actions, 1);
        toolbar.Children.Add(tools);
        toolbar.Children.Add(actions);

        _canvas = new CanvasControl { Width = width * _scale, Height = height * _scale, ClearColor = Colors.Transparent };
        _canvas.CreateResources += (s, e) => e.TrackAsyncAction(LoadAsync(s).AsAsyncAction());
        _canvas.Draw += Draw;
        _canvas.PointerPressed += Pressed;
        _canvas.PointerMoved += Moved;
        _canvas.PointerReleased += Released;
        _canvas.PointerExited += (_, _) => { _pointer = new(-100, -100); _canvas.Invalidate(); };
        _canvas.PointerCaptureLost += (_, _) => { _last = null; _boxStart = null; _canvas.Invalidate(); };

        var hint = (allowMask, allowMarks) switch
        {
            (true, true) => "涂抹的区域会被重绘；点选和框选用来告诉模型要改的位置。",
            (true, false) => "涂抹需要重绘的区域，其余部分保持不变。生成时这张图会作为第一张参考图，蒙版只作用于它。",
            _ => "点选或框选要修改的位置，并在提示词里描述怎么改；位置会以坐标形式附在提示词后。",
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(toolbar);
        content.Children.Add(new Border { Child = _canvas, CornerRadius = new(8), HorizontalAlignment = HorizontalAlignment.Center, Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"] });
        content.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["SecondaryCaption"] });
        Content = content;

        PrimaryButtonClick += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            // Always release the deferral: a stuck dialog would block every dialog queued behind it.
            try { MaskPng = _painted ? await ExportMaskAsync() : null; }
            catch (Exception ex)
            {
                App.Log(ex);
                e.Cancel = true;
                Title = "局部编辑 · 蒙版导出失败";
            }
            finally { deferral.Complete(); }
        };
        Closed += (_, _) => { _canvas.RemoveFromVisualTree(); _image?.Dispose(); _paint?.Dispose(); };
    }

    private async Task LoadAsync(CanvasControl sender)
    {
        try
        {
            await LoadCoreAsync(sender);
            // Saving before the old mask is loaded would erase it.
            IsPrimaryButtonEnabled = true;
        }
        catch (Exception ex) when (sender.Device?.IsDeviceLost(ex.HResult) != true)
        {
            App.Log(ex);
            _image?.Dispose();
            _paint?.Dispose();
            (_image, _paint) = (null, null);
            Title = "局部编辑 · 图片载入失败";
        }
    }

    private async Task LoadCoreAsync(CanvasControl sender)
    {
        using (var stream = File.OpenRead(_imagePath).AsRandomAccessStream()) _image = await CanvasBitmap.LoadAsync(sender, stream);
        _paint = new CanvasRenderTarget(sender, _width, _height, 96);
        using (var ds = _paint.CreateDrawingSession()) ds.Clear(Colors.Transparent);
        if (_initialMask is null) return;
        // An existing mask is transparent where it repaints; show those pixels as painted.
        using var maskStream = new MemoryStream(_initialMask);
        using var mask = await CanvasBitmap.LoadAsync(sender, maskStream.AsRandomAccessStream());
        if (mask.SizeInPixels.Width != _width || mask.SizeInPixels.Height != _height) return;
        var pixels = mask.GetPixelBytes();
        var paint = new byte[pixels.Length];
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i + 3] < 128) (paint[i], paint[i + 1], paint[i + 2], paint[i + 3]) = (_accent.B, _accent.G, _accent.R, 255);
        _paint.SetPixelBytes(paint);
    }

    private Vector2 ToImage(Point p) => new((float)(p.X / _scale), (float)(p.Y / _scale));

    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_image is null || _paint is null) return;
        var ds = args.DrawingSession;
        var view = new Rect(0, 0, sender.Width, sender.Height);
        ds.DrawImage(_image, view);
        ds.DrawImage(_paint, view, new Rect(0, 0, _width, _height), 0.5f);
        for (var i = 0; i < Selections.Count; i++)
        {
            var s = Selections[i];
            var a = new Point(s.X * view.Width, s.Y * view.Height);
            if (s.Kind == ImageSelection.Point)
            {
                ds.FillCircle((float)a.X, (float)a.Y, 11, _accent);
                ds.DrawCircle((float)a.X, (float)a.Y, 11, Colors.White, 2);
            }
            else
            {
                var rect = new Rect(a, new Point(s.X2 * view.Width, s.Y2 * view.Height));
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
        if (_tool is Brush or Eraser)
        {
            // Dark halo under a white ring keeps the cursor visible on any image.
            var (x, y, r) = ((float)_pointer.X, (float)_pointer.Y, (float)(_size.Value / 2));
            ds.DrawCircle(x, y, r, Color.FromArgb(160, 0, 0, 0), 3);
            ds.DrawCircle(x, y, r, Colors.White, 1.5f);
        }
    }

    private void Pressed(object sender, PointerRoutedEventArgs e)
    {
        _pointer = e.GetCurrentPoint(_canvas).Position;
        _canvas.CapturePointer(e.Pointer);
        if (_tool is Brush or Eraser) { _last = _pointer; Stroke(_pointer); }
        else if (_tool == ImageSelection.Box) _boxStart = _pointer;
        else if (_tool == ImageSelection.Point) Selections.Add(new(ImageSelection.Point, Fraction(_pointer.X, _canvas.Width), Fraction(_pointer.Y, _canvas.Height)));
        _canvas.Invalidate();
    }

    private void Moved(object sender, PointerRoutedEventArgs e)
    {
        _pointer = e.GetCurrentPoint(_canvas).Position;
        if (_last is not null) Stroke(_pointer);
        _canvas.Invalidate();
    }

    private void Released(object sender, PointerRoutedEventArgs e)
    {
        if (_boxStart is { } start && Math.Abs(_pointer.X - start.X) > 6 && Math.Abs(_pointer.Y - start.Y) > 6)
        {
            var (w, h) = (_canvas.Width, _canvas.Height);
            Selections.Add(new(ImageSelection.Box, Fraction(Math.Min(start.X, _pointer.X), w), Fraction(Math.Min(start.Y, _pointer.Y), h), Fraction(Math.Max(start.X, _pointer.X), w), Fraction(Math.Max(start.Y, _pointer.Y), h)));
        }
        _last = null;
        _boxStart = null;
        _canvas.ReleasePointerCapture(e.Pointer);
        _canvas.Invalidate();
    }

    private static double Fraction(double value, double size) => Math.Clamp(value / size, 0, 1);

    private void Stroke(Point to)
    {
        if (_paint is null || _last is not { } from) return;
        var erase = _tool == Eraser;
        var radius = (float)(_size.Value / 2 / _scale);
        using var ds = _paint.CreateDrawingSession();
        if (erase) ds.Blend = CanvasBlend.Copy;
        var color = erase ? Colors.Transparent : _accent;
        using var round = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
        ds.DrawLine(ToImage(from), ToImage(to), color, radius * 2, round);
        ds.FillCircle(ToImage(to), radius, color);
        _last = to;
        if (!erase) _painted = true;
    }

    private void ClearAll()
    {
        Selections.Clear();
        if (_paint is not null)
            using (var ds = _paint.CreateDrawingSession()) ds.Clear(Colors.Transparent);
        _painted = false;
        _canvas.Invalidate();
    }

    /// <summary>Painted pixels become fully transparent (repaint here); everything else is opaque white.</summary>
    private async Task<byte[]?> ExportMaskAsync()
    {
        if (_paint is null) return null;
        var pixels = _paint.GetPixelBytes();
        var mask = new byte[pixels.Length];
        var any = false;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] > 0) { any = true; continue; }
            mask[i] = mask[i + 1] = mask[i + 2] = mask[i + 3] = 255;
        }
        if (!any) return null;
        using var bitmap = CanvasBitmap.CreateFromBytes(_canvas.Device, mask, _width, _height, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var stream = new InMemoryRandomAccessStream();
        await bitmap.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
