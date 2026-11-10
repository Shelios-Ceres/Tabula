using InfiniteCanvas.App.Pages;
using InfiniteCanvas.Core.Models;
using InfiniteCanvas.Core.Settings;
using InfiniteCanvas.Providers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace InfiniteCanvas.App.Board;

/// <summary>
/// The canvas toolbar's quota summary, one block per vendor: today's and this month's results from the local ledger,
/// and the vendor's balance, queried each time it opens when a billing credential is set in settings.
/// </summary>
internal sealed class QuotaFlyout : Flyout
{
    private readonly StackPanel _list = new() { Spacing = 14 };

    public QuotaFlyout()
    {
        Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft;
        var caption = (Style)Application.Current.Resources["SecondaryCaption"];
        Content = new StackPanel
        {
            Width = 340, Spacing = 12, Children =
            {
                new ScrollViewer { MaxHeight = 420, Padding = new(0, 0, 12, 0), Content = _list },
                new TextBlock { Text = "用量是本机统计，只计这个客户端成功生成的结果；余额来自服务商，需要先在设置里填写查账凭据。", Style = caption, TextWrapping = TextWrapping.Wrap },
            },
        };
        Opening += (_, _) => Refresh();
    }

    private void Refresh()
    {
        _list.Children.Clear();
        var settings = SettingsStore.Current;
        var caption = (Style)Application.Current.Resources["SecondaryCaption"];
        if (settings.Channels.Count == 0) _list.Children.Add(new TextBlock { Text = "还没有服务商，请先在设置里添加。", Style = caption });
        foreach (var channel in settings.Channels)
        {
            var balance = new TextBlock
            {
                Text = ModelChannel.NoBillingApi(channel.Vendor) ? $"{ModelChannel.VendorName(channel.Vendor)} 不提供余额查询" : channel.HasBilling ? "正在查询余额…" : "未设置查账凭据，可在设置里填写",
                Style = caption, TextWrapping = TextWrapping.Wrap,
            };
            // Google and MiniMax have no billing API, so their line is an estimate from the reported tokens and per-model prices.
            if (ModelChannel.NoBillingApi(channel.Vendor)) Accent(balance, $"本月估算 ≈ {(UsageReport.CurrencyOf(channel) == "CNY" ? "¥" : "$")}{Prices.Estimate(channel, Labels.MonthStart):N2}（按单价和本机 token）");
            var block = new StackPanel
            {
                Spacing = 2, Children =
                {
                    new TextBlock { Text = settings.ChannelName(channel), FontWeight = FontWeights.SemiBold },
                    balance,
                    new TextBlock { Text = $"今天：{Labels.Usage(channel.Id, Labels.Today)}", Style = caption },
                    new TextBlock { Text = $"本月：{Labels.Usage(channel.Id, Labels.MonthStart)}", Style = caption },
                },
            };
            if (Labels.Tokens(channel.Id, Labels.MonthStart) is { } tokens) block.Children.Add(new TextBlock { Text = $"本月 Token：{tokens}", Style = caption });
            _list.Children.Add(block);
            if (channel.HasBilling) _ = LoadAsync(channel, balance);
        }
    }

    private static async Task LoadAsync(ModelChannel channel, TextBlock target)
    {
        try { Accent(target, (await AiProvider.For(channel, App.Http).BalanceAsync(CancellationToken.None)).Text); }
        catch (Exception ex) { target.Text = $"查询余额失败：{ex.Message}"; }
    }

    private static void Accent(TextBlock target, string text)
    {
        target.Text = text;
        target.Style = null;
        target.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
    }
}
