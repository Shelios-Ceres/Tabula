using System.IO.Pipes;
using System.Text.Json;
using InfiniteCanvas.Agents;
using InfiniteCanvas.Core.Canvas;
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
        public Task<T> OnUiAsync<T>(Func<T> action)
        {
            var result = action();
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
        public Task ReplaceMediaAsync(CanvasNode node, string path)
        {
            RecordHistory();
            Replaced.Add((node.Id, path));
            node.Content = Path.GetFileName(path);
            return Task.CompletedTask;
        }
        public Task AddToLibraryAsync(CanvasNode node) { LibraryProjects.Add(Document.Id); return Task.CompletedTask; }
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
        Assert.Equal(12, (await client.ListToolsAsync()).Count);
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
        Assert.Equal(["canvas_apply_ops", "canvas_generate", "canvas_get_image", "canvas_get_node", "canvas_get_state", "canvas_import_file", "canvas_replace_media", "canvas_run_generation", "generation_get_status", "library_add", "library_list", "library_place"], tools.Select(t => t.Name).Order());

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
            var rejected = new[] { text, missing, "relative.png", unc, @"\\?\" + good, @"\\.\" + good, "//server/share/a.png", good + ":stream", Path.Combine(dir, "NUL.png"), "" };
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
            Assert.Contains("UNC", failed["//server/share/a.png"]);

            // Nothing usable at all is an error naming each path, not an empty success.
            var (allBad, message) = await session.CallAsync("canvas_import_file", new() { ["paths"] = new[] { text, "relative.png" } });
            Assert.True(allBad);
            Assert.Contains("relative.png", message);
            Assert.True((await session.CallAsync("canvas_import_file", new() { ["paths"] = Array.Empty<string>() })).IsError);
        }
        finally { Directory.Delete(dir, true); }
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
