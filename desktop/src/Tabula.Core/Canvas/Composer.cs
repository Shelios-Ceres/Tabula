namespace Tabula.Core.Canvas;

/// <summary>What the composer is for, from the selection: a new generation, a bound 配方 or empty node, or stopping and retrying nodes.</summary>
public enum ComposerMode { New, Recipe, Fill, Stop, Retry }

/// <summary>The composer's mode and the nodes it acts on (the bound node, or the nodes to stop or retry; empty for a new generation).</summary>
public sealed record ComposerState(ComposerMode Mode, IReadOnlyList<CanvasNode> Nodes)
{
    /// <summary>The node whose prompt and settings the composer reads and writes: a 配方, or an empty node that is filled. Null for the other modes.</summary>
    public CanvasNode? Bound => Mode is ComposerMode.Recipe or ComposerMode.Fill ? Nodes[0] : null;
}

/// <summary>The left version strip of the full-screen picture view: the picture's upstream image sources, the members of its batch, and newer image versions derived from it.</summary>
public sealed record PictureStripData(List<CanvasNode> Sources, List<CanvasNode> Batch, List<CanvasNode> Versions);

/// <summary>
/// The composer is the UI form of what the agent tool canvas_generate does: generating from the selection creates a node beside the references,
/// wires the references into it as inputs and runs it. The wires stay as the history of what was made from what.
/// </summary>
public static class Composer
{
    /// <summary>Anything still generating is stopped, anything failed or stopped is retried; one 配方 binds the composer; one empty node is filled; otherwise a new generation.</summary>
    public static ComposerState StateOf(CanvasDocument doc, IEnumerable<string> selected, Func<string, bool> running, Func<CanvasNode, bool> canGenerate)
    {
        var ids = selected.ToHashSet();
        var hidden = Stacks.Hidden(doc);
        var shown = doc.Nodes.Where(n => ids.Contains(n.Id) && !hidden.Contains(n)).ToList();
        // A collapsed batch counts as a whole: its hidden members may be the ones still generating.
        var expanded = Stacks.Expand(doc, ids);
        var all = doc.Nodes.Where(n => expanded.Contains(n.Id)).ToList();
        if (all.Where(n => running(n.Id)).ToList() is { Count: > 0 } busy) return new(ComposerMode.Stop, busy);
        if (all.Where(n => n.Status == NodeStatus.Error && !n.HasContent && canGenerate(n)).ToList() is { Count: > 0 } failed) return new(ComposerMode.Retry, failed);
        if (shown.Count == 1)
        {
            var one = shown[0];
            if (one.Kind == NodeKind.Config) return new(ComposerMode.Recipe, [one]);
            if (one.Kind is NodeKind.Image or NodeKind.Audio or NodeKind.Text && !one.HasContent && one.Status != NodeStatus.Loading && canGenerate(one)) return new(ComposerMode.Fill, [one]);
        }
        return new(ComposerMode.New, []);
    }

    /// <summary>A picture with a file (not a mask layer) or a text with content: what a generation can take as input.</summary>
    public static bool IsReference(CanvasNode node) => node.Kind switch
    {
        NodeKind.Image => node.HasContent && node.Option("role") != "mask",
        NodeKind.Text => !string.IsNullOrWhiteSpace(node.Content),
        _ => false,
    };

    /// <summary>
    /// The references of a new generation, in the order they are numbered: pinned nodes first, then the selection in the order given, a selected group lending
    /// its pictures and texts. Nodes hidden inside a collapsed batch never count (its cover does), and neither does anything in <paramref name="dropped"/>.
    /// </summary>
    public static List<CanvasNode> References(CanvasDocument doc, IEnumerable<string> selected, IEnumerable<string> pins, IReadOnlyCollection<string> dropped)
    {
        var hidden = Stacks.Hidden(doc);
        var result = new List<CanvasNode>();
        void Add(CanvasNode node)
        {
            if (IsReference(node) && !hidden.Contains(node) && !dropped.Contains(node.Id) && !result.Contains(node)) result.Add(node);
        }
        foreach (var id in pins) if (doc.Find(id) is { } pinned) Add(pinned);
        foreach (var id in selected)
        {
            if (doc.Find(id) is not { } node) continue;
            if (node.Kind == NodeKind.Group) foreach (var member in CanvasGraph.GroupMembers(doc, node.Id)) Add(member);
            else Add(node);
        }
        return result;
    }

