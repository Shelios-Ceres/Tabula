using System.Numerics;
using InfiniteCanvas.Core.Canvas;
using InfiniteCanvas.Core.Storage;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI;
using Rect = Windows.Foundation.Rect;

namespace InfiniteCanvas.App.Board;

/// <summary>
/// Image node tools: crop, split into a grid, a new camera angle and reverse prompt.
/// Every tool leaves the source untouched and puts its result in new nodes to the right, connected from it, as one undo step.
/// </summary>
public static class ImageTools
{
    public const string ReversePromptText = "请仔细观察输入的图片，写一段可以用 AI 生图模型复现它的中文提示词：主体、动作、构图、镜头与视角、光线、色彩、材质、风格和关键细节都要写到。只输出提示词本身，不要解释。";

    private static string? PathOf(CanvasView view, CanvasNode node) =>
        node.Kind == NodeKind.Image && node.HasContent && ProjectStore.AssetPath(view.Doc.Id, node.Content!) is { } path && File.Exists(path) ? path : null;

    private static async Task<CanvasBitmap?> LoadAsync(string path)
    {
        try { return await CanvasBitmap.LoadAsync(CanvasDevice.GetSharedDevice(), path); }
        catch (Exception ex)
        {
            App.Log(ex);
            await Dialogs.MessageAsync("无法打开图片", "这张图片无法解码，或尺寸超过显卡支持的最大位图尺寸。");
            return null;
        }
    }

