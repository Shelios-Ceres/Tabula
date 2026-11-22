using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Tabula.App.Board;

/// <summary>
/// Scales its child by the glass UI scale (<see cref="GlassSettings.UiScale"/>) and takes up the scaled size in layout, like WPF's
/// LayoutTransform: the child is measured and arranged at the unscaled size and drawn through a render transform, which hit testing follows.
/// </summary>
[ContentProperty(Name = nameof(Child))]
public sealed partial class GlassScale : Panel
{
    private static readonly List<WeakReference<GlassScale>> All = [];

    static GlassScale() => GlassSettings.Changed += scaleChanged =>
    {
        if (!scaleChanged) return;
        All.RemoveAll(r => !r.TryGetTarget(out _));
        foreach (var reference in All) if (reference.TryGetTarget(out var host)) host.InvalidateMeasure();
    };

    public GlassScale() => All.Add(new(this));

    private static double Factor => GlassSettings.Current.UiScale;

    public UIElement? Child
    {
        get => Children.Count > 0 ? Children[0] : null;
        set
        {
            Children.Clear();
            if (value is not null) Children.Add(value);
        }
    }

    /// <summary>Puts a user control's content inside a scale host.</summary>
    public static void Wrap(UserControl control)
    {
        var content = control.Content;
        control.Content = null;
        control.Content = new GlassScale { Child = content };
    }

    protected override Size MeasureOverride(Size available)
    {
        if (Child is not { } child) return new(0, 0);
        var s = Factor;
        child.Measure(new Size(available.Width / s, available.Height / s));
        return new Size(child.DesiredSize.Width * s, child.DesiredSize.Height * s);
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (Child is not { } child) return final;
        var s = Factor;
        child.Arrange(new Rect(0, 0, final.Width / s, final.Height / s));
        child.RenderTransform = new ScaleTransform { ScaleX = s, ScaleY = s };
        return final;
    }
}
