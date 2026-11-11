using System.IO.Pipes;
using System.Text.Json;
using InfiniteCanvas.Agents;
using InfiniteCanvas.Core.Canvas;
using InfiniteCanvas.Core.Models;
using InfiniteCanvas.Core.Settings;
using InfiniteCanvas.Core.Storage;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace InfiniteCanvas.Tests;

/// <summary>ACP client against an in-memory fake agent, canvas tools against a fake canvas, and the MCP server end to end over its pipe.</summary>
public class AgentTests
{
    private sealed class FakeHost : ICanvasHost
    {
        public CanvasDocument Document { get; set; } = new() { Id = "canvas-1", Title = "测试" };
        public Action? AfterUi { get; set; }
        public List<string> ViewedProjects { get; } = [];
        public List<string> LibraryProjects { get; } = [];
        public List<string> Selected { get; } = [];
        public List<string> Generated { get; } = [];
        public int Records { get; private set; }
        private CanvasHistory? _history;
        public CanvasHistory History => _history ??= new CanvasHistory(Document);
        public IReadOnlyCollection<string> Selection => Selected;
        public Viewport Viewport { get; set; } = new(0, 0, 1);
        public (double X, double Y) ViewCenter => (500, 400);
        /// <summary>True while this thread is inside OnUiAsync: the stand-in for "on the UI thread", since the fake runs the action where it is called.</summary>
        [ThreadStatic] public static bool OnUi;
        /// <summary>Like a closing canvas: every OnUiAsync fails, so cleanup that needs the UI thread cannot run.</summary>
        public bool Closing { get; set; }
        public Task<T> OnUiAsync<T>(Func<T> action)
        {
            if (Closing) return Task.FromException<T>(new ModelContextProtocol.McpException("画布正在关闭，请稍后重试"));
            var nested = OnUi;
            OnUi = true;
            T result;
            try { result = action(); }
            finally { OnUi = nested; }
            AfterUi?.Invoke();
            return Task.FromResult(result);
        }
        public void RecordHistory() { Records++; History.Record(); }
        public void Select(IEnumerable<string> ids) { Selected.Clear(); Selected.AddRange(ids); }
        public void Delete(IEnumerable<string> ids)
        {
            var set = Stacks.Expand(Document, ids);
            foreach (var node in Document.Nodes.Where(n => set.Contains(n.Id)).ToList()) Document.Nodes.Remove(node);
            foreach (var c in Document.Connections.Where(c => set.Contains(c.From) || set.Contains(c.To)).ToList()) Document.Connections.Remove(c);
        }
        public void Reveal(IReadOnlyList<CanvasNode> nodes) { }
        public bool IsRunning(string nodeId) => Generated.Contains(nodeId);
        public event Action? RunningChanged;
        /// <summary>A node leaves the running set with no Status change, as when a run is cancelled while it still loads its references.</summary>
        public void StopRunning(string nodeId)
        {
            Generated.Remove(nodeId);
            RunningChanged?.Invoke();
        }
        public string? CannotGenerate(CanvasNode node) => string.IsNullOrWhiteSpace(node.Prompt) ? "请先输入提示词" : null;
        public void Generate(CanvasNode node, bool recordHistory = true)
        {
            if (recordHistory) RecordHistory();
            Generated.Add(node.Id);
        }
        public Task<(byte[] Bytes, string MimeType)?> LoadImageAsync(CanvasNode node, int maxEdge)
        {
            ViewedProjects.Add(Document.Id);
            return Task.FromResult<(byte[], string)?>(node.HasContent ? ([1, 2, 3], "image/png") : null);
        }
        public string? Group(IReadOnlyCollection<string> ids, string? title)
        {
            var members = Document.Nodes.Where(n => n.Kind != NodeKind.Group && Stacks.Expand(Document, ids).Contains(n.Id)).ToList();
            if (members.Count < 2 || members.All(n => n.GroupId is not null && n.GroupId == members[0].GroupId)) return null;
            var group = new CanvasNode { Kind = NodeKind.Group, Title = title ?? "分组" };
            Document.Nodes.Insert(0, group);
            foreach (var node in members) node.GroupId = group.Id;
            return group.Id;
        }
        public void Ungroup(IReadOnlyCollection<string> ids)
        {
            ids = Stacks.Expand(Document, ids);
            var released = Document.Nodes.Where(n => ids.Contains(n.Id) || n.GroupId is not null && ids.Contains(n.GroupId)).ToList();
            var old = released.Select(n => n.GroupId).OfType<string>().Concat(ids).ToHashSet();
            foreach (var node in released) node.GroupId = null;
            RemoveEmptyGroups(old);
        }
        public IReadOnlyList<string> AddToGroup(string groupId, IReadOnlyCollection<string> ids, bool fit) => Regroup(ids, groupId, fit);
        public IReadOnlyList<string> RemoveFromGroup(IReadOnlyCollection<string> ids, bool fit) => Regroup(ids, null, fit);
        private List<string> Regroup(IReadOnlyCollection<string> ids, string? groupId, bool fit)
        {
            var (moved, left) = CanvasGraph.MoveToGroup(Document, Stacks.Expand(Document, ids), groupId);
            if (fit) foreach (var id in left.Append(groupId).OfType<string>()) CanvasGraph.FitGroup(Document, id);
            RemoveEmptyGroups(left);
            return moved.Select(n => n.Id).ToList();
        }
        private void RemoveEmptyGroups(HashSet<string> ids)
        {
            ids.RemoveWhere(id => Document.Nodes.Any(n => n.GroupId == id));
            foreach (var group in Document.Nodes.Where(n => n.Kind == NodeKind.Group && ids.Contains(n.Id)).ToList()) Document.Nodes.Remove(group);
            foreach (var connection in Document.Connections.Where(c => ids.Contains(c.From) || ids.Contains(c.To)).ToList()) Document.Connections.Remove(connection);
        }
        public List<string> Imported { get; } = [];
        /// <summary>File names this host cannot copy; they come back in Failed, as the real host reports them without a dialog.</summary>
        public HashSet<string> Unreadable { get; } = [];
        public List<(string NodeId, string Path)> Replaced { get; } = [];
        public Task<ImportResult> ImportAsync(IReadOnlyList<string> paths, double x, double y)
        {
            var failed = paths.Where(p => Unreadable.Contains(Path.GetFileName(p))).ToList();
            var imported = paths.Except(failed).ToList();
            Imported.AddRange(imported);
            return Task.FromResult(new ImportResult(imported.Select(_ => Ids.New("node")).ToList(), failed.Select(p => new ImportFailure(p, "文件被占用")).ToList()));
        }
        /// <summary>Specs the host was asked to compose; it adds one picture node and records one undo step, like the real one.</summary>
        public List<ComposeSpec> Composed { get; } = [];
        public double ComposeScale { get; set; } = 1;
        public Task<(string Id, int Width, int Height, double Scale)> ComposeAsync(ComposeSpec spec)
        {
            RecordHistory();
            Composed.Add(spec);
            var node = new CanvasNode { Kind = NodeKind.Image, Title = $"拼版 · {spec.Cells.Count} 张", Content = "图片-900.png", Status = NodeStatus.Success };
            Document.Nodes.Add(node);
            foreach (var cell in spec.Cells) Document.Connections.Add(CanvasConnection.Create(cell.NodeId, node.Id));
            return Task.FromResult((node.Id, 1000, 500, ComposeScale));
        }
        public List<(string A, string B, double Threshold)> Compared { get; } = [];
        public ImageDifference Difference { get; set; } = new(0.1, 0.9, 0.25, new UnitBox(0.1, 0.2, 0.3, 0.4), 0.05, 1000);
        public bool SizeMismatch { get; set; }
        public Task<(ImageDifference Difference, bool SizeMismatch)> CompareAsync(CanvasNode a, CanvasNode b, double threshold)
        {
            Compared.Add((a.Id, b.Id, threshold));
            return Task.FromResult((Difference with { Threshold = threshold }, SizeMismatch));
        }
        public Task ReplaceMediaAsync(CanvasNode node, string path)
        {
            if (Unreadable.Contains(Path.GetFileName(path))) return Task.FromException(new IOException("文件被占用"));
            RecordHistory();
            Replaced.Add((node.Id, path));
            node.Content = Path.GetFileName(path);
            return Task.CompletedTask;
        }
        public async Task<string> AddToLibraryAsync(CanvasNode node, bool replace)
        {
            LibraryProjects.Add(Document.Id);
            var dir = Directory.CreateTempSubdirectory("ic-fake").FullName;
            try
            {
                var file = Path.Combine(dir, node.Title + ".png");
                await File.WriteAllBytesAsync(file, [9, 9]);
                return await InfiniteCanvas.Core.Storage.LibraryStore.PutAsync(Document.Id, file, replace);
            }
            finally { Directory.Delete(dir, true); }
        }
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Apply_ops_resolve_refs_within_one_batch_and_record_one_undo_step()
    {
        var host = new FakeHost();
        var result = Parse(CanvasTools.Apply(host,
        [
            new CanvasOp { Type = "add_node", Kind = "text", Ref = "a", Text = "一只猫" },
            new CanvasOp { Type = "add_node", Kind = "image", Ref = "b", Prompt = "画出来", X = 600, Y = 0 },
            new CanvasOp { Type = "connect", From = "a", To = "b" },
            new CanvasOp { Type = "update_node", Id = "b", Title = "猫图", Ratio = "16:9", Options = new() { ["watermark"] = "true", ["background"] = null } },
            new CanvasOp { Type = "select", Ids = ["b"] },
            new CanvasOp { Type = "add_node", Kind = "group" },
        ]));
        Assert.Equal(1, host.Records);
        var (text, image) = (host.Document.Nodes[0], host.Document.Nodes[1]);
        Assert.Equal(("一只猫", "猫图", "16:9", 600d), (text.Content, image.Title, image.Ratio, image.X));
        Assert.Equal("true", image.Option("watermark"));
        Assert.False(image.Options.ContainsKey("background"));
        Assert.Equal((text.Id, image.Id), (host.Document.Connections.Single().From, host.Document.Connections.Single().To));
        Assert.Equal([image.Id], host.Selected);
        var errors = result.GetProperty("results").EnumerateArray().Where(r => r.TryGetProperty("error", out _)).ToList();
        Assert.Contains("不支持的节点类型", errors.Single().GetProperty("error").GetString());
    }

    [Fact]
    public void Apply_ops_group_new_nodes_by_ref_and_ungroup_them()
    {
        var host = new FakeHost();
        var result = Parse(CanvasTools.Apply(host,
        [
            new CanvasOp { Type = "add_node", Kind = "text", Ref = "a", Text = "一" },
            new CanvasOp { Type = "add_node", Kind = "text", Ref = "b", Text = "二" },
            new CanvasOp { Type = "group", Ids = ["a", "b"], Title = "分镜", Ref = "g" },
        ]));
        var group = host.Document.Nodes.Single(n => n.Kind == NodeKind.Group);
        Assert.Equal(("分镜", group.Id), (group.Title, result.GetProperty("results")[2].GetProperty("groupId").GetString()));
        Assert.All(host.Document.Nodes.Where(n => n.Kind == NodeKind.Text), n => Assert.Equal(group.Id, n.GroupId));
        CanvasTools.Apply(host, [new CanvasOp { Type = "ungroup", Ids = [group.Id] }]);
        Assert.All(host.Document.Nodes.Where(n => n.Kind == NodeKind.Text), n => Assert.Null(n.GroupId));
        var single = Parse(CanvasTools.Apply(host, [new CanvasOp { Type = "group", Ids = [host.Document.Nodes.Last().Id] }]));
        Assert.Contains("至少要有两个节点", single.GetProperty("results")[0].GetProperty("error").GetString());
    }

    private static (FakeHost Host, CanvasNode Group, CanvasNode A, CanvasNode B) GroupedPair()
    {
        var host = new FakeHost();
        var a = CanvasNode.Create(NodeKind.Text, 100, 100);
        var b = CanvasNode.Create(NodeKind.Text, 600, 100);
        var rect = CanvasGraph.GroupRect([a, b]);
        var group = new CanvasNode { Kind = NodeKind.Group, Title = "旧分组", X = rect.Left, Y = rect.Top, Width = rect.Width, Height = rect.Height };
        a.GroupId = b.GroupId = group.Id;
        foreach (var node in new[] { group, a, b }) host.Document.Nodes.Add(node);
        return (host, group, a, b);
    }

    [Fact]
    public void Group_after_ungroup_in_one_batch_names_the_group_that_is_gone()
    {
        var (host, group, a, _) = GroupedPair();
        var result = Parse(CanvasTools.Apply(host,
        [
            new CanvasOp { Type = "ungroup", Ids = [group.Id] },
            new CanvasOp { Type = "group", Ids = [group.Id, a.Id] },
        ]));
        var error = result.GetProperty("results")[1].GetProperty("error").GetString();
        Assert.Contains("至少要有两个节点", error);
        Assert.Contains(group.Id, error);
    }

