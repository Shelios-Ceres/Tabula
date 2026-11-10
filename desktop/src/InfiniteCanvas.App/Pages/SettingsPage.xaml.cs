using System.Diagnostics;
using System.Globalization;
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
        WaitingAnimationsSwitch.IsOn = Settings.WaitingAnimations;
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
        ConcurrencyBox.Value = channel.MaxConcurrency ?? double.NaN;
        VersionBox.SelectedItem = channel.ApiVersion;
        VersionBox.Visibility = channel.Vendor == Vendor.Google ? Visibility.Visible : Visibility.Collapsed;
        SiteBox.Visibility = channel.Vendor == Vendor.MiniMax ? Visibility.Visible : Visibility.Collapsed;
        SiteBox.SelectedIndex = channel.IsInternationalSite ? 1 : 0;
        NewEndpointBox.Visibility = channel.Vendor == Vendor.Ark ? Visibility.Visible : Visibility.Collapsed;
        // A pending endpoint typed for another channel or vendor must not stick to the next model added here.
        NewEndpointBox.Text = "";
        NewModelBox.PlaceholderText = channel.Vendor == Vendor.Ark ? "模型名称，例如 doubao-seedream-5-0-pro-260628" : "手动输入模型名称，例如 gpt-image-2";
        ApiHint.Text = $"请求地址：{channel.ApiBase()}" + channel.Vendor switch
        {
            Vendor.Qwen => "\n也可填写业务空间专属域名，例如 https://{WorkspaceId}.cn-beijing.maas.aliyuncs.com；模型、地址和 API Key 需属于同一地域。",
            Vendor.Ark => "\n模型名称填底层型号（如 doubao-seedream-5-0-pro-260628），推理接入点填 ep- 开头的 ID：界面显示模型名称，实际请求使用接入点。",
            Vendor.MiniMax => "\n站点决定请求地址和估算花费的币种（中国大陆 ¥，国际 $）；接口地址框里填了别的地址（如代理）时以它为准。",
            Vendor.OpenAI => "\nOpenAI 的 Sora 视频接口已于 2026-09-24 下线，这里只提供图片、文本和语音。",
            _ => "",
        };
        ShowBilling(channel);
        _loading = false;
        RenderModels();
    }

    /// <summary>The 额度 section: the local ledger, then the billing credential this vendor needs (never the API key) and a balance query.</summary>
    private void ShowBilling(ModelChannel channel)
    {
        UsageText.Text = $"本机统计　今天：{Labels.Usage(channel.Id, Labels.Today)}　本月：{Labels.Usage(channel.Id, Labels.MonthStart)}";
        var month = Labels.Tokens(channel.Id, Labels.MonthStart);
        TokenText.Text = $"Token　今天：{Labels.Tokens(channel.Id, Labels.Today) ?? "暂无"}　本月：{month}";
        TokenText.Visibility = month is null ? Visibility.Collapsed : Visibility.Visible;
        var estimated = ModelChannel.NoBillingApi(channel.Vendor);
        EstimateText.Visibility = PriceRows.Visibility = estimated ? Visibility.Visible : Visibility.Collapsed;
        if (estimated) ShowEstimate(channel);
        var (idHeader, keyHeader, hint) = channel.Vendor switch
        {
            Vendor.OpenAI => (null, "Admin Key", "OpenAI 没有查询余额的接口，填入 Admin Key 后显示本月花费（Costs API）。Admin Key 只能由组织 Owner 在 OpenAI 平台的 Admin keys 页面创建，建议只开放用量读取权限。"),
            Vendor.XAI => (null, "Management Key", "填入 Management Key 后显示团队的预付余额。Management Key 在 xAI 控制台的设置里创建，账户需要有 Management Keys 权限。"),
            Vendor.Ark => ("Access Key ID", "Secret Access Key", "方舟的 API Key 不能查账，这里用火山引擎的访问密钥（AK/SK）查询账户可用余额；建议新建一个只授予费用中心只读权限的子用户来创建密钥。"),
            Vendor.Qwen => ("AccessKey ID", "AccessKey Secret", "百炼的免费额度没有公开接口，只能在百炼控制台查看；这里用阿里云 AccessKey 查询账户可用额度，建议新建只授予 AliyunBSSReadOnlyAccess 权限的 RAM 用户来创建密钥。"),
            Vendor.MiniMax => (null, null, "MiniMax 没有能查询额度或余额的接口，请在 MiniMax 开放平台的账户管理里查看；上面的花费是按单价和本机 token 估算的。"),
            _ => ((string?)null, (string?)null, "Google Gemini 没有能用 API Key 查询额度或余额的接口，请在 Google AI Studio 查看用量与费用。"),
        };
        BillingHint.Text = keyHeader is null ? hint : hint + "凭据用 Windows 数据保护（DPAPI）加密保存在本机，只用来查余额。";
        BillingFields.Visibility = BalanceRow.Visibility = keyHeader is null ? Visibility.Collapsed : Visibility.Visible;
        BillingIdBox.Visibility = idHeader is null ? Visibility.Collapsed : Visibility.Visible;
        BillingIdBox.Header = idHeader;
        BillingIdBox.Text = channel.BillingId ?? "";
        BillingKeyBox.Header = keyHeader;
        BillingKeyBox.Password = channel.BillingKey;
        Grid.SetColumn(BillingKeyBox, idHeader is null ? 0 : 1);
        Grid.SetColumnSpan(BillingKeyBox, idHeader is null ? 2 : 1);
        BalanceText.Text = "";
    }

    private void ShowEstimate(ModelChannel channel)
    {
        var sign = UsageReport.CurrencyOf(channel) == "CNY" ? "¥" : "$";
        EstimateText.Text = $"估算花费　今天 ≈ {sign}{Prices.Estimate(channel, Labels.Today):N2}　本月 ≈ {sign}{Prices.Estimate(channel, Labels.MonthStart):N2}（按下面的单价和本机 token 估算，不含免费额度）";
    }

    /// <summary>Google's per-model prices for the estimate: an empty box uses the built-in official price shown as its placeholder.</summary>
    private void RenderPrices(ModelChannel channel)
    {
        var caption = (Style)Application.Current.Resources["SecondaryCaption"];
        PriceRows.Children.Add(new TextBlock { Text = $"估算单价（{(UsageReport.CurrencyOf(channel) == "CNY" ? "人民币" : "美元")} / 百万 token，留空使用内置的官方价格）", Style = caption });
        foreach (var model in channel.Models)
        {
            var builtin = Prices.Default(channel, model.Name);
            TextBox Box(string header, decimal? own, decimal? fallback) => new()
            {
                Header = header, Width = 130, Text = own?.ToString(CultureInfo.InvariantCulture) ?? "",
                PlaceholderText = fallback?.ToString(CultureInfo.InvariantCulture) ?? "未知",
            };
            var boxes = new[] { Box("输入", model.Price?.Input, builtin?.Input), Box("输出（含思考）", model.Price?.Output, builtin?.Output), Box("图片 / 音频输出", model.Price?.Media, builtin?.Media) };
            foreach (var box in boxes)
            {
                AutomationProperties.SetName(box, $"{model.Name} {box.Header} 单价");
                box.TextChanged += (_, _) =>
                {
                    var values = boxes.Select(b => decimal.TryParse(b.Text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : (decimal?)null).ToArray();
                    var price = values.All(v => v is null) ? null : new TokenPrice(values[0] ?? builtin?.Input ?? 0, values[1] ?? builtin?.Output ?? 0, values[2] ?? builtin?.Media ?? 0);
                    if (price == model.Price) return;
                    model.Price = price;
                    Save();
                    ShowEstimate(channel);
                };
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = AppSettings.DisplayName(channel, model), Width = 180, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6), TextTrimming = TextTrimming.CharacterEllipsis });
            foreach (var box in boxes) row.Children.Add(box);
            PriceRows.Children.Add(row);
        }
    }

    private void Billing_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _channel is null) return;
        var (id, key) = (BillingIdBox.Text.Trim() is { Length: > 0 } text ? text : null, BillingKeyBox.Password.Trim());
        if (id == _channel.BillingId && key == _channel.BillingKey) return;
        _channel.BillingId = id;
        _channel.BillingKey = key;
        Save();
        BalanceText.Text = "";
    }

    private async void Balance_Click(object sender, RoutedEventArgs e)
    {
        if (_channel is not { } channel) return;
        if (!channel.HasBilling)
        {
            BalanceText.Text = ModelChannel.BillingNeedsId(channel.Vendor) ? "请先填写上面两项访问密钥" : "请先填写上面的 Key";
            return;
        }
        BalanceButton.IsEnabled = false;
        BalanceText.Text = "正在查询…";
        try
        {
            var balance = await AiProvider.For(channel, App.Http).BalanceAsync(CancellationToken.None);
            if (channel == _channel) BalanceText.Text = balance.Text;
        }
        catch (Exception ex)
        {
            if (channel == _channel) BalanceText.Text = $"查询失败：{ex.Message}";
        }
        finally { BalanceButton.IsEnabled = true; }
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
        // Prices of the vendors without a billing API follow the model list, so they are rebuilt with it.
        PriceRows.Children.Clear();
        if (ModelChannel.NoBillingApi(_channel.Vendor)) RenderPrices(_channel);
        if (_channel.Models.Count == 0)
            ModelRows.Children.Add(new TextBlock { Text = "还没有模型，可以拉取模型列表、添加推荐模型或手动输入。", Style = (Style)Application.Current.Resources["SecondaryCaption"] });
    }

    private void RefreshDefaults()
    {
        _loading = true;
        foreach (var box in new[] { ImageDefault, TextDefault, AudioDefault })
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
        // The address decides MiniMax's site and with it the currency of its prices.
        if (_channel.Vendor == Vendor.MiniMax)
        {
            _loading = true;
            SiteBox.SelectedIndex = _channel.IsInternationalSite ? 1 : 0;
            ShowBilling(_channel);
            _loading = false;
            RenderModels();
        }
        // Rebuilding the list replays its entrance animation, so it happens only when an entry's text really changed.
        if (ChannelList.ItemsSource is List<ChannelRow> rows && rows.Zip(Rows()).Any(pair => pair.First.Name != pair.Second.Name || pair.First.Detail != pair.Second.Detail)) RefreshChannelNames();
    }

    /// <summary>MiniMax's two sites are two hosts, so picking one writes its host into the address box; a custom address there still wins.</summary>
    private void Site_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _channel is not { Vendor: Vendor.MiniMax } channel || SiteBox.SelectedIndex < 0) return;
        var url = SiteBox.SelectedIndex == 1 ? ModelChannel.MiniMaxInternational : ModelChannel.DefaultBaseUrl(Vendor.MiniMax);
        if (url == channel.BaseUrl) return;
        channel.BaseUrl = url;
        Save();
        ShowChannel(channel);
    }

    private void Concurrency_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_loading || _channel is null) return;
        int? limit = double.IsNaN(sender.Value) ? null : (int)Math.Round(sender.Value);
        if (limit == _channel.MaxConcurrency) return;
        _channel.MaxConcurrency = limit;
        Save();
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

    private void OpenLoaders_Click(object sender, RoutedEventArgs e)
    {
        Board.Loaders.EnsureInitialized();
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Board.Loaders.Folder}\"") { UseShellExecute = true });
    }

    private void WaitingAnimations_Toggled(object sender, RoutedEventArgs e)
    {
        if (Settings.WaitingAnimations == WaitingAnimationsSwitch.IsOn) return;
        Settings.WaitingAnimations = WaitingAnimationsSwitch.IsOn;
        Save();
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

    /// <summary>A token count as 1.2 万 or 3,456.</summary>
    public static string TokenCount(long n) => n >= 10_000 ? $"{n / 10_000d:0.#} 万" : $"{n:N0}";

    /// <summary>A channel's reported tokens from a day on, e.g. 输入 1.2 万 · 输出 3.4 万 (image / audio tokens count as output); null when none.</summary>
    public static string? Tokens(string channelId, DateOnly from)
    {
        var total = UsageStore.Tokens(channelId, from).Values.Aggregate(default(TokenUsage), (sum, t) => sum + t);
        return total.Total == 0 ? null : $"输入 {TokenCount(total.Input)} · 输出 {TokenCount(total.Output + total.Media)}";
    }

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
    public static DateOnly MonthStart => new(Today.Year, Today.Month, 1);

    /// <summary>A channel's local ledger from a day on, e.g. 图片 12 张 · 文本 3 条.</summary>
    public static string Usage(string channelId, DateOnly from)
    {
        var counts = UsageStore.Since(channelId, from);
        string Unit(Capability c) => c switch { Capability.Image => "张", Capability.Audio => "段", Capability.Text => "条", _ => "个" };
        return counts.Count == 0 ? "暂无" : string.Join(" · ", Capabilities.Where(counts.ContainsKey).Select(c => $"{Name(c)} {counts[c]} {Unit(c)}"));
    }
}
