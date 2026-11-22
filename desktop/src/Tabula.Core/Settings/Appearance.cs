namespace Tabula.Core.Settings;

/// <summary>The materials of the canvas's floating surfaces (settings › 外观).</summary>
public static class Appearance
{
    public const string Frosted = "frosted", Liquid = "liquid", Acrylic = "acrylic";
    public static readonly string[] Materials = [Frosted, Liquid, Acrylic];

    /// <summary>An unknown or missing name (a hand-edited settings file) is the default, frosted.</summary>
    public static string Material(string? name) => Materials.Contains(name) ? name! : Frosted;
}

/// <summary>One tint of the canvas ground: its label and its colour in the light and the dark theme (RGB hex).</summary>
public sealed record GroundTint(string Id, string Label, string Light, string Dark);

/// <summary>The canvas ground tints of the redesign: pale teal by default, blue, warm and gray.</summary>
public static class CanvasGrounds
{
    public const string Default = "teal";

    public static readonly IReadOnlyList<GroundTint> All =
    [
        new("teal", "青", "D5E9E6", "121F1E"),
        new("blue", "蓝", "DFE7F3", "121824"),
        new("warm", "暖", "EEE6DA", "1F1A14"),
        new("gray", "灰", "ECEEF1", "16171B"),
    ];

    /// <summary>An unknown or missing name is the default tint.</summary>
    public static GroundTint Find(string? id) => All.FirstOrDefault(g => g.Id == id) ?? All[0];
}