    /// <summary>The pictures and texts already wired into a node: what a 配方 or an empty node reads when the composer is bound to it.</summary>
    public static List<CanvasNode> Wired(CanvasDocument doc, CanvasNode node) =>
        CanvasGraph.UpstreamResources(doc, node.Id).Where(r => r.Type is NodeKind.Image or NodeKind.Text).Select(r => r.Node).ToList();

    /// <summary>
    /// All upstream image and text references that fed into <paramref name="node"/> (generated, running, failed, recipe or tool-derived),
    /// expanding groups and traversing upstream 配方 (config) nodes and <see cref="NodeKeys.SourceId"/> links with cycle protection.
    /// </summary>
    public static List<CanvasNode> SourcesOf(CanvasDocument doc, CanvasNode node)
    {
        var result = new List<CanvasNode>();
        var seen = new HashSet<string> { node.Id };
        var visited = new HashSet<string> { node.Id };

        void Collect(CanvasNode target)
        {
            foreach (var connection in doc.Connections.Where(c => c.To == target.Id))
            {
                if (doc.Find(connection.From) is not { } upstream) continue;
                if (upstream.Kind == NodeKind.Group)
                {
                    foreach (var member in CanvasGraph.GroupMembers(doc, upstream.Id)) AddLeaf(member);
                }
                else if (upstream.Kind == NodeKind.Config)
                {
                    if (visited.Add(upstream.Id)) Collect(upstream);
                }
                else AddLeaf(upstream);
            }
            if (target.Option(NodeKeys.SourceId) is { Length: > 0 } sourceId && doc.Find(sourceId) is { } source && visited.Add(source.Id))
            {
                if (source.Kind == NodeKind.Config || !source.HasContent) Collect(source);
                else AddLeaf(source);
            }
        }

        void AddLeaf(CanvasNode candidate)
        {
            if (candidate.Kind == NodeKind.Image && candidate.HasContent && candidate.Option("role") != "mask" && seen.Add(candidate.Id))
                result.Add(candidate);
            else if (candidate.Kind == NodeKind.Text && (!string.IsNullOrWhiteSpace(candidate.Content) || !string.IsNullOrWhiteSpace(candidate.Prompt)) && seen.Add(candidate.Id))
                result.Add(candidate);
        }

        Collect(node);
        return result;
    }

    /// <summary>Downstream image nodes derived from <paramref name="node"/> (directly wired or referencing it via <see cref="NodeKeys.SourceId"/>), including ones still generating.</summary>
    public static List<CanvasNode> DescendantsOf(CanvasDocument doc, CanvasNode node)
    {
        var wiredTo = doc.Connections.Where(c => c.From == node.Id).Select(c => c.To).ToHashSet();
        var seen = new HashSet<string> { node.Id };
        var result = new List<CanvasNode>();
        foreach (var candidate in doc.Nodes)
        {
            if (candidate.Kind != NodeKind.Image || !seen.Add(candidate.Id)) continue;
            if (wiredTo.Contains(candidate.Id) || candidate.Option(NodeKeys.SourceId) == node.Id)
                result.Add(candidate);
        }
        return result;
    }

    /// <summary>The three sections of the full-screen picture view's left strip: upstream image sources, this batch's members, and newer image versions.</summary>
    public static PictureStripData PictureStrip(CanvasDocument doc, CanvasNode node)
    {
        var sources = SourcesOf(doc, node).Where(n => n.Kind == NodeKind.Image && n.HasContent).ToList();
        var batch = Stacks.Of(node) is { } stack ? Stacks.Members(doc, stack) : [node];
        var versions = DescendantsOf(doc, node);
        return new(sources, batch, versions);
    }

    /// <summary>The composer's draft of this canvas, created with the first call.</summary>
    public static CanvasNode DraftOf(CanvasDocument doc) => doc.Draft ??= new CanvasNode { Kind = NodeKind.Config, Title = "草稿" };

    /// <summary>
    /// The node whose prompt and settings made this one: the node itself when it carries a prompt (a generated text, picture or audio), else the node
    /// that produced it. Null for a node nobody generated, such as an imported picture or typed text.
    /// </summary>
    public static CanvasNode? ReuseSource(CanvasDocument doc, CanvasNode node)
    {
        if (node.Kind is not (NodeKind.Image or NodeKind.Text or NodeKind.Audio)) return null;
        if (!string.IsNullOrWhiteSpace(node.Prompt)) return node;
        return node.Option(NodeKeys.SourceId) is { Length: > 0 } id && doc.Find(id) is { } source && !string.IsNullOrWhiteSpace(source.Prompt) ? source : null;
    }

