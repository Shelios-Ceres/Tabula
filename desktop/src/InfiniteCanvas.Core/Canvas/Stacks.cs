namespace InfiniteCanvas.Core.Canvas;

/// <summary>How the outputs of a batch sit once generated: gathered on one spot as a stack, or spread out in a row or a grid.</summary>
public enum StackLayout { Stack, Row, Grid }

/// <summary>
/// Outputs of one batch generation share a stack ID. Collapsed (the default) they sit on one spot and only the lead card,
/// the first member in document order, is shown; opened they spread into a grid from the lead's position.
/// </summary>
public static class Stacks
{
    public const double Gap = 24;

    public static string? Of(CanvasNode node) => node.Option(NodeKeys.Stack) is { Length: > 0 } id ? id : null;
    public static bool IsOpen(CanvasNode node) => node.Option(NodeKeys.StackOpen) == "true";

    public static List<CanvasNode> Members(CanvasDocument doc, string id) => doc.Nodes.Where(n => n.Option(NodeKeys.Stack) == id).ToList();

    /// <summary>
    /// A stack is named after the node it formed on, so that node's title names the whole batch. Shown on its own
    /// (an opened stack, a list of pictures) it goes by its file's number like the other members.
    /// </summary>
    public static string OwnTitle(CanvasNode node) => Of(node) == node.Id && node.Content is { Length: > 0 } asset ? Path.GetFileNameWithoutExtension(asset) : node.Title;

    /// <summary>Selection operations on a collapsed stack include every member; opened members remain independent.</summary>
    public static HashSet<string> Expand(CanvasDocument doc, IEnumerable<string> ids)
    {
        var result = ids.ToHashSet();
        foreach (var node in doc.Nodes.Where(n => result.Contains(n.Id) && Of(n) is not null && !IsOpen(n)).ToList())
            foreach (var member in Members(doc, Of(node)!)) result.Add(member.Id);
        return result;
    }

    /// <summary>Members of collapsed stacks other than their lead, in one pass over the document.</summary>
    public static HashSet<CanvasNode> Hidden(CanvasDocument doc)
    {
        var leads = new HashSet<string>();
        var hidden = new HashSet<CanvasNode>();
        foreach (var node in doc.Nodes)
            if (Of(node) is { } id && !IsOpen(node) && !leads.Add(id)) hidden.Add(node);
        return hidden;
    }

    /// <summary>Make the nodes one collapsed stack on the first one's spot.</summary>
    public static void Form(IReadOnlyList<CanvasNode> members)
    {
        if (members.Count < 2) return;
        foreach (var node in members)
        {
            node.SetOption(NodeKeys.Stack, members[0].Id);
            node.SetOption(NodeKeys.StackOpen, null);
            (node.X, node.Y) = (members[0].X, members[0].Y);
        }
    }

    /// <summary>Grid positions for an opened stack: about square, row by row from the lead's top-left, rows as tall as the tallest member.</summary>
    public static List<(double X, double Y)> Grid(IReadOnlyList<CanvasNode> members, double gap = Gap)
    {
        var columns = (int)Math.Ceiling(Math.Sqrt(members.Count));
        var (width, height) = (members.Max(n => n.Width), members.Max(n => n.Height));
        return members.Select((_, i) => (members[0].X + i % columns * (width + gap), members[0].Y + i / columns * (height + gap))).ToList();
    }

    /// <summary>Row positions: one line from the lead's top-left, each member right of the previous one.</summary>
    public static List<(double X, double Y)> Row(IReadOnlyList<CanvasNode> members, double gap = Gap)
    {
        var x = members[0].X;
        return members.Select(n =>
        {
            var at = (x, members[0].Y);
            x += n.Width + gap;
            return at;
        }).ToList();
    }

    /// <summary>The width and height a batch takes up once spread out in a row or a grid; one card's size when it stays a stack.</summary>
    public static (double Width, double Height) Footprint(IReadOnlyList<CanvasNode> members, StackLayout layout, double gap)
    {
        if (layout == StackLayout.Stack || members.Count < 2) return (members.Max(n => n.Width), members.Max(n => n.Height));
        var at = Spread(members, layout, gap);
        return (at.Select((p, i) => p.X + members[i].Width).Max() - at.Min(p => p.X), at.Select((p, i) => p.Y + members[i].Height).Max() - at.Min(p => p.Y));
    }

    /// <summary>The layout named by a tool argument (stack, row, grid; any case); null when it is none of them.</summary>
    public static StackLayout? ParseLayout(string? text) => text?.Trim().ToLowerInvariant() switch { "stack" => StackLayout.Stack, "row" => StackLayout.Row, "grid" => StackLayout.Grid, _ => null };

    /// <summary>The gap a node asks for between the members of its next batch. Agents can set options freely, so anything but a finite, non-negative number is the default.</summary>
    public static double GapOf(CanvasNode node) =>
        double.TryParse(node.Option(NodeKeys.Gap), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gap) && double.IsFinite(gap) && gap >= 0 ? gap : Gap;

    /// <summary>
    /// Mark the members as an opened stack and return where each one goes (row or grid from the lead's top-left); the caller moves them, at once or gliding.
    /// The one place both the stack button and the agent tools open a stack, so they cannot disagree.
    /// </summary>
    public static List<(double X, double Y)> Open(IReadOnlyList<CanvasNode> members, StackLayout layout = StackLayout.Grid, double gap = Gap)
    {
        foreach (var member in members) member.SetOption(NodeKeys.StackOpen, "true");
        return Spread(members, layout, gap);
    }

    /// <summary>Row or grid positions; a gap large enough to overflow gives positions that cannot be saved, and the default gap is used instead.</summary>
    private static List<(double X, double Y)> Spread(IReadOnlyList<CanvasNode> members, StackLayout layout, double gap)
    {
        List<(double X, double Y)> At(double between) => layout == StackLayout.Row ? Row(members, between) : Grid(members, between);
        var positions = At(gap);
        return positions.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y)) ? positions : At(Gap);
    }

    /// <summary>Gather an opened stack back onto its lead's spot at once and mark it collapsed.</summary>
    public static void Collapse(IReadOnlyList<CanvasNode> members)
    {
        foreach (var member in members)
        {
            (member.X, member.Y) = (members[0].X, members[0].Y);
            member.SetOption(NodeKeys.StackOpen, null);
        }
    }

    /// <summary>
    /// Make the outputs of a new batch one stack, then spread them when the run asked for a row or a grid. Only for nodes just created:
    /// a retry of one failed output never comes through here, so members the user has moved stay where they are.
    /// </summary>
    public static void FormBatch(IReadOnlyList<CanvasNode> members, StackLayout layout, double gap)
    {
        Form(members);
        if (layout == StackLayout.Stack || members.Count < 2) return;
        var positions = Open(members, layout, gap);
        for (var i = 0; i < members.Count; i++) (members[i].X, members[i].Y) = positions[i];
    }
}
