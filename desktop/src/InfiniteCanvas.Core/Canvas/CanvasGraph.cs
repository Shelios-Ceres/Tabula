using InfiniteCanvas.Core.Models;
using InfiniteCanvas.Core.Settings;

namespace InfiniteCanvas.Core.Canvas;

public sealed record Rect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public bool Intersects(Rect other) => Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;
    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;
    public static Rect Of(CanvasNode node) => new(node.X, node.Y, node.Right, node.Bottom);
}

/// <summary>A resource an upstream node contributes to a generation request.</summary>
public sealed record GenerationResource(CanvasNode Node, NodeKind Type, string? Text);

public static class CanvasGraph
{
    public const double GroupPadding = 24, GroupTopPadding = 52;

    public static Rect Bounds(IEnumerable<CanvasNode> nodes)
    {
        var list = nodes.ToList();
        return list.Count == 0 ? new(0, 0, 0, 0) : new(list.Min(n => n.X), list.Min(n => n.Y), list.Max(n => n.Right), list.Max(n => n.Bottom));
    }

    /// <summary>
    /// Shift right in node-width steps until the node no longer overlaps other top-level nodes.
    /// With <paramref name="avoidGroupsOf"/> the node (an automatically placed one) also never overlaps a group of that document (so it neither joins nor half-sits in one):
    /// it moves past the right edge of the overlapped group with the furthest edge plus an 80 px gap and is checked again, up to 50 times.
    /// </summary>
    public static double FreeX(CanvasNode node, IEnumerable<CanvasNode> nodes, double gap = 40, CanvasDocument? avoidGroupsOf = null)
    {
        var others = nodes.Where(n => n.Kind != NodeKind.Group && n != node).ToList();
        double Free(double left)
        {
            for (var step = 0; step < 50 && others.Any(n => left < n.Right + gap && left + node.Width + gap > n.X && node.Y < n.Bottom + gap && node.Bottom + gap > n.Y); step++) left += node.Width + gap;
            return left;
        }
        var x = Free(node.X);
        for (var step = 0; avoidGroupsOf is not null && step < 50 && GroupOverlapping(avoidGroupsOf, node, x) is { } group; step++) x = Free(group.Right + 80);
        return x;
    }

    /// <summary>
    /// Like <see cref="FreeX"/> for the first card of a batch, but the whole spread (row or grid) has to fit, so the batch does not land on the nodes beside it.
    /// <paramref name="others"/> must not include the batch itself.
    /// </summary>
    public static double FreeBatchX(IReadOnlyList<CanvasNode> members, StackLayout layout, double gap, IEnumerable<CanvasNode> others, CanvasDocument? avoidGroupsOf = null)
    {
        var lead = members[0];
        var (width, height) = Stacks.Footprint(members, layout, gap);
        var x = FreeX(new CanvasNode { Kind = NodeKind.Image, X = lead.X, Y = lead.Y, Width = width, Height = height }, others, avoidGroupsOf: avoidGroupsOf);
        // Sizes this large can overflow the search; the batch then stays where it is rather than get a coordinate that cannot be saved.
        return double.IsFinite(x) ? x : lead.X;
    }

    /// <summary>
    /// Where the node of a new generation goes (the one place the composer and the agent tool canvas_generate agree on): to the right of its inputs,
    /// top-aligned with the highest of them, or where it already sits (the view centre) when it has none; clear of other nodes and of every group;
    /// and when the run asks for a row or a grid, with room for the whole spread. The node must already be in the document.
    /// </summary>
    public static void PlaceGeneration(CanvasDocument doc, CanvasNode node, IReadOnlyList<CanvasNode> inputs)
    {
        if (inputs.Count > 0)
        {
            node.X = inputs.Max(i => i.Right) + 80;
            node.Y = inputs.Min(i => i.Y);
        }
        node.X = FreeX(node, doc.Nodes, avoidGroupsOf: doc);
        var spread = Stacks.ParseLayout(node.Option(NodeKeys.Layout)) ?? StackLayout.Stack;
        if (spread == StackLayout.Stack || node.Count <= 1) return;
        // A row or grid spreads right from this card: find a spot where all of it, not just one card, is free.
        var batch = new List<CanvasNode> { node };
        for (var i = 1; i < node.Count; i++)
        {
            var card = CanvasNode.Create(node.Kind, 0, 0);
            card.Width = node.Width;
            if (node.Kind == NodeKind.Image) card.Height = OutputHeight(node.Width, node.Ratio);
            batch.Add(card);
        }
        node.X = FreeBatchX(batch, spread, Stacks.GapOf(node), doc.Nodes.Where(n => n != node), doc);
    }

