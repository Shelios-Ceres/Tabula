using InfiniteCanvas.Agents;
using InfiniteCanvas.Core.Canvas;
using InfiniteCanvas.Core.Storage;
using ModelContextProtocol;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace InfiniteCanvas.App.Board;

/// <summary>The open canvas as agent tools see it. Tool calls arrive on pipe threads, so everything is marshalled onto the UI thread.</summary>
public sealed class CanvasAgentHost(CanvasView view) : ICanvasHost
{
    public async Task<T> OnUiAsync<T>(Func<T> action)
    {
        T Run() => view.IsClosing ? throw new McpException("画布正在关闭，请稍后重试") : action();
        if (view.DispatcherQueue.HasThreadAccess) return Run();
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = view.DispatcherQueue.TryEnqueue(() =>
        {
            try { done.SetResult(Run()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        if (!queued) throw new McpException("画布已经关闭");
        return await done.Task;
    }

    public CanvasDocument Document => view.Doc;
    public IReadOnlyCollection<string> Selection => view.Selection.ToList();
    public Viewport Viewport { get => view.Viewport; set => view.Viewport = value; }
    public (double X, double Y) ViewCenter => (view.ViewCenterWorld.X, view.ViewCenterWorld.Y);
    public void RecordHistory() => view.History.Record();
    public void Select(IEnumerable<string> ids) => view.Select(ids);
    public void Delete(IEnumerable<string> ids) => view.DeleteNodes(ids.ToList());
    public void Reveal(IReadOnlyList<CanvasNode> nodes) => view.EnsureVisible(CanvasGraph.Bounds(nodes));
    public bool IsRunning(string nodeId) => view.Generator.IsRunning(nodeId);
    public string? CannotGenerate(CanvasNode node) => CanvasGenerator.CannotGenerate(view.Doc, node);
    public void Generate(CanvasNode node, bool recordHistory = true) => _ = view.Generator.GenerateAsync(node, recordHistory);
    public string? Group(IReadOnlyCollection<string> ids, string? title) => view.GroupNodes(ids, title)?.Id;
    public void Ungroup(IReadOnlyCollection<string> ids) => view.UngroupNodes(ids);
    public IReadOnlyList<string> AddToGroup(string groupId, IReadOnlyCollection<string> ids, bool fit) => view.AddToGroupNodes(view.Doc.Find(groupId)!, ids, fit);
    public IReadOnlyList<string> RemoveFromGroup(IReadOnlyCollection<string> ids, bool fit) => view.RemoveFromGroupNodes(ids, fit);
    public Task<ImportResult> ImportAsync(IReadOnlyList<string> paths, double x, double y) => view.ImportFilesQuietAsync(paths, new Windows.Foundation.Point(x, y));
    public Task ReplaceMediaAsync(CanvasNode node, string path) => view.ReplaceMediaAsync(node, path);
    public Task AddToLibraryAsync(CanvasNode node) => view.AddToLibraryAsync(node);

    /// <summary>JPEG keeps the image small in the agent's context; transparency is not needed to understand the picture.</summary>
    public async Task<(byte[] Bytes, string MimeType)?> LoadImageAsync(CanvasNode node, int maxEdge)
    {
        if (node.Kind != NodeKind.Image || !node.HasContent) return null;
        var path = ProjectStore.AssetPath(view.Doc.Id, node.Content!);
        if (!File.Exists(path)) return null;
        using var input = await (await StorageFile.GetFileFromPathAsync(path)).OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(input);
        var scale = Math.Min(1, (double)maxEdge / Math.Max(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        using var output = new MemoryStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output.AsRandomAccessStream());
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        return (output.ToArray(), "image/jpeg");
    }
}