    [Fact]
    public void Group_add_keeps_the_group_id_and_connections_and_deletes_the_group_it_empties()
    {
        var (host, group, a, b) = GroupedPair();
        var other = CanvasNode.Create(NodeKind.Text, 100, 700);
        var solo = CanvasNode.Create(NodeKind.Text, 900, 700);
        var second = new CanvasNode { Kind = NodeKind.Group, Title = "第二组", X = 850, Y = 650, Width = 400, Height = 300 };
        solo.GroupId = second.Id;
        foreach (var node in new[] { second, other, solo }) host.Document.Nodes.Add(node);
        host.Document.Connections.Add(CanvasConnection.Create(other.Id, group.Id));
        var before = (group.X, group.Y, group.Width, group.Height);

        var result = Parse(CanvasTools.Apply(host, [new CanvasOp { Type = "group_add", Id = group.Id, Ids = [other.Id, solo.Id] }]));

        Assert.Equal(1, host.Records);
        Assert.Equal([other.Id, solo.Id], result.GetProperty("results")[0].GetProperty("added").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.All(new[] { a, b, other, solo }, n => Assert.Equal(group.Id, n.GroupId));
        Assert.Equal("旧分组", host.Document.Find(group.Id)!.Title);
        Assert.Single(host.Document.Connections);
        Assert.Null(host.Document.Find(second.Id));
        var rect = CanvasGraph.GroupRect([a, b, other, solo]);
        Assert.Equal((rect.Left, rect.Top, rect.Width, rect.Height), (group.X, group.Y, group.Width, group.Height));
        Assert.NotEqual(before, (group.X, group.Y, group.Width, group.Height));
        Assert.True(host.History.Undo());
        Assert.Equal(second.Id, host.Document.Find(solo.Id)!.GroupId);
    }

    [Fact]
    public void Group_add_without_fit_leaves_the_group_size_and_remove_releases_members()
    {
        var (host, group, a, b) = GroupedPair();
        var outside = CanvasNode.Create(NodeKind.Text, 3000, 3000);
        host.Document.Nodes.Add(outside);
        var size = (group.X, group.Y, group.Width, group.Height);
        CanvasTools.Apply(host, [new CanvasOp { Type = "group_add", Id = group.Id, Ids = [outside.Id], Fit = false }]);
        Assert.Equal(size, (group.X, group.Y, group.Width, group.Height));
        Assert.Equal(group.Id, outside.GroupId);

        var removed = Parse(CanvasTools.Apply(host, [new CanvasOp { Type = "group_remove", Ids = [outside.Id, a.Id] }]));
        Assert.Equal(2, removed.GetProperty("results")[0].GetProperty("removed").GetArrayLength());
        Assert.Null(outside.GroupId);
        Assert.Null(a.GroupId);
        Assert.Equal(CanvasGraph.GroupRect([b]).Left, group.X);

        CanvasTools.Apply(host, [new CanvasOp { Type = "group_remove", Ids = [b.Id] }]);
        Assert.Null(host.Document.Find(group.Id));
    }

    [Fact]
    public void Group_add_moves_collapsed_stacks_whole_but_open_members_alone()
    {
        var (host, group, _, _) = GroupedPair();
        var first = CanvasNode.Create(NodeKind.Image, 0, 2000);
        var second = CanvasNode.Create(NodeKind.Image, 0, 2000);
        Stacks.Form([first, second]);
        host.Document.Nodes.Add(first);
        host.Document.Nodes.Add(second);
        CanvasTools.Apply(host, [new CanvasOp { Type = "group_add", Id = group.Id, Ids = [first.Id], Fit = false }]);
        Assert.Equal((group.Id, group.Id), (first.GroupId, second.GroupId));

        CanvasTools.Apply(host, [new CanvasOp { Type = "group_remove", Ids = [first.Id, second.Id], Fit = false }]);
        first.SetOption(NodeKeys.StackOpen, "true");
        second.SetOption(NodeKeys.StackOpen, "true");
        CanvasTools.Apply(host, [new CanvasOp { Type = "group_add", Id = group.Id, Ids = [first.Id], Fit = false }]);
        Assert.Equal((group.Id, (string?)null), (first.GroupId, second.GroupId));
    }

    [Fact]
    public void Group_add_and_remove_reject_bad_input_without_changing_the_document()
    {
        var (host, group, a, _) = GroupedPair();
        var text = CanvasNode.Create(NodeKind.Text, 0, 0);
        host.Document.Nodes.Add(text);
        var original = host.Document.Nodes.Select(n => InfiniteCanvas.Core.Json.Serialize(n)).ToList();
        var result = Parse(CanvasTools.Apply(host,
        [
            new CanvasOp { Type = "group_add", Id = text.Id, Ids = [a.Id] },
            new CanvasOp { Type = "group_add", Id = group.Id, Ids = [group.Id] },
            new CanvasOp { Type = "group_add", Id = group.Id, Ids = [text.Id, "missing"] },
            new CanvasOp { Type = "group_remove", Ids = [group.Id] },
            new CanvasOp { Type = "group_remove", Ids = ["missing"] },
        ]));
        var errors = result.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("error").GetString()!).ToList();
        Assert.Equal(5, errors.Count);
        Assert.Contains("必须是分组节点", errors[0]);
        Assert.Contains("不能嵌套", errors[1]);
        Assert.Contains("missing", errors[2]);
        Assert.Contains("不能嵌套", errors[3]);
        Assert.Equal(original, host.Document.Nodes.Select(n => InfiniteCanvas.Core.Json.Serialize(n)).ToList());
    }

    [Fact]
    public void Group_reports_already_grouped_apart_from_missing_nodes()
    {
        var (host, _, a, b) = GroupedPair();
        var result = Parse(CanvasTools.Apply(host, [new CanvasOp { Type = "group", Ids = [a.Id, b.Id] }]));
        Assert.Contains("已经都在同一个分组", result.GetProperty("results")[0].GetProperty("error").GetString());
    }

    [Fact]
    public async Task Get_state_is_brief_by_default_filters_and_get_node_returns_full_fields()
    {
        var host = new FakeHost();
        var (group, inside, outside) = (new CanvasNode { Kind = NodeKind.Group, Title = "组", X = 0, Y = 0, Width = 500, Height = 400 }, CanvasNode.Create(NodeKind.Image, 100, 100), CanvasNode.Create(NodeKind.Text, 2000, 2000));
        inside.Prompt = "一只猫";
        inside.Content = "图片-001.png";
        inside.GroupId = group.Id;
        outside.Content = "正文内容";
        outside.SetOption("watermark", "true");
        foreach (var added in new[] { group, inside, outside }) host.Document.Nodes.Add(added);
        host.Document.Connections.Add(CanvasConnection.Create(inside.Id, outside.Id));
        host.Document.Connections.Add(CanvasConnection.Create(inside.Id, group.Id));
        await using var server = new CanvasMcpServer(host);
        await using var pipe = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe, null));
        async Task<JsonElement> Call(string tool, Dictionary<string, object?>? args = null) => Parse(((TextContentBlock)(await client.CallToolAsync(tool, args)).Content[0]).Text);

