using System.Numerics;
using Tabula.Core.Canvas;
using Tabula.Core.Storage;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Svg;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI;
using Rect = Windows.Foundation.Rect;

namespace Tabula.App.Board;

/// <summary>
/// Image node tools: crop, split into a grid, a new camera angle and reverse prompt.
/// Every tool leaves the source untouched and puts its result in new nodes to the right, connected from it, as one undo step.
/// </summary>
public static class ImageTools
{
    public const string ReversePromptText = "请仔细观察输入的图片，写一段可以用 AI 生图模型复现它的中文提示词：主体、动作、构图、镜头与视角、光线、色彩、材质、风格和关键细节都要写到。只输出提示词本身，不要解释。";

    private static string? PathOf(CanvasView view, CanvasNode node) =>
        node.Kind == NodeKind.Image && node.HasContent && ProjectStore.AssetPath(view.Doc.Id, node.Content!) is { } path && File.Exists(path) ? path : null;

    /// <summary>Draw an SVG at the size it declares (1024 × 1024 when it declares none, keeping its own aspect inside) and encode it as PNG.</summary>
    public static async Task<byte[]> RasterizeSvgAsync(byte[] svg)
    {
        var device = CanvasDevice.GetSharedDevice();
        using var input = new MemoryStream(svg);
        using var document = await CanvasSvgDocument.LoadAsync(device, input.AsRandomAccessStream());
        var (width, height) = Media.SvgSize(svg) is { Width: > 0 } size ? size : (1024, 1024);
        using var target = new CanvasRenderTarget(device, width, height, 96);
        using (var session = target.CreateDrawingSession())
        {
            session.Clear(Colors.Transparent);
            session.DrawSvg(document, new Size(width, height));
        }
        using var output = new MemoryStream();
        await target.SaveAsync(output.AsRandomAccessStream(), CanvasBitmapFileFormat.Png);
        return output.ToArray();
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
        return await EncodeAsync(target, sourceMime == "image/jpeg");
    }

