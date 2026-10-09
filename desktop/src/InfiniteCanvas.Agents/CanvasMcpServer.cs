using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace InfiniteCanvas.Agents;

/// <summary>
/// Serves the canvas tools over MCP on a named pipe that only the current Windows user can open. Agents reach it through
/// the stdio declaration from <see cref="Declaration"/>: the client exe in bridge mode, which every ACP agent must support.
/// The panel's agents get a random per-canvas pipe; external agents (Claude Code, a terminal CLI…) use <see cref="SharedPipeName"/>.
/// </summary>
public sealed class CanvasMcpServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly ICanvasHost _host;

    public string PipeName { get; }

    public CanvasMcpServer(ICanvasHost host, string? pipeName = null)
    {
        _host = host;
        PipeName = pipeName ?? $"InfiniteCanvas-mcp-{Guid.NewGuid():N}";
        _ = AcceptAsync();
    }

    /// <summary>The fixed pipe for external agents, one per data folder so a test instance never answers for the real one.</summary>
    public static string SharedPipeName(string dataRoot) =>
        "InfiniteCanvas-mcp-shared-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataRoot).TrimEnd('\\', '/').ToLowerInvariant())))[..16];

    /// <summary>The stdio MCP server entry for ACP session/new.</summary>
    public object Declaration(string clientExe) => new { name = "infinite-canvas", command = clientExe, args = new[] { McpBridge.Argument, PipeName }, env = Array.Empty<object>() };

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(_stop.Token); }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                return;
            }
            _ = ServeAsync(pipe);
        }
    }

    /// <summary>One MCP session per bridge connection, i.e. per agent session.</summary>
    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            try
            {
                var tools = new McpServerPrimitiveCollection<McpServerTool>();
                foreach (var tool in CanvasTools.Create(_host)) tools.Add(tool);
                var options = new McpServerOptions
                {
                    ServerInfo = new Implementation { Name = "infinite-canvas", Title = "无限画布", Version = "0.1.0" },
                    ServerInstructions = CanvasTools.Instructions,
                    ToolCollection = tools,
                };
                await using var transport = new StreamServerTransport(pipe, pipe, "infinite-canvas");
                await using var server = McpServer.Create(transport, options);
                await server.RunAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException) { /* Agent session ended or the canvas closed. */ }
        }
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Bridge mode of the client exe: relays an agent's stdio MCP traffic to the canvas pipe without opening a window.</summary>
public static class McpBridge
{
    public const string Argument = "--mcp-bridge";

    public static async Task<int> RunAsync(string pipeName)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(10_000); }
        catch (TimeoutException)
        {
            // Agents read stderr as UTF-8, not the console code page.
            await using var error = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false));
            await error.WriteLineAsync("无限画布：连接不到画布。请确认客户端正在运行；外部 Agent 还需要在设置里打开「允许外部 Agent 连接」。");
            return 1;
        }
        await using var input = Console.OpenStandardInput();
        await using var output = Console.OpenStandardOutput();
        // Whichever side closes first ends the relay: the agent closing stdin, or the canvas closing the pipe.
        await Task.WhenAny(input.CopyToAsync(pipe), pipe.CopyToAsync(output));
        return 0;
    }
}