    /// <summary>
    /// Where a finished node came from, for the composer's origin capsule: prefers <see cref="ReuseSource"/>, and falls back to a generated node
    /// (or its source) that recorded a model without a prompt (for example an image edit with no text). Null for imported files or plain notes.
    /// </summary>
    public static CanvasNode? OriginOf(CanvasDocument doc, CanvasNode node)
    {
        if (!node.HasContent || node.Kind is not (NodeKind.Image or NodeKind.Text or NodeKind.Audio)) return null;
        if (ReuseSource(doc, node) is { } reuse) return reuse;
        if (!string.IsNullOrWhiteSpace(node.Model)) return node;
        return node.Option(NodeKeys.SourceId) is { Length: > 0 } id && doc.Find(id) is { } source && !string.IsNullOrWhiteSpace(source.Model) ? source : null;
    }

    /// <summary>Fill the draft with how a node was made (沿用这次的设置): its prompt, model and parameters, without the marks and batch bookkeeping of that one picture.</summary>
    public static void Reuse(CanvasNode draft, CanvasNode source)
    {
        draft.Mode = NodeSpec.CapabilityOf(source);
        draft.Prompt = source.Prompt;
        Copy(source, draft);
        draft.Options = NodeKeys.OutputOptions(source, draft);
    }

    /// <summary>The generation settings of one node onto another (a copy of the options, so the two never share them).</summary>
    public static void Copy(CanvasNode from, CanvasNode to)
    {
        to.Model = from.Model;
        to.Count = from.Count;
        to.Size = from.Size;
        to.Ratio = from.Ratio;
        to.Quality = from.Quality;
        to.Options = new(from.Options);
    }

    /// <summary>
    /// A new generation: an empty node of the draft's output kind with its prompt and settings, placed with the same rule as canvas_generate
    /// (<see cref="CanvasGraph.PlaceGeneration"/>) and wired from every reference. The caller runs it. <paramref name="centerX"/> / <paramref name="centerY"/> is the view centre,
    /// where it lands when there are no references.
    /// </summary>
    public static CanvasNode NewGeneration(CanvasDocument doc, CanvasNode draft, IReadOnlyList<CanvasNode> references, double centerX, double centerY)
    {
        var node = Make(NodeSpec.KindOf(draft.Mode), doc, draft, references, centerX, centerY);
        node.Prompt = draft.Prompt;
        // Editing a marked picture or continuing its conversation works from the first picture of the request, so its marks and conversation come along.
        if (node.Kind == NodeKind.Image && references.FirstOrDefault(r => r.Kind == NodeKind.Image) is { } first)
            foreach (var key in new[] { NodeKeys.Mask, NodeKeys.Markup, NodeKeys.Selections, NodeKeys.ConversationId, NodeKeys.ConversationModel })
                if (first.Option(key) is { Length: > 0 } value) node.SetOption(key, value);
        return node;
    }

    /// <summary>存为配方: the draft's prompt and settings as a new 配方 node, wired from the references like a generation would be.</summary>
    public static CanvasNode SaveRecipe(CanvasDocument doc, CanvasNode draft, IReadOnlyList<CanvasNode> references, double centerX, double centerY)
    {
        var node = Make(NodeKind.Config, doc, draft, references, centerX, centerY);
        node.Mode = draft.Mode;
        node.Prompt = draft.Prompt;
        return node;
    }

    private static CanvasNode Make(NodeKind kind, CanvasDocument doc, CanvasNode draft, IReadOnlyList<CanvasNode> references, double centerX, double centerY)
    {
        var node = CanvasNode.Create(kind, centerX, centerY);
        Copy(draft, node);
        doc.Nodes.Add(node);
        CanvasGraph.PlaceGeneration(doc, node, references);
        foreach (var reference in references)
            if (CanvasGraph.NormalizeConnection(reference, node, true) is { } pair && !doc.Connections.Any(c => c.From == pair.From && c.To == pair.To))
                doc.Connections.Add(CanvasConnection.Create(pair.From, pair.To));
        return node;
    }
}
