using System.Text.Json;
using InfiniteCanvas.Core.Canvas;
using InfiniteCanvas.Core.Models;

namespace InfiniteCanvas.Core.Storage;

/// <summary>Results one model of one channel delivered on one day, and the tokens the vendor reported for them.</summary>
public sealed class UsageRow
{
    public DateOnly Day { get; set; }
    public string Channel { get; set; } = "";
    public string Model { get; set; } = "";
    public Capability Capability { get; set; }
    public int Count { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    /// <summary>Image / audio output tokens, priced apart from text output by Gemini.</summary>
    public long MediaTokens { get; set; }
}

/// <summary>
/// The local usage ledger: successful results this client received, per day, channel and model, with the tokens each vendor reported.
/// Works for every vendor without extra keys, but it is not the vendor's bill: other apps using the same key are not seen.
/// </summary>
public static class UsageStore
{
    private static string FilePath => Path.Combine(ProjectStore.Root, "usage.json");
    private static string ImagesPath => Path.Combine(ProjectStore.Root, "usage-images.json");
    private static readonly object Gate = new();
    private static List<UsageRow>? _rows;
    private static Dictionary<string, TokenUsage>? _images;

    private static List<UsageRow> Rows => _rows ??= Load<List<UsageRow>>(FilePath);
    /// <summary>Tokens per picture of the last run for each model and settings (<see cref="Prices.ImageKey"/>), for estimates without a published price.</summary>
    private static Dictionary<string, TokenUsage> Images => _images ??= Load<Dictionary<string, TokenUsage>>(ImagesPath);

    private static T Load<T>(string path) where T : new()
    {
        if (!File.Exists(path)) return new();
        try { return Json.Deserialize<T>(File.ReadAllText(path)) ?? new(); }
        catch (JsonException)
        {
            // Back up a corrupt file before starting empty, so the next record cannot overwrite the history.
            File.Copy(path, $"{path}.broken-{DateTime.Now:yyyyMMddHHmmss}", true);
            return new();
        }
    }

    private static void Write<T>(string path, T value)
    {
        try { Json.WriteFile(path, value); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Kept in memory; the next write retries. */ }
    }

    public static void RememberImage(string key, TokenUsage perImage)
    {
        lock (Gate)
        {
            Images[key] = perImage;
            Write(ImagesPath, Images);
        }
    }

    public static TokenUsage? LastImage(string key)
    {
        lock (Gate) return Images.TryGetValue(key, out var tokens) ? tokens : null;
    }

    public static void Record(string channel, string model, Capability capability, int count, TokenUsage tokens = default)
    {
        if (count <= 0 && tokens.Total <= 0) return;
        lock (Gate)
        {
            var day = DateOnly.FromDateTime(DateTime.Now);
            var row = Rows.FirstOrDefault(r => r.Day == day && r.Channel == channel && r.Model == model && r.Capability == capability);
            if (row is null) Rows.Add(row = new UsageRow { Day = day, Channel = channel, Model = model, Capability = capability });
            row.Count += Math.Max(0, count);
            row.InputTokens += tokens.Input;
            row.OutputTokens += tokens.Output;
            row.MediaTokens += tokens.Media;
            Write(FilePath, Rows);
        }
    }

    /// <summary>Results per capability for a channel from a day on (inclusive).</summary>
    public static Dictionary<Capability, int> Since(string channel, DateOnly from)
    {
        lock (Gate) return Rows.Where(r => r.Channel == channel && r.Day >= from && r.Count > 0).GroupBy(r => r.Capability).ToDictionary(g => g.Key, g => g.Sum(r => r.Count));
    }

    /// <summary>Tokens per model for a channel from a day on (inclusive).</summary>
    public static Dictionary<string, TokenUsage> Tokens(string channel, DateOnly from)
    {
        lock (Gate)
            return Rows.Where(r => r.Channel == channel && r.Day >= from).GroupBy(r => r.Model)
                .ToDictionary(g => g.Key, g => g.Aggregate(default(TokenUsage), (sum, r) => sum + new TokenUsage(r.InputTokens, r.OutputTokens, r.MediaTokens)));
    }
}
