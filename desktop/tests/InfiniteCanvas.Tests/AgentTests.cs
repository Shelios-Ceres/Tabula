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
        public CanvasDocument Document { get; } = new() { Id = "canvas-1", Title = "测试" };
        public List<string> Selected { get; } = [];
        public List<string> Generated { get; } = [];
        public int Records { get; private set; }
        public IReadOnlyCollection<string> Selection => Selected;
        public Viewport Viewport { get; set; } = new(0, 0, 1);
        public (double X, double Y) ViewCenter => (500, 400);
        public Task<T> OnUiAsync<T>(Func<T> action) => Task.FromResult(action());
        public void RecordHistory() => Records++;
        public void Select(IEnumerable<string> ids) { Selected.Clear(); Selected.AddRange(ids); }
        public void Delete(IEnumerable<string> ids)
        {
            var set = ids.ToHashSet();
            foreach (var node in Document.Nodes.Where(n => set.Contains(n.Id)).ToList()) Document.Nodes.Remove(node);
            foreach (var c in Document.Connections.Where(c => set.Contains(c.From) || set.Contains(c.To)).ToList()) Document.Connections.Remove(c);
        }
        public void Reveal(IReadOnlyList<CanvasNode> nodes) { }
        public bool IsRunning(string nodeId) => Generated.Contains(nodeId);
        public string? CannotGenerate(CanvasNode node) => string.IsNullOrWhiteSpace(node.Prompt) ? "请先输入提示词" : null;
        public void Generate(CanvasNode node) => Generated.Add(node.Id);
        public Task<(byte[] Bytes, string MimeType)?> LoadImageAsync(CanvasNode node, int maxEdge) =>
            Task.FromResult<(byte[], string)?>(node.HasContent ? ([1, 2, 3], "image/png") : null);
        public string? Group(IReadOnlyCollection<string> ids, string? title)
        {
            if (ids.Count < 2) return null;
            var group = new CanvasNode { Kind = NodeKind.Group, Title = title ?? "分组" };
            Document.Nodes.Insert(0, group);
            foreach (var node in Document.Nodes.Where(n => ids.Contains(n.Id))) node.GroupId = group.Id;
            return group.Id;
        }
        public void Ungroup(IReadOnlyCollection<string> ids)
        {
            foreach (var node in Document.Nodes.Where(n => ids.Contains(n.Id) || n.GroupId is not null && ids.Contains(n.GroupId))) node.GroupId = null;
        }
        public List<string> Imported { get; } = [];
        public Task<IReadOnlyList<string>> ImportAsync(IReadOnlyList<string> paths, double x, double y)
        {
            Imported.AddRange(paths);
            return Task.FromResult<IReadOnlyList<string>>(paths.Select(_ => Ids.New("node")).ToList());
        }
        public Task AddToLibraryAsync(CanvasNode node) => Task.CompletedTask;
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
        Assert.Equal(9, (await client.ListToolsAsync()).Count);
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
        Assert.Equal(["canvas_apply_ops", "canvas_generate", "canvas_get_image", "canvas_get_state", "canvas_run_generation", "generation_get_status", "library_add", "library_list", "library_place"], tools.Select(t => t.Name).Order());

        var generated = await client.CallToolAsync("canvas_generate", new Dictionary<string, object?> { ["mode"] = "image", ["prompt"] = "一只猫", ["ratio"] = "1:1" });
        var nodeId = Parse(((TextContentBlock)generated.Content[0]).Text).GetProperty("nodeId").GetString();
        Assert.Equal([nodeId!], host.Generated);
        Assert.Equal(("一只猫", "1:1"), (host.Document.Nodes.Single().Prompt, host.Document.Nodes.Single().Ratio));

        var state = Parse(((TextContentBlock)(await client.CallToolAsync("canvas_get_state")).Content[0]).Text);
        Assert.Equal("image", state.GetProperty("nodes")[0].GetProperty("kind").GetString());

        var failed = await client.CallToolAsync("canvas_run_generation", new Dictionary<string, object?> { ["nodeId"] = "missing" });
        Assert.True(failed.IsError);

        // The library lives in the test data folder (TestEnvironment points ProjectStore.Root at a temp directory).
        Directory.CreateDirectory(InfiniteCanvas.Core.Storage.LibraryStore.Dir);
        var file = Path.Combine(InfiniteCanvas.Core.Storage.LibraryStore.Dir, $"mcp-test-{Guid.NewGuid():N}.png");
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
        finally { File.Delete(file); }
    }
}
