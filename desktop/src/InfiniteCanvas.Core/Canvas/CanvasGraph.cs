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

    /// <summary>Shift right in node-width steps until the node no longer overlaps other top-level nodes.</summary>
    public static double FreeX(CanvasNode node, IEnumerable<CanvasNode> nodes, double gap = 40)
    {
        var others = nodes.Where(n => n.Kind != NodeKind.Group && n != node).ToList();
        var x = node.X;
        bool Overlaps(double left) => others.Any(n => left < n.Right + gap && left + node.Width + gap > n.X && node.Y < n.Bottom + gap && node.Bottom + gap > n.Y);
        for (var step = 0; step < 50 && Overlaps(x); step++) x += node.Width + gap;
        return x;
    }

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

    /// <summary>The innermost (smallest) group whose area contains the node centre.</summary>
    public static CanvasNode? ContainingGroup(CanvasDocument doc, CanvasNode node)
    {
        var (cx, cy) = (node.X + node.Width / 2, node.Y + node.Height / 2);
        return doc.Nodes.Where(g => g.Kind == NodeKind.Group && g.Id != node.Id && Rect.Of(g).Contains(cx, cy)).MinBy(g => g.Width * g.Height);
    }
}
