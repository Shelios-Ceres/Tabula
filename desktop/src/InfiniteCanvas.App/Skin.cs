using InfiniteCanvas.App.Board;
using InfiniteCanvas.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace InfiniteCanvas.App;

/// <summary>
/// The look chosen in 设置 › 外观: the material of the floating surfaces (frosted, liquid, acrylic), the tint of the canvas ground, and the
/// window theme. The material and the ground work by changing the colours of the shared brushes in App.xaml's theme dictionaries, which
/// every ThemeResource user follows at once. Liquid glass keeps its own settings (<see cref="GlassSettings"/>) and its shader surfaces;
/// choosing it sets <see cref="GlassSettings.Enabled"/>, and turning that off in the tuning panel falls back to acrylic.
/// </summary>
public static class Skin
{
    /// <summary>Raised on the UI thread after the material, the ground or the theme changed.</summary>
    public static event Action? Changed;

    public static string Material => Appearance.Material(SettingsStore.Current.GlassMaterial);
    public static bool Liquid => Material == Appearance.Liquid;

    /// <summary>Set by 设置 › 外观 so the canvas it returns to opens the liquid glass tuning panel.</summary>
    public static bool OpenTuning { get; set; }

    /// <summary>Once at startup, after the window exists: the saved material, ground and theme, and the follow-ups to later changes.</summary>
    public static void Start()
    {
        GlassSettings.Current.Enabled = Liquid;
        ApplyBrushes();
        ApplyTheme();
        GlassSettings.Changed += OnGlassChanged;
    }

    public static void SetMaterial(string material)
    {
        material = Appearance.Material(material);
        SettingsStore.Current.GlassMaterial = material;
        SettingsStore.Save();
        // Liquid is the shader's switch; anything else turns it off. Update raises GlassSettings.Changed, which applies the brushes.
        GlassSettings.Update(g => g.Enabled = material == Appearance.Liquid);
    }

    public static void SetGround(string id)
    {
        SettingsStore.Current.CanvasGround = CanvasGrounds.Find(id).Id;
        SettingsStore.Save();
        ApplyBrushes();
        Changed?.Invoke();
    }

    private static void OnGlassChanged(bool scaleChanged)
    {
        // The tuning panel's own switch decides between liquid and acrylic too: keep the saved material in step with it.
        if (GlassSettings.Current.Enabled != Liquid)
        {
            SettingsStore.Current.GlassMaterial = GlassSettings.Current.Enabled ? Appearance.Liquid : Appearance.Acrylic;
            SettingsStore.Save();
        }
        ApplyBrushes();
        ApplyTheme();
        Changed?.Invoke();
    }

    /// <summary>The window theme (system, light or dark) is a setting of the glass file: it applies to every page, whatever the material.</summary>
    public static void ApplyTheme()
    {
        if (App.Window?.Content is not FrameworkElement root) return;
        var theme = GlassSettings.Current.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        if (root.RequestedTheme != theme) root.RequestedTheme = theme;
    }

    internal static Color Rgb(string hex, byte alpha = 255) => Color.FromArgb(alpha, Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..], 16));

    private static void ApplyBrushes()
    {
        var resources = Application.Current.Resources;
        var ground = CanvasGrounds.Find(SettingsStore.Current.CanvasGround);
        var frosted = Material == Appearance.Frosted;
        foreach (var dark in new[] { false, true })
        {
            var theme = (ResourceDictionary)resources.ThemeDictionaries[dark ? "Dark" : "Light"];
            // Still translucent, so the window's desktop acrylic shows faintly through the tint.
            ((SolidColorBrush)theme["CanvasBackgroundBrush"]).Color = Rgb(dark ? ground.Dark : ground.Light, 0xCC);
            // Frosted: thin white (or slate) glass with a white hairline rim; acrylic and liquid keep App.xaml's clearer values.
            Acrylic((AcrylicBrush)theme["GlassBrush"], frosted ? (dark ? "24262E" : "FFFFFF") : (dark ? "2C2C2C" : "FCFCFC"),
                frosted ? 0.34 : dark ? 0.15 : 0.1, frosted ? 0.4 : dark ? 0.7 : 0.6, dark ? Color.FromArgb(0xF2, 0x2C, 0x2C, 0x2C) : Color.FromArgb(0xF2, 0xF9, 0xF9, 0xF9));
            Acrylic((AcrylicBrush)theme["GlassThickBrush"], frosted ? (dark ? "24262E" : "FFFFFF") : (dark ? "2C2C2C" : "FCFCFC"),
                frosted ? (dark ? 0.62 : 0.58) : dark ? 0.4 : 0.3, frosted ? 0.6 : dark ? 0.85 : 0.78, dark ? Color.FromArgb(0xFF, 0x2C, 0x2C, 0x2C) : Color.FromArgb(0xFF, 0xF9, 0xF9, 0xF9));
            ((SolidColorBrush)theme["GlassStrokeBrush"]).Color = frosted ? (dark ? Color.FromArgb(0x1A, 255, 255, 255) : Color.FromArgb(0xB3, 255, 255, 255)) : dark ? Color.FromArgb(0x2E, 255, 255, 255) : Color.FromArgb(0x1F, 0, 0, 0);
        }
    }

    private static void Acrylic(AcrylicBrush brush, string tint, double tintOpacity, double luminosity, Color fallback)
    {
        brush.TintColor = Rgb(tint);
        brush.TintOpacity = tintOpacity;
        brush.TintLuminosityOpacity = luminosity;
        brush.FallbackColor = fallback;
    }
}
