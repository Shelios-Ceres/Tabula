using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamJsonRpc;

namespace InfiniteCanvas.Agents;

public sealed record AcpPermissionOption(string OptionId, string Name, string Kind);

/// <summary>An advertised login method; Type is "agent" (call authenticate) or "terminal" (run the agent interactively with Args / Env).</summary>
public sealed record AcpAuthMethod(string Id, string Name, string? Description, string Type, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env);

public sealed class AcpException(string message, int code = 0) : Exception(message)
{
    public int Code { get; } = code;
    /// <summary>ACP's auth_required error: the agent needs a login before sessions can be created.</summary>
    public bool AuthRequired => Code == -32000;
}

/// <summary>What the client does with traffic the agent starts: streamed session updates and permission prompts.</summary>
public interface IAcpClientHandler
{
    void OnSessionUpdate(string sessionId, JsonElement update);
    /// <summary>The chosen option ID, or null when the turn was cancelled.</summary>
    Task<string?> RequestPermissionAsync(string sessionId, JsonElement toolCall, IReadOnlyList<AcpPermissionOption> options);
}

/// <summary>
/// Client side of the Agent Client Protocol v1 over stdio: newline-delimited JSON-RPC 2.0 with the agent as a subprocess.
/// The client advertises no fs / terminal methods, so agents use their own tools; canvas access goes through MCP.
/// </summary>
public sealed class AcpClient : IAsyncDisposable
{
    public const int ProtocolVersion = 1;

    private readonly JsonRpc _rpc;
    private readonly Process? _process;
    private readonly Queue<string> _stderr = new();

    public JsonElement AgentCapabilities { get; private set; }
    public IReadOnlyList<AcpAuthMethod> AuthMethods { get; private set; } = [];
    public string AgentTitle { get; private set; } = "";
    public bool SupportsImages => AgentCapabilities.ValueKind == JsonValueKind.Object && AgentCapabilities.TryGetProperty("promptCapabilities", out var p) && p.TryGetProperty("image", out var image) && image.GetBoolean();
    public event Action? Disconnected;

    internal AcpClient(Stream toAgent, Stream fromAgent, IAcpClientHandler handler, Process? process = null)
    {
        _process = process;
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        formatter.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        _rpc = new JsonRpc(new NewLineDelimitedMessageHandler(toAgent, fromAgent, formatter))
        {
            // ACP cancels a turn with session/cancel; StreamJsonRpc's $/cancelRequest is not part of the protocol.
            CancellationStrategy = null,
        };
        _rpc.AddLocalRpcTarget(new Callbacks(handler));
        _rpc.Disconnected += (_, _) => Disconnected?.Invoke();
        _rpc.StartListening();
    }

    /// <summary>Launch the agent from its command line and run the initialize handshake.</summary>
    public static async Task<AcpClient> StartAsync(string commandLine, string cwd, IAcpClientHandler handler, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var (file, args) = CommandLine.Parse(commandLine);
        var path = CommandLine.Resolve(file) ?? throw new AcpException($"找不到命令「{file}」，请确认已安装并在 PATH 中，或在设置里修改启动命令。");
        var info = new ProcessStartInfo(path)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        var process = Process.Start(info) ?? throw new AcpException($"无法启动「{commandLine}」");
        ChildProcessJob.Add(process);
        var client = new AcpClient(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, handler, process);
        _ = client.ReadStderrAsync(process.StandardError);
        try
        {
            await client.InitializeAsync(ct);
            ct.ThrowIfCancellationRequested();
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var result = await CallAsync("initialize", new
        {
            protocolVersion = ProtocolVersion,
            clientCapabilities = new
            {
                fs = new { readTextFile = false, writeTextFile = false },
                terminal = false,
                // The client can rerun the agent command in a console window for terminal logins.
                auth = new { terminal = true },
            },
            clientInfo = new { name = "infinite-canvas", title = "无限画布", version = "0.1.0" },
        }, ct);
        if (result.TryGetProperty("protocolVersion", out var version) && version.GetInt32() != ProtocolVersion)
            throw new AcpException($"Agent 使用的 ACP 协议版本 {version.GetInt32()} 不受支持（客户端支持版本 {ProtocolVersion}）");
        AgentCapabilities = result.TryGetProperty("agentCapabilities", out var capabilities) ? capabilities.Clone() : default;
        AgentTitle = result.TryGetProperty("agentInfo", out var agent) ? Str(agent, "title") ?? Str(agent, "name") ?? "" : "";
        AuthMethods = result.TryGetProperty("authMethods", out var methods) && methods.ValueKind == JsonValueKind.Array
            ? methods.EnumerateArray().Select(m => new AcpAuthMethod(
                Str(m, "id") ?? "", Str(m, "name") ?? Str(m, "id") ?? "", Str(m, "description"), Str(m, "type") ?? "agent",
                m.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [],
                m.TryGetProperty("env", out var e) && e.ValueKind == JsonValueKind.Object ? e.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "") : [])).ToList()
            : [];
    }

