using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace InfiniteCanvas.App;

/// <summary>
/// Windows 11 style motion on the composition layer: short fades with a slight scale, decelerating in and accelerating out.
/// Only Opacity, Scale and CenterPoint of an element's visual are animated, so XAML layout and Translation-based shadows stay untouched.
/// </summary>
public static class Motion
{
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(120), Normal = TimeSpan.FromMilliseconds(220);

    private static CompositionEasingFunction Decelerate(Compositor c) => c.CreateCubicBezierEasingFunction(new(0.1f, 0.9f), new(0.2f, 1f));
    private static CompositionEasingFunction Accelerate(Compositor c) => c.CreateCubicBezierEasingFunction(new(0.7f, 0f), new(1f, 0.5f));

    /// <summary>Scale around a point of the element given as fractions of its size (0.5, 0.5 is the centre), following later size changes.</summary>
    private static void Pivot(Visual visual, float x, float y)
    {
        var center = visual.Compositor.CreateExpressionAnimation($"Vector3(this.Target.Size.X * {x}, this.Target.Size.Y * {y}, 0)");
        visual.StartAnimation("CenterPoint", center);
    }

    private static CompositionAnimationGroup Enter(Compositor c, float scale)
    {
        var group = c.CreateAnimationGroup();
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, Decelerate(c));
        fade.Duration = Normal;
        var grow = c.CreateVector3KeyFrameAnimation();
        grow.Target = "Scale";
        grow.InsertKeyFrame(0, new Vector3(scale, scale, 1));
        grow.InsertKeyFrame(1, Vector3.One, Decelerate(c));
        grow.Duration = Normal;
        group.Add(fade);
        group.Add(grow);
        return group;
    }

    /// <summary>Fade and grow in whenever the element becomes visible; fade out before it collapses. <paramref name="pivotX"/> / <paramref name="pivotY"/> place the growth origin.</summary>
    public static void ShowHide(UIElement element, float pivotX = 0.5f, float pivotY = 0.5f, float scale = 0.96f)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var c = visual.Compositor;
        Pivot(visual, pivotX, pivotY);
        var hide = c.CreateAnimationGroup();
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.InsertKeyFrame(1, 0, Accelerate(c));
        fade.Duration = Fast;
        var shrink = c.CreateVector3KeyFrameAnimation();
        shrink.Target = "Scale";
        shrink.InsertKeyFrame(1, new Vector3(scale, scale, 1), Accelerate(c));
        shrink.Duration = Fast;
        hide.Add(fade);
        hide.Add(shrink);
        ElementCompositionPreview.SetImplicitShowAnimation(element, Enter(c, scale));
        ElementCompositionPreview.SetImplicitHideAnimation(element, hide);
    }

    /// <summary>One-off arrival of an element that was just created, such as a new node: fade in from a slightly smaller size.</summary>
    public static void Appear(UIElement element, float scale = 0.9f)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        Pivot(visual, 0.5f, 0.5f);
        visual.StartAnimationGroup(Enter(visual.Compositor, scale));
    }

    /// <summary>Content replaced in place (another item picked in a list): fade in while rising a little, like Windows 11 page content.</summary>
    public static void Refresh(UIElement element, float distance = 16)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var c = visual.Compositor;
        var group = c.CreateAnimationGroup();
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, Decelerate(c));
        fade.Duration = TimeSpan.FromMilliseconds(300);
        var rise = c.CreateVector3KeyFrameAnimation();
        rise.Target = "Translation";
        rise.InsertKeyFrame(0, new Vector3(0, distance, 0));
        rise.InsertKeyFrame(1, Vector3.Zero, Decelerate(c));
        rise.Duration = TimeSpan.FromMilliseconds(300);
        group.Add(fade);
        group.Add(rise);
        visual.StartAnimationGroup(group);
    }

    /// <summary>Fluent decelerate curve for tweens driven from code (0..1 in, 0..1 out).</summary>
    public static double EaseOut(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);
}
