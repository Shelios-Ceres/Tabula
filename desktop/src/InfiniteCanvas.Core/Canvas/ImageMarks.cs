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

    public static readonly string[] PerImage = [Mask, Selections, ConversationId, ConversationModel, ModelText, InputNote, SourceId, Stack, StackOpen];

    public static List<ImageSelection> SelectionsOf(CanvasNode node) =>
        node.Option(Selections) is { Length: > 0 } json ? Json.Deserialize<List<ImageSelection>>(json) ?? [] : [];

    public static void SetSelections(CanvasNode node, IReadOnlyCollection<ImageSelection> selections) =>
        node.SetOption(Selections, selections.Count == 0 ? null : Json.Serialize(selections));
}
