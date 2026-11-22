using Tabula.Core.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Tabula.App;

/// <summary>The per-kind colours of App.xaml (Kind*Brush) for code: Win2D drawing and elements built at run time. Read again when ActualTheme changes.</summary>
public static class Palette
{
    private static bool HighContrast
    {
        get
        {
            try { return new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast; }
            catch { return false; }
        }
    }

    public static Color Kind(NodeKind kind, ElementTheme theme)
    {
        var key = HighContrast ? "HighContrast" : theme == ElementTheme.Dark ? "Dark" : "Light";
        return ((SolidColorBrush)((ResourceDictionary)Application.Current.Resources.ThemeDictionaries[key])[$"Kind{kind}Brush"]).Color;
    }

    public static SolidColorBrush Brush(NodeKind kind, ElementTheme theme) => new(Kind(kind, theme));
}
