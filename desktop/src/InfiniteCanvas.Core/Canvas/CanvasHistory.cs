using System.Collections.ObjectModel;
using System.Text.Json;

namespace InfiniteCanvas.Core.Canvas;

/// <summary>Snapshot undo: nodes and connections are small JSON because media lives in asset files.</summary>
public sealed class CanvasHistory(CanvasDocument doc)
{
    private sealed record Snapshot(List<CanvasNode> Nodes, List<CanvasConnection> Connections);

    /// <summary>Undo points kept; the oldest is dropped. Undo/Redo keep undo + redo constant, so trimming on Record bounds both.</summary>
    private const int Limit = 100;
    private static readonly JsonSerializerOptions Compact = new(Json.Options) { WriteIndented = false };

    private readonly List<string> _undo = [];
    private readonly List<string> _redo = [];
    private object? _coalesce;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// Call before a mutation; the pre-change state becomes the undo point.
    /// Repeated calls with the same key (typing in one box) share one point until anything else records, undoes or redoes.
    /// </summary>
    public void Record(object? key = null)
    {
        if (key is not null && Equals(key, _coalesce)) return;
        _coalesce = key;
        _undo.Add(Capture());
        if (_undo.Count > Limit) _undo.RemoveAt(0);
        _redo.Clear();
    }

    public bool Undo() => Step(_undo, _redo);
    public bool Redo() => Step(_redo, _undo);

    private bool Step(List<string> from, List<string> to)
    {
        if (from.Count == 0) return false;
        _coalesce = null;
        to.Add(Capture());
        var json = from[^1];
        from.RemoveAt(from.Count - 1);
        Restore(json);
        return true;
    }

    private string Capture() => JsonSerializer.Serialize(new Snapshot([.. doc.Nodes], [.. doc.Connections]), Compact);

    private void Restore(string json)
    {
        var snapshot = Json.Deserialize<Snapshot>(json)!;
        // Generating nodes stay out of undo: the generator keeps writing into the live instance,
        // and a snapshot taken mid-generation must not roll a finished result back to Loading.
        var live = new Dictionary<string, CanvasNode>();
        foreach (var node in doc.Nodes) live.TryAdd(node.Id, node);
        var nodes = snapshot.Nodes.Select(n =>
        {
            if (live.Remove(n.Id, out var current) && (current.Status == NodeStatus.Loading || n.Status == NodeStatus.Loading)) return current;
            // Deleted mid-generation: nothing will ever fill this copy (a video task ID still resumes on reopen).
            if (n.Status == NodeStatus.Loading) (n.Status, n.Error) = (NodeStatus.Error, "生成已中断");
            return n;
        }).ToList();
        nodes.AddRange(live.Values.Where(n => n.Status == NodeStatus.Loading));
        Replace(doc.Nodes, nodes);
        Replace(doc.Connections, snapshot.Connections);
    }

    private static void Replace<T>(ObservableCollection<T> target, List<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}
