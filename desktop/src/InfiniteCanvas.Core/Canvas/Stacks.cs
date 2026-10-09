namespace InfiniteCanvas.Core.Canvas;

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
    public static List<(double X, double Y)> Grid(IReadOnlyList<CanvasNode> members)
    {
        var columns = (int)Math.Ceiling(Math.Sqrt(members.Count));
        var (width, height) = (members.Max(n => n.Width), members.Max(n => n.Height));
        return members.Select((_, i) => (members[0].X + i % columns * (width + Gap), members[0].Y + i / columns * (height + Gap))).ToList();
    }
}
