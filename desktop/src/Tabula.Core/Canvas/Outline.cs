namespace Tabula.Core.Canvas;

public enum OutlineRowKind { Header, Batch, Node }

/// <summary>One row in the searchable canvas outline tree: a section header, a collapsible multi-card batch, or a single node.</summary>
public sealed record OutlineRow(
    OutlineRowKind Kind,
    int Depth,
    string Label,
    string? Subtitle = null,
    string? BatchId = null,
    CanvasNode? Node = null,
    int Count = 0,
    bool Expanded = true);

/// <summary>
/// Builds the searchable tree of the canvas for the 大纲 drawer: groups (with their members and batches),
/// then top-level multi-card batches, then loose nodes grouped by kind.
/// </summary>
public static class Outline
{
    private static readonly (NodeKind Kind, string Name)[] LooseOrder =
    [
        (NodeKind.Image, "图片"),
        (NodeKind.Text, "文本"),
        (NodeKind.Config, "配方"),
        (NodeKind.Audio, "音频"),
        (NodeKind.Video, "视频"),
    ];

    public static List<OutlineRow> Build(CanvasDocument doc, string query = "", IReadOnlySet<string>? collapsedBatches = null)
    {
        var q = query.Trim();
        var searching = q.Length > 0;
        var collapsed = collapsedBatches ?? new HashSet<string>();
        var rows = new List<OutlineRow>();
        var seen = new HashSet<string>();

        // Stable 1-based round numbers across every multi-member batch in the document.
        var roundNumbers = new Dictionary<string, int>();
        var round = 0;
        foreach (var node in doc.Nodes)
            if (Stacks.Of(node) is { } sid && !roundNumbers.ContainsKey(sid) && Stacks.Members(doc, sid).Count > 1)
                roundNumbers[sid] = ++round;

        bool Matches(CanvasNode n)
        {
            if (!searching) return true;
            var haystack = $"{n.Title} {Stacks.OwnTitle(n)} {n.Prompt} {(n.Kind == NodeKind.Text ? n.Content : "")} {n.Model}";
            return haystack.Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        static string? SnippetOf(CanvasNode n)
        {
            var raw = n.Kind == NodeKind.Text && !string.IsNullOrWhiteSpace(n.Content) ? n.Content : n.Prompt;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var single = raw.Trim().Replace("\r", " ").Replace("\n", " ");
            return single.Length > 32 ? single[..32] + "…" : single;
        }

        void AddBatch(string stackId, int depth)
        {
            var members = Stacks.Members(doc, stackId);
            if (members.Count < 2) return;
            foreach (var m in members) seen.Add(m.Id);
            var matched = searching ? members.Where(Matches).ToList() : members;
            var lead = members[0];
            var batchMatches = !searching || matched.Count > 0 || Matches(lead);
            if (!batchMatches) return;

            var done = members.Count(m => m.HasContent && m.Status != NodeStatus.Loading);
            var failed = members.Count(m => m.Status == NodeStatus.Error && !m.HasContent);
            var title = lead.Title != Stacks.OwnTitle(lead) && !string.IsNullOrWhiteSpace(lead.Title)
                ? lead.Title
                : $"第 {roundNumbers.GetValueOrDefault(stackId, 1)} 轮";
            var parts = new List<string> { title, $"{members.Count} 张" };
            if (done < members.Count) parts.Add($"{done}/{members.Count} 完成");
            if (failed > 0) parts.Add($"{failed} 失败");

            var expanded = searching || !collapsed.Contains(stackId);
            rows.Add(new(OutlineRowKind.Batch, depth, string.Join(" · ", parts), lead.Model, stackId, lead, members.Count, expanded));
            if (!expanded) return;
            foreach (var m in matched)
                rows.Add(new(OutlineRowKind.Node, depth + 1, Stacks.OwnTitle(m), SnippetOf(m), stackId, m));
        }

        // 1. Groups with their members (including batches living inside a group).
        foreach (var group in doc.Nodes.Where(n => n.Kind == NodeKind.Group))
        {
            seen.Add(group.Id);
            var kids = CanvasGraph.GroupMembers(doc, group.Id).ToList();
            var startIndex = rows.Count;
            var groupMatched = Matches(group);
            var childBatches = new HashSet<string>();

            // Placeholder; inserted only if group or any descendant matches.
            var groupRow = new OutlineRow(OutlineRowKind.Node, 0, group.Title, $"{kids.Count} 个节点", null, group, kids.Count);
            rows.Add(groupRow);

            foreach (var kid in kids)
            {
                if (seen.Contains(kid.Id)) continue;
                if (Stacks.Of(kid) is { } sid && Stacks.Members(doc, sid).Count > 1)
                {
                    if (childBatches.Add(sid)) AddBatch(sid, 1);
                }
                else
                {
                    seen.Add(kid.Id);
                    if (!searching || Matches(kid) || groupMatched)
                        rows.Add(new(OutlineRowKind.Node, 1, Stacks.OwnTitle(kid), SnippetOf(kid), null, kid));
                }
            }
            if (searching && !groupMatched && rows.Count == startIndex + 1)
                rows.RemoveAt(startIndex);
        }

        // 2. Top-level multi-member batches not already inside a group.
        foreach (var sid in roundNumbers.Keys)
        {
            var members = Stacks.Members(doc, sid);
            if (members.Count > 1 && !members.Any(m => seen.Contains(m.Id)))
                AddBatch(sid, 0);
        }

        // 3. Loose nodes grouped by kind.
        foreach (var (kind, name) in LooseOrder)
        {
            var loose = doc.Nodes.Where(n => n.Kind == kind && !seen.Contains(n.Id)).ToList();
            if (loose.Count == 0) continue;
            var matched = searching ? loose.Where(Matches).ToList() : loose;
            if (matched.Count == 0) continue;
            rows.Add(new(OutlineRowKind.Header, 0, name, null, null, null, matched.Count));
            foreach (var node in matched)
            {
                seen.Add(node.Id);
                rows.Add(new(OutlineRowKind.Node, 1, Stacks.OwnTitle(node), SnippetOf(node), null, node));
            }
        }

        return rows;
    }
}
