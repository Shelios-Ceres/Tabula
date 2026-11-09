namespace InfiniteCanvas.Core.Canvas;

/// <summary>
/// Choosing between pictures on the canvas: keep one member of a batch, or make a picture take the place of another (finalising).
/// Both move the connections of the nodes that go over to the node that stays, before those nodes are deleted, and change nothing else
/// in the document; the caller records history once and deletes the returned nodes, so a file is never touched and one undo restores everything.
/// </summary>
public static class Picks
{
    /// <summary>
    /// Keep one member of a stack: it leaves the stack and takes the lead's place, title (when the lead was named) and group, and the connections
    /// of the other members. Returns the IDs of the other members, to delete.
    /// </summary>
    public static List<string> KeepOne(CanvasDocument doc, string keptId)
    {
        var kept = doc.Find(keptId) ?? throw new ArgumentException($"找不到节点「{keptId}」");
        if (Stacks.Of(kept) is not { } stack || Stacks.Members(doc, stack) is not { Count: > 1 } members) throw new ArgumentException("这个节点不在一批（摞）里，或这一批只有一个成员");
        var lead = members[0];
        var others = members.Where(m => m != kept).ToList();
        MoveConnections(doc, others.Select(m => m.Id).ToHashSet(), kept);
        // Cleared before anything is deleted, so deleting the others cannot take the kept card along with the stack.
        kept.SetOption(NodeKeys.Stack, null);
        kept.SetOption(NodeKeys.StackOpen, null);
        if (kept != lead)
        {
            (kept.X, kept.Y, kept.GroupId) = (lead.X, lead.Y, lead.GroupId);
            // A batch the user has named keeps its name; one still called after its file number does not.
            if (lead.Title != Stacks.OwnTitle(lead)) kept.Title = lead.Title;
        }
        return others.Select(m => m.Id).ToList();
    }

    /// <summary>
    /// Let a picture take the place of another: it moves to the old node's position and width (its height follows its own ratio), takes its title and group
    /// and keeps its connections besides the new ones. Returns the old node's ID, to delete. The old node must not be one of several in a stack.
    /// </summary>
    public static List<string> Promote(CanvasDocument doc, string newId, string oldId)
    {
        CheckPromote(doc, newId, oldId);
        var (fresh, old) = (doc.Find(newId)!, doc.Find(oldId)!);
        MoveConnections(doc, [old.Id], fresh);
        // The picture leaves any batch it came from; the rest of that batch stays.
        fresh.SetOption(NodeKeys.Stack, null);
        fresh.SetOption(NodeKeys.StackOpen, null);
        (fresh.X, fresh.Y, fresh.Width) = (old.X, old.Y, old.Width);
        fresh.Height = fresh.NaturalWidth > 0 && fresh.NaturalHeight > 0 ? Math.Round(fresh.Width * fresh.NaturalHeight / fresh.NaturalWidth) : old.Height;
        (fresh.Title, fresh.GroupId) = (old.Title, old.GroupId);
        return [old.Id];
    }

    /// <summary>Whether <see cref="Promote"/> would work, without changing anything; throws the reason when it would not.</summary>
    public static bool CheckPromote(CanvasDocument doc, string newId, string oldId)
    {
        var fresh = doc.Find(newId) ?? throw new ArgumentException($"找不到节点「{newId}」");
        var old = doc.Find(oldId) ?? throw new ArgumentException($"找不到节点「{oldId}」");
        if (fresh == old) throw new ArgumentException("定稿的图片和被替换的图片不能是同一个节点");
        if (fresh.Kind != NodeKind.Image || !fresh.HasContent) throw new ArgumentException("定稿的必须是带内容的图片节点");
        if (old.Kind != NodeKind.Image) throw new ArgumentException("被替换的必须是图片节点");
        if (Stacks.Of(old) is { } stack && Stacks.Members(doc, stack).Count > 1) throw new ArgumentException("被替换的图片在一批（摞）里，请先对它用「留这张」");
        return true;
    }

    /// <summary>Give <paramref name="to"/> every connection that <paramref name="from"/> nodes have to a node outside them, unless it has it already.</summary>
    private static void MoveConnections(CanvasDocument doc, HashSet<string> from, CanvasNode to)
    {
        var added = new List<CanvasConnection>();
        foreach (var connection in doc.Connections.ToList())
        {
            var (fromGone, toGone) = (from.Contains(connection.From), from.Contains(connection.To));
            if (fromGone == toGone || connection.From == to.Id || connection.To == to.Id) continue;
            var (source, target) = fromGone ? (to.Id, connection.To) : (connection.From, to.Id);
            if (doc.Find(source) is not { } a || doc.Find(target) is not { } b || CanvasGraph.NormalizeConnection(a, b, true) is null) continue;
            if (doc.Connections.Concat(added).Any(c => c.From == source && c.To == target)) continue;
            added.Add(CanvasConnection.Create(source, target));
        }
        foreach (var connection in added) doc.Connections.Add(connection);
    }
}
