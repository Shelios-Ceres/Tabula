using System.Text.Json;

namespace Tabula.Core.Storage;

public sealed class PromptEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public DateTime Updated { get; set; } = DateTime.Now;

    /// <summary>Tags typed as one line, split on commas, Chinese commas, semicolons or spaces.</summary>
    public static List<string> ParseTags(string text) =>
        text.Split([',', '，', ';', '；', ' ', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
}

/// <summary>The local prompt library: one JSON file in the data folder, shared by all canvases.</summary>
public static class PromptStore
{
    private static string FilePath => Path.Combine(ProjectStore.Root, "prompts.json");
    private static List<PromptEntry>? _items;

    public static List<PromptEntry> Items => _items ??= Load();

    private static List<PromptEntry> Load()
    {
        if (!File.Exists(FilePath)) return [];
        try { return Json.Deserialize<List<PromptEntry>>(File.ReadAllText(FilePath)) ?? []; }
        catch (JsonException)
        {
            // Back up a corrupt file before starting empty, so the next save cannot overwrite the saved prompts.
            File.Copy(FilePath, $"{FilePath}.broken-{DateTime.Now:yyyyMMddHHmmss}", true);
            return [];
        }
    }

    public static void Save() => Json.WriteFile(FilePath, Items);

    public static IEnumerable<string> Tags => Items.SelectMany(p => p.Tags).Distinct().Order();

    /// <summary>Newest first; the query matches title, text or tags, ignoring case.</summary>
    public static IEnumerable<PromptEntry> Search(string query, string? tag) => Items
        .Where(p => tag is null || p.Tags.Contains(tag))
        .Where(p => query.Length == 0 || p.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Text.Contains(query, StringComparison.OrdinalIgnoreCase)
            || p.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)))
        .OrderByDescending(p => p.Updated);
}