    /// <summary>Height of a generated picture card of this width and ratio, as the generator sizes its outputs.</summary>
    public static double OutputHeight(double width, string? ratio) => Math.Clamp(width / ModelFamilies.AspectOf(ratio is null or "auto" ? "1:1" : ratio), 120, 640);

    /// <summary>Config nodes sit between resources and outputs; a group can only be a source and never feeds its own members.</summary>
    public static (string From, string To)? NormalizeConnection(CanvasNode first, CanvasNode second, bool firstIsSource)
    {
        if (first.Id == second.Id || first.Kind == NodeKind.Config && second.Kind == NodeKind.Config) return null;
        var (from, to) = firstIsSource || second.Kind == NodeKind.Config ? (first, second) : (second, first);
        return to.Kind == NodeKind.Group || to.GroupId == from.Id ? null : (from.Id, to.Id);
    }

    public static IEnumerable<CanvasNode> GroupMembers(CanvasDocument doc, string groupId) => doc.Nodes.Where(n => n.GroupId == groupId && n.Kind != NodeKind.Group);

    /// <summary>Upstream resources in connection order; groups expand to their members (never the node itself), duplicates removed.</summary>
    public static List<GenerationResource> UpstreamResources(CanvasDocument doc, string nodeId)
    {
        var result = new List<GenerationResource>();
        var seen = new HashSet<string> { nodeId };
        foreach (var connection in doc.Connections.Where(c => c.To == nodeId))
        {
            if (doc.Find(connection.From) is not { } source) continue;
            IEnumerable<CanvasNode> nodes = source.Kind == NodeKind.Group ? GroupMembers(doc, source.Id) : [source];
            foreach (var node in nodes)
                if (seen.Add(node.Id) && Resource(node) is { } resource) result.Add(resource);
        }
        return result;
    }

    /// <summary>A node that already holds an image or text joins its own request as the first input; the generation panel and the agent tools count the same list.</summary>
    public static List<GenerationResource> GenerationInputs(CanvasDocument doc, CanvasNode node)
    {
        var resources = UpstreamResources(doc, node.Id);
        if (node.HasContent && node.Kind is NodeKind.Image)
        {
            resources.Insert(0, new(node, NodeKind.Image, null));
            // Layer decomposition and Seedream 5.0's transparent background take exactly one image: on an existing image only the node itself is sent.
            // (5.5 draws transparency from up to 10 references, so its background setting never trims them.)
            if ((node.Option("layers") == "true" || node.Option("transparent") == "true") && SettingsStore.Current.Resolve(node.Model) is { } resolved
                && ModelFamilies.FindImage(resolved.Channel.Vendor, resolved.Model.Name) is { Interactive: true } family && (node.Option("layers") == "true" || !family.Rgba))
                resources.RemoveAll(r => r.Type == NodeKind.Image && r.Node != node);
        }
        if (node.HasContent && node.Kind is NodeKind.Text) resources.Insert(0, new(node, NodeKind.Text, node.Content));
        return resources;
    }

    private static GenerationResource? Resource(CanvasNode node) => node.Kind switch
    {
        NodeKind.Image or NodeKind.Video or NodeKind.Audio when node.HasContent && node.Option("role") != "mask" => new(node, node.Kind, null),
        NodeKind.Text when !string.IsNullOrWhiteSpace(node.Content) => new(node, NodeKind.Text, node.Content),
        NodeKind.Text when !string.IsNullOrWhiteSpace(node.Prompt) => new(node, NodeKind.Text, node.Prompt),
        _ => null,
    };

