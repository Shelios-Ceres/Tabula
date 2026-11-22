using Tabula.Agents;

namespace Tabula.App;

/// <summary>
/// Replaces the generated entry point (DISABLE_XAML_GENERATED_MAIN) so the same exe can also run as the agents' MCP bridge:
/// with "--mcp-bridge &lt;pipe&gt;" it relays stdio to the open canvas and exits without starting WinUI.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is [McpBridge.Argument, var pipe]) return McpBridge.RunAsync(pipe).GetAwaiter().GetResult();
        // Without a pipe name: an external agent (Claude Code, a terminal CLI) reaching the open canvas through the shared pipe.
        if (args is [McpBridge.Argument]) return McpBridge.RunAsync(CanvasMcpServer.SharedPipeName(Core.Storage.ProjectStore.Root)).GetAwaiter().GetResult();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            new App();
        });
        return 0;
    }
}
