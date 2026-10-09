using System.Diagnostics;
using System.Text.Json;
using InfiniteCanvas.Agents;
using InfiniteCanvas.Core.Settings;
using InfiniteCanvas.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace InfiniteCanvas.App.Board;

/// <summary>
/// Chat with a local coding agent (Claude Code, Kimi Code, Grok Build, DeepSeek Harness…) over ACP.
/// The agent process starts on the first message and reaches this canvas through the MCP tools in <see cref="CanvasMcpServer"/>.
/// </summary>
public sealed partial class AgentPanel : UserControl, IAcpClientHandler
{
    private CanvasView? _board;
    private CanvasMcpServer? _mcp;
    private AcpClient? _client;
    private AgentConfig? _clientAgent;
    private string? _session;
    private bool _busy;
    private TextBlock? _reply, _thought;
    private StackPanel? _plan;
    private readonly Dictionary<string, (Expander Card, TextBlock Title, TextBlock Status, TextBlock Detail)> _tools = [];
    private readonly List<TaskCompletionSource<string?>> _pendingChoices = [];

    public AgentPanel()
    {
        InitializeComponent();
        RefreshAgents();
    }

    public void Attach(CanvasView board) => _board = board;

    public void CastShadowOn(UIElement receiver)
    {
        var shadow = new ThemeShadow();
        shadow.Receivers.Add(receiver);
        Shell.Shadow = shadow;
        Shell.Translation = new System.Numerics.Vector3(0, 0, 24);
    }

    private static List<AgentConfig> Agents => SettingsStore.Current.Agents.Where(a => a.Enabled).ToList();
    private AgentConfig? Selected => AgentPicker.SelectedIndex >= 0 && AgentPicker.SelectedIndex < Agents.Count ? Agents[AgentPicker.SelectedIndex] : null;

    /// <summary>Reload the picker after the agent list changed in settings.</summary>
    public void RefreshAgents()
    {
        var agents = Agents;
        AgentPicker.ItemsSource = agents.Select(a => a.Name).ToList();
        AgentPicker.SelectedIndex = Math.Max(0, agents.FindIndex(a => a.Id == SettingsStore.Current.Agent));
        AgentPicker.IsEnabled = agents.Count > 0;
    }