    /// <summary>Compose the final prompt: own prompt, then numbered text blocks; media keep their order as 图片1/视频1/音频1.</summary>
    public static string ComposePrompt(string prompt, IReadOnlyList<GenerationResource> resources)
    {
        var texts = resources.Where(r => r.Type == NodeKind.Text).Select((r, i) => $"【文本{i + 1}】\n{r.Text}").ToList();
        var images = resources.Count(r => r.Type == NodeKind.Image);
        var text = prompt.Trim();
        if (texts.Count > 0) text = $"{text}\n\n{string.Join("\n\n", texts)}".Trim();
        if (images > 0) text = $"参考图片编号：{string.Join("、", Enumerable.Range(1, images).Select(i => $"图片{i}"))}。请按这些编号理解提示词中的图片引用。\n\n{text}";
        return text;
    }

    /// <summary>
    /// Match nodes to the anchor's width, height or both, keeping each top-left corner.
    /// Images and videos keep their aspect for a single dimension; matching both switches them to free resize.
    /// </summary>
    public static void MatchSize(CanvasNode anchor, IEnumerable<CanvasNode> targets, bool width, bool height)
    {
        foreach (var node in targets)
        {
            var media = node.Kind is NodeKind.Image or NodeKind.Video && !node.FreeResize;
            var aspect = node.Width / node.Height;
            if (width && height)
            {
                if (media && Math.Abs(aspect - anchor.Width / anchor.Height) > 0.01) node.FreeResize = true;
                node.Width = anchor.Width;
                node.Height = anchor.Height;
            }
            else if (width)
            {
                node.Width = anchor.Width;
                if (media) node.Height = node.Width / aspect;
            }
            else if (height)
            {
                node.Height = anchor.Height;
                if (media) node.Width = node.Height * aspect;
            }
        }
    }

    public static Rect GroupRect(IEnumerable<CanvasNode> members)
    {
        var bounds = Bounds(members);
        return new(bounds.Left - GroupPadding, bounds.Top - GroupTopPadding, bounds.Right + GroupPadding, bounds.Bottom + GroupPadding);
    }

    /// <summary>Set GroupId on the given non-group nodes (ids already stack-expanded); the moved nodes and the groups they left. Positions never change.</summary>
    public static (List<CanvasNode> Moved, HashSet<string> Left) MoveToGroup(CanvasDocument doc, IReadOnlyCollection<string> ids, string? groupId)
    {
        var moved = doc.Nodes.Where(n => n.Kind != NodeKind.Group && n.GroupId != groupId && ids.Contains(n.Id)).ToList();
        var left = moved.Select(n => n.GroupId).OfType<string>().ToHashSet();
        foreach (var node in moved) node.GroupId = groupId;
        return (moved, left);
    }

    /// <summary>Resize a group to GroupRect of its members; no change when it has none.</summary>
    public static void FitGroup(CanvasDocument doc, string groupId)
    {
        var members = GroupMembers(doc, groupId).ToList();
        if (members.Count == 0 || doc.Find(groupId) is not { } group) return;
        var rect = GroupRect(members);
        (group.X, group.Y, group.Width, group.Height) = (rect.Left, rect.Top, rect.Width, rect.Height);
    }

    /// <summary>The innermost (smallest) group whose area contains the node centre.</summary>
    public static CanvasNode? ContainingGroup(CanvasDocument doc, CanvasNode node) => GroupAt(doc, node.Id, node.X + node.Width / 2, node.Y + node.Height / 2);

    /// <summary>Of the groups the node's frame would overlap at this x, the one reaching furthest right.</summary>
    private static CanvasNode? GroupOverlapping(CanvasDocument doc, CanvasNode node, double x) =>
        doc.Nodes.Where(g => g.Kind == NodeKind.Group && g.Id != node.Id && new Rect(x, node.Y, x + node.Width, node.Bottom).Intersects(Rect.Of(g))).MaxBy(g => g.Right);

    private static CanvasNode? GroupAt(CanvasDocument doc, string nodeId, double x, double y) =>
        doc.Nodes.Where(g => g.Kind == NodeKind.Group && g.Id != nodeId && Rect.Of(g).Contains(x, y)).MinBy(g => g.Width * g.Height);
}