    /// <summary>Cut a pixel box out of the bitmap; JPEG stays JPEG, everything else becomes PNG.</summary>
    private static async Task<(byte[] Bytes, string Mime)> CutAsync(CanvasBitmap source, PixelRect box, string sourceMime)
    {
        using var target = new CanvasRenderTarget(source.Device, box.Width, box.Height, 96);
        using (var session = target.CreateDrawingSession())
        {
            session.Clear(Colors.Transparent);
            session.DrawImage(source, new Rect(0, 0, box.Width, box.Height), new Rect(box.X, box.Y, box.Width, box.Height));
        }
        var jpeg = sourceMime == "image/jpeg";
        using var stream = new InMemoryRandomAccessStream();
        await target.SaveAsync(stream, jpeg ? CanvasBitmapFileFormat.Jpeg : CanvasBitmapFileFormat.Png, 0.95f);
        var bytes = new byte[stream.Size];
        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
        }
        return (bytes, jpeg ? "image/jpeg" : "image/png");
    }

    private static CanvasNode ImageNode(CanvasView view, byte[] bytes, string mime, int width, int height, string title)
    {
        var node = CanvasNode.Create(NodeKind.Image, 0, 0);
        node.Title = title;
        node.Content = ProjectStore.SaveAsset(view.Doc.Id, bytes, mime);
        node.MimeType = mime;
        node.NaturalWidth = width;
        node.NaturalHeight = height;
        node.Height = Math.Round(node.Width * height / width);
        node.Status = NodeStatus.Success;
        return node;
    }

    /// <summary>Right of the source at its top edge, shifted down past anything already there.</summary>
    private static void PlaceRightOf(CanvasView view, CanvasNode source, CanvasNode node)
    {
        node.X = source.Right + 80;
        node.Y = source.Y;
        node.X = CanvasGraph.FreeX(node, view.Doc.Nodes);
        node.GroupId = CanvasGraph.ContainingGroup(view.Doc, node)?.Id;
    }

    private static void Finish(CanvasView view, CanvasNode source, IReadOnlyList<CanvasNode> nodes, bool connectAll = true)
    {
        foreach (var node in nodes)
        {
            view.Doc.Nodes.Add(node);
            if (connectAll) view.Doc.Connections.Add(CanvasConnection.Create(source.Id, node.Id));
        }
        view.Select(nodes.Select(n => n.Id));
        view.EnsureVisible(CanvasGraph.Bounds(nodes));
    }

    // ---------- Crop ----------

    public static async Task CropAsync(CanvasView view, CanvasNode node)
    {
        if (PathOf(view, node) is not { } path || await LoadAsync(path) is not { } bitmap) return;
        using (bitmap)
        {
            var dialog = new CropDialog(bitmap);
            if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
            var box = dialog.Box;
            var (bytes, mime) = await CutAsync(bitmap, box, node.MimeType ?? Media.MimeOf(node.Content!));
            view.History.Record();
            var result = ImageNode(view, bytes, mime, box.Width, box.Height, $"{node.Title} · 裁剪");
            PlaceRightOf(view, node, result);
            Finish(view, node, [result]);
        }
    }

    // ---------- Split ----------

    public static async Task SplitAsync(CanvasView view, CanvasNode node)
    {
        if (PathOf(view, node) is not { } path || await LoadAsync(path) is not { } bitmap) return;
        using (bitmap)
        {
            var dialog = new SplitDialog(bitmap);
            if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
            var (rows, columns) = (dialog.Rows, dialog.Columns);
            var (width, height) = ((int)bitmap.SizeInPixels.Width, (int)bitmap.SizeInPixels.Height);
            var tiles = ImageGeometry.Split(width, height, rows, columns);
            var mime = node.MimeType ?? Media.MimeOf(node.Content!);
            var cut = new List<(byte[] Bytes, string Mime, PixelRect Box)>();
            foreach (var tile in tiles)
            {
                var (tileBytes, tileMime) = await CutAsync(bitmap, tile, mime);
                cut.Add((tileBytes, tileMime, tile));
            }
            view.History.Record();
            // Tiles keep the picture's layout: the same grid, at the source node's scale, with a small gap.
            const double gap = 16;
            var scale = node.Width / width;
            var origin = (X: node.Right + 80, Y: node.Y);
            var nodes = new List<CanvasNode>();
            for (var i = 0; i < cut.Count; i++)
            {
                var (bytes, type, box) = cut[i];
                var tile = ImageNode(view, bytes, type, box.Width, box.Height, $"{node.Title} · {i / columns + 1}-{i % columns + 1}");
                tile.Width = Math.Max(60, box.Width * scale);
                tile.Height = Math.Max(40, box.Height * scale);
                tile.X = origin.X + box.X * scale + i % columns * gap;
                tile.Y = origin.Y + box.Y * scale + i / columns * gap;
                nodes.Add(tile);
            }
            // Move the whole grid right past anything in the way, then group it.
            var bounds = CanvasGraph.Bounds(nodes);
            var probe = new CanvasNode { X = bounds.Left, Y = bounds.Top, Width = bounds.Width, Height = bounds.Height };
            var shift = CanvasGraph.FreeX(probe, view.Doc.Nodes) - bounds.Left;
            foreach (var tile in nodes) tile.X += shift;
            var rect = CanvasGraph.GroupRect(nodes);
            var group = new CanvasNode { Kind = NodeKind.Group, Title = $"{node.Title} · 拆分 {rows}×{columns}", X = rect.Left, Y = rect.Top, Width = rect.Width, Height = rect.Height };
            foreach (var tile in nodes) tile.GroupId = group.Id;
            view.Doc.Nodes.Insert(0, group);
            Finish(view, node, nodes, connectAll: false);
        }
    }

    // ---------- Angle ----------

    /// <summary>A new image node generating the same subject from another camera angle, with this image as the reference.</summary>
    public static async Task AngleAsync(CanvasView view, CanvasNode node)
    {
        if (PathOf(view, node) is not { } path) return;
        var dialog = new AngleDialog(path);
        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        view.History.Record();
        var result = CanvasNode.Create(NodeKind.Image, 0, 0);
        result.Title = $"{node.Title} · 多角度";
        result.Prompt = ImageGeometry.AnglePrompt(dialog.Horizontal, dialog.Pitch, dialog.Distance, dialog.Wide);
        result.Model = node.Model;
        result.Ratio = node.Ratio;
        result.Size = node.Size;
        result.Quality = node.Quality;
        if (node.NaturalWidth > 0) result.Height = Math.Round(result.Width * node.NaturalHeight / node.NaturalWidth);
        PlaceRightOf(view, node, result);
        Finish(view, node, [result]);
        _ = view.Generator.GenerateAsync(result);
    }

    // ---------- Reverse prompt ----------

    /// <summary>A text node that describes this image as a reusable prompt, written by the default text model.</summary>
    public static Task ReversePromptAsync(CanvasView view, CanvasNode node)
    {
        if (PathOf(view, node) is null) return Task.CompletedTask;
        view.History.Record();
        var text = CanvasNode.Create(NodeKind.Text, 0, 0);
        text.Title = $"{node.Title} · 反推提示词";
        text.Prompt = ReversePromptText;
        PlaceRightOf(view, node, text);
        Finish(view, node, [text]);
        return view.Generator.GenerateAsync(text);
    }

    // ---------- Dialogs ----------

    private static ContentDialog Shell(ContentDialog dialog, string title, string primary)
    {
        dialog.XamlRoot = App.Window.XamlRoot;
        dialog.Title = title;
        dialog.PrimaryButtonText = primary;
        dialog.CloseButtonText = "取消";
        dialog.DefaultButton = ContentDialogButton.Primary;
        dialog.Resources["ContentDialogMaxWidth"] = 1400d;
        dialog.Resources["ContentDialogMaxHeight"] = 1100d;
        return dialog;
    }

    /// <summary>Preview scale that fits the picture in at most 900 × 600 of the window.</summary>
    private static double FitScale(double width, double height)
    {
        var root = App.Window.XamlRoot.Size;
        return Math.Min(Math.Max(200, Math.Min(900, root.Width - 160)) / width, Math.Max(160, Math.Min(600, root.Height - 320)) / height);
    }

    private static Color Accent => Application.Current.Resources.TryGetValue("SystemAccentColor", out var value) && value is Color color ? color : Colors.DodgerBlue;

    private sealed class CropDialog : ContentDialog
    {
        private static readonly (string Label, double Aspect)[] Ratios = [("自由", 0), ("原图比例", -1), ("1:1", 1), ("4:3", 4 / 3d), ("3:4", 3 / 4d), ("16:9", 16 / 9d), ("9:16", 9 / 16d), ("3:2", 1.5), ("2:3", 2 / 3d)];
        private readonly CanvasBitmap _bitmap;
        private readonly int _width, _height;
        private readonly double _scale;
        private readonly CanvasControl _canvas;
        private readonly TextBlock _info = new() { Style = (Style)Application.Current.Resources["SecondaryCaption"], VerticalAlignment = VerticalAlignment.Center };
        private double _aspect;
        private Rect _box;
        private string? _drag;
        private Point _start;
        private Rect _startBox;

        public PixelRect Box => ImageGeometry.Clamp(new((int)Math.Round(_box.X), (int)Math.Round(_box.Y), (int)Math.Round(_box.Width), (int)Math.Round(_box.Height)), _width, _height);

        public CropDialog(CanvasBitmap bitmap)
        {
            _bitmap = bitmap;
            (_width, _height) = ((int)bitmap.SizeInPixels.Width, (int)bitmap.SizeInPixels.Height);
            _scale = FitScale(_width, _height);
            _box = new Rect(0, 0, _width, _height);
            Shell(this, "裁剪", "裁剪到新节点");
            var ratios = new ComboBox { Header = "比例", ItemsSource = Ratios.Select(r => r.Label).ToList(), SelectedIndex = 0, MinWidth = 140 };
            ratios.SelectionChanged += (_, _) =>
            {
                var aspect = Ratios[Math.Max(0, ratios.SelectedIndex)].Aspect;
                _aspect = aspect < 0 ? (double)_width / _height : aspect;
                if (_aspect > 0)
                {
                    var box = ImageGeometry.CenteredCrop(_width, _height, _aspect);
                    _box = new Rect(box.X, box.Y, box.Width, box.Height);
                }
                Refresh();
            };
            _canvas = new CanvasControl { Width = _width * _scale, Height = _height * _scale, ClearColor = Colors.Transparent };
            _canvas.Draw += Draw;
            _canvas.PointerPressed += Pressed;
            _canvas.PointerMoved += Moved;
            _canvas.PointerReleased += (_, e) => { _drag = null; _canvas.ReleasePointerCapture(e.Pointer); };
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { ratios, _info } };
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    bar,
                    new Border { Child = _canvas, CornerRadius = new(8), HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "拖动框内移动，拖动四角调整大小，在框外拖动重新框选。原图不会被修改。", Style = (Style)Application.Current.Resources["SecondaryCaption"] },
                },
            };
            Refresh();
        }

        private void Refresh()
        {
            var box = Box;
            _info.Text = $"{box.Width} × {box.Height} 像素";
            _canvas.Invalidate();
        }

        private Point ToImage(PointerRoutedEventArgs e)
        {
            var p = e.GetCurrentPoint(_canvas).Position;
            return new(Math.Clamp(p.X / _scale, 0, _width), Math.Clamp(p.Y / _scale, 0, _height));
        }

        private void Pressed(object sender, PointerRoutedEventArgs e)
        {
            var p = ToImage(e);
            var grip = 14 / _scale;
            Point[] corners = [new(_box.Left, _box.Top), new(_box.Right, _box.Top), new(_box.Left, _box.Bottom), new(_box.Right, _box.Bottom)];
            string[] names = ["nw", "ne", "sw", "se"];
            _drag = corners.Select((c, i) => (c, i)).Where(t => Math.Abs(t.c.X - p.X) <= grip && Math.Abs(t.c.Y - p.Y) <= grip).Select(t => names[t.i]).FirstOrDefault()
                ?? (_box.Contains(p) ? "move" : "new");
            _start = p;
            _startBox = _drag == "new" ? new Rect(p, p) : _box;
            _canvas.CapturePointer(e.Pointer);
        }

        private void Moved(object sender, PointerRoutedEventArgs e)
        {
            if (_drag is null) return;
            var p = ToImage(e);
            if (_drag == "move")
            {
                var x = Math.Clamp(_startBox.X + p.X - _start.X, 0, _width - _startBox.Width);
                var y = Math.Clamp(_startBox.Y + p.Y - _start.Y, 0, _height - _startBox.Height);
                _box = new Rect(x, y, _startBox.Width, _startBox.Height);
            }
            else
            {
                // The corner opposite the one being dragged stays put; "new" anchors at the press point.
                var anchor = _drag switch
                {
                    "nw" => new Point(_startBox.Right, _startBox.Bottom),
                    "ne" => new Point(_startBox.Left, _startBox.Bottom),
                    "sw" => new Point(_startBox.Right, _startBox.Top),
                    "se" => new Point(_startBox.Left, _startBox.Top),
                    _ => _start,
                };
                var (w, h) = (Math.Abs(p.X - anchor.X), Math.Abs(p.Y - anchor.Y));
                if (_aspect > 0)
                {
                    // Locked ratio: the longer drag wins, then the box is clipped to the image edge it grows toward.
                    if (w / Math.Max(h, 1) > _aspect) h = w / _aspect; else w = h * _aspect;
                    var maxW = p.X < anchor.X ? anchor.X : _width - anchor.X;
                    var maxH = p.Y < anchor.Y ? anchor.Y : _height - anchor.Y;
                    var fit = Math.Min(1, Math.Min(maxW / Math.Max(w, 1), maxH / Math.Max(h, 1)));
                    (w, h) = (w * fit, h * fit);
                }
                var left = p.X < anchor.X ? anchor.X - w : anchor.X;
                var top = p.Y < anchor.Y ? anchor.Y - h : anchor.Y;
                _box = new Rect(left, top, Math.Max(1, w), Math.Max(1, h));
            }
            Refresh();
        }

        private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            var ds = args.DrawingSession;
            var view = new Rect(0, 0, _width * _scale, _height * _scale);
            ds.DrawImage(_bitmap, view);
            var box = new Rect(_box.X * _scale, _box.Y * _scale, _box.Width * _scale, _box.Height * _scale);
            var shade = Color.FromArgb(140, 0, 0, 0);
            ds.FillRectangle(new Rect(0, 0, view.Width, box.Top), shade);
            ds.FillRectangle(new Rect(0, box.Bottom, view.Width, Math.Max(0, view.Height - box.Bottom)), shade);
            ds.FillRectangle(new Rect(0, box.Top, box.Left, box.Height), shade);
            ds.FillRectangle(new Rect(box.Right, box.Top, Math.Max(0, view.Width - box.Right), box.Height), shade);
            ds.DrawRectangle(box, Colors.White, 1.5f);
            for (var i = 1; i < 3; i++)
            {
                ds.DrawLine((float)(box.Left + box.Width * i / 3), (float)box.Top, (float)(box.Left + box.Width * i / 3), (float)box.Bottom, Color.FromArgb(110, 255, 255, 255));
                ds.DrawLine((float)box.Left, (float)(box.Top + box.Height * i / 3), (float)box.Right, (float)(box.Top + box.Height * i / 3), Color.FromArgb(110, 255, 255, 255));
            }
            foreach (var corner in new[] { new Vector2((float)box.Left, (float)box.Top), new((float)box.Right, (float)box.Top), new((float)box.Left, (float)box.Bottom), new((float)box.Right, (float)box.Bottom) })
            {
                ds.FillCircle(corner, 6, Colors.White);
                ds.DrawCircle(corner, 6, Accent, 2);
            }
        }
    }

    private sealed class SplitDialog : ContentDialog
    {
        private readonly CanvasBitmap _bitmap;
        private readonly int _width, _height;
        private readonly double _scale;
        private readonly CanvasControl _canvas;
        private readonly NumberBox _rows = new() { Header = "行数", Value = 2, Minimum = 1, Maximum = 12, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, MinWidth = 120 };
        private readonly NumberBox _columns = new() { Header = "列数", Value = 2, Minimum = 1, Maximum = 12, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, MinWidth = 120 };
        private readonly TextBlock _info = new() { Style = (Style)Application.Current.Resources["SecondaryCaption"], VerticalAlignment = VerticalAlignment.Bottom, Margin = new(0, 0, 0, 6) };

        public int Rows => Count(_rows);
        public int Columns => Count(_columns);
        private static int Count(NumberBox box) => double.IsNaN(box.Value) ? 1 : (int)Math.Clamp(Math.Round(box.Value), 1, 12);

        public SplitDialog(CanvasBitmap bitmap)
        {
            _bitmap = bitmap;
            (_width, _height) = ((int)bitmap.SizeInPixels.Width, (int)bitmap.SizeInPixels.Height);
            _scale = FitScale(_width, _height);
            Shell(this, "拆分", "拆分到新节点");
            _canvas = new CanvasControl { Width = _width * _scale, Height = _height * _scale, ClearColor = Colors.Transparent };
            _canvas.Draw += Draw;
            _rows.ValueChanged += (_, _) => Refresh();
            _columns.ValueChanged += (_, _) => Refresh();
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _rows, _columns, _info } },
                    new Border { Child = _canvas, CornerRadius = new(8), HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "按行列均分；拆出的图按原来的位置排在右侧并自动编组。原图不会被修改。", Style = (Style)Application.Current.Resources["SecondaryCaption"] },
                },
            };
            Refresh();
        }

        private void Refresh()
        {
            var (rows, columns) = (Rows, Columns);
            _info.Text = $"共 {rows * columns} 张，每张约 {_width / columns} × {_height / rows} 像素";
            _canvas.Invalidate();
        }

        private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            var ds = args.DrawingSession;
            ds.DrawImage(_bitmap, new Rect(0, 0, _width * _scale, _height * _scale));
            foreach (var tile in ImageGeometry.Split(_width, _height, Rows, Columns))
                ds.DrawRectangle(new Rect(tile.X * _scale, tile.Y * _scale, tile.Width * _scale, tile.Height * _scale), Colors.White, 1.5f);
        }
    }

    private sealed class AngleDialog : ContentDialog
    {
        private readonly Slider _horizontal = new() { Header = "水平旋转（度，负数向左）", Minimum = -60, Maximum = 60, Value = 30, StepFrequency = 1 };
        private readonly Slider _pitch = new() { Header = "俯仰（度，正数俯视、负数仰视）", Minimum = -45, Maximum = 45, Value = 0, StepFrequency = 1 };
        private readonly Slider _distance = new() { Header = "镜头距离（越大越远）", Minimum = 1, Maximum = 10, Value = 5, StepFrequency = 0.5 };
        private readonly ToggleSwitch _wide = new() { Header = "广角镜头", OnContent = "广角", OffContent = "标准" };
        private readonly TextBlock _prompt = new() { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["SecondaryCaption"] };

        public int Horizontal => (int)_horizontal.Value;
        public int Pitch => (int)_pitch.Value;
        public double Distance => _distance.Value;
        public bool Wide => _wide.IsOn;

        public AngleDialog(string imagePath)
        {
            Shell(this, "多角度", "生成新视角");
            var preview = new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(imagePath)) { DecodePixelWidth = 600 }, MaxWidth = 320, MaxHeight = 320, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
            foreach (var slider in new[] { _horizontal, _pitch, _distance }) slider.ValueChanged += (_, _) => Refresh();
            _wide.Toggled += (_, _) => Refresh();
            var controls = new StackPanel { Spacing = 8, Width = 340, Children = { _horizontal, _pitch, _distance, _wide, _prompt } };
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Children = { new Border { Child = preview, CornerRadius = new(8) }, controls } },
                    new TextBlock { Text = "以这张图为参考，用节点当前的图片模型重新画一个视角，结果放在右侧新节点。", Style = (Style)Application.Current.Resources["SecondaryCaption"] },
                },
            };
            Refresh();
        }

        private void Refresh() => _prompt.Text = ImageGeometry.AnglePrompt(Horizontal, Pitch, Distance, Wide);
    }
}
