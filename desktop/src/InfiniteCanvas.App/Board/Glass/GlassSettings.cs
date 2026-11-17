using System.Text.Json;
using System.Text.Json.Serialization;
using InfiniteCanvas.Core.Storage;
using Microsoft.UI.Dispatching;

namespace InfiniteCanvas.App.Board;

/// <summary>
/// One glass material (light or dark), with glasswork's names and ranges (kubai087/glasswork, MIT, playground.js MATERIALS / LIMITS).
/// </summary>
public sealed class GlassMaterial
{
    public string Tint { get; set; } = "#FFFFFF";
    /// <summary>Scales the tint, 0–1.5; below 1 the tone and bevel thin with it.</summary>
    public double Opacity { get; set; } = 1;
    /// <summary>Tint opacity over a quiet backdrop (density 0) and over a busy one (density 1).</summary>
    public double Thin { get; set; } = 0.06;
    public double Dense { get; set; } = 0.22;
    public double Blur { get; set; } = 2;
    public double Saturation { get; set; } = 1.8;
    public double Brightness { get; set; } = 1.06;
    public double Contrast { get; set; } = 1;
    public double RimLight { get; set; } = 1;
    public double Bevel { get; set; } = 1;
    public double Shadow { get; set; } = 1;

    public static GlassMaterial LightDefaults() => new();
    public static GlassMaterial DarkDefaults() => new() { Tint = "#1E1E22", Thin = 0.16, Dense = 0.38, Saturation = 1.6, Brightness = 0.8 };
    public GlassMaterial Clone() => (GlassMaterial)MemberwiseClone();
}

/// <summary>
/// Settings of the liquid glass experiment, saved as liquid-glass.json in the data directory. Defaults are glasswork's (Lumno's) values;
/// the UI scale is ours.
/// </summary>
public sealed class GlassSettings
{
    /// <summary>Liquid glass (shader) or the acrylic of App.xaml.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Size of the canvas's glass toolbars and panels, text included.</summary>
    public double UiScale { get; set; } = DefaultScale;
    public string Preset { get; set; } = "Lumno";
    /// <summary>The window's theme while comparing (glasswork's theme switch): system, light or dark.</summary>
    public string Theme { get; set; } = "system";
    public bool Lens { get; set; } = true;
    public double Rim { get; set; } = 40;
    public double Edge { get; set; } = 1.1;
    public double Power { get; set; } = 2.6;
    public double Dispersion { get; set; } = 0.15;
    public double Radius { get; set; } = 28;
    /// <summary>CSS superellipse(k): the corner's exponent is 2^k, 1 is a circle.</summary>
    public double Smoothing { get; set; } = 1.25;
    /// <summary>theme, auto, dark or light.</summary>
    public string Ink { get; set; } = "theme";
    public bool AutoDensity { get; set; } = true;
    public double Density { get; set; } = 0.35;
    public bool Legible { get; set; } = true;
    public double Threshold { get; set; } = 0.5;
    public GlassMaterial Light { get; set; } = GlassMaterial.LightDefaults();
    public GlassMaterial Dark { get; set; } = GlassMaterial.DarkDefaults();

    public const double DefaultScale = 1.25;
    public static readonly string[] InkModes = ["theme", "auto", "dark", "light"];

    // ---------- Presets (glasswork playground.js PRESETS): only the lens and the material's body change ----------

    public static readonly string[] Presets = ["Lumno", "Clear glass", "Frosted", "Thick slab", "Vivid"];

    public void ApplyPreset(string name)
    {
        var (light, dark) = (GlassMaterial.LightDefaults(), GlassMaterial.DarkDefaults());
        (Lens, Rim, Edge, Power, Dispersion) = (true, 40, 1.1, 2.6, 0.15);
        switch (name)
        {
            case "Clear glass":
                (light.Thin, light.Dense, dark.Thin, dark.Dense) = (0, 0, 0, 0);
                break;
            case "Frosted":
                Lens = false;
                (light.Thin, light.Dense, light.Blur, light.Saturation, light.Contrast, light.Brightness, light.Bevel) = (0.42, 0.68, 30, 1.7, 0.6, 1.18, 0.4);
                (dark.Thin, dark.Dense, dark.Blur, dark.Saturation, dark.Contrast, dark.Brightness, dark.Bevel) = (0.24, 0.48, 30, 1.5, 0.82, 0.7, 0.4);
                break;
            case "Thick slab":
                (Rim, Edge, Power, Dispersion) = (64, 1.6, 2, 0.24);
                (light.RimLight, light.Bevel, dark.RimLight, dark.Bevel) = (1.4, 1.8, 1.4, 1.8);
                break;
            case "Vivid":
                Dispersion = 0.22;
                (light.Thin, light.Dense, light.Saturation) = (0.03, 0.14, 2.4);
                (dark.Thin, dark.Dense, dark.Saturation) = (0.12, 0.3, 2.2);
                break;
        }
        // The tint's colour and strength stay as they are.
        (light.Tint, light.Opacity, dark.Tint, dark.Opacity) = (Light.Tint, Light.Opacity, Dark.Tint, Dark.Opacity);
        (Light, Dark, Preset) = (light, dark, name);
    }

    // ---------- Current settings, persistence and change notification ----------

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    private static string FilePath => Path.Combine(ProjectStore.Root, "liquid-glass.json");
    private static DispatcherQueueTimer? _saveTimer;

    public static GlassSettings Current { get; private set; } = Load();
    /// <summary>Raised on the UI thread after any change; the scale flag says whether the UI scale changed.</summary>
    public static event Action<bool>? Changed;

    private static GlassSettings Load()
    {
        try { return File.Exists(FilePath) ? Parse(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public static GlassSettings? Parse(string json)
    {
        var settings = JsonSerializer.Deserialize<GlassSettings>(json, Json);
        if (settings is null) return null;
        settings.Light ??= GlassMaterial.LightDefaults();
        settings.Dark ??= GlassMaterial.DarkDefaults();
        settings.UiScale = Math.Clamp(settings.UiScale, 0.75, 2);
        if (!InkModes.Contains(settings.Ink)) settings.Ink = "theme";
        if (settings.Theme is not ("light" or "dark")) settings.Theme = "system";
        return settings;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Applies a change live and saves it shortly after the last one.</summary>
    public static void Update(Action<GlassSettings> change)
    {
        var scale = Current.UiScale;
        change(Current);
        Notify(scale != Current.UiScale);
    }

    public static void Replace(GlassSettings settings)
    {
        var scale = Current.UiScale;
        Current = settings;
        Notify(scale != Current.UiScale);
    }

    private static void Notify(bool scaleChanged)
    {
        Changed?.Invoke(scaleChanged);
        if (_saveTimer is null && DispatcherQueue.GetForCurrentThread() is { } queue)
        {
            _saveTimer = queue.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => Save();
        }
        _saveTimer?.Stop();
        _saveTimer?.Start();
    }

    private static void Save()
    {
        try { File.WriteAllText(FilePath, Current.ToJson()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.Log(ex); }
    }
}