    /// <summary>Encode a render target: JPEG at 0.95 or PNG.</summary>
    private static async Task<(byte[] Bytes, string Mime)> EncodeAsync(CanvasRenderTarget target, bool jpeg)
    {
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

    private static void Finish(CanvasView view, CanvasNode source, IReadOnlyList<CanvasNode> nodes, bool connectAll = true, bool select = true)
    {
        foreach (var node in nodes)
        {
            view.Doc.Nodes.Add(node);
            if (connectAll) view.Doc.Connections.Add(CanvasConnection.Create(source.Id, node.Id));
        }
        if (select)
        {
            view.Select(nodes.Select(n => n.Id));
            view.EnsureVisible(CanvasGraph.Bounds(nodes));
        }
    }

    // ---------- Crop ----------

    public static async Task<CanvasNode> ExecuteCropAsync(CanvasView view, CanvasNode node, CanvasBitmap bitmap, PixelRect box, bool select = false)
    {
        var (bytes, mime) = await CutAsync(bitmap, box, node.MimeType ?? Media.MimeOf(node.Content!));
        view.History.Record();
        var result = ImageNode(view, bytes, mime, box.Width, box.Height, $"{node.Title} · 裁剪");
        PlaceRightOf(view, node, result);
        Finish(view, node, [result], connectAll: true, select: select);
        return result;
    }

    // ---------- Split ----------

    public static async Task<List<CanvasNode>> ExecuteSplitAsync(CanvasView view, CanvasNode node, CanvasBitmap bitmap, int rows, int columns, bool select = false)
    {
        var (width, height) = ((int)bitmap.SizeInPixels.Width, (int)bitmap.SizeInPixels.Height);
        (rows, columns) = (Math.Min(rows, height), Math.Min(columns, width));
        var tiles = ImageGeometry.Split(width, height, rows, columns);
        var mime = node.MimeType ?? Media.MimeOf(node.Content!);
        var cut = new List<(byte[] Bytes, string Mime, PixelRect Box)>();
        foreach (var tile in tiles)
        {
            var (tileBytes, tileMime) = await CutAsync(bitmap, tile, mime);
            cut.Add((tileBytes, tileMime, tile));
        }
        view.History.Record();
        // One scale keeps every tile's aspect and the grid spacing consistent, even when small tiles need enlarging.
        const double gap = 16;
        var scale = Math.Max(node.Width / width, tiles.Max(tile => Math.Max(60d / tile.Width, 40d / tile.Height)));
        var origin = (X: node.Right + 80, Y: node.Y);
        var nodes = new List<CanvasNode>();
        for (var i = 0; i < cut.Count; i++)
        {
            var (bytes, type, box) = cut[i];
            var tile = ImageNode(view, bytes, type, box.Width, box.Height, $"{node.Title} · {i / columns + 1}-{i % columns + 1}");
            tile.SetOption(NodeKeys.SourceId, node.Id);
            tile.Width = box.Width * scale;
            tile.Height = box.Height * scale;
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
        Finish(view, node, nodes, connectAll: false, select: select);
        return nodes;
    }

    // ---------- Compose ----------

    /// <summary>Decode every input at full size, quietly: failures come back as an <see cref="InvalidOperationException"/> naming the picture, and the caller decides how to tell the user. Dispose the bitmaps when done.</summary>
    internal static async Task<List<CanvasBitmap>> LoadInputsAsync(CanvasView view, IReadOnlyList<CanvasNode> nodes)
    {
        var bitmaps = new List<CanvasBitmap>();
        try
        {
            foreach (var node in nodes)
            {
                if (PathOf(view, node) is not { } path) throw new InvalidOperationException($"「{node.Title}」的图片文件不存在");
                try { bitmaps.Add(await CanvasBitmap.LoadAsync(CanvasDevice.GetSharedDevice(), path)); }
                catch (Exception ex) when (ex is not InvalidOperationException)
                {
                    App.Log(ex);
                    throw new InvalidOperationException($"「{node.Title}」无法解码，或尺寸超过显卡支持的最大位图尺寸", ex);
                }
            }
            return bitmaps;
        }
        catch
        {
            foreach (var bitmap in bitmaps) bitmap.Dispose();
            throw;
        }
    }

    private static int DeviceMax => (int)CanvasDevice.GetSharedDevice().MaximumBitmapSizeInPixels;

    private static bool IsJpeg(CanvasNode node) => (node.MimeType ?? Media.MimeOf(node.Content!)) == "image/jpeg";

    /// <summary>Draw the composition on the UI thread, as the other image tools do. Every cell is clipped to its own box; JPEG only when every input is JPEG and the background is opaque.</summary>
    private static async Task<(byte[] Bytes, string Mime, ComposeResult Layout)> RenderComposeAsync(IReadOnlyList<CanvasBitmap> bitmaps, ComposeSpec spec, bool inputsAreJpeg)
    {
        var device = CanvasDevice.GetSharedDevice();
        var layout = ImageCompose.Layout(spec, bitmaps.Select(b => ((int)b.SizeInPixels.Width, (int)b.SizeInPixels.Height)).ToList(), DeviceMax);
        var (red, green, blue, alpha) = ImageCompose.ParseBackground(spec.Background)!.Value;
        using var target = new CanvasRenderTarget(device, layout.Width, layout.Height, 96);
        using (var session = target.CreateDrawingSession())
        {
            session.Clear(Color.FromArgb(alpha, red, green, blue));
            for (var i = 0; i < bitmaps.Count; i++)
            {
                var place = layout.Cells[i];
                using var clip = session.CreateLayer(1f, new Rect(place.Cell.X, place.Cell.Y, place.Cell.Width, place.Cell.Height));
                session.DrawImage(bitmaps[i], new Rect(place.X, place.Y, place.Width, place.Height), new Rect(0, 0, bitmaps[i].SizeInPixels.Width, bitmaps[i].SizeInPixels.Height), 1f, CanvasImageInterpolation.HighQualityCubic);
            }
        }
        var (bytes, mime) = await EncodeAsync(target, inputsAreJpeg && alpha == 255);
        return (bytes, mime, layout);
    }

    /// <summary>Render and put the result in a new node right of the inputs, connected from each of them, as one undo step.</summary>
    private static async Task<(CanvasNode Node, ComposeResult Layout)> ComposeIntoNewNodeAsync(CanvasView view, IReadOnlyList<CanvasNode> inputs, IReadOnlyList<CanvasBitmap> bitmaps, ComposeSpec spec)
    {
        var (bytes, mime, layout) = await RenderComposeAsync(bitmaps, spec, inputs.All(IsJpeg));
        if (inputs.Any(n => view.Doc.Find(n.Id) != n)) throw new InvalidOperationException("拼版期间有输入的图片被删除，已取消");
        view.History.Record();
        var node = ImageNode(view, bytes, mime, layout.Width, layout.Height, $"拼版 · {inputs.Count} 张");
        // As tall as the tallest input, as wide as the picture's proportion makes it.
        node.Height = inputs.Max(n => n.Height);
        node.Width = Math.Round(node.Height * layout.Width / layout.Height);
        var bounds = CanvasGraph.Bounds(inputs);
        node.X = bounds.Right + 80;
        node.Y = bounds.Top;
        node.X = CanvasGraph.FreeX(node, view.Doc.Nodes);
        node.GroupId = CanvasGraph.ContainingGroup(view.Doc, node)?.Id;
        node.SetOption(NodeKeys.Compose, Tabula.Core.Json.Serialize(spec));
        view.Doc.Nodes.Add(node);
        foreach (var input in inputs) view.Doc.Connections.Add(CanvasConnection.Create(input.Id, node.Id));
        view.Select([node.Id]);
        view.EnsureVisible(CanvasGraph.Bounds([node]));
        return (node, layout);
    }

    /// <summary>The same composition for agent tools: no dialog, everything that goes wrong is an <see cref="InvalidOperationException"/>.</summary>
    public static async Task<(string Id, int Width, int Height, double Scale)> ComposeSpecAsync(CanvasView view, ComposeSpec spec)
    {
        var inputs = spec.Cells.Select(c => view.Doc.Find(c.NodeId) ?? throw new InvalidOperationException($"找不到节点「{c.NodeId}」")).ToList();
        var bitmaps = await LoadInputsAsync(view, inputs);
        try
        {
            var (node, layout) = await ComposeIntoNewNodeAsync(view, inputs, bitmaps, spec);
            return (node.Id, layout.Width, layout.Height, layout.Scale);
        }
        finally { foreach (var bitmap in bitmaps) bitmap.Dispose(); }
    }

    /// <summary>The compose dialog for the selected pictures (in the order given); the result is a new node.</summary>
    public static async Task ComposeAsync(CanvasView view, IReadOnlyList<CanvasNode> inputs)
    {
        List<CanvasBitmap> bitmaps;
        try { bitmaps = await LoadInputsAsync(view, inputs); }
        catch (InvalidOperationException ex)
        {
            await Dialogs.MessageAsync("无法拼版", ex.Message);
            return;
        }
        try
        {
            var dialog = new ComposeDialog(inputs, bitmaps, null, "拼到新节点");
            if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
            await ComposeIntoNewNodeAsync(view, dialog.Order.Select(i => inputs[i]).ToList(), dialog.Order.Select(i => bitmaps[i]).ToList(), dialog.Spec);
        }
        catch (InvalidOperationException ex) { await Dialogs.MessageAsync("拼版失败", ex.Message); }
        finally { foreach (var bitmap in bitmaps) bitmap.Dispose(); }
    }

    /// <summary>Open the dialog with the settings stored on a composed picture and replace its picture in place (same node, same connections), as one undo step. The old file stays.</summary>
    public static async Task RecomposeAsync(CanvasView view, CanvasNode node)
    {
        ComposeSpec? spec = null;
        try { spec = Tabula.Core.Json.Deserialize<ComposeSpec>(node.Option(NodeKeys.Compose)); }
        catch (System.Text.Json.JsonException) { /* Reported below like a spec with no inputs. */ }
        if (spec is not { Cells.Count: > 1 })
        {
            await Dialogs.MessageAsync("无法重新拼", "这张图上记录的拼版设置已经损坏。");
            return;
        }
        var lost = spec.Cells.Where(c => view.Doc.Find(c.NodeId) is not { Kind: NodeKind.Image, HasContent: true }).Select(c => c.NodeId).ToList();
        if (lost.Count > 0)
        {
            await Dialogs.MessageAsync("缺少输入图片", "这些输入已经不在画布上（或没有图片）：\n" + string.Join("\n", lost));
            return;
        }
        var inputs = spec.Cells.Select(c => view.Doc.Find(c.NodeId)!).ToList();
        if (inputs.Any(n => Media.IsSvg(n.Content)))
        {
            await Dialogs.MessageAsync("无法重新拼", "有输入已经换成了 SVG，拼版只能用位图。");
            return;
        }
        List<CanvasBitmap> bitmaps;
        try { bitmaps = await LoadInputsAsync(view, inputs); }
        catch (InvalidOperationException ex)
        {
            await Dialogs.MessageAsync("无法重新拼", ex.Message);
            return;
        }
        try
        {
            var dialog = new ComposeDialog(inputs, bitmaps, spec, "按此设置重新拼");
            if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
            var ordered = dialog.Order.Select(i => inputs[i]).ToList();
            var (bytes, mime, layout) = await RenderComposeAsync(dialog.Order.Select(i => bitmaps[i]).ToList(), dialog.Spec, ordered.All(IsJpeg));
            if (view.Doc.Find(node.Id) != node || ordered.Any(n => view.Doc.Find(n.Id) != n)) throw new InvalidOperationException("拼版期间有节点被删除，已取消");
            view.History.Record();
            node.Content = ProjectStore.SaveAsset(view.Doc.Id, bytes, mime);
            node.MimeType = mime;
            (node.NaturalWidth, node.NaturalHeight) = (layout.Width, layout.Height);
            node.SetOption(NodeKeys.Compose, Tabula.Core.Json.Serialize(dialog.Spec));
            if (!node.FreeResize) node.Height = Math.Round(node.Width * layout.Height / layout.Width);
        }
        catch (InvalidOperationException ex) { await Dialogs.MessageAsync("重新拼失败", ex.Message); }
        finally { foreach (var bitmap in bitmaps) bitmap.Dispose(); }
    }

    // ---------- Angle ----------

    /// <summary>A new image node generating the same subject from another camera angle, with this image as the reference.</summary>
    public static CanvasNode ExecuteAngle(CanvasView view, CanvasNode node, int horizontal, int pitch, double distance, bool wide, string? model = null, bool select = false)
    {
        view.History.Record();
        var result = CanvasNode.Create(NodeKind.Image, 0, 0);
        result.Title = $"{node.Title} · 多角度";
        result.Prompt = ImageGeometry.AnglePrompt(horizontal, pitch, distance, wide);
        result.Model = model ?? node.Model;
        result.Ratio = node.Ratio;
        result.Size = node.Size;
        result.Quality = node.Quality;
        if (node.NaturalWidth > 0) result.Height = Math.Round(result.Width * node.NaturalHeight / node.NaturalWidth);
        PlaceRightOf(view, node, result);
        Finish(view, node, [result], connectAll: true, select: select);
        _ = view.Generator.GenerateAsync(result, recordUndo: false);
        return result;
    }

    // ---------- Reverse prompt ----------

    public static readonly (string Label, string Prompt)[] ReverseStyles =
    [
        ("标准", ReversePromptText),
        ("简洁", "请用简洁精准的中文写一段 AI 生图提示词，概括图片的主体、构图、光线和画风。只输出提示词本身，不要解释。"),
        ("详细", "请非常细致地观察输入的图片，写一段完整、可直接复现细节的中文 AI 生图提示词：人物外貌与服装纹理、动作表情、前景与背景元素、色彩搭配、光影层次、材质与风格。只输出提示词本身，不要解释。"),
        ("按镜头语言", "请用摄影与电影镜头语言（景别、机位角度、焦段景深、布光方式、色调氛围、主体构图）写一段中文 AI 生图提示词来复现这张图。只输出提示词本身，不要解释。"),
    ];

    /// <summary>A text node that describes this image as a reusable prompt, written by the chosen (or default) text model.</summary>
    public static CanvasNode ExecuteReversePrompt(CanvasView view, CanvasNode node, string? textModel = null, string? prompt = null, bool select = false)
    {
        view.History.Record();
        var text = CanvasNode.Create(NodeKind.Text, 0, 0);
        text.Title = $"{node.Title} · 反推提示词";
        text.Model = textModel;
        text.Prompt = string.IsNullOrWhiteSpace(prompt) ? ReversePromptText : prompt;
        PlaceRightOf(view, node, text);
        Finish(view, node, [text], connectAll: true, select: select);
        _ = view.Generator.GenerateAsync(text, recordUndo: false);
        return text;
    }

    // ---------- Dialogs (Compose only; single-image tools live in PictureView) ----------

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


    private sealed class ComposeDialog : ContentDialog
    {
        private static readonly string[] LayoutLabels = ["一行", "一列", "网格"], FitLabels = ["等高", "等宽"];
        private static readonly (string Label, string Value)[] Backgrounds = [("白色", "#FFFFFF"), ("浅灰", "#EEEEEE"), ("黑色", "#000000"), ("透明", "transparent")];

        /// <summary>One input as the dialog holds it: where it came from, its bitmap, and its head-top and feet lines as fractions of the picture's height.</summary>
        private sealed class Item(int index, string nodeId, CanvasBitmap bitmap, double top, double bottom)
        {
            public int Index { get; } = index;
            public string NodeId { get; } = nodeId;
            public CanvasBitmap Bitmap { get; } = bitmap;
            public double Top { get; set; } = top;
            public double Bottom { get; set; } = bottom;
        }

        private readonly List<Item> _items;
        private readonly bool _allJpeg;
        private readonly List<(string Label, string Value)> _backgrounds = [.. Backgrounds];
        private CanvasControl _canvas;
        private readonly ComboBox _layout = new() { Header = "布局", ItemsSource = LayoutLabels, SelectedIndex = 0, MinWidth = 110 };
        private readonly ComboBox _fit = new() { Header = "适配", ItemsSource = FitLabels, SelectedIndex = 0, MinWidth = 100 };
        private readonly ComboBox _background = new() { Header = "背景", MinWidth = 100 };
        private readonly NumberBox _columns = new() { Header = "列数（0 = 自动）", Value = 0, Minimum = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, MinWidth = 140 };
        private readonly NumberBox _gap = new() { Header = "间距（像素）", Value = 64, Minimum = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, MinWidth = 140 };
        private readonly NumberBox _margin = new() { Header = "外边距（像素）", Value = 64, Minimum = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, MinWidth = 140 };
        private readonly ToggleSwitch _align = new() { Header = "按头顶和脚底对齐", OnContent = "开", OffContent = "关" };
        private readonly Button _earlier = new() { Content = new FontIcon { Glyph = "\uE76B" } };
        private readonly Button _later = new() { Content = new FontIcon { Glyph = "\uE76C" } };
        private readonly TextBlock _info = new() { Style = (Style)Application.Current.Resources["SecondaryCaption"], TextWrapping = TextWrapping.Wrap };
        private int _selected;
        private double _scale = 1;
        private ComposeResult? _result;
        private (int Item, bool Top, ComposePlacement Place)? _drag;

        /// <summary>The inputs' indexes (into the list the dialog was given) in the order the user left them.</summary>
        public List<int> Order => _items.Select(i => i.Index).ToList();

        public ComposeSpec Spec
        {
            get
            {
                var aligned = _align.IsOn && _align.IsEnabled;
                var layout = LayoutNames(_layout.SelectedIndex);
                return new(_items.Select(i => aligned ? new ComposeCell(i.NodeId, i.Top, i.Bottom) : new ComposeCell(i.NodeId)).ToList(), layout, Whole(_columns), Whole(_gap), Whole(_margin),
                    _background.SelectedIndex >= 0 ? _backgrounds[_background.SelectedIndex].Value : "#FFFFFF", _fit.SelectedIndex == 1 ? "width" : "height");
            }
        }

        private static string LayoutNames(int index) => index switch { 1 => "column", 2 => "grid", _ => "row" };
        private static int Whole(NumberBox box) => double.IsNaN(box.Value) ? 0 : (int)Math.Clamp(Math.Round(box.Value), 0, int.MaxValue);

        public ComposeDialog(IReadOnlyList<CanvasNode> inputs, IReadOnlyList<CanvasBitmap> bitmaps, ComposeSpec? previous, string primary)
        {
            _allJpeg = inputs.All(IsJpeg);
            // A spec brings its own order and lines; without one the inputs keep the order given and the lines start near the top and bottom.
            var order = previous is null ? Enumerable.Range(0, inputs.Count).ToList() : previous.Cells.Select(c => inputs.ToList().FindIndex(n => n.Id == c.NodeId)).ToList();
            _items = order.Select((index, position) => new Item(index, inputs[index].Id, bitmaps[index], previous?.Cells[position].Top ?? 0.05, previous?.Cells[position].Bottom ?? 0.95)).ToList();
            Shell(this, "拼版", primary);
            _background.ItemsSource = _backgrounds.Select(b => b.Label).ToList();
            _background.SelectedIndex = 0;
            if (previous is not null)
            {
                _layout.SelectedIndex = Math.Max(0, Array.IndexOf(ImageCompose.Layouts, previous.Layout));
                _fit.SelectedIndex = Math.Max(0, Array.IndexOf(ImageCompose.Fits, previous.Fit));
                (_columns.Value, _gap.Value, _margin.Value) = (previous.Columns, previous.Gap, previous.Margin);
                var known = _backgrounds.FindIndex(b => b.Value.Equals(previous.Background, StringComparison.OrdinalIgnoreCase));
                if (known < 0)
                {
                    _backgrounds.Add((previous.Background, previous.Background));
                    _background.ItemsSource = _backgrounds.Select(b => b.Label).ToList();
                    known = _backgrounds.Count - 1;
                }
                _background.SelectedIndex = known;
                _align.IsOn = previous.Cells.All(c => c.Top is not null);
            }
            AutomationProperties.SetName(_earlier, "前移");
            ToolTipService.SetToolTip(_earlier, "前移");
            AutomationProperties.SetName(_later, "后移");
            ToolTipService.SetToolTip(_later, "后移");
            AutomationProperties.SetName(_align, "按头顶和脚底对齐");
            _earlier.Click += (_, _) => Move(-1);
            _later.Click += (_, _) => Move(1);
            foreach (var combo in new[] { _layout, _fit, _background }) combo.SelectionChanged += (_, _) => Refresh();
            foreach (var box in new[] { _columns, _gap, _margin }) box.ValueChanged += (_, _) => Refresh();
            _align.Toggled += (_, _) => Refresh();
            _canvas = new CanvasControl { ClearColor = Colors.Transparent };
            _canvas.Draw += Draw;
            _canvas.PointerPressed += Pressed;
            _canvas.PointerMoved += Moved;
            _canvas.PointerReleased += (_, e) => { _drag = null; _canvas.ReleasePointerCapture(e.Pointer); };
            Closed += (_, _) => { _canvas.RemoveFromVisualTree(); _canvas = null!; };
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _layout, _fit, _columns, _gap, _margin, _background } },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center, Children = { _align, _earlier, _later } },
                    new Border { Child = _canvas, CornerRadius = new(8), HorizontalAlignment = HorizontalAlignment.Center },
                    _info,
                    new TextBlock { Text = "点预览里的图片选中它，用左右按钮调整顺序；打开对齐后拖动每张图上的两条线（实线是头顶，虚线是脚底）。原图不会被修改。", Style = (Style)Application.Current.Resources["SecondaryCaption"], TextWrapping = TextWrapping.Wrap },
                },
            };
            Refresh();
        }

        private void Move(int by)
        {
            var to = _selected + by;
            if (to < 0 || to >= _items.Count) return;
            (_items[_selected], _items[to]) = (_items[to], _items[_selected]);
            _selected = to;
            Refresh();
        }

        private void Refresh()
        {
            // Lines only make sense for a row of equal-height pictures.
            var canAlign = _layout.SelectedIndex == 0 && _fit.SelectedIndex == 0;
            _align.IsEnabled = canAlign;
            _columns.IsEnabled = _layout.SelectedIndex == 2;
            _earlier.IsEnabled = _selected > 0;
            _later.IsEnabled = _selected < _items.Count - 1;
            try
            {
                _result = ImageCompose.Layout(Spec, _items.Select(i => ((int)i.Bitmap.SizeInPixels.Width, (int)i.Bitmap.SizeInPixels.Height)).ToList(), DeviceMax);
                _scale = FitScale(_result.Width, _result.Height);
                _canvas.Width = _result.Width * _scale;
                _canvas.Height = _result.Height * _scale;
                var transparent = _backgrounds[Math.Max(0, _background.SelectedIndex)].Value == "transparent";
                _info.Text = $"输出 {_result.Width} × {_result.Height} 像素 · {(_allJpeg && !transparent ? "JPEG" : "PNG")}" + (_result.Scale < 1 ? $" · 已缩小到 {_result.Scale:P0}（超过显卡的位图上限）" : "");
                IsPrimaryButtonEnabled = true;
            }
            catch (ArgumentException ex)
            {
                _result = null;
                _info.Text = ex.Message;
                IsPrimaryButtonEnabled = false;
            }
            _canvas.Invalidate();
        }

        private Point ToOutput(PointerRoutedEventArgs e)
        {
            var p = e.GetCurrentPoint(_canvas).Position;
            return new(p.X / _scale, p.Y / _scale);
        }

        private void Pressed(object sender, PointerRoutedEventArgs e)
        {
            if (_result is null) return;
            var p = ToOutput(e);
            var grip = 8 / _scale;
            if (_align.IsOn && _align.IsEnabled)
            {
                for (var i = 0; i < _items.Count; i++)
                {
                    var place = _result.Cells[i];
                    if (p.X < place.X || p.X > place.X + place.Width) continue;
                    foreach (var top in new[] { true, false })
                        if (Math.Abs(p.Y - (place.Y + (top ? _items[i].Top : _items[i].Bottom) * place.Height)) <= grip)
                        {
                            _drag = (i, top, place);
                            _selected = i;
                            _canvas.CapturePointer(e.Pointer);
                            Refresh();
                            return;
                        }
                }
            }
            for (var i = 0; i < _items.Count; i++)
            {
                var cell = _result.Cells[i].Cell;
                if (p.X >= cell.X && p.X <= cell.X + cell.Width && p.Y >= cell.Y && p.Y <= cell.Y + cell.Height)
                {
                    _selected = i;
                    Refresh();
                    return;
                }
            }
        }

        private void Moved(object sender, PointerRoutedEventArgs e)
        {
            if (_drag is not var (index, top, place)) return;
            // Measured against the picture as it was when the line was grabbed, so the line follows the pointer while the sheet reflows.
            var fraction = Math.Clamp((ToOutput(e).Y - place.Y) / place.Height, 0, 1);
            var item = _items[index];
            if (top) item.Top = Math.Min(fraction, item.Bottom - 0.02);
            else item.Bottom = Math.Max(fraction, item.Top + 0.02);
            Refresh();
        }

        private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            if (_result is null) return;
            var ds = args.DrawingSession;
            var (red, green, blue, alpha) = ImageCompose.ParseBackground(_backgrounds[Math.Max(0, _background.SelectedIndex)].Value)!.Value;
            // A transparent background shows as a neutral grey so the pictures can still be told apart.
            ds.Clear(alpha == 0 ? Color.FromArgb(255, 190, 190, 190) : Color.FromArgb(255, red, green, blue));
            for (var i = 0; i < _items.Count; i++)
            {
                var place = _result.Cells[i];
                var bitmap = _items[i].Bitmap;
                ds.DrawImage(bitmap, new Rect(place.X * _scale, place.Y * _scale, place.Width * _scale, place.Height * _scale), new Rect(0, 0, bitmap.SizeInPixels.Width, bitmap.SizeInPixels.Height), 1f, CanvasImageInterpolation.Linear);
                if (i == _selected)
                    ds.DrawRectangle(new Rect(place.Cell.X * _scale, place.Cell.Y * _scale, place.Cell.Width * _scale, place.Cell.Height * _scale), Accent, 3);
                if (!_align.IsOn || !_align.IsEnabled) continue;
                var dashed = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
                var (left, right) = ((float)(place.X * _scale), (float)((place.X + place.Width) * _scale));
                var (top, bottom) = ((float)((place.Y + _items[i].Top * place.Height) * _scale), (float)((place.Y + _items[i].Bottom * place.Height) * _scale));
                ds.DrawLine(left, top, right, top, Accent, 2);
                ds.DrawLine(left, bottom, right, bottom, Accent, 2, dashed);
            }
        }
    }
}