        var brief = await Call("canvas_get_state");
        var node = brief.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == inside.Id);
        Assert.False(node.TryGetProperty("prompt", out _) || node.TryGetProperty("options", out _) || node.TryGetProperty("text", out _));
        Assert.Equal("图片-001.png", node.GetProperty("file").GetString());
        Assert.Equal(3, brief.GetProperty("nodes").GetArrayLength());

        var full = await Call("canvas_get_state", new() { ["detail"] = "full" });
        Assert.Equal("一只猫", full.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == inside.Id).GetProperty("prompt").GetString());

        var byGroup = await Call("canvas_get_state", new() { ["groupId"] = group.Id });
        Assert.Equal(2, byGroup.GetProperty("nodes").GetArrayLength());
        Assert.Equal(1, byGroup.GetProperty("connections").GetArrayLength());
        var byIds = await Call("canvas_get_state", new() { ["ids"] = new[] { inside.Id, outside.Id } });
        Assert.Equal(1, byIds.GetProperty("connections").GetArrayLength());
        var byRect = await Call("canvas_get_state", new() { ["rect"] = new[] { 1900d, 1900d, 2500d, 2500d } });
        Assert.Equal([outside.Id], byRect.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("id").GetString()!).ToArray());
        Assert.Equal(0, byRect.GetProperty("connections").GetArrayLength());

        foreach (var bad in new Dictionary<string, object?>[] { new() { ["detail"] = "x" }, new() { ["ids"] = new[] { "missing" } }, new() { ["groupId"] = inside.Id }, new() { ["rect"] = new[] { 5d, 5d, 1d, 9d } }, new() { ["rect"] = new[] { 1d, 2d } } })
            Assert.True((await client.CallToolAsync("canvas_get_state", bad)).IsError);

        var detail = await Call("canvas_get_node", new() { ["ids"] = new[] { inside.Id, outside.Id } });
        Assert.Equal("一只猫", detail.GetProperty("nodes")[0].GetProperty("prompt").GetString());
        Assert.Equal("正文内容", detail.GetProperty("nodes")[1].GetProperty("text").GetString());
        Assert.True((await client.CallToolAsync("canvas_get_node", new Dictionary<string, object?> { ["ids"] = new[] { "missing" } })).IsError);
    }

    [Theory]
    [InlineData("delete_nodes", false)]
    [InlineData("delete_nodes", true)]
    [InlineData("ungroup", false)]
    [InlineData("ungroup", true)]
    public void Apply_ops_expand_collapsed_stacks_but_keep_open_members_independent(string operation, bool open)
    {
        var host = new FakeHost();
        var first = CanvasNode.Create(NodeKind.Image, 0, 0);
        var second = CanvasNode.Create(NodeKind.Image, 0, 0);
        first.GroupId = second.GroupId = "group-1";
        Stacks.Form([first, second]);
        foreach (var node in new[] { first, second })
        {
            node.SetOption(NodeKeys.StackOpen, open ? "true" : null);
            host.Document.Nodes.Add(node);
        }

        CanvasTools.Apply(host, [new CanvasOp { Type = operation, Ids = [first.Id] }]);

        Assert.Equal(1, host.Records);
        if (operation == "delete_nodes")
            Assert.Equal(open ? new[] { second.Id } : [], host.Document.Nodes.Select(n => n.Id).ToArray());
        else
        {
            Assert.Null(first.GroupId);
            Assert.Equal(open ? "group-1" : null, second.GroupId);
        }
        Assert.True(host.History.Undo());
        Assert.Equal(new[] { first.Id, second.Id }, host.Document.Nodes.Select(n => n.Id).ToArray());
        Assert.All(host.Document.Nodes, node => Assert.Equal("group-1", node.GroupId));
        Assert.False(host.History.CanUndo);
    }

    [Fact]
    public void Apply_ops_reject_unknown_nodes_and_models_without_stopping_the_batch()
    {
        var host = new FakeHost();
        var result = Parse(CanvasTools.Apply(host,
        [
            new CanvasOp { Type = "update_node", Id = "missing", Title = "x" },
            new CanvasOp { Type = "add_node", Kind = "image", Model = "nowhere::model" },
            new CanvasOp { Type = "add_node", Kind = "text", Text = "ok" },
        ]));
        var results = result.GetProperty("results").EnumerateArray().ToList();
        Assert.Contains("找不到节点", results[0].GetProperty("error").GetString());
        Assert.Contains("未知模型", results[1].GetProperty("error").GetString());
        Assert.Equal("ok", host.Document.Nodes.Last().Content);
        Assert.Single(host.Document.Nodes);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_geometry_does_not_change_existing_nodes_or_leave_new_ones(double width)
    {
        var host = new FakeHost();
        var node = CanvasNode.Create(NodeKind.Image, 0, 0);
        host.Document.Nodes.Add(node);
        var original = InfiniteCanvas.Core.Json.Serialize(node);
        var result = Parse(CanvasTools.Apply(host,
        [
            new CanvasOp { Type = "update_node", Id = node.Id, Title = "不应保存", Width = width },
            new CanvasOp { Type = "add_node", Kind = "image", Width = width },
        ]));
        Assert.All(result.GetProperty("results").EnumerateArray(), item => Assert.True(item.TryGetProperty("error", out _)));
        Assert.Single(host.Document.Nodes);
        Assert.Equal(original, InfiniteCanvas.Core.Json.Serialize(node));
    }

    [Fact]
    public void Invalid_model_mode_or_text_is_rejected_before_any_field_is_changed()
    {
        var host = new FakeHost();
        var node = CanvasNode.Create(NodeKind.Image, 0, 0);
        host.Document.Nodes.Add(node);
        var original = InfiniteCanvas.Core.Json.Serialize(node);
        var result = Parse(CanvasTools.Apply(host,
        [
            new CanvasOp { Type = "update_node", Id = node.Id, Title = "不应保存", Model = "missing::model" },
            new CanvasOp { Type = "update_node", Id = node.Id, X = 900, Text = "不能写入图片" },
            new CanvasOp { Type = "update_node", Id = node.Id, X = 900, Mode = "99" },
            new CanvasOp { Type = "add_node", Kind = "config", Mode = "bad" },
            new CanvasOp { Type = "add_node", Kind = "99" },
        ]));
        Assert.All(result.GetProperty("results").EnumerateArray(), item => Assert.True(item.TryGetProperty("error", out _)));
        Assert.Equal(original, InfiniteCanvas.Core.Json.Serialize(Assert.Single(host.Document.Nodes)));
    }

    [Fact]
    public void Generation_in_an_apply_batch_uses_the_batch_undo_record()
    {
        var host = new FakeHost();
        CanvasTools.Apply(host,
        [
            new CanvasOp { Type = "add_node", Kind = "image", Ref = "image", Prompt = "一只猫" },
            new CanvasOp { Type = "run_generation", Id = "image" },
        ]);
        Assert.Single(host.Generated);
        Assert.Equal(1, host.Records);
        Assert.True(host.History.Undo());
        Assert.Empty(host.Document.Nodes);
        Assert.False(host.History.CanUndo);
    }

    [Fact]
    public void Command_lines_split_quotes_and_resolve_through_pathext()
    {
        var (npx, npxArgs) = CommandLine.Parse("npx -y @deepseek-ai/dsh --profile acp");
        Assert.Equal("npx", npx);
        Assert.Equal(["-y", "@deepseek-ai/dsh", "--profile", "acp"], npxArgs);
        var (file, args) = CommandLine.Parse("\"C:\\Program Files\\x\\agent.exe\" acp \"a b\"");
        Assert.Equal(("C:\\Program Files\\x\\agent.exe", "a b"), (file, args[1]));
        Assert.EndsWith("cmd.exe", CommandLine.Resolve("cmd"), StringComparison.OrdinalIgnoreCase);
        Assert.Null(CommandLine.Resolve("definitely-not-an-agent-command"));
    }

    private sealed class Handler : IAcpClientHandler
    {
        public List<JsonElement> Updates { get; } = [];
        public List<AcpPermissionOption> Asked { get; } = [];
        public void OnSessionUpdate(string sessionId, JsonElement update) { lock (Updates) Updates.Add(update); }
        public Task<string?> RequestPermissionAsync(string sessionId, JsonElement toolCall, IReadOnlyList<AcpPermissionOption> options)
        {
            Asked.AddRange(options);
            return Task.FromResult<string?>(options[0].OptionId);
        }
    }

    /// <summary>A scripted ACP agent: answers the handshake, then streams a chunk and asks permission during the prompt turn.</summary>
    private sealed class FakeAgent(Func<JsonRpc> rpc)
    {
        public JsonElement Session { get; private set; }

        [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
        public object Initialize(JsonElement p) => new { protocolVersion = 1, agentCapabilities = new { promptCapabilities = new { image = true } }, agentInfo = new { name = "fake", title = "假 Agent" }, authMethods = new[] { new { id = "login", name = "登录", type = "terminal", args = new[] { "--login" } } } };

        [JsonRpcMethod("session/new", UseSingleObjectParameterDeserialization = true)]
        public object NewSession(JsonElement p)
        {
            Session = p.Clone();
            return new { sessionId = "sess-1" };
        }

        [JsonRpcMethod("session/prompt", UseSingleObjectParameterDeserialization = true)]
        public async Task<object> Prompt(JsonElement p)
        {
            await rpc().NotifyWithParameterObjectAsync("session/update", new { sessionId = "sess-1", update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "好的" } } });
            var answer = await rpc().InvokeWithParameterObjectAsync<JsonElement>("session/request_permission", new
            {
                sessionId = "sess-1",
                toolCall = new { toolCallId = "call-1", title = "canvas_apply_ops" },
                options = new[] { new { optionId = "allow", name = "允许", kind = "allow_once" }, new { optionId = "reject", name = "拒绝", kind = "reject_once" } },
            });
            return new { stopReason = answer.GetProperty("outcome").GetProperty("optionId").GetString() == "allow" ? "end_turn" : "refusal" };
        }
    }

    [Fact]
    public async Task Acp_client_runs_a_prompt_turn_with_updates_and_permission()
    {
        var (clientSide, agentSide) = FullDuplexStream.CreatePair();
        JsonRpc agentRpc = null!;
        var agent = new FakeAgent(() => agentRpc);
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        agentRpc = new JsonRpc(new NewLineDelimitedMessageHandler(agentSide, agentSide, formatter));
        agentRpc.AddLocalRpcTarget(agent);
        agentRpc.StartListening();

        var handler = new Handler();
        await using var client = new AcpClient(clientSide, clientSide, handler);
        await client.InitializeAsync();
        Assert.Equal(("假 Agent", true, "terminal", "--login"), (client.AgentTitle, client.SupportsImages, client.AuthMethods.Single().Type, client.AuthMethods.Single().Args.Single()));

        var session = await client.NewSessionAsync("C:\\work", [new { name = "infinite-canvas", command = "C:\\app.exe", args = new[] { "--mcp-bridge", "pipe" }, env = Array.Empty<object>() }]);
        Assert.Equal("sess-1", session);
        Assert.Equal("--mcp-bridge", agent.Session.GetProperty("mcpServers")[0].GetProperty("args")[0].GetString());

        var stop = await client.PromptAsync(session, [new { type = "text", text = "画一只猫" }]);
        Assert.Equal("end_turn", stop);
        Assert.Equal("好的", handler.Updates.Single().GetProperty("content").GetProperty("text").GetString());
        Assert.Equal(["allow", "reject"], handler.Asked.Select(o => o.OptionId));
        agentRpc.Dispose();
    }

    private sealed class WaitingAgent
    {
        public TaskCompletionSource<string> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<object> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? CancelledSession { get; private set; }

        private Task<object> Wait(string method) { Received.TrySetResult(method); return Result.Task; }

        [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
        public Task<object> Initialize(JsonElement p) => Wait("initialize");
        [JsonRpcMethod("authenticate", UseSingleObjectParameterDeserialization = true)]
        public Task<object> Authenticate(JsonElement p) => Wait("authenticate");
        [JsonRpcMethod("session/new", UseSingleObjectParameterDeserialization = true)]
        public Task<object> NewSession(JsonElement p) => Wait("session/new");
        [JsonRpcMethod("session/prompt", UseSingleObjectParameterDeserialization = true)]
        public Task<object> Prompt(JsonElement p) => Wait("session/prompt");
        [JsonRpcMethod("session/cancel", UseSingleObjectParameterDeserialization = true)]
        public void Cancel(JsonElement p)
        {
            CancelledSession = p.GetProperty("sessionId").GetString();
            Result.TrySetResult(new { stopReason = "cancelled" });
        }
    }

    [Theory]
    [InlineData("initialize")]
    [InlineData("authenticate")]
    [InlineData("session/new")]
    [InlineData("session/prompt")]
    public async Task Acp_waits_can_be_cancelled_before_the_agent_replies(string method)
    {
        var (clientSide, agentSide) = FullDuplexStream.CreatePair();
        var agent = new WaitingAgent();
        using var rpc = new JsonRpc(new NewLineDelimitedMessageHandler(agentSide, agentSide, new SystemTextJsonFormatter()));
        rpc.AddLocalRpcTarget(agent);
        rpc.StartListening();
        await using var client = new AcpClient(clientSide, clientSide, new Handler());
        using var stop = new CancellationTokenSource();
        Task pending = method switch
        {
            "initialize" => client.InitializeAsync(stop.Token),
            "authenticate" => client.AuthenticateAsync("login", stop.Token),
            "session/new" => client.NewSessionAsync("C:\\work", [], stop.Token),
            _ => client.PromptAsync("sess-1", [], stop.Token),
        };
        Assert.Equal(method, await agent.Received.Task);
        stop.Cancel();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); }
        finally { agent.Result.TrySetResult(new { protocolVersion = 1, sessionId = "late-session", stopReason = "end_turn" }); }
    }

    [Fact]
    public async Task Acp_cancel_ends_a_live_turn_with_the_protocol_notification()
    {
        var (clientSide, agentSide) = FullDuplexStream.CreatePair();
        var agent = new WaitingAgent();
        using var rpc = new JsonRpc(new NewLineDelimitedMessageHandler(agentSide, agentSide, new SystemTextJsonFormatter()));
        rpc.AddLocalRpcTarget(agent);
        rpc.StartListening();
        await using var client = new AcpClient(clientSide, clientSide, new Handler());
        var pending = client.PromptAsync("sess-1", []);
        await agent.Received.Task;
        await client.CancelAsync("sess-1");
        Assert.Equal("cancelled", await pending);
        Assert.Equal("sess-1", agent.CancelledSession);
    }

    [Fact]
    public async Task Acp_cancelled_start_never_resolves_or_launches_a_command()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AcpClient.StartAsync("definitely-not-an-agent-command", Path.GetTempPath(), new Handler(), stop.Token));
    }

    [Fact]
    public async Task Shared_pipe_is_fixed_per_data_folder_and_serves_tools()
    {
        Assert.Equal(CanvasMcpServer.SharedPipeName("C:\\Data\\Canvas\\"), CanvasMcpServer.SharedPipeName("c:\\data\\canvas"));
        Assert.NotEqual(CanvasMcpServer.SharedPipeName("C:\\Data\\Canvas"), CanvasMcpServer.SharedPipeName("C:\\Data\\Other"));
        var name = CanvasMcpServer.SharedPipeName(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        await using var server = new CanvasMcpServer(new FakeHost(), name);
        Assert.Equal(name, server.PipeName);
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000);
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe, null));
        Assert.Equal(18, (await client.ListToolsAsync()).Count);
    }

    [Fact]
    public async Task Mcp_server_serves_canvas_tools_over_its_pipe()
    {
        var host = new FakeHost();
        await using var server = new CanvasMcpServer(host);
        await using var pipe = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe, null));

        var tools = await client.ListToolsAsync();
        Assert.Equal(["canvas_apply_ops", "canvas_compose", "canvas_generate", "canvas_get_image", "canvas_get_node", "canvas_get_state", "canvas_import_file", "canvas_replace_media", "canvas_run_generation", "generation_get_status", "generation_wait", "image_compare", "library_add", "library_list", "library_place", "library_remove", "library_rename", "usage_summary"], tools.Select(t => t.Name).Order());

        var generated = await client.CallToolAsync("canvas_generate", new Dictionary<string, object?> { ["mode"] = "image", ["prompt"] = "一只猫", ["ratio"] = "1:1" });
        var nodeId = Parse(((TextContentBlock)generated.Content[0]).Text).GetProperty("nodeId").GetString();
        Assert.Equal([nodeId!], host.Generated);
        Assert.Equal(1, host.Records);
        Assert.Equal(("一只猫", "1:1"), (host.Document.Nodes.Single().Prompt, host.Document.Nodes.Single().Ratio));

        var state = Parse(((TextContentBlock)(await client.CallToolAsync("canvas_get_state")).Content[0]).Text);
        Assert.Equal("image", state.GetProperty("nodes")[0].GetProperty("kind").GetString());

        var first = host.Document.Find(nodeId!)!;
        var second = CanvasNode.Create(NodeKind.Image, 0, 0);
        var third = CanvasNode.Create(NodeKind.Image, 0, 0);
        var downstream = CanvasNode.Create(NodeKind.Image, 0, 0);
        first.Status = NodeStatus.Success;
        first.Content = "图片-001.png";
        second.Status = NodeStatus.Error;
        second.Error = "第二张失败";
        third.Status = NodeStatus.Loading;
        foreach (var node in new[] { second, third, downstream }) host.Document.Nodes.Add(node);
        Stacks.Form([first, second, third]);
        host.Document.Connections.Add(CanvasConnection.Create(first.Id, downstream.Id));
        var status = Parse(((TextContentBlock)(await client.CallToolAsync("generation_get_status", new Dictionary<string, object?> { ["nodeIds"] = new[] { first.Id } })).Content[0]).Text);
        var batch = status.GetProperty("nodes")[0].GetProperty("batch");
        Assert.Equal([first.Id, second.Id, third.Id], batch.EnumerateArray().Select(n => n.GetProperty("id").GetString()!).ToArray());
        Assert.Equal("第二张失败", batch[1].GetProperty("error").GetString());
        Assert.Equal(downstream.Id, status.GetProperty("nodes")[0].GetProperty("outputs")[0].GetProperty("id").GetString());

        var failed = await client.CallToolAsync("canvas_run_generation", new Dictionary<string, object?> { ["nodeId"] = "missing" });
        Assert.True(failed.IsError);

        // Each canvas has its own library in the test data folder (TestEnvironment points ProjectStore.Root at a temp directory).
        var library = InfiniteCanvas.Core.Storage.ProjectStore.LibraryDir(host.Document.Id);
        Directory.CreateDirectory(library);
        var file = Path.Combine(library, $"mcp-test-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        try
        {
            var list = Parse(((TextContentBlock)(await client.CallToolAsync("library_list", new Dictionary<string, object?> { ["keyword"] = "mcp-test" })).Content[0]).Text);
            Assert.Equal(Path.GetFileName(file), list.GetProperty("items")[0].GetProperty("name").GetString());
            var placed = await client.CallToolAsync("library_place", new Dictionary<string, object?> { ["names"] = new[] { Path.GetFileName(file) } });
            Assert.Equal(1, Parse(((TextContentBlock)placed.Content[0]).Text).GetProperty("nodeIds").GetArrayLength());
            Assert.Equal([file], host.Imported);
            Assert.True((await client.CallToolAsync("library_place", new Dictionary<string, object?> { ["names"] = new[] { "nope.png" } })).IsError);
        }
        finally { Directory.Delete(Path.GetDirectoryName(library)!, true); }
    }

    private sealed class Session(FakeHost host) : IAsyncDisposable
    {
        private readonly CanvasMcpServer _server = new(host);
        private NamedPipeClientStream? _pipe;
        public McpClient Client { get; private set; } = null!;

        public static async Task<Session> StartAsync(FakeHost host)
        {
            var session = new Session(host);
            session._pipe = new NamedPipeClientStream(".", session._server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await session._pipe.ConnectAsync(5000);
            session.Client = await McpClient.CreateAsync(new StreamClientTransport(session._pipe, session._pipe, null));
            return session;
        }

        public async Task<(bool IsError, string Text)> CallAsync(string tool, Dictionary<string, object?> args)
        {
            var result = await Client.CallToolAsync(tool, args);
            return (result.IsError == true, ((TextContentBlock)result.Content[0]).Text);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _pipe!.DisposeAsync();
            await _server.DisposeAsync();
        }
    }

    private static string TempMedia(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    [Fact]
    public async Task Import_file_places_valid_paths_and_reports_every_rejected_path_without_a_dialog()
    {
        var host = new FakeHost();
        var dir = Directory.CreateTempSubdirectory("ic-import").FullName;
        try
        {
            var good = TempMedia(dir, "参考.png");
            var locked = TempMedia(dir, "占用.jpg");
            host.Unreadable.Add("占用.jpg");
            var text = TempMedia(dir, "notes.txt");
            var missing = Path.Combine(dir, "missing.png");
            var unc = @"\\server\share\a.png";
            // Mixed separators and NT-style prefixes are UNC / device paths too; none may reach the host.
            var rejected = new[] { text, missing, "relative.png", unc, @"\\?\" + good, @"\\.\" + good, "//server/share/a.png", @"\/server/share/a.png", @"/\server\share\a.png", @"\??\UNC\server\share\a.png", @"\??\" + good, @"C:a.png", @"C:\", good + ":stream", Path.Combine(dir, "NUL.png"), "" };
            await using var session = await Session.StartAsync(host);

            var (isError, body) = await session.CallAsync("canvas_import_file", new() { ["paths"] = rejected.Append(good).Append(locked).ToArray(), ["x"] = 10d, ["y"] = 20d });

            Assert.False(isError);
            var json = Parse(body);
            Assert.Equal(1, json.GetProperty("nodeIds").GetArrayLength());
            Assert.Equal([good], host.Imported);
            var failed = json.GetProperty("failed").EnumerateArray().ToDictionary(f => f.GetProperty("path").GetString()!, f => f.GetProperty("reason").GetString()!);
            Assert.Equal(rejected.Length + 1, failed.Count);
            Assert.All(rejected, path => Assert.True(failed.ContainsKey(path), path));
            Assert.Equal("文件被占用", failed[locked]);
            Assert.Contains("不支持的文件类型", failed[text]);
            Assert.Contains("不存在", failed[missing]);
            Assert.Contains("绝对路径", failed["relative.png"]);
            Assert.Contains("UNC", failed[unc]);
            Assert.Contains("UNC", failed[@"\\?\" + good]);
            foreach (var network in new[] { "//server/share/a.png", @"\/server/share/a.png", @"/\server\share\a.png", @"\??\UNC\server\share\a.png" }) Assert.Contains("UNC", failed[network]);
            Assert.Contains("盘符", failed["C:a.png"]);

            // Nothing usable at all is an error naming each path, not an empty success.
            var (allBad, message) = await session.CallAsync("canvas_import_file", new() { ["paths"] = new[] { text, "relative.png" } });
            Assert.True(allBad);
            Assert.Contains("relative.png", message);
            Assert.True((await session.CallAsync("canvas_import_file", new() { ["paths"] = Array.Empty<string>() })).IsError);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static CanvasNode Generating(FakeHost host)
    {
        var node = CanvasNode.Create(NodeKind.Image, 0, 0);
        node.Status = NodeStatus.Loading;
        host.Document.Nodes.Add(node);
        return node;
    }

    private static void Fail(CanvasNode node, string error) { node.Status = NodeStatus.Error; node.Error = error; }

    /// <summary>Starts generation_wait, gives it a moment to subscribe, and runs the change; the wait must end on its own, long before its limit.</summary>
    private static async Task<(bool IsError, string Text)> WaitDuring(Session session, Dictionary<string, object?> args, Action change)
    {
        var wait = session.CallAsync("generation_wait", args);
        await Task.Delay(150);
        change();
        return await wait.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Wait_returns_at_once_on_a_failure_while_another_node_is_still_loading()
    {
        var host = new FakeHost();
        var first = Generating(host);
        var second = Generating(host);
        await using var session = await Session.StartAsync(host);

        var (isError, text) = await WaitDuring(session, new() { ["nodeIds"] = new[] { first.Id, second.Id } }, () => Fail(second, "429 Too Many Requests"));
        Assert.False(isError);
        var result = Parse(text);
        Assert.Equal("failed", result.GetProperty("reason").GetString());
        var failed = result.GetProperty("failed").EnumerateArray().Single();
        Assert.Equal((second.Id, "429 Too Many Requests"), (failed.GetProperty("nodeId").GetString(), failed.GetProperty("error").GetString()));
        var nodes = result.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("node").GetProperty("status").GetString()!).ToArray();
        Assert.Equal(["loading", "error"], nodes);

        // A node that has already failed ends the next wait at once, even though the other one still runs.
        var again = Parse((await session.CallAsync("generation_wait", new() { ["nodeIds"] = new[] { first.Id, second.Id } })).Text);
        Assert.Equal("failed", again.GetProperty("reason").GetString());

        // Once the first one finishes and only the failed one is asked about, the answer is still the failure; the first alone is done.
        first.Status = NodeStatus.Success;
        var done = Parse((await session.CallAsync("generation_wait", new() { ["nodeIds"] = new[] { first.Id } })).Text);
        Assert.Equal("done", done.GetProperty("reason").GetString());
        Assert.False(done.TryGetProperty("failed", out _));
    }

    [Fact]
    public async Task Wait_returns_done_when_every_node_has_finished_and_none_failed()
    {
        var host = new FakeHost();
        var first = Generating(host);
        var second = Generating(host);
        await using var session = await Session.StartAsync(host);

        var (_, text) = await WaitDuring(session, new() { ["nodeIds"] = new[] { first.Id, second.Id } }, () =>
        {
            first.Status = NodeStatus.Success;
            Task.Delay(100).Wait();
            second.Status = NodeStatus.Success;
        });
        var result = Parse(text);
        Assert.Equal("done", result.GetProperty("reason").GetString());
        Assert.Equal(2, result.GetProperty("nodes").GetArrayLength());
    }

    [Fact]
    public async Task Wait_with_no_node_ids_watches_what_is_generating_when_it_is_called()
    {
        var host = new FakeHost();
        var finished = CanvasNode.Create(NodeKind.Image, 0, 0);
        finished.Status = NodeStatus.Success;
        host.Document.Nodes.Add(finished);
        await using var session = await Session.StartAsync(host);
        // Nothing generating: nothing to wait for.
        var none = Parse((await session.CallAsync("generation_wait", new())).Text);
        Assert.Equal(("done", 0), (none.GetProperty("reason").GetString(), none.GetProperty("nodes").GetArrayLength()));

        var running = Generating(host);
        var (_, text) = await WaitDuring(session, new(), () => Fail(running, "出错了"));
        var result = Parse(text);
        Assert.Equal("failed", result.GetProperty("reason").GetString());
        Assert.Equal(running.Id, result.GetProperty("nodes").EnumerateArray().Single().GetProperty("node").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Wait_reports_timedOut_without_stopping_anything_and_checks_the_limit()
    {
        var host = new FakeHost();
        var running = Generating(host);
        await using var session = await Session.StartAsync(host);

        var started = DateTime.UtcNow;
        var timedOut = Parse((await session.CallAsync("generation_wait", new() { ["nodeIds"] = new[] { running.Id }, ["timeoutSeconds"] = 1 })).Text);
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(8));
        Assert.Equal("timedOut", timedOut.GetProperty("reason").GetString());
        Assert.Equal("loading", timedOut.GetProperty("nodes")[0].GetProperty("node").GetProperty("status").GetString());
        Assert.Equal(NodeStatus.Loading, running.Status);

        foreach (var bad in new[] { 0, -5, 601 }) Assert.True((await session.CallAsync("generation_wait", new() { ["nodeIds"] = new[] { running.Id }, ["timeoutSeconds"] = bad })).IsError, $"timeoutSeconds {bad}");
        Assert.True((await session.CallAsync("generation_wait", new() { ["nodeIds"] = new[] { "missing" } })).IsError);
    }

    [Fact]
    public async Task Wait_expands_a_batch_to_its_members()
    {
        var host = new FakeHost();
        var first = Generating(host);
        var second = Generating(host);
        var third = Generating(host);
        Stacks.Form([first, second, third]);
        await using var session = await Session.StartAsync(host);

        var (_, text) = await WaitDuring(session, new() { ["nodeIds"] = new[] { first.Id } }, () => Fail(third, "第三张失败"));
        var result = Parse(text);
        Assert.Equal("failed", result.GetProperty("reason").GetString());
        Assert.Equal(third.Id, result.GetProperty("failed").EnumerateArray().Single().GetProperty("nodeId").GetString());
        Assert.Equal(3, result.GetProperty("nodes")[0].GetProperty("batch").GetArrayLength());
    }

    [Fact]
    public async Task Wait_returns_at_once_when_an_idle_node_stops_running_without_a_status_change()
    {
        var host = new FakeHost();
        var preparing = CanvasNode.Create(NodeKind.Image, 0, 0);
        host.Document.Nodes.Add(preparing);
        host.Generated.Add(preparing.Id);
        await using var session = await Session.StartAsync(host);

        var started = DateTime.UtcNow;
        var (isError, text) = await WaitDuring(session, new() { ["nodeIds"] = new[] { preparing.Id }, ["timeoutSeconds"] = 60 }, () => host.StopRunning(preparing.Id));
        Assert.False(isError);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        var result = Parse(text);
        Assert.Equal("done", result.GetProperty("reason").GetString());
        Assert.Equal("idle", result.GetProperty("nodes")[0].GetProperty("node").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Wait_leaves_nothing_that_throws_when_the_canvas_closes_before_its_cleanup_can_run()
    {
        var host = new FakeHost();
        var first = Generating(host);
        var others = new[] { Generating(host), Generating(host) };
        await using var session = await Session.StartAsync(host);

        var (isError, _) = await WaitDuring(session, new() { ["nodeIds"] = new[] { first.Id, others[0].Id, others[1].Id } }, () =>
        {
            host.Closing = true;
            first.Status = NodeStatus.Success;
        });
        Assert.True(isError);

        // The handlers could not be removed; the shutdown still changes the other members one by one, and none of that may throw into the caller.
        foreach (var node in others)
        {
            Fail(node, "生成已中断");
            node.Status = NodeStatus.Loading;
            node.Status = NodeStatus.Success;
        }
        host.StopRunning(others[0].Id);
    }

    [Fact]
    public async Task Wait_fails_with_the_canvas_switched_error_when_another_canvas_is_opened_meanwhile()
    {
        var host = new FakeHost();
        var running = Generating(host);
        await using var session = await Session.StartAsync(host);

        var (isError, text) = await WaitDuring(session, new() { ["nodeIds"] = new[] { running.Id } }, () =>
        {
            host.Document = new CanvasDocument { Id = "canvas-2", Title = "另一张" };
            running.Status = NodeStatus.Success;
        });
        Assert.True(isError);
        Assert.Contains("画布已切换", text);
    }

    [Fact]
    public async Task Status_reports_a_request_waiting_for_its_channel_as_queued_with_its_place()
    {
        var host = new FakeHost();
        var waiting = CanvasNode.Create(NodeKind.Image, 0, 0);
        waiting.Status = NodeStatus.Loading;
        host.Document.Nodes.Add(waiting);
        await using var session = await Session.StartAsync(host);
        async Task<JsonElement> Node() => Parse((await session.CallAsync("generation_get_status", new() { ["nodeIds"] = new[] { waiting.Id } })).Text).GetProperty("nodes")[0].GetProperty("node");

        var output = CanvasNode.Create(NodeKind.Image, 0, 0);
        output.Status = NodeStatus.Loading;
        output.QueueAhead = 2;
        host.Document.Nodes.Add(output);
        host.Document.Connections.Add(CanvasConnection.Create(waiting.Id, output.Id));
        var outputs = Parse((await session.CallAsync("generation_get_status", new() { ["nodeIds"] = new[] { waiting.Id } })).Text).GetProperty("nodes")[0].GetProperty("outputs");
        Assert.Equal("queued", outputs[0].GetProperty("status").GetString());

        waiting.QueueAhead = 3;
        var queued = await Node();
        Assert.Equal(("queued", 3), (queued.GetProperty("status").GetString(), queued.GetProperty("queueAhead").GetInt32()));
        var summary = Parse((await session.CallAsync("canvas_get_state", new())).Text).GetProperty("nodes")[0];
        Assert.Equal(("queued", 3), (summary.GetProperty("status").GetString(), summary.GetProperty("queueAhead").GetInt32()));

        waiting.QueueAhead = null;
        var running = await Node();
        Assert.Equal("loading", running.GetProperty("status").GetString());
        Assert.False(running.TryGetProperty("queueAhead", out _));
    }

    [Fact]
    public async Task Replace_media_keeps_the_node_and_rejects_wrong_kinds_busy_nodes_and_bad_paths()
    {
        var host = new FakeHost();
        var dir = Directory.CreateTempSubdirectory("ic-replace").FullName;
        try
        {
            var image = CanvasNode.Create(NodeKind.Image, 0, 0);
            image.Content = "图片-001.png";
            image.Status = NodeStatus.Success;
            var text = CanvasNode.Create(NodeKind.Text, 0, 0);
            var busy = CanvasNode.Create(NodeKind.Image, 0, 0);
            busy.Status = NodeStatus.Loading;
            foreach (var node in new[] { image, text, busy }) host.Document.Nodes.Add(node);
            var picture = TempMedia(dir, "新图.webp");
            var clip = TempMedia(dir, "clip.mp4");
            await using var session = await Session.StartAsync(host);

            var ok = await session.CallAsync("canvas_replace_media", new() { ["nodeId"] = image.Id, ["path"] = picture });
            Assert.False(ok.IsError);
            Assert.Equal(("新图.webp", image.Id, 1), (image.Content, Parse(ok.Text).GetProperty("nodeId").GetString(), host.Records));
            Assert.Equal([(image.Id, picture)], host.Replaced);

            // The path is normalised once and used as checked: a trailing space no longer hides the file's kind.
            Assert.False((await session.CallAsync("canvas_replace_media", new() { ["nodeId"] = image.Id, ["path"] = picture + " " })).IsError);
            Assert.Equal([(image.Id, picture), (image.Id, picture)], host.Replaced);
            host.Replaced.RemoveAt(1);

            // A file the host cannot copy comes back with its reason, not as a bare failure.
            host.Unreadable.Add("占用.png");
            var locked = await session.CallAsync("canvas_replace_media", new() { ["nodeId"] = image.Id, ["path"] = TempMedia(dir, "占用.png") });
            Assert.True(locked.IsError);
            Assert.Contains("文件被占用", locked.Text);

            foreach (var (id, path) in new[] { (image.Id, clip), (text.Id, picture), (busy.Id, picture), ("missing", picture), (image.Id, Path.Combine(dir, "gone.png")), (image.Id, @"\\server\a.png"), (image.Id, "a.png") })
                Assert.True((await session.CallAsync("canvas_replace_media", new() { ["nodeId"] = id, ["path"] = path })).IsError, $"{id} {path}");
            Assert.Single(host.Replaced);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Library_place_returns_an_error_for_a_file_the_host_cannot_import_instead_of_a_dialog()
    {
        var host = new FakeHost();
        var library = InfiniteCanvas.Core.Storage.ProjectStore.LibraryDir(host.Document.Id);
        Directory.CreateDirectory(library);
        try
        {
            TempMedia(library, "坏文件.png");
            TempMedia(library, "好文件.png");
            host.Unreadable.Add("坏文件.png");
            await using var session = await Session.StartAsync(host);

            var bad = await session.CallAsync("library_place", new() { ["names"] = new[] { "坏文件.png" } });
            Assert.True(bad.IsError);
            Assert.Contains("坏文件.png", bad.Text);

            var mixed = await session.CallAsync("library_place", new() { ["names"] = new[] { "坏文件.png", "好文件.png" } });
            Assert.False(mixed.IsError);
            Assert.Equal(1, Parse(mixed.Text).GetProperty("nodeIds").GetArrayLength());
            Assert.Equal(1, Parse(mixed.Text).GetProperty("failed").GetArrayLength());
        }
        finally { Directory.Delete(Path.GetDirectoryName(library)!, true); }
    }

    [Fact]
    public async Task Library_tools_add_replace_rename_and_remove_by_name_and_refuse_bad_names()
    {
        var host = new FakeHost();
        var cat = CanvasNode.Create(NodeKind.Image, 0, 0);
        (cat.Title, cat.Content) = ("猫", "图片-001.png");
        var dog = CanvasNode.Create(NodeKind.Image, 0, 0);
        (dog.Title, dog.Content) = ("狗", "图片-002.png");
        host.Document.Nodes.Add(cat);
        host.Document.Nodes.Add(dog);
        var folder = Path.GetDirectoryName(InfiniteCanvas.Core.Storage.ProjectStore.LibraryDir(host.Document.Id))!;
        // The open library panel answers Changed by touching the UI, so rename and remove must raise it inside OnUiAsync (other tests' events arrive on other threads).
        var changedOnUi = 0;
        void CountOnUi() { if (FakeHost.OnUi) changedOnUi++; }
        LibraryStore.Changed += CountOnUi;
        try
        {
            await using var session = await Session.StartAsync(host);
            string[] Names(string json) => Parse(json).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).ToArray();
            async Task<string[]> ListAsync() => Names((await session.CallAsync("library_list", new())).Text);

            Assert.Equal("猫.png", Parse((await session.CallAsync("library_add", new() { ["nodeId"] = cat.Id })).Text).GetProperty("added").GetString());
            var clash = await session.CallAsync("library_add", new() { ["nodeId"] = cat.Id });
            Assert.True(clash.IsError);
            Assert.Contains("replace", clash.Text);
            Assert.Equal(["猫.png"], await ListAsync());
            Assert.False((await session.CallAsync("library_add", new() { ["nodeId"] = cat.Id, ["replace"] = true })).IsError);
            Assert.Equal(["猫.png"], await ListAsync());
            Assert.False((await session.CallAsync("library_add", new() { ["nodeId"] = dog.Id })).IsError);

            changedOnUi = 0;
            Assert.False((await session.CallAsync("library_rename", new() { ["name"] = "猫", ["newName"] = "小猫" })).IsError);
            Assert.Equal(["小猫.png", "狗.png"], await ListAsync());
            Assert.False((await session.CallAsync("library_rename", new() { ["name"] = "小猫.png", ["newName"] = "小猫二.PNG" })).IsError);
            Assert.Equal(["小猫二.png", "狗.png"], await ListAsync());
            foreach (var newName in new[] { "狗", "狗.png", "con", "NUL.png", "COM1", "lpt9", "..", "a/b", @"a\b", @"..\x", "../x", "C:x", "x:", "a*b", "", " ", "x ", "x.", "x.jpg", "x.mp4" })
                Assert.True((await session.CallAsync("library_rename", new() { ["name"] = "小猫二", ["newName"] = newName })).IsError, $"rename to '{newName}'");
            foreach (var name in new[] { "missing", @"..\..\evil.png", "../evil.png", Path.Combine(folder, "library", "小猫二.png") })
                Assert.True((await session.CallAsync("library_rename", new() { ["name"] = name, ["newName"] = "x" })).IsError, $"rename '{name}'");
            Assert.Equal(["小猫二.png", "狗.png"], await ListAsync());

            var removed = Parse((await session.CallAsync("library_remove", new() { ["names"] = new[] { "小猫二", "missing", @"..\evil.png" } })).Text);
            Assert.Equal(["小猫二.png"], removed.GetProperty("removed").EnumerateArray().Select(r => r.GetString()!).ToArray());
            Assert.Equal(["missing", @"..\evil.png"], removed.GetProperty("failed").EnumerateArray().Select(f => f.GetProperty("name").GetString()!).ToArray());
            Assert.Equal(["狗.png"], await ListAsync());
            Assert.Equal(0, host.Records);
            Assert.Equal(3, changedOnUi); // two renames and one remove

            var tools = await session.Client.ListToolsAsync();
            Assert.All(new[] { "library_add", "library_rename", "library_remove" }, name => Assert.True(tools.Single(t => t.Name == name).ProtocolTool.Annotations?.DestructiveHint));
        }
        finally
        {
            LibraryStore.Changed -= CountOnUi;
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    private static (CanvasNode Lead, CanvasNode Second, CanvasNode Third) OpenAgentStack(FakeHost host)
    {
        var members = Enumerable.Range(0, 3).Select(_ => CanvasNode.Create(NodeKind.Image, 400, 300)).ToList();
        foreach (var node in members) host.Document.Nodes.Add(node);
        Stacks.Form(members);
        return (members[0], members[1], members[2]);
    }

    [Fact]
    public async Task Generate_and_add_node_remember_the_layout_and_gap_and_reject_bad_values_before_creating_anything()
    {
        var host = new FakeHost();
        await using var session = await Session.StartAsync(host);

        var row = Parse((await session.CallAsync("canvas_generate", new() { ["mode"] = "image", ["prompt"] = "猫", ["count"] = 4, ["layout"] = "row", ["gap"] = 10d })).Text);
        var node = host.Document.Find(row.GetProperty("nodeId").GetString()!)!;
        Assert.Equal(("row", "10"), (node.Option(NodeKeys.Layout), node.Option(NodeKeys.Gap)));
        Assert.Equal(1, host.Records);

        await session.CallAsync("canvas_generate", new() { ["mode"] = "image", ["prompt"] = "狗", ["layout"] = "STACK" });
        Assert.Equal("", host.Document.Nodes.Last().Option(NodeKeys.Layout));

        var before = host.Document.Nodes.Count;
        foreach (var args in new Dictionary<string, object?>[] { new() { ["layout"] = "circle" }, new() { ["layout"] = "grid", ["gap"] = -1d }, new() { ["gap"] = -5d } })
        {
            args["mode"] = "image";
            args["prompt"] = "x";
            Assert.True((await session.CallAsync("canvas_generate", args)).IsError);
        }
        Assert.Equal((before, 2), (host.Document.Nodes.Count, host.Records));

        var result = Parse(CanvasTools.Apply(host,
        [
            new CanvasOp { Type = "add_node", Kind = "image", Count = 4, Layout = "grid", Gap = 40, Prompt = "x" },
            new CanvasOp { Type = "add_node", Kind = "image", Layout = "diagonal" },
            new CanvasOp { Type = "add_node", Kind = "image", Gap = double.PositiveInfinity },
        ]));
        var results = result.GetProperty("results").EnumerateArray().ToList();
        var added = host.Document.Find(results[0].GetProperty("id").GetString()!)!;
        Assert.Equal(("grid", "40"), (added.Option(NodeKeys.Layout), added.Option(NodeKeys.Gap)));
        Assert.True(results[1].TryGetProperty("error", out _) && results[2].TryGetProperty("error", out _));
        Assert.Equal(before + 1, host.Document.Nodes.Count);
    }

    [Fact]
    public void Stack_open_and_close_lay_out_an_existing_batch_in_one_undo_step()
    {
        var host = new FakeHost();
        var (lead, second, third) = OpenAgentStack(host);
        var start = (lead.X, lead.Y);

        var grid = Parse(CanvasTools.Apply(host, [new CanvasOp { Type = "stack_open", Id = second.Id }]));
        Assert.Equal(1, host.Records);
        Assert.True(grid.GetProperty("results")[0].GetProperty("open").GetBoolean());
        Assert.Equal(Stacks.Grid([lead, second, third]), new[] { lead, second, third }.Select(n => (n.X, n.Y)));
        Assert.All(new[] { lead, second, third }, n => Assert.True(Stacks.IsOpen(n)));

        CanvasTools.Apply(host, [new CanvasOp { Type = "stack_open", Id = lead.Id, Layout = "row", Gap = 5 }]);
        Assert.Equal(Stacks.Row([lead, second, third], 5), new[] { lead, second, third }.Select(n => (n.X, n.Y)));

        CanvasTools.Apply(host, [new CanvasOp { Type = "stack_close", Id = third.Id }]);
        Assert.All(new[] { lead, second, third }, n => Assert.Equal((start.X, start.Y, false), (n.X, n.Y, Stacks.IsOpen(n))));

        Assert.Equal(3, host.Records);
        Assert.True(host.History.Undo());
        Assert.Equal(Stacks.Row([host.Document.Find(lead.Id)!, host.Document.Find(second.Id)!, host.Document.Find(third.Id)!], 5), new[] { lead.Id, second.Id, third.Id }.Select(id => host.Document.Find(id)!).Select(n => (n.X, n.Y)));
        Assert.All(new[] { lead.Id, second.Id, third.Id }, id => Assert.True(Stacks.IsOpen(host.Document.Find(id)!)));
    }

    [Fact]
    public void Stack_ops_reject_loose_nodes_one_member_stacks_and_bad_arguments_without_changing_the_document()
    {
        var host = new FakeHost();
        var (lead, _, _) = OpenAgentStack(host);
        var loose = CanvasNode.Create(NodeKind.Image, 0, 0);
        var single = CanvasNode.Create(NodeKind.Image, 0, 0);
        single.SetOption(NodeKeys.Stack, single.Id);
        host.Document.Nodes.Add(loose);
        host.Document.Nodes.Add(single);
        var original = host.Document.Nodes.Select(n => InfiniteCanvas.Core.Json.Serialize(n)).ToList();

        var result = Parse(CanvasTools.Apply(host,
        [
            new CanvasOp { Type = "stack_open", Id = loose.Id },
            new CanvasOp { Type = "stack_close", Id = single.Id },
            new CanvasOp { Type = "stack_open", Id = "missing" },
            new CanvasOp { Type = "stack_open", Id = lead.Id, Layout = "stack" },
            new CanvasOp { Type = "stack_open", Id = lead.Id, Layout = "spiral" },
            new CanvasOp { Type = "stack_open", Id = lead.Id, Gap = -1 },
        ]));
        Assert.All(result.GetProperty("results").EnumerateArray(), r => Assert.True(r.TryGetProperty("error", out _)));
        Assert.Equal(original, host.Document.Nodes.Select(n => InfiniteCanvas.Core.Json.Serialize(n)).ToList());
    }

    /// <summary>A channel added to the in-memory settings (never saved) with one model, so estimates have a price to find; the model reference to put on a node.</summary>
    private static (ModelChannel Channel, string Ref) AddChannel(Vendor vendor, string model, Capability capability = Capability.Image)
    {
        var channel = new ModelChannel { Id = Ids.New("test"), Vendor = vendor };
        channel.Models.Add(new ChannelModel { Name = model, Capability = capability });
        lock (SettingsStore.Current.Channels) SettingsStore.Current.Channels.Add(channel);
        return (channel, ModelRef.Encode(channel.Id, model));
    }

    [Fact]
    public async Task Generate_and_run_report_the_estimated_cost_with_its_basis_or_null_with_a_reason()
    {
        var host = new FakeHost();
        var (_, xai) = AddChannel(Vendor.XAI, "grok-imagine-image");
        var (_, qwen) = AddChannel(Vendor.Qwen, "qwen-image-2.1-pro");
        await using var session = await Session.StartAsync(host);

        var usd = Parse((await session.CallAsync("canvas_generate", new() { ["mode"] = "image", ["prompt"] = "猫", ["model"] = xai, ["count"] = 3 })).Text).GetProperty("estimatedCost");
        Assert.Equal((0.06m, "USD"), (usd.GetProperty("amount").GetDecimal(), usd.GetProperty("currency").GetString()));
        Assert.Contains("3 张", usd.GetProperty("basis").GetString());
        var cny = Parse((await session.CallAsync("canvas_generate", new() { ["mode"] = "image", ["prompt"] = "猫", ["model"] = qwen, ["count"] = 3 })).Text).GetProperty("estimatedCost");
        Assert.Equal((0.75m, "CNY"), (cny.GetProperty("amount").GetDecimal(), cny.GetProperty("currency").GetString()));

        // No published price and no earlier run with these settings: an explicit null and the reason, not a missing key.
        var unknown = Parse((await session.CallAsync("canvas_generate", new() { ["mode"] = "image", ["prompt"] = "猫", ["ratio"] = "21:9", ["quality"] = "high" })).Text);
        Assert.Equal(JsonValueKind.Null, unknown.GetProperty("estimatedCost").ValueKind);
        Assert.Contains("单价", unknown.GetProperty("estimatedCostNote").GetString());
        var text = Parse((await session.CallAsync("canvas_generate", new() { ["mode"] = "text", ["prompt"] = "写一句话" })).Text);
        Assert.Equal(JsonValueKind.Null, text.GetProperty("estimatedCost").ValueKind);
        Assert.Contains("图片", text.GetProperty("estimatedCostNote").GetString());

        var node = host.Document.Nodes.First(n => n.Model == xai);
        host.Generated.Clear();
        var run = Parse((await session.CallAsync("canvas_run_generation", new() { ["nodeId"] = node.Id })).Text);
        Assert.True(run.GetProperty("started").GetBoolean());
        Assert.Equal(0.06m, run.GetProperty("estimatedCost").GetProperty("amount").GetDecimal());
        var again = Parse((await session.CallAsync("canvas_run_generation", new() { ["nodeId"] = node.Id })).Text);
        Assert.False(again.GetProperty("started").GetBoolean());
        Assert.False(again.TryGetProperty("estimatedCost", out _));
        Assert.True((await session.CallAsync("canvas_run_generation", new() { ["nodeId"] = "missing" })).IsError);

        // The same estimate inside a batch of ops, which stays one undo step.
        var recordsBefore = host.Records;
        var batch = Parse((await session.CallAsync("canvas_apply_ops", new()
        {
            ["ops"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "add_node", ["ref"] = "a", ["kind"] = "image", ["prompt"] = "猫", ["model"] = xai, ["count"] = 3 },
                new Dictionary<string, object?> { ["type"] = "run_generation", ["id"] = "a" },
            },
        })).Text).GetProperty("results");
        Assert.True(batch[1].GetProperty("started").GetBoolean());
        Assert.Equal(0.06m, batch[1].GetProperty("estimatedCost").GetProperty("amount").GetDecimal());
        Assert.Equal(1, host.Records - recordsBefore);
    }

    [Fact]
    public async Task Dry_run_estimates_with_the_connected_references_and_creates_no_node_history_or_request()
    {
        var host = new FakeHost();
        var (channel, qwen) = AddChannel(Vendor.Qwen, "qwen-image-3.0");
        var reference = CanvasNode.Create(NodeKind.Image, 0, 0);
        (reference.Content, reference.Status) = ("图片-001.png", NodeStatus.Success);
        host.Document.Nodes.Add(reference);
        var group = new CanvasNode { Kind = NodeKind.Group, Title = "组" };
        var member = CanvasNode.Create(NodeKind.Image, 0, 0);
        (member.Content, member.GroupId) = ("图片-002.png", group.Id);
        host.Document.Nodes.Add(group);
        host.Document.Nodes.Add(member);
        var snapshot = host.Document.Nodes.Select(n => InfiniteCanvas.Core.Json.Serialize(n)).ToList();
        await using var session = await Session.StartAsync(host);

        decimal Expected(int references) => Prices.EstimateImage(channel, "qwen-image-3.0", new CanvasNode { Kind = NodeKind.Image, Count = 2, Prompt = "猫" }, references)!.Amount;
        async Task<JsonElement> Dry(params string[] refs) => Parse((await session.CallAsync("canvas_generate", new() { ["mode"] = "image", ["prompt"] = "猫", ["model"] = qwen, ["count"] = 2, ["dryRun"] = true, ["referenceNodeIds"] = refs })).Text);

        var none = await Dry();
        Assert.True(none.GetProperty("dryRun").GetBoolean() && none.GetProperty("valid").GetBoolean());
        Assert.Equal(Expected(0), none.GetProperty("estimatedCost").GetProperty("amount").GetDecimal());
        Assert.Equal(Expected(1), (await Dry(reference.Id)).GetProperty("estimatedCost").GetProperty("amount").GetDecimal());
        // A group stands for its members, as it does in a real run.
        Assert.Equal(Expected(1), (await Dry(group.Id)).GetProperty("estimatedCost").GetProperty("amount").GetDecimal());
        Assert.Equal(Expected(2), (await Dry(reference.Id, group.Id)).GetProperty("estimatedCost").GetProperty("amount").GetDecimal());
        Assert.True(Expected(2) > Expected(0));

        Assert.Equal((0, 0), (host.Records, host.Generated.Count));
        Assert.Equal(snapshot, host.Document.Nodes.Select(n => InfiniteCanvas.Core.Json.Serialize(n)).ToList());
        Assert.Empty(host.Document.Connections);

        // The same validation as a real call, so a dry run that passes will start.
        foreach (var bad in new Dictionary<string, object?>[] { new() { ["model"] = "nowhere::model" }, new() { ["layout"] = "spiral" }, new() { ["referenceNodeIds"] = new[] { "missing" } }, new() { ["mode"] = "video" } })
        {
            var args = new Dictionary<string, object?> { ["mode"] = "image", ["prompt"] = "x", ["dryRun"] = true };
            foreach (var (key, value) in bad) args[key] = value;
            Assert.True((await session.CallAsync("canvas_generate", args)).IsError);
        }
        Assert.Equal(snapshot, host.Document.Nodes.Select(n => InfiniteCanvas.Core.Json.Serialize(n)).ToList());
        Assert.Equal(0, host.Records);

        var blank = Parse((await session.CallAsync("canvas_generate", new() { ["mode"] = "image", ["prompt"] = "", ["dryRun"] = true })).Text);
        Assert.Contains("提示词", blank.GetProperty("cannotStart").GetString());
    }

    [Fact]
    public async Task Usage_summary_lists_each_currency_apart_is_read_only_and_never_limits_generation()
    {
        var host = new FakeHost();
        var (google, _) = AddChannel(Vendor.Google, "gemini-2.5-flash-image");
        var (qwen, _) = AddChannel(Vendor.Qwen, "qwen-image-2.1-pro");
        UsageStore.Record(google.Id, "gemini-2.5-flash-image", Capability.Image, 100, new TokenUsage(0, 0, 129000));
        UsageStore.Record(qwen.Id, "qwen-image-2.1-pro", Capability.Image, 7);
        await using var session = await Session.StartAsync(host);

        var tools = await session.Client.ListToolsAsync();
        Assert.True(tools.Single(t => t.Name == "usage_summary").ProtocolTool.Annotations?.ReadOnlyHint);

        var summary = Parse((await session.CallAsync("usage_summary", new())).Text);
        var currencies = summary.GetProperty("currencies").EnumerateArray().ToDictionary(c => c.GetProperty("currency").GetString()!);
        Assert.Equal(["CNY", "USD"], currencies.Keys.Order());
        Assert.Equal(7, currencies["CNY"].GetProperty("results").GetProperty("image").GetInt32());
        Assert.False(currencies["CNY"].TryGetProperty("estimatedAmount", out _));
        Assert.True(currencies["USD"].GetProperty("estimatedAmount").GetDecimal() >= 3.87m);
        Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd"), summary.GetProperty("since").GetString());

        Assert.Empty(Parse((await session.CallAsync("usage_summary", new() { ["since"] = DateTime.Now.AddDays(1).ToString("yyyy-MM-dd") })).Text).GetProperty("currencies").EnumerateArray());
        Assert.False((await session.CallAsync("usage_summary", new() { ["since"] = "month" })).IsError);
        Assert.False((await session.CallAsync("usage_summary", new() { ["since"] = "2020-01-02" })).IsError);
        Assert.True((await session.CallAsync("usage_summary", new() { ["since"] = "yesterday" })).IsError);

        // Spending is reported, not capped: a generation still starts however much has been recorded.
        Assert.False((await session.CallAsync("canvas_generate", new() { ["mode"] = "image", ["prompt"] = "猫" })).IsError);
        Assert.Single(host.Generated);
    }

    [Fact]
    public void Stack_close_drops_the_hidden_cards_from_the_selection_like_the_stack_button()
    {
        var host = new FakeHost();
        var (lead, second, third) = OpenAgentStack(host);
        var other = CanvasNode.Create(NodeKind.Image, 0, 0);
        host.Document.Nodes.Add(other);
        CanvasTools.Apply(host, [new CanvasOp { Type = "stack_open", Id = lead.Id }]);
        host.Select([second.Id, third.Id, other.Id]);

        CanvasTools.Apply(host, [new CanvasOp { Type = "stack_close", Id = lead.Id }]);
        Assert.Equal([other.Id], host.Selected);

        // The lead itself stays selected, and a selection that does not touch the stack is left as it was.
        CanvasTools.Apply(host, [new CanvasOp { Type = "stack_open", Id = lead.Id }]);
        host.Select([lead.Id, second.Id, other.Id]);
        CanvasTools.Apply(host, [new CanvasOp { Type = "stack_close", Id = second.Id }]);
        Assert.Equal([lead.Id, other.Id], host.Selected);
        host.Select([other.Id]);
        CanvasTools.Apply(host, [new CanvasOp { Type = "stack_open", Id = lead.Id }, new CanvasOp { Type = "stack_close", Id = lead.Id }]);
        Assert.Equal([other.Id], host.Selected);
    }

    [Fact]
    public async Task A_row_or_grid_from_canvas_generate_starts_where_the_whole_spread_clears_the_nodes_beside_it()
    {
        var host = new FakeHost();
        // Free for one card at the view centre, but only 160 to its right: a row of four cannot start there.
        var neighbour = new CanvasNode { Kind = NodeKind.Image, X = 500 + 170 + 160, Y = 400 - 120, Width = 340, Height = 240 };
        host.Document.Nodes.Add(neighbour);
        await using var session = await Session.StartAsync(host);

        async Task<CanvasNode> Generate(string layout, double? x = null)
        {
            var args = new Dictionary<string, object?> { ["mode"] = "image", ["prompt"] = "猫", ["count"] = 4, ["layout"] = layout, ["gap"] = 24d };
            if (x is not null) args["x"] = x;
            var result = Parse((await session.CallAsync("canvas_generate", args)).Text);
            return host.Document.Find(result.GetProperty("nodeId").GetString()!)!;
        }
        bool Clears(CanvasNode lead, StackLayout layout, int count)
        {
            var batch = Enumerable.Range(0, count).Select(i => new CanvasNode { Kind = NodeKind.Image, X = lead.X, Y = lead.Y, Width = lead.Width, Height = CanvasGraph.OutputHeight(lead.Width, null) }).ToList();
            batch[0] = lead;
            var (w, h) = Stacks.Footprint(batch, layout, 24);
            return !Rect.Of(new CanvasNode { X = lead.X, Y = lead.Y, Width = w, Height = h }).Intersects(Rect.Of(neighbour));
        }

        var stack = await Generate("stack");
        Assert.False(Clears(stack, StackLayout.Row, 4)); // one card fits there, a row of four would not
        var row = await Generate("row");
        Assert.True(Clears(row, StackLayout.Row, 4));
        Assert.True(row.X >= neighbour.Right);
        var grid = await Generate("grid");
        Assert.True(Clears(grid, StackLayout.Grid, 4));

        // A position the agent chose is kept as given.
        Assert.Equal(520, (await Generate("row", 520)).X);
    }

    [Fact]
    public async Task Dry_run_refuses_a_blank_prompt_with_only_empty_inputs_as_the_real_call_would()
    {
        var host = new FakeHost();
        var empty = CanvasNode.Create(NodeKind.Image, 0, 0);
        var emptyText = CanvasNode.Create(NodeKind.Text, 0, 0);
        var picture = CanvasNode.Create(NodeKind.Image, 0, 0);
        (picture.Content, picture.Status) = ("图片-001.png", NodeStatus.Success);
        foreach (var node in new[] { empty, emptyText, picture }) host.Document.Nodes.Add(node);
        await using var session = await Session.StartAsync(host);

        async Task<JsonElement> Dry(params string[] refs) => Parse((await session.CallAsync("canvas_generate", new() { ["mode"] = "image", ["prompt"] = "", ["dryRun"] = true, ["referenceNodeIds"] = refs })).Text);

        Assert.NotEqual(JsonValueKind.Null, (await Dry()).GetProperty("cannotStart").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, (await Dry(empty.Id, emptyText.Id)).GetProperty("cannotStart").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await Dry(picture.Id)).GetProperty("cannotStart").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await Dry(empty.Id, picture.Id)).GetProperty("cannotStart").ValueKind);

        // The real call refuses the same inputs.
        var real = await session.CallAsync("canvas_generate", new() { ["mode"] = "image", ["prompt"] = "", ["referenceNodeIds"] = new[] { empty.Id } });
        Assert.True(real.IsError);
    }

    [Fact]
    public void Keep_one_keeps_the_chosen_card_moves_the_connections_and_group_over_and_is_one_undo_step()
    {
        var host = new FakeHost();
        var group = new CanvasNode { Kind = NodeKind.Group, Title = "分镜" };
        var (lead, second, third) = OpenAgentStack(host);
        var (input, config) = (new CanvasNode { Kind = NodeKind.Text, Title = "说明", Content = "文字" }, new CanvasNode { Kind = NodeKind.Config, Title = "配置" });
        host.Document.Nodes.Insert(0, group);
        host.Document.Nodes.Add(input);
        host.Document.Nodes.Add(config);
        foreach (var node in new[] { lead, second, third }) node.GroupId = group.Id;
        host.Document.Connections.Add(CanvasConnection.Create(input.Id, lead.Id));
        host.Document.Connections.Add(CanvasConnection.Create(lead.Id, config.Id));

        var result = Parse(CanvasTools.Apply(host, [new CanvasOp { Type = "keep_one", Id = third.Id }]));

        Assert.Equal(1, host.Records);
        Assert.Equal([lead.Id, second.Id], result.GetProperty("results")[0].GetProperty("removed").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal([third.Id], host.Document.Nodes.Where(n => n.Kind == NodeKind.Image).Select(n => n.Id).ToArray());
        Assert.Null(Stacks.Of(third));
        Assert.Equal(group.Id, third.GroupId);
        Assert.NotNull(host.Document.Find(group.Id)); // the group stays, with the kept card in it
        Assert.Equal(new[] { (input.Id, third.Id), (third.Id, config.Id) }.Order().ToList(), host.Document.Connections.Select(c => (c.From, c.To)).Order().ToList());

        Assert.True(host.History.Undo());
        Assert.Equal(3, host.Document.Nodes.Count(n => n.Kind == NodeKind.Image));
        Assert.All(host.Document.Nodes.Where(n => n.Kind == NodeKind.Image), n => Assert.Equal(lead.Id, Stacks.Of(n)));
        Assert.Equal(2, host.Document.Connections.Count);
        Assert.Contains(host.Document.Connections, c => c.From == input.Id && c.To == lead.Id);
        Assert.False(host.History.CanUndo);
    }

    [Fact]
    public void Keep_one_on_a_collapsed_stack_does_not_take_the_kept_card_along_and_bad_input_changes_nothing()
    {
        var host = new FakeHost();
        var (lead, second, _) = OpenAgentStack(host);
        var loose = CanvasNode.Create(NodeKind.Image, 0, 0);
        host.Document.Nodes.Add(loose);
        var original = host.Document.Nodes.Select(n => InfiniteCanvas.Core.Json.Serialize(n)).ToList();

        var bad = Parse(CanvasTools.Apply(host, [new CanvasOp { Type = "keep_one", Id = loose.Id }, new CanvasOp { Type = "keep_one", Id = "missing" }]));
        Assert.All(bad.GetProperty("results").EnumerateArray(), r => Assert.True(r.TryGetProperty("error", out _)));
        Assert.Equal(original, host.Document.Nodes.Select(n => InfiniteCanvas.Core.Json.Serialize(n)).ToList());

        // Keeping the lead of a collapsed stack: the others go, the lead survives although it was the one the stack hung on.
        CanvasTools.Apply(host, [new CanvasOp { Type = "keep_one", Id = lead.Id }]);
        Assert.Equal(new[] { lead.Id, loose.Id }, host.Document.Nodes.Select(n => n.Id).ToArray());
        Assert.DoesNotContain(second, host.Document.Nodes);
    }

    [Fact]
    public async Task Promote_takes_the_old_picture_s_place_and_connections_and_replaces_its_library_file_only_when_asked()
    {
        var host = new FakeHost();
        var folder = Path.GetDirectoryName(ProjectStore.LibraryDir(host.Document.Id))!;
        try
        {
            Directory.CreateDirectory(ProjectStore.AssetsDir(host.Document.Id));
            Directory.CreateDirectory(ProjectStore.LibraryDir(host.Document.Id));
            await File.WriteAllBytesAsync(ProjectStore.AssetPath(host.Document.Id, "图片-002.png"), [2]);
            await File.WriteAllBytesAsync(Path.Combine(ProjectStore.LibraryDir(host.Document.Id), "定稿正面.png"), [1]);
            var group = new CanvasNode { Kind = NodeKind.Group, Title = "定稿" };
            var old = new CanvasNode { Kind = NodeKind.Image, Title = "定稿正面", X = 800, Y = 100, Width = 300, Height = 400, Content = "图片-001.png", GroupId = group.Id };
            var fresh = new CanvasNode { Kind = NodeKind.Image, Title = "图片-002", X = 0, Y = 0, Width = 340, Height = 240, Content = "图片-002.png", NaturalWidth = 1000, NaturalHeight = 500 };
            var input = new CanvasNode { Kind = NodeKind.Text, Title = "输入", Content = "x" };
            foreach (var node in new[] { group, old, fresh, input }) host.Document.Nodes.Add(node);
            host.Document.Connections.Add(CanvasConnection.Create(input.Id, old.Id));

            // Without the library switch the library is left alone.
            var plain = Parse(CanvasTools.Apply(host, [new CanvasOp { Type = "promote", Id = fresh.Id, Replaces = old.Id }]));
            Assert.Equal(1, host.Records);
            Assert.Null(host.Document.Find(old.Id));
            Assert.Equal((800d, 100d, 300d, 150d, "定稿正面", group.Id), (fresh.X, fresh.Y, fresh.Width, fresh.Height, fresh.Title, fresh.GroupId));
            Assert.Equal([(input.Id, fresh.Id)], host.Document.Connections.Select(c => (c.From, c.To)).ToList());
            Assert.False(plain.GetProperty("results")[0].TryGetProperty("library", out _));
            Assert.Equal([1], await File.ReadAllBytesAsync(Path.Combine(ProjectStore.LibraryDir(host.Document.Id), "定稿正面.png")));
            Assert.True(File.Exists(ProjectStore.AssetPath(host.Document.Id, "图片-002.png")));
            Assert.True(host.History.Undo());
            Assert.NotNull(host.Document.Find(old.Id));
            Assert.Equal((0d, 0d), (host.Document.Find(fresh.Id)!.X, host.Document.Find(fresh.Id)!.Y));

            // With it, the library file named after the old picture takes the new content.
            var done = Parse(CanvasTools.Apply(host, [new CanvasOp { Type = "promote", Id = fresh.Id, Replaces = old.Id, Library = true }]));
            Assert.Equal("定稿正面.png", done.GetProperty("results")[0].GetProperty("library").GetProperty("file").GetString());
            Assert.Equal([2], await File.ReadAllBytesAsync(Path.Combine(ProjectStore.LibraryDir(host.Document.Id), "定稿正面.png")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task Promote_with_a_library_that_cannot_be_replaced_fails_whole_and_a_missing_library_file_is_only_noted()
    {
        var host = new FakeHost();
        var folder = Path.GetDirectoryName(ProjectStore.LibraryDir(host.Document.Id))!;
        try
        {
            Directory.CreateDirectory(ProjectStore.AssetsDir(host.Document.Id));
            Directory.CreateDirectory(ProjectStore.LibraryDir(host.Document.Id));
            await File.WriteAllBytesAsync(ProjectStore.AssetPath(host.Document.Id, "图片-002.png"), [2]);
            var locked = Path.Combine(ProjectStore.LibraryDir(host.Document.Id), "旧图.png");
            await File.WriteAllBytesAsync(locked, [1]);
            var old = new CanvasNode { Kind = NodeKind.Image, Title = "旧图", X = 800, Content = "图片-001.png", Width = 300, Height = 400 };
            var fresh = new CanvasNode { Kind = NodeKind.Image, Title = "图片-002", Content = "图片-002.png", Width = 340, Height = 240 };
            host.Document.Nodes.Add(old);
            host.Document.Nodes.Add(fresh);
            var original = host.Document.Nodes.Select(n => InfiniteCanvas.Core.Json.Serialize(n)).ToList();

            await using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var failed = Parse(CanvasTools.Apply(host, [new CanvasOp { Type = "promote", Id = fresh.Id, Replaces = old.Id, Library = true }]));
                Assert.Contains("占用", failed.GetProperty("results")[0].GetProperty("error").GetString());
            }
            Assert.Equal(original, host.Document.Nodes.Select(n => InfiniteCanvas.Core.Json.Serialize(n)).ToList());
            Assert.Equal([1], await File.ReadAllBytesAsync(locked));

            // Not in the library at all: the canvas part still happens and the result says so.
            File.Delete(locked);
            var noted = Parse(CanvasTools.Apply(host, [new CanvasOp { Type = "promote", Id = fresh.Id, Replaces = old.Id, Library = true }]));
            Assert.False(noted.GetProperty("results")[0].GetProperty("library").GetProperty("replaced").GetBoolean());
            Assert.Null(host.Document.Find(old.Id));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void Promote_rejects_bad_input_without_changing_the_document_and_resolves_refs_in_one_batch()
    {
        var host = new FakeHost();
        var (old, fresh) = (new CanvasNode { Kind = NodeKind.Image, Title = "旧", Content = "a.png", Width = 300, Height = 200 }, new CanvasNode { Kind = NodeKind.Image, Title = "新", Content = "b.png", Width = 340, Height = 240 });
        var text = CanvasNode.Create(NodeKind.Text, 0, 0);
        var (a, b) = (CanvasNode.Create(NodeKind.Image, 0, 0), CanvasNode.Create(NodeKind.Image, 0, 0));
        foreach (var node in new[] { old, fresh, text, a, b }) host.Document.Nodes.Add(node);
        Stacks.Form([a, b]);
        var original = host.Document.Nodes.Select(n => InfiniteCanvas.Core.Json.Serialize(n)).ToList();

        var result = Parse(CanvasTools.Apply(host,
        [
            new CanvasOp { Type = "promote", Id = fresh.Id, Replaces = fresh.Id },
            new CanvasOp { Type = "promote", Id = text.Id, Replaces = old.Id },
            new CanvasOp { Type = "promote", Id = fresh.Id, Replaces = text.Id },
            new CanvasOp { Type = "promote", Id = fresh.Id, Replaces = a.Id },
            new CanvasOp { Type = "promote", Id = "missing", Replaces = old.Id },
            new CanvasOp { Type = "promote", Id = fresh.Id },
        ]));
        Assert.All(result.GetProperty("results").EnumerateArray(), r => Assert.True(r.TryGetProperty("error", out _)));
        Assert.Equal(original, host.Document.Nodes.Select(n => InfiniteCanvas.Core.Json.Serialize(n)).ToList());

        var refs = Parse(CanvasTools.Apply(host,
        [
            new CanvasOp { Type = "add_node", Kind = "image", Ref = "final", Width = 200, Height = 100 },
            new CanvasOp { Type = "update_node", Id = "final", Title = "稿" },
        ]));
        Assert.Equal(2, refs.GetProperty("results").GetArrayLength());
    }

    private static CanvasNode Bitmap(FakeHost host, string title, string file = "图片-001.png")
    {
        var node = new CanvasNode { Kind = NodeKind.Image, Title = title, Content = file, Status = NodeStatus.Success, Width = 340, Height = 240 };
        host.Document.Nodes.Add(node);
        return node;
    }

    [Fact]
    public async Task Canvas_compose_passes_the_order_and_defaults_to_the_host_and_is_one_undo_step()
    {
        var host = new FakeHost();
        var (a, b, c) = (Bitmap(host, "正面"), Bitmap(host, "侧面", "图片-002.jpg"), Bitmap(host, "背面", "图片-003.webp"));
        await using var session = await Session.StartAsync(host);

        var plain = await session.CallAsync("canvas_compose", new() { ["ids"] = new[] { c.Id, a.Id, b.Id } });
        Assert.False(plain.IsError);
        var spec = Assert.Single(host.Composed);
        Assert.Equal([c.Id, a.Id, b.Id], spec.Cells.Select(x => x.NodeId));
        Assert.All(spec.Cells, x => Assert.Null(x.Top));
        Assert.Equal(("row", 0, 64, 64, "#FFFFFF", "height"), (spec.Layout, spec.Columns, spec.Gap, spec.Margin, spec.Background, spec.Fit));
        var json = Parse(plain.Text);
        Assert.Equal((1000, 500, 1d), (json.GetProperty("width").GetInt32(), json.GetProperty("height").GetInt32(), json.GetProperty("scale").GetDouble()));
        Assert.False(json.TryGetProperty("note", out _));
        Assert.Equal(1, host.Records);
        Assert.Equal(3, host.Document.Connections.Count(x => x.To == json.GetProperty("nodeId").GetString()));
        Assert.True(host.History.Undo());
        Assert.Equal(3, host.Document.Nodes.Count);
        Assert.Empty(host.Document.Connections);

        host.ComposeScale = 0.5;
        var custom = await session.CallAsync("canvas_compose", new() { ["ids"] = new[] { a.Id, b.Id }, ["layout"] = " Grid ", ["columns"] = 2, ["gap"] = 8, ["margin"] = 0, ["background"] = "transparent", ["fit"] = "WIDTH" });
        Assert.Equal(("grid", 2, 8, 0, "transparent", "width"), (host.Composed[1].Layout, host.Composed[1].Columns, host.Composed[1].Gap, host.Composed[1].Margin, host.Composed[1].Background, host.Composed[1].Fit));
        Assert.Contains("50%", Parse(custom.Text).GetProperty("note").GetString());
    }

    [Fact]
    public async Task Canvas_compose_marks_reach_the_spec_and_every_bad_request_is_refused_before_the_host_is_called()
    {
        var host = new FakeHost();
        var (a, b) = (Bitmap(host, "A"), Bitmap(host, "B", "图片-002.png"));
        var svg = Bitmap(host, "矢量", "图.svg");
        var text = new CanvasNode { Kind = NodeKind.Text, Title = "文", Content = "字" };
        var empty = new CanvasNode { Kind = NodeKind.Image, Title = "空" };
        host.Document.Nodes.Add(text);
        host.Document.Nodes.Add(empty);
        await using var session = await Session.StartAsync(host);

        var aligned = await session.CallAsync("canvas_compose", new() { ["ids"] = new[] { a.Id, b.Id }, ["marks"] = new[] { new[] { 0.1, 0.9 }, new[] { 0.2, 0.6 } } });
        Assert.False(aligned.IsError);
        Assert.Equal([(0.1, 0.9), (0.2, 0.6)], host.Composed[0].Cells.Select(x => (x.Top!.Value, x.Bottom!.Value)));

        var before = (host.Composed.Count, host.Records, host.Document.Nodes.Count);
        var bad = new Dictionary<string, object?>[]
        {
            new() { ["ids"] = new[] { a.Id } },                                             // one picture
            new() { ["ids"] = new[] { a.Id, "missing" } },
            new() { ["ids"] = new[] { a.Id, svg.Id } },                                     // SVG has no pixels
            new() { ["ids"] = new[] { a.Id, text.Id } },
            new() { ["ids"] = new[] { a.Id, empty.Id } },
            new() { ["ids"] = new[] { a.Id, b.Id }, ["layout"] = "diagonal" },
            new() { ["ids"] = new[] { a.Id, b.Id }, ["fit"] = "both" },
            new() { ["ids"] = new[] { a.Id, b.Id }, ["gap"] = -1 },
            new() { ["ids"] = new[] { a.Id, b.Id }, ["margin"] = -1 },
            new() { ["ids"] = new[] { a.Id, b.Id }, ["columns"] = -1 },
            new() { ["ids"] = new[] { a.Id, b.Id }, ["background"] = "white" },
            new() { ["ids"] = new[] { a.Id, b.Id }, ["marks"] = new[] { new[] { 0.1, 0.9 } } },                       // one mark for two pictures
            new() { ["ids"] = new[] { a.Id, b.Id }, ["marks"] = new[] { new[] { 0.1, 0.9 }, new[] { 0.5 } } },        // not a pair
            new() { ["ids"] = new[] { a.Id, b.Id }, ["marks"] = new[] { new[] { 0.9, 0.1 }, new[] { 0.2, 0.6 } } },   // top below bottom
            new() { ["ids"] = new[] { a.Id, b.Id }, ["marks"] = new[] { new[] { 0.1, 1.5 }, new[] { 0.2, 0.6 } } },
            new() { ["ids"] = new[] { a.Id, b.Id }, ["marks"] = new[] { new[] { 0.1, 0.9 }, new[] { 0.2, 0.6 } }, ["layout"] = "column" },
            new() { ["ids"] = new[] { a.Id, b.Id }, ["marks"] = new[] { new[] { 0.1, 0.9 }, new[] { 0.2, 0.6 } }, ["fit"] = "width" },
        };
        foreach (var args in bad) Assert.True((await session.CallAsync("canvas_compose", args)).IsError, string.Join(",", args.Select(p => p.Key)));
        Assert.Equal(before, (host.Composed.Count, host.Records, host.Document.Nodes.Count));
    }

    [Fact]
    public async Task Image_compare_returns_the_statistics_and_notes_a_size_mismatch_and_changes_nothing()
    {
        var host = new FakeHost();
        var (a, b) = (Bitmap(host, "前"), Bitmap(host, "后", "图片-002.png"));
        var snapshot = host.Document.Nodes.Select(n => InfiniteCanvas.Core.Json.Serialize(n)).ToList();
        await using var session = await Session.StartAsync(host);

        var result = await session.CallAsync("image_compare", new() { ["a"] = a.Id, ["b"] = b.Id });
        Assert.False(result.IsError);
        var json = Parse(result.Text);
        Assert.Equal((0.1, 0.9, 0.25, 0.05, 1000), (json.GetProperty("meanDifference").GetDouble(), json.GetProperty("maxDifference").GetDouble(), json.GetProperty("changedFraction").GetDouble(), json.GetProperty("threshold").GetDouble(), json.GetProperty("comparedPixels").GetInt32()));
        var box = json.GetProperty("changedBox");
        Assert.Equal((0.1, 0.2, 0.3, 0.4), (box.GetProperty("x").GetDouble(), box.GetProperty("y").GetDouble(), box.GetProperty("width").GetDouble(), box.GetProperty("height").GetDouble()));
        Assert.False(json.GetProperty("sizeMismatch").GetBoolean());
        Assert.False(json.TryGetProperty("note", out _));
        Assert.Equal([(a.Id, b.Id, 0.05)], host.Compared);

        host.SizeMismatch = true;
        host.Difference = host.Difference with { ChangedBox = null, ChangedFraction = 0 };
        var other = Parse((await session.CallAsync("image_compare", new() { ["a"] = b.Id, ["b"] = a.Id, ["threshold"] = 0.2 })).Text);
        Assert.True(other.GetProperty("sizeMismatch").GetBoolean());
        Assert.Contains("缩放", other.GetProperty("note").GetString());
        Assert.False(other.TryGetProperty("changedBox", out _));
        Assert.Equal(0.2, other.GetProperty("threshold").GetDouble());
        Assert.Equal((b.Id, a.Id), (host.Compared[1].A, host.Compared[1].B));

        // Read-only: no history, no change to any node.
        Assert.Equal(0, host.Records);
        Assert.Equal(snapshot, host.Document.Nodes.Select(n => InfiniteCanvas.Core.Json.Serialize(n)).ToList());
        var tools = await session.Client.ListToolsAsync();
        Assert.True(tools.Single(t => t.Name == "image_compare").ProtocolTool.Annotations?.ReadOnlyHint);
    }

    [Fact]
    public async Task Image_compare_refuses_what_it_cannot_measure_before_the_host_is_called()
    {
        var host = new FakeHost();
        var (a, b) = (Bitmap(host, "A"), Bitmap(host, "B", "图片-002.png"));
        var svg = Bitmap(host, "矢量", "图.svg");
        var text = new CanvasNode { Kind = NodeKind.Text, Title = "文", Content = "字" };
        var empty = new CanvasNode { Kind = NodeKind.Image, Title = "空" };
        host.Document.Nodes.Add(text);
        host.Document.Nodes.Add(empty);
        await using var session = await Session.StartAsync(host);

        var bad = new Dictionary<string, object?>[]
        {
            new() { ["a"] = a.Id, ["b"] = a.Id },
            new() { ["a"] = a.Id, ["b"] = "missing" },
            new() { ["a"] = "missing", ["b"] = b.Id },
            new() { ["a"] = a.Id, ["b"] = svg.Id },
            new() { ["a"] = text.Id, ["b"] = b.Id },
            new() { ["a"] = a.Id, ["b"] = empty.Id },
            new() { ["a"] = a.Id, ["b"] = b.Id, ["threshold"] = -0.1 },
            new() { ["a"] = a.Id, ["b"] = b.Id, ["threshold"] = 1 },
            new() { ["a"] = a.Id, ["b"] = b.Id, ["threshold"] = 7 },
        };
        foreach (var args in bad) Assert.True((await session.CallAsync("image_compare", args)).IsError, string.Join(",", args.Select(p => $"{p.Key}={p.Value}")));
        Assert.Empty(host.Compared);
        Assert.False((await session.CallAsync("image_compare", new() { ["a"] = a.Id, ["b"] = b.Id, ["threshold"] = 0 })).IsError);
    }

    [Fact]
    public async Task Async_tools_do_not_mix_canvases_between_ui_dispatches()
    {
        var host = new FakeHost();
        var original = new CanvasDocument();
        var next = new CanvasDocument();
        var image = CanvasNode.Create(NodeKind.Image, 0, 0);
        image.Content = "图片-001.png";
        original.Nodes.Add(image);
        host.Document = original;
        host.AfterUi = () => host.Document = next;
        await using var server = new CanvasMcpServer(host);
        await using var pipe = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe, null));

        Assert.False((await client.CallToolAsync("canvas_get_image", new Dictionary<string, object?> { ["nodeId"] = image.Id })).IsError == true);
        Assert.Equal([original.Id], host.ViewedProjects);
        host.Document = original;
        Assert.False((await client.CallToolAsync("library_add", new Dictionary<string, object?> { ["nodeId"] = image.Id })).IsError == true);
        Assert.Equal([original.Id], host.LibraryProjects);

        var library = InfiniteCanvas.Core.Storage.ProjectStore.LibraryDir(original.Id);
        Directory.CreateDirectory(library);
        var file = Path.Combine(library, "reference.png");
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        try
        {
            host.Document = original;
            var placed = await client.CallToolAsync("library_place", new Dictionary<string, object?> { ["names"] = new[] { "reference.png" } });
            Assert.True(placed.IsError);
            Assert.Empty(host.Imported);
        }
        finally { Directory.Delete(Path.GetDirectoryName(library)!, true); }
    }
}