    public Task AuthenticateAsync(string methodId, CancellationToken ct = default) => CallAsync("authenticate", new { methodId }, ct);

    /// <summary>Create a session rooted at an absolute working directory, with MCP servers the agent should connect to.</summary>
    public async Task<string> NewSessionAsync(string cwd, IEnumerable<object> mcpServers, CancellationToken ct = default)
    {
        var result = await CallAsync("session/new", new { cwd, mcpServers = mcpServers.ToArray() }, ct);
        return Str(result, "sessionId") ?? throw new AcpException("Agent 没有返回会话 ID");
    }

    /// <summary>Run one prompt turn; updates stream through the handler meanwhile. Returns the stop reason.</summary>
    public async Task<string> PromptAsync(string sessionId, IReadOnlyList<object> prompt, CancellationToken ct = default)
    {
        var result = await CallAsync("session/prompt", new { sessionId, prompt }, ct);
        return Str(result, "stopReason") ?? "end_turn";
    }

    public Task CancelAsync(string sessionId) => _rpc.NotifyWithParameterObjectAsync("session/cancel", new { sessionId });

    /// <summary>Recent stderr lines, shown when the agent fails or exits.</summary>
    public string StderrTail
    {
        get { lock (_stderr) return string.Join("\n", _stderr); }
    }

    private async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Cancels this wait only; the panel disposes interrupted setup, and live turns use session/cancel.
        try { return await _rpc.InvokeWithParameterObjectAsync<JsonElement>(method, parameters).WaitAsync(ct); }
        catch (RemoteInvocationException ex) { throw new AcpException(ex.Message, ex.ErrorCode); }
        catch (ConnectionLostException)
        {
            var tail = StderrTail;
            throw new AcpException(tail.Length > 0 ? $"Agent 进程已退出：{tail}" : "Agent 进程已退出");
        }
    }

    private async Task ReadStderrAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Trim().Length == 0) continue;
                lock (_stderr)
                {
                    _stderr.Enqueue(line.Trim());
                    while (_stderr.Count > 8) _stderr.Dequeue();
                }
            }
        }
        catch (IOException) { /* The process went away. */ }
    }

    public async ValueTask DisposeAsync()
    {
        _rpc.Dispose();
        if (_process is null) return;
        try
        {
            // npx and cmd wrappers start child processes: end the whole tree.
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { }
        _process.Dispose();
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Methods the agent calls on the client. Params arrive as one object so unknown fields such as _meta never break matching.</summary>
    private sealed class Callbacks(IAcpClientHandler handler)
    {
        [JsonRpcMethod("session/update", UseSingleObjectParameterDeserialization = true)]
        public void Update(JsonElement p) => handler.OnSessionUpdate(Str(p, "sessionId") ?? "", p.GetProperty("update").Clone());

        [JsonRpcMethod("session/request_permission", UseSingleObjectParameterDeserialization = true)]
        public async Task<object> RequestPermission(JsonElement p)
        {
            var options = p.TryGetProperty("options", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(o => new AcpPermissionOption(Str(o, "optionId") ?? "", Str(o, "name") ?? "", Str(o, "kind") ?? "")).ToList()
                : [];
            var toolCall = p.TryGetProperty("toolCall", out var call) ? call.Clone() : default;
            var chosen = await handler.RequestPermissionAsync(Str(p, "sessionId") ?? "", toolCall, options);
            return chosen is null ? new { outcome = new { outcome = "cancelled" } } : new { outcome = new { outcome = "selected", optionId = chosen } };
        }
    }
}

/// <summary>Splits a configured command line and finds the executable the way a shell would (PATH plus PATHEXT, so "npx" finds npx.cmd).</summary>
public static class CommandLine
{
    public static (string File, List<string> Args) Parse(string commandLine)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in commandLine.Trim())
        {
            if (c == '"') quoted = !quoted;
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0) parts.Add(current.ToString());
                current.Clear();
            }
            else current.Append(c);
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return parts.Count == 0 ? ("", []) : (parts[0], parts.Skip(1).ToList());
    }

    public static string? Resolve(string file)
    {
        if (file.Length == 0) return null;
        IEnumerable<string> extensions = Path.HasExtension(file) ? [""] : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries).Prepend("");
        IEnumerable<string> folders = Path.IsPathRooted(file) ? [""] : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var folder in folders)
        foreach (var extension in extensions)
        {
            var candidate = folder.Length == 0 ? file + extension : Path.Combine(folder.Trim('"'), file + extension);
            // A bare name without extension is never directly runnable on Windows (e.g. the npx shell script next to npx.cmd).
            if (File.Exists(candidate) && (extension.Length > 0 || Path.HasExtension(file))) return candidate;
        }
        return null;
    }
}
