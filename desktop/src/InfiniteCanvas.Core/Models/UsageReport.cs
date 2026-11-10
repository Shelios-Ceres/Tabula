using InfiniteCanvas.Core.Settings;
using InfiniteCanvas.Core.Storage;

namespace InfiniteCanvas.Core.Models;

/// <summary>What one channel delivered from a day on. The amount is null when no model with usage has a token price (per-picture vendors keep no size in the ledger).</summary>
public sealed record ChannelUsage(string Channel, Dictionary<string, int> Results, decimal? EstimatedAmount, List<string> UnpricedModels);

/// <summary>The channels billed in one currency: amounts of different currencies are never added together.</summary>
public sealed record CurrencyUsage(string Currency, Dictionary<string, int> Results, decimal? EstimatedAmount, List<ChannelUsage> Channels);

/// <summary>The local usage ledger as a spending overview, per currency. Local estimates only, not the vendor's bill.</summary>
public static class UsageReport
{
    /// <summary>Volcengine and Alibaba Cloud bill in yuan, the others in dollars (MiniMax by site, see the channel overload).</summary>
    public static string CurrencyOf(Vendor vendor) => vendor is Vendor.Ark or Vendor.Qwen ? "CNY" : "USD";

    /// <summary>MiniMax's currency follows the site the channel talks to (mainland CNY, international USD).</summary>
    public static string CurrencyOf(ModelChannel channel) => channel.Vendor == Vendor.MiniMax ? channel.IsInternationalSite ? "USD" : "CNY" : CurrencyOf(channel.Vendor);

    public static List<CurrencyUsage> Summarize(AppSettings settings, DateOnly from)
    {
        var channels = new List<(string Currency, ChannelUsage Usage)>();
        foreach (var channel in settings.Channels)
        {
            var results = UsageStore.Since(channel.Id, from).ToDictionary(pair => pair.Key.ToString().ToLowerInvariant(), pair => pair.Value);
            var tokens = UsageStore.Tokens(channel.Id, from).Where(pair => pair.Value.Total > 0).ToList();
            if (results.Count == 0 && tokens.Count == 0) continue;
            var priced = tokens.Where(pair => Prices.Of(channel, pair.Key) is not null).ToList();
            decimal? amount = priced.Count > 0 ? priced.Sum(pair => Prices.Cost(pair.Value, Prices.Of(channel, pair.Key)!)) : null;
            var unpriced = tokens.Except(priced).Select(pair => pair.Key).ToList();
            channels.Add((CurrencyOf(channel), new ChannelUsage(settings.ChannelName(channel), results, amount, unpriced)));
        }
        return channels.GroupBy(c => c.Currency).OrderBy(g => g.Key).Select(group =>
        {
            var usages = group.Select(c => c.Usage).ToList();
            var results = usages.SelectMany(u => u.Results).GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.Sum(r => r.Value));
            decimal? amount = usages.Any(u => u.EstimatedAmount is not null) ? usages.Sum(u => u.EstimatedAmount ?? 0) : null;
            return new CurrencyUsage(group.Key, results, amount, usages);
        }).ToList();
    }
}
