using Tabula.Agents;
using Tabula.Core.Canvas;
using Tabula.Core.Storage;
using Microsoft.UI.Dispatching;
using ModelContextProtocol;

namespace Tabula.App.Board;

/// <summary>
/// The canvas external agents reach through the shared pipe: whichever canvas page is open, or none.
/// Lives as long as the app, so an agent configured once keeps working across canvases.
/// </summary>
public sealed class SharedCanvasHost(DispatcherQueue ui) : ICanvasHost
{
    private static CanvasMcpServer? _server;

    /// <summary>Set by the canvas page while it is open.</summary>
    public static CanvasAgentHost? Open { get; set; }

    private static CanvasAgentHost Canvas => Open ?? throw new McpException("Tabula（丹青）里还没有打开画布，请先在客户端里打开一张画布");

    /// <summary>Start or stop the shared pipe to follow the setting.</summary>
    public static async Task ApplyAsync(bool enabled)
    {
        if (enabled && _server is null) _server = new CanvasMcpServer(new SharedCanvasHost(App.Window.DispatcherQueue), CanvasMcpServer.SharedPipeName(ProjectStore.Root));
        else if (!enabled && _server is not null)
        {
            await _server.DisposeAsync();
            _server = null;
        }
    }

    public async Task<T> OnUiAsync<T>(Func<T> action)
    {
        if (ui.HasThreadAccess) return action();
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!ui.TryEnqueue(() =>
        {
            try { done.SetResult(action()); }
            catch (Exception ex) { done.SetException(ex); }
        })) throw new McpException("Tabula（丹青）正在退出");
        return await done.Task;
    }

    public CanvasDocument Document => Canvas.Document;
    public IReadOnlyCollection<string> Selection => Canvas.Selection;
    public Viewport Viewport { get => Canvas.Viewport; set => Canvas.Viewport = value; }
    public (double X, double Y) ViewCenter => Canvas.ViewCenter;
    public void RecordHistory() => Canvas.RecordHistory();
    public void Select(IEnumerable<string> ids) => Canvas.Select(ids);
    public void Delete(IEnumerable<string> ids) => Canvas.Delete(ids);
    public void Reveal(IReadOnlyList<CanvasNode> nodes) => Canvas.Reveal(nodes);
    public bool IsRunning(string nodeId) => Canvas.IsRunning(nodeId);

    // Handlers stay on the canvas that was open when they were added, so removing one later still reaches it after another canvas has been opened.
    private readonly List<(Action Handler, CanvasAgentHost Canvas)> _running = [];

    public event Action? RunningChanged
    {
        add
        {
            var canvas = Canvas;
            canvas.RunningChanged += value;
            _running.Add((value!, canvas));
        }
        remove
        {
            var index = _running.FindIndex(r => r.Handler == value);
            if (index < 0) return;
            _running[index].Canvas.RunningChanged -= value;
            _running.RemoveAt(index);
        }
    }
    public string? CannotGenerate(CanvasNode node) => Canvas.CannotGenerate(node);
    public void Generate(CanvasNode node, bool recordHistory = true) => Canvas.Generate(node, recordHistory);
    public Task<(byte[] Bytes, string MimeType)?> LoadImageAsync(CanvasNode node, int maxEdge) => Canvas.LoadImageAsync(node, maxEdge);
    public string? Group(IReadOnlyCollection<string> ids, string? title) => Canvas.Group(ids, title);
    public void Ungroup(IReadOnlyCollection<string> ids) => Canvas.Ungroup(ids);
    public IReadOnlyList<string> AddToGroup(string groupId, IReadOnlyCollection<string> ids, bool fit) => Canvas.AddToGroup(groupId, ids, fit);
    public IReadOnlyList<string> RemoveFromGroup(IReadOnlyCollection<string> ids, bool fit) => Canvas.RemoveFromGroup(ids, fit);
    public Task<ImportResult> ImportAsync(IReadOnlyList<string> paths, double x, double y) => Canvas.ImportAsync(paths, x, y);
    public Task ReplaceMediaAsync(CanvasNode node, string path) => Canvas.ReplaceMediaAsync(node, path);
    public Task<(string Id, int Width, int Height, double Scale)> ComposeAsync(ComposeSpec spec) => Canvas.ComposeAsync(spec);
    public Task<(ImageDifference Difference, bool SizeMismatch)> CompareAsync(CanvasNode a, CanvasNode b, double threshold) => Canvas.CompareAsync(a, b, threshold);
    public Task<string> AddToLibraryAsync(CanvasNode node, bool replace) => Canvas.AddToLibraryAsync(node, replace);
}