    private async void AgentPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is not { } agent || agent.Id == SettingsStore.Current.Agent && _clientAgent is null) return;
        SettingsStore.Current.Agent = agent.Id;
        SettingsStore.Save();
        if (_clientAgent is not null && _clientAgent.Id != agent.Id)
        {
            await StopAgentAsync();
            AddNote($"已切换到 {agent.Name}，下一条消息开始新对话");
        }
    }

    private async void NewChat_Click(object sender, RoutedEventArgs e)
    {
        if (_busy && _client is not null && _session is not null) await _client.CancelAsync(_session);
        _session = null;
        Messages.Children.Clear();
        _tools.Clear();
        _reply = _thought = null;
        _plan = null;
        EmptyText.Visibility = Visibility.Visible;
    }

    /// <summary>Leaving the canvas ends the agent process and the tool pipe.</summary>
    public async Task ShutdownAsync()
    {
        await StopAgentAsync();
        if (_mcp is not null) await _mcp.DisposeAsync();
        _mcp = null;
    }

    private async Task StopAgentAsync()
    {
        CancelChoices();
        var client = _client;
        _client = null;
        _clientAgent = null;
        _session = null;
        if (client is not null) await client.DisposeAsync();
    }

    // ---------- Sending ----------

    private void InputBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key != VirtualKey.Enter || shift) return;
        e.Handled = true;
        if (!_busy) Send_Click(sender, e);
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            if (_client is not null && _session is not null) await _client.CancelAsync(_session);
            CancelChoices();
            return;
        }
        var text = InputBox.Text.Trim();
        if (text.Length == 0 || _board is null) return;
        if (Selected is not { } agent)
        {
            AddNote("没有可用的 Agent，请在设置里启用至少一个", error: true);
            return;
        }
        InputBox.Text = "";
        AddBubble(text);
        // Tool call IDs only need to be unique within a turn: a new turn never updates an earlier card.
        _tools.Clear();
        SetBusy(true);
        try
        {
            await EnsureSessionAsync(agent);
            var prompt = new List<object>();
            if (Context() is { } context) prompt.Add(new { type = "text", text = context });
            prompt.Add(new { type = "text", text });
            string stop;
            // Some agents (Claude's adapter) only report a missing login on the first prompt, not at session/new.
            try { stop = await _client!.PromptAsync(_session!, prompt); }
            catch (AcpException ex) when (ex.AuthRequired)
            {
                await LoginAsync(agent);
                await EnsureSessionAsync(agent);
                stop = await _client!.PromptAsync(_session!, prompt);
            }
            if (stop != "end_turn")
                AddNote(stop switch
                {
                    "cancelled" => "已停止",
                    "max_tokens" => "回复达到长度上限，已中断",
                    "max_turn_requests" => "这一轮调用模型的次数达到上限，已中断",
                    "refusal" => "Agent 拒绝了这个请求",
                    _ => $"已结束：{stop}",
                });
        }
        catch (AcpException ex)
        {
            AddNote(ex.Message, error: true);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            AddNote(ex.Message, error: true);
        }
        finally
        {
            _reply = _thought = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SendIcon.Glyph = busy ? "" : "";
        ToolTipService.SetToolTip(SendButton, busy ? "停止" : "发送");
        AutomationProperties.SetName(SendButton, busy ? "停止" : "发送");
        AgentPicker.IsEnabled = !busy && Agents.Count > 0;
    }

    /// <summary>Selected nodes travel with each message, so "this picture" means something to the agent.</summary>
    private string? Context()
    {
        var doc = _board!.Doc;
        var selected = _board.Selection.Select(doc.Find).OfType<Core.Canvas.CanvasNode>().Take(20).ToList();
        if (selected.Count == 0) return null;
        return $"[无限画布] 当前画布「{doc.Title}」，用户选中了：" + string.Join("、", selected.Select(n => $"{n.Id}（{n.Kind.ToString().ToLowerInvariant()}「{n.Title}」）"));
    }

    private string Workspace
    {
        get
        {
            var dir = Path.Combine(ProjectStore.Root, "agent", _board!.Doc.Id);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private async Task EnsureSessionAsync(AgentConfig agent)
    {
        _mcp ??= new CanvasMcpServer(new CanvasAgentHost(_board!));
        if (_client is null)
        {
            var status = AddNote($"正在启动 {agent.Name}…（首次启动可能需要下载，稍等片刻）");
            try { await StartClientAsync(agent); }
            finally { Messages.Children.Remove(status); }
        }
        if (_session is not null) return;
        try { _session = await _client!.NewSessionAsync(Workspace, [_mcp.Declaration(Environment.ProcessPath!)]); }
        catch (AcpException ex) when (ex.AuthRequired)
        {
            await LoginAsync(agent);
            _session = await _client!.NewSessionAsync(Workspace, [_mcp.Declaration(Environment.ProcessPath!)]);
        }
    }

    /// <summary>
    /// ACP logins: "agent" methods run inside the agent (authenticate); "terminal" methods rerun the agent command
    /// with extra arguments in a console window, after which the agent is restarted.
    /// </summary>
    private async Task LoginAsync(AgentConfig agent)
    {
        var methods = _client!.AuthMethods;
        if (methods.Count == 0) throw new AcpException($"{agent.Name} 需要登录，请先在终端里运行它完成登录");
        var chosen = methods.Count == 1 ? methods[0].Id
            : await AskAsync($"{agent.Name} 需要登录，选择登录方式", methods.Select(m => (m.Id, m.Name, m == methods[0])).ToList());
        if (methods.FirstOrDefault(m => m.Id == chosen) is not { } method) throw new AcpException("已取消登录");
        if (method.Type != "terminal")
        {
            AddNote($"正在通过「{method.Name}」登录 {agent.Name}，请按提示在浏览器或弹出窗口里完成");
            await _client.AuthenticateAsync(method.Id);
            return;
        }
        var (file, args) = CommandLine.Parse(agent.Command);
        var info = new ProcessStartInfo(CommandLine.Resolve(file) ?? file) { UseShellExecute = false, CreateNoWindow = false, WorkingDirectory = Workspace };
        foreach (var arg in args.Concat(method.Args)) info.ArgumentList.Add(arg);
        foreach (var (key, value) in method.Env) info.Environment[key] = value;
        AddNote($"已打开 {agent.Name} 的登录窗口，完成后会自动继续");
        using (var login = Process.Start(info) ?? throw new AcpException("无法打开登录窗口"))
        {
            await login.WaitForExitAsync();
            if (login.ExitCode != 0) throw new AcpException($"{agent.Name} 登录没有完成（退出码 {login.ExitCode}）");
        }
        await StopAgentAsync();
        await StartClientAsync(agent);
    }

    private async Task StartClientAsync(AgentConfig agent)
    {
        var cwd = Workspace;
        var client = await Task.Run(() => AcpClient.StartAsync(agent.Command, cwd, this));
        _client = client;
        _clientAgent = agent;
        client.Disconnected += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (_client != client) return;
            _client = null;
            _clientAgent = null;
            _session = null;
            CancelChoices();
            AddNote($"{agent.Name} 已退出{(client.StderrTail is { Length: > 0 } tail ? "：" + tail : "")}", error: true);
        });
    }

    // ---------- Agent → client ----------

    public void OnSessionUpdate(string sessionId, JsonElement update) => DispatcherQueue.TryEnqueue(() =>
    {
        if (sessionId != _session) return;
        var kind = Str(update, "sessionUpdate");
        switch (kind)
        {
            case "agent_message_chunk":
                _thought = null;
                Append(ref _reply, ContentText(update), thought: false);
                break;
            case "agent_thought_chunk":
                Append(ref _thought, ContentText(update), thought: true);
                break;
            case "tool_call":
            case "tool_call_update":
                _reply = _thought = null;
                ShowTool(update);
                break;
            case "plan":
                ShowPlan(update);
                break;
        }
    });

    public Task<string?> RequestPermissionAsync(string sessionId, JsonElement toolCall, IReadOnlyList<AcpPermissionOption> options)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherQueue.TryEnqueue(async () =>
        {
            var title = Str(toolCall, "title") ?? (Str(toolCall, "toolCallId") is { } id && _tools.TryGetValue(id, out var tool) ? tool.Title.Text : null) ?? "执行一个操作";
            var labels = options.Select(o => (o.OptionId, o.Kind switch
            {
                "allow_once" => "允许",
                "allow_always" => "始终允许",
                "reject_once" => "拒绝",
                "reject_always" => "始终拒绝",
                _ => o.Name,
            }, o.Kind == "allow_once")).ToList();
            done.TrySetResult(await AskAsync($"{_clientAgent?.Name ?? "Agent"} 想要：{title}", labels));
        });
        return done.Task;
    }

    // ---------- Rendering ----------

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string ContentText(JsonElement update) => update.TryGetProperty("content", out var content) ? BlockText(content) : "";

    private static string BlockText(JsonElement block) => Str(block, "type") switch
    {
        "text" => Str(block, "text") ?? "",
        "image" => "［图片］",
        "resource_link" => $"［{Str(block, "name") ?? Str(block, "uri")}］",
        "resource" => block.TryGetProperty("resource", out var r) ? Str(r, "text") ?? $"［{Str(r, "uri")}］" : "",
        _ => "",
    };

    /// <summary>Tool output: content blocks, diffs (as the file path) and terminals.</summary>
    private static string ToolDetail(JsonElement update)
    {
        if (!update.TryGetProperty("content", out var items) || items.ValueKind != JsonValueKind.Array) return "";
        return string.Join("\n", items.EnumerateArray().Select(item => Str(item, "type") switch
        {
            "content" when item.TryGetProperty("content", out var block) => BlockText(block),
            "diff" => $"修改了 {Str(item, "path")}",
            "terminal" => "（终端输出）",
            _ => "",
        }).Where(t => t.Length > 0));
    }

    private void Append(ref TextBlock? target, string text, bool thought)
    {
        if (text.Length == 0) return;
        if (target is null)
        {
            target = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            if (thought)
            {
                target.Style = (Style)Application.Current.Resources["SecondaryCaption"];
                Add(new Expander { Header = "思考过程", Content = target, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            }
            else Add(target);
        }
        target.Text += text;
        ScrollToEnd();
    }

    private void ShowTool(JsonElement update)
    {
        var id = Str(update, "toolCallId") ?? Guid.NewGuid().ToString();
        if (!_tools.TryGetValue(id, out var tool))
        {
            var title = new TextBlock { Text = "工具调用", TextTrimming = TextTrimming.CharacterEllipsis };
            var status = new TextBlock { Style = (Style)Application.Current.Resources["SecondaryCaption"], VerticalAlignment = VerticalAlignment.Center };
            var header = new Grid { ColumnSpacing = 8, Children = { title, status } };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(status, 1);
            var detail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Style = (Style)Application.Current.Resources["SecondaryCaption"], MaxLines = 12 };
            var card = new Expander { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = detail };
            tool = (card, title, status, detail);
            _tools[id] = tool;
            Add(card);
        }
        // Updates usually carry only what changed: keep the title until a new one arrives.
        if (Str(update, "title") is { } newTitle)
        {
            tool.Title.Text = newTitle;
            AutomationProperties.SetName(tool.Card, newTitle);
        }
        if (Str(update, "status") is { } state) tool.Status.Text = state switch { "pending" => "等待中", "in_progress" => "进行中", "completed" => "完成", "failed" => "失败", _ => state };
        if (ToolDetail(update) is { Length: > 0 } output) tool.Detail.Text = output;
        ScrollToEnd();
    }

    private void ShowPlan(JsonElement update)
    {
        if (_plan is null)
        {
            _plan = new StackPanel { Spacing = 4 };
            Add(new Border { Child = _plan, Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(8), Background = (Brush)Application.Current.Resources["CardBrush"] });
        }
        _plan.Children.Clear();
        _plan.Children.Add(new TextBlock { Text = "计划", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (!update.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return;
        foreach (var entry in entries.EnumerateArray())
        {
            var mark = Str(entry, "status") switch { "completed" => "✓", "in_progress" => "▸", _ => "○" };
            _plan.Children.Add(new TextBlock { Text = $"{mark} {Str(entry, "content")}", TextWrapping = TextWrapping.Wrap });
        }
        ScrollToEnd();
    }

    /// <summary>Inline buttons for a permission or login choice; resolves to the chosen key, or null when the turn is stopped.</summary>
    private Task<string?> AskAsync(string question, IReadOnlyList<(string Key, string Label, bool Primary)> choices)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingChoices.Add(done);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var panel = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap }, buttons } };
        var card = new Border
        {
            Child = panel, Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["CardBrush"], BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
        };
        foreach (var (key, label, primary) in choices)
        {
            var button = new Button { Content = label, Style = (Style)Application.Current.Resources[primary ? "AccentActionButton" : "ActionButton"] };
            button.Click += (_, _) => done.TrySetResult(key);
            buttons.Children.Add(button);
        }
        _ = done.Task.ContinueWith(t =>
        {
            _pendingChoices.Remove(done);
            buttons.Children.Clear();
            buttons.Children.Add(new TextBlock
            {
                Text = t.Result is { } key ? $"已选择：{choices.First(c => c.Key == key).Label}" : "已取消",
                Style = (Style)Application.Current.Resources["SecondaryCaption"],
            });
        }, TaskScheduler.FromCurrentSynchronizationContext());
        Add(card);
        return done.Task;
    }

    private void CancelChoices()
    {
        foreach (var choice in _pendingChoices.ToList()) choice.TrySetResult(null);
    }

    private void AddBubble(string text)
    {
        _reply = _thought = null;
        Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 280, Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"] },
        });
    }

    private TextBlock AddNote(string text, bool error = false)
    {
        var note = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["SecondaryCaption"] };
        if (error) note.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        Add(note);
        return note;
    }

    private void Add(UIElement element)
    {
        EmptyText.Visibility = Visibility.Collapsed;
        Messages.Children.Add(element);
        ScrollToEnd();
    }

    private void ScrollToEnd() => DispatcherQueue.TryEnqueue(() => Scroller.ChangeView(null, Scroller.ScrollableHeight, null, true));
}
