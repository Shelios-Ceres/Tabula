namespace InfiniteCanvas.Core.Canvas;

/// <summary>A point or box on an image, in fractions (0–1) of the image size so it survives any display zoom.</summary>
public sealed record ImageSelection(string Kind, double X, double Y, double X2 = 0, double Y2 = 0)
{
    public const string Point = "point", Box = "box";
}

/// <summary>Node option keys for per-image edit marks and conversation state; they belong to one image and are never copied to outputs.</summary>
public static class NodeKeys
{
    /// <summary>Asset name of a PNG mask the size of the image: transparent pixels mark the area to repaint.</summary>
    public const string Mask = "mask";
    /// <summary>Asset name of a transparent PNG the size of the image with circles and sketches showing where to change it (GPT Image markup edits).</summary>
    public const string Markup = "markup";
    /// <summary>JSON list of <see cref="ImageSelection"/> for point / box editing.</summary>
    public const string Selections = "selections";
    /// <summary>Server-side conversation (OpenAI response / Gemini interaction) that produced this image.</summary>
    public const string ConversationId = "conversationId";
    public const string ConversationModel = "conversationModel";
    /// <summary>Text the model returned alongside the image.</summary>
    public const string ModelText = "modelText";
    /// <summary>How references were shrunk to fit the vendor's limits for the run that produced this node.</summary>
    public const string InputNote = "inputNote";

    /// <summary>The node whose run produced this output; retries go through it.</summary>
    public const string SourceId = "sourceId";

    /// <summary>Batch outputs shown as one stack (see <see cref="Stacks"/>); the ID is shared by all members, open marks a spread-out stack.</summary>
    public const string Stack = "stack";
    public const string StackOpen = "stackOpen";

    /// <summary>How the next batch from this node is laid out (stack, row, grid) and the gap between its members; a property of the run, so outputs never inherit it.</summary>
    public const string Layout = "layout", Gap = "gap";

    /// <summary>JSON <see cref="ComposeSpec"/> of the composition that made this image, kept for "re-compose with the last settings"; it describes this image only, so outputs never inherit it.</summary>
    public const string Compose = "compose";

    public static readonly string[] PerImage = [Mask, Markup, Selections, ConversationId, ConversationModel, ModelText, InputNote, SourceId, Stack, StackOpen, Layout, Gap, Compose];

    /// <summary>Keys that describe one picture's pixels or the server conversation that made it; dropped when the node gets another file.</summary>
    public static readonly string[] OfPixels = [Mask, Markup, Selections, ConversationId, ConversationModel, ModelText, InputNote];

    public static void ForgetPixels(CanvasNode node)
    {
        foreach (var key in OfPixels) node.SetOption(key, null);
    }

    /// <summary>
    /// Options for an output of <paramref name="from"/>: its settings without the per-image keys and the one-off layer split. The output keeps what says
    /// where it belongs (its source and its batch), so a retried member stays in its stack and can be retried again.
    /// </summary>
    public static Dictionary<string, string> OutputOptions(CanvasNode from, CanvasNode to) =>
        from.Options.Where(o => !PerImage.Contains(o.Key) && o.Key != "layers")
            .Concat(to.Options.Where(o => o.Key is SourceId or Stack or StackOpen))
            .ToDictionary(o => o.Key, o => o.Value);

    public static List<ImageSelection> SelectionsOf(CanvasNode node) =>
        node.Option(Selections) is { Length: > 0 } json ? Json.Deserialize<List<ImageSelection>>(json) ?? [] : [];

    public static void SetSelections(CanvasNode node, IReadOnlyCollection<ImageSelection> selections) =>
        node.SetOption(Selections, selections.Count == 0 ? null : Json.Serialize(selections));
}
