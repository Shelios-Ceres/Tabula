using System.Diagnostics;
using System.Reflection;
using InfiniteCanvas.Core.Canvas;
using InfiniteCanvas.Core.Models;
using InfiniteCanvas.Core.Settings;
using InfiniteCanvas.Core.Storage;
using InfiniteCanvas.Providers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace InfiniteCanvas.App.Pages;

/// <summary>Two-line list entry: the vendor, then its host or a missing-key warning. A class, not a record: two entries of the same vendor must stay distinct items for ListView selection.</summary>
public sealed class ChannelRow(string name, string detail, bool missingKey)
{
    public string Name { get; } = name;
    public string Detail { get; } = detail;
    public bool MissingKey { get; } = missingKey;
}

public sealed partial class SettingsPage : Page
{
    private static AppSettings Settings => SettingsStore.Current;
    private ModelChannel? _channel;
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        foreach (var vendor in Enum.GetValues<Vendor>())
        {
            var item = new MenuFlyoutItem { Text = ModelChannel.VendorName(vendor) };
            item.Click += (_, _) => AddChannel(vendor);
            VendorMenu.Items.Add(item);
        }
        DataPath.Text = ProjectStore.Root;
        var assembly = typeof(App).Assembly;
        var version = assembly.GetName().Version!;
        ProductText.Text = $"{assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product} {version.Major}.{version.Minor}";
        VersionText.Text = $"版本 {version.ToString(3)}";
        SystemPromptBox.Text = Settings.SystemPrompt;
        BuildAgents();
        ExternalAgentsSwitch.IsOn = Settings.ExternalAgents;
        BridgeCommand.Text = $"\"{Environment.ProcessPath}\" {Agents.McpBridge.Argument}";
        RefreshChannels(Settings.Channels.FirstOrDefault());
        App.Window.SetCaption("设置");
    }

    private void RefreshChannels(ModelChannel? select)
    {
        _loading = true;
        ChannelList.ItemsSource = Rows();
        ChannelList.SelectedIndex = select is null ? -1 : Settings.Channels.IndexOf(select);
        _loading = false;
        ShowChannel(select);
        RefreshDefaults();
    }

    // Judge by the decrypted key: a key copied from another PC or Windows user is stored but unusable here.
    private static List<ChannelRow> Rows() => Settings.Channels.Select(c => new ChannelRow(Settings.ChannelName(c), c.ApiKey.Length == 0 ? "未填写 API Key" : Host(c), c.ApiKey.Length == 0)).ToList();

    private static string Host(ModelChannel channel) => Uri.TryCreate(channel.BaseUrl.Trim(), UriKind.Absolute, out var uri) ? uri.Host : ModelChannel.DefaultBaseUrl(channel.Vendor)[8..];

    private void ShowChannel(ModelChannel? channel)
    {
        _channel = channel;
        EditorCard.Visibility = channel is null ? Visibility.Collapsed : Visibility.Visible;
        NoChannelCard.Visibility = Settings.Channels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (channel is null) return;
        _loading = true;
        EditorTitle.Text = Settings.ChannelName(channel);
        BaseUrlBox.Text = channel.BaseUrl;
        BaseUrlBox.PlaceholderText = ModelChannel.DefaultBaseUrl(channel.Vendor);
        KeyBox.Password = channel.ApiKey;
        KeyWarning.IsOpen = KeyBox.Password.Length == 0;
        VersionBox.SelectedItem = channel.ApiVersion;
        VersionBox.Visibility = channel.Vendor == Vendor.Google ? Visibility.Visible : Visibility.Collapsed;
        NewEndpointBox.Visibility = channel.Vendor == Vendor.Ark ? Visibility.Visible : Visibility.Collapsed;
        // A pending endpoint typed for another channel or vendor must not stick to the next model added here.
        NewEndpointBox.Text = "";
        NewModelBox.PlaceholderText = channel.Vendor == Vendor.Ark ? "模型名称，例如 doubao-seedream-5-0-pro-260628" : "手动输入模型名称，例如 gpt-image-2";
        ApiHint.Text = $"请求地址：{channel.ApiBase()}" + channel.Vendor switch
        {
            Vendor.Qwen => "\n也可填写业务空间专属域名，例如 https://{WorkspaceId}.cn-beijing.maas.aliyuncs.com；模型、地址和 API Key 需属于同一地域。",
            Vendor.Ark => "\n模型名称填底层型号（如 doubao-seedream-5-0-pro-260628），推理接入点填 ep- 开头的 ID：界面显示模型名称，实际请求使用接入点。",
            Vendor.OpenAI => "\nOpenAI 的 Sora 视频接口已于 2026-09-24 下线，这里只提供图片、文本和语音。",
            _ => "",
        };
        _loading = false;
        RenderModels();
    }

    private void RenderModels()
    {
        ModelRows.Children.Clear();
        if (_channel is null) return;
        foreach (var model in _channel.Models)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            // The display name is editable; empty shows the automatic short name. Requests always use the full model ID below it.
            var alias = new TextBox { Text = model.Alias ?? "", PlaceholderText = AppSettings.DisplayName(_channel, new ChannelModel { Name = model.Name }) };
            AutomationProperties.SetName(alias, $"{model.Name} 的显示名称");
            ToolTipService.SetToolTip(alias, "在模型选择里显示的名字，留空使用自动简化的名字；实际请求仍用完整型号");
            alias.TextChanged += (_, _) =>
            {
                var value = alias.Text.Trim() is { Length: > 0 } text ? text : null;
                if (value == model.Alias) return;
                model.Alias = value;
                Save();
                RefreshDefaults();
            };
            var name = new TextBlock { Text = model.Name, Style = (Style)Application.Current.Resources["SecondaryCaption"], TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true, Margin = new Thickness(2, 0, 0, 0) };
            ToolTipService.SetToolTip(name, model.Name);
            row.Children.Add(new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { alias, name } });

            var capability = new ComboBox { Width = 96, VerticalAlignment = VerticalAlignment.Top, ItemsSource = Labels.Capabilities.Select(Labels.Name).ToList(), SelectedIndex = (int)model.Capability };
            AutomationProperties.SetName(capability, $"{model.Name} 的类型");
            capability.SelectionChanged += (_, _) => { model.Capability = (Capability)capability.SelectedIndex; Save(); RefreshDefaults(); };
            Grid.SetColumn(capability, 1);
            row.Children.Add(capability);

            if (_channel.Vendor == Vendor.Ark)
            {
                var endpoint = new TextBox { Width = 200, VerticalAlignment = VerticalAlignment.Top, PlaceholderText = "推理接入点 ep-（可选）", Text = model.EndpointId ?? "" };
                AutomationProperties.SetName(endpoint, $"{model.Name} 的推理接入点");
                endpoint.TextChanged += (_, _) => { model.EndpointId = string.IsNullOrWhiteSpace(endpoint.Text) ? null : endpoint.Text.Trim(); Save(); };
                Grid.SetColumn(endpoint, 2);
                row.Children.Add(endpoint);
            }

            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Style = (Style)Application.Current.Resources["ToolbarButton"], VerticalAlignment = VerticalAlignment.Top };
            ToolTipService.SetToolTip(remove, "移除模型");
            AutomationProperties.SetName(remove, "移除模型");
            remove.Click += (_, _) => { _channel.Models.Remove(model); Save(); RenderModels(); RefreshDefaults(); };
            Grid.SetColumn(remove, 3);
            row.Children.Add(remove);
            ModelRows.Children.Add(row);
        }
        if (_channel.Models.Count == 0)
            ModelRows.Children.Add(new TextBlock { Text = "还没有模型，可以拉取模型列表、添加推荐模型或手动输入。", Style = (Style)Application.Current.Resources["SecondaryCaption"] });
    }

    private void RefreshDefaults()
    {
        _loading = true;
        foreach (var box in new[] { ImageDefault, VideoDefault, TextDefault, AudioDefault })
        {
            var capability = Enum.Parse<Capability>((string)box.Tag);
            var values = Settings.ModelsFor(capability).ToList();
            box.ItemsSource = values.Select(Settings.Label).ToList();
            box.SelectedIndex = values.IndexOf(Settings.DefaultModel(capability));
            box.PlaceholderText = values.Count == 0 ? "暂无此类模型" : "请选择";
        }
        _loading = false;
    }

    private static void Save() => SettingsStore.Save();

    private void AddChannel(Vendor vendor)
    {
        var channel = new ModelChannel { Vendor = vendor, BaseUrl = ModelChannel.DefaultBaseUrl(vendor) };
        Settings.Channels.Add(channel);
        Save();
        RefreshChannels(channel);
    }

    private void ChannelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ShowChannel(ChannelList.SelectedIndex >= 0 ? Settings.Channels[ChannelList.SelectedIndex] : null);
        // The picked channel slides in at the top instead of swapping in place, even when the page was scrolled to a later section.
        ContentScroller.ChangeView(null, 0, null);
        Motion.Refresh(EditorCard);
    }

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _channel is null) return;
        var (url, key, version) = (BaseUrlBox.Text.Trim(), KeyBox.Password.Trim(), VersionBox.SelectedItem as string ?? _channel.ApiVersion);
        // Filling the fields for a newly picked entry echoes change events after loading ends; only real edits save and refresh.
        if (url == _channel.BaseUrl && key == _channel.ApiKey && version == _channel.ApiVersion) return;
        _channel.BaseUrl = url;
        _channel.ApiKey = key;
        _channel.ApiVersion = version;
        Save();
        KeyWarning.IsOpen = KeyBox.Password.Trim().Length == 0;
        ApiHint.Text = ApiHint.Text.Contains('\n') ? $"请求地址：{_channel.ApiBase()}{ApiHint.Text[ApiHint.Text.IndexOf('\n')..]}" : $"请求地址：{_channel.ApiBase()}";
        // Rebuilding the list replays its entrance animation, so it happens only when an entry's text really changed.
        if (ChannelList.ItemsSource is List<ChannelRow> rows && rows.Zip(Rows()).Any(pair => pair.First.Name != pair.Second.Name || pair.First.Detail != pair.Second.Detail)) RefreshChannelNames();
    }

    private void AddChannelButton_Click(object sender, RoutedEventArgs e) => VendorMenu.ShowAt((FrameworkElement)sender);

    private void RefreshChannelNames()
    {
        _loading = true;
        var index = ChannelList.SelectedIndex;
        ChannelList.ItemsSource = Rows();
        ChannelList.SelectedIndex = index;
        _loading = false;
        RefreshDefaults();
    }


    private void AddModels(IEnumerable<string> names)
    {
        if (_channel is null) return;
        foreach (var name in names.Select(n => n.Trim()).Where(n => n.Length > 0))
            if (_channel.Models.All(m => m.Name != name)) _channel.Models.Add(new ChannelModel { Name = name, Capability = ModelFamilies.GuessCapability(name) });
        Save();
        RenderModels();
        RefreshDefaults();
    }

    private async void AddModel_Click(object sender, RoutedEventArgs e)
    {
        if (_channel is null) return;
        var name = NewModelBox.Text.Trim();
        var endpoint = NewEndpointBox.Text.Trim();
        if (_channel.Vendor == Vendor.Ark && name.StartsWith("ep-"))
        {
            await Dialogs.MessageAsync("请填写模型名称", "ep- 开头的是推理接入点 ID，请填到右侧「推理接入点」；模型名称填对应的底层型号，例如 doubao-seedream-5-0-pro-260628。");
            return;
        }
        if (endpoint.Length > 0 && !endpoint.StartsWith("ep-"))
        {
            await Dialogs.MessageAsync("推理接入点格式不对", "推理接入点 ID 以 ep- 开头，可在方舟控制台「在线推理」的接入点详情里复制。");
            return;
        }
        AddModels([name]);
        // A model added together with its endpoint keeps the friendly name for display and the endpoint for requests.
        if (endpoint.Length > 0 && _channel.Models.FirstOrDefault(m => m.Name == name) is { } model)
        {
            model.EndpointId = endpoint;
            Save();
            RenderModels();
        }
        NewModelBox.Text = "";
        NewEndpointBox.Text = "";
    }

    private void NewModelBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) AddModel_Click(sender, e);
    }

    private void Recommended_Click(object sender, RoutedEventArgs e)
    {
        if (_channel is not null) AddModels(ModelFamilies.Recommended(_channel.Vendor));
    }

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        if (_channel is not { } channel) return;
        FetchButton.IsEnabled = false;
        FetchText.Text = "正在拉取…";
        try
        {
            var models = await AiProvider.For(channel, App.Http).ListModelsAsync(CancellationToken.None);
            // Switched or deleted meanwhile, or the picker queued behind another dialog: check again.
            if (channel != _channel) return;
            if (await Dialogs.PickAsync($"选择要添加的模型（共 {models.Count} 个）", models, channel.Models.Select(m => m.Name)) is { } picked && channel == _channel) AddModels(picked);
        }
        catch (Exception ex)
        {
            await Dialogs.MessageAsync("拉取模型列表失败", ex.Message);
        }
        finally
        {
            FetchButton.IsEnabled = true;
            FetchText.Text = "拉取模型列表";
        }
    }

    private async void DeleteChannel_Click(object sender, RoutedEventArgs e)
    {
        if (_channel is null || !await Dialogs.ConfirmAsync("删除服务商？", $"「{Settings.ChannelName(_channel)}」及其模型配置会被删除，已生成的内容不受影响。", "删除", destructive: true)) return;
        Settings.Channels.Remove(_channel);
        Save();
        RefreshChannels(Settings.Channels.FirstOrDefault());
    }

    private void Default_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || sender is not ComboBox box || box.SelectedIndex < 0) return;
        var capability = Enum.Parse<Capability>((string)box.Tag);
        Settings.SetDefaultModel(capability, Settings.ModelsFor(capability).ElementAt(box.SelectedIndex));
        Save();
    }

    /// <summary>One row per agent: enable switch and the ACP launch command, with a reset to the vendor's documented command.</summary>
    private void BuildAgents()
    {
        AgentList.Children.Clear();
        var defaults = AgentConfig.Defaults();
        foreach (var agent in Settings.Agents)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var toggle = new ToggleSwitch { IsOn = agent.Enabled, OnContent = agent.Name, OffContent = agent.Name, MinWidth = 0 };
            AutomationProperties.SetName(toggle, $"启用 {agent.Name}");
            toggle.Toggled += (_, _) => { agent.Enabled = toggle.IsOn; Save(); };
            var command = new TextBox { Text = agent.Command, FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(command, $"{agent.Name} 启动命令");
            command.TextChanged += (_, _) => { agent.Command = command.Text.Trim(); Save(); };
            Grid.SetColumn(command, 1);
            row.Children.Add(toggle);
            row.Children.Add(command);
            if (defaults.FirstOrDefault(d => d.Id == agent.Id) is { } fallback)
            {
                var reset = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Padding = new Thickness(9, 6, 9, 6), Content = new FontIcon { Glyph = "", FontSize = 13 } };
                ToolTipService.SetToolTip(reset, "恢复默认命令");
                AutomationProperties.SetName(reset, $"恢复 {agent.Name} 的默认命令");
                reset.Click += (_, _) => command.Text = fallback.Command;
                Grid.SetColumn(reset, 2);
                row.Children.Add(reset);
            }
            AgentList.Children.Add(row);
        }
    }

    private async void ExternalAgents_Toggled(object sender, RoutedEventArgs e)
    {
        if (Settings.ExternalAgents == ExternalAgentsSwitch.IsOn) return;
        Settings.ExternalAgents = ExternalAgentsSwitch.IsOn;
        Save();
        await Board.SharedCanvasHost.ApplyAsync(Settings.ExternalAgents);
    }

    private void SystemPrompt_Changed(object sender, TextChangedEventArgs e)
    {
        Settings.SystemPrompt = SystemPromptBox.Text;
        Save();
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ProjectStore.Root);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ProjectStore.Root}\"") { UseShellExecute = true });
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(ProjectsPage));
    }
}

public static class Labels
{
    public static readonly Capability[] Capabilities = [Capability.Image, Capability.Video, Capability.Text, Capability.Audio];

    public static string Name(Capability capability) => capability switch
    {
        Capability.Image => "图片",
        Capability.Video => "视频",
        Capability.Audio => "音频",
        _ => "文本",
    };
}
