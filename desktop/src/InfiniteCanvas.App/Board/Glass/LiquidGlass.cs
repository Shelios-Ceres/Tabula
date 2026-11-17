using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using InfiniteCanvas.Core.Storage;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace InfiniteCanvas.App.Board;

/// <summary>
/// Liquid glass for the canvas's floating surfaces (experiment). A surface opts in through the attached <c>Surface</c> property, which the
/// GlassCapsule and FloatingPanel styles set. Each surface gets a Win2D control behind its content that re-renders what lies under it
/// (canvas colour, dot grid, cards and pictures, from <see cref="CanvasView.DrawGlassScene"/>), blurs it and runs <see cref="GlassShader"/>.
/// With <see cref="GlassSettings.Enabled"/> off the surface keeps its acrylic from App.xaml.
/// </summary>
public static class LiquidGlass
{
    public static readonly DependencyProperty SurfaceProperty =
        DependencyProperty.RegisterAttached("Surface", typeof(bool), typeof(LiquidGlass), new PropertyMetadata(false, OnSurfaceChanged));

    public static bool GetSurface(DependencyObject element) => (bool)element.GetValue(SurfaceProperty);
    public static void SetSurface(DependencyObject element, bool value) => element.SetValue(SurfaceProperty, value);

    private static readonly ConditionalWeakTable<Border, Pane> Panes = [];
    private static readonly List<WeakReference<Pane>> Live = [];

    /// <summary>The canvas whose content the glass shows; set by the open canvas view.</summary>
    public static CanvasView? Scene { get; set; }

    static LiquidGlass() => GlassSettings.Changed += _ =>
    {
        foreach (var pane in LivePanes()) pane.Apply();
    };

    private static void OnSurfaceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Border border && e.NewValue is true && !Panes.TryGetValue(border, out _))
        {
            var pane = new Pane(border);
            Panes.Add(border, pane);
            Live.Add(new(pane));
        }
    }

    private static IEnumerable<Pane> LivePanes()
    {
        Live.RemoveAll(r => !r.TryGetTarget(out _));
        return Live.Select(r => r.TryGetTarget(out var p) ? p : null).OfType<Pane>().ToList();
    }

    /// <summary>The scene under the glass changed (viewport, nodes, theme, a picture loaded): every visible surface re-renders.</summary>
    public static void Invalidate()
    {
        if (!GlassSettings.Current.Enabled) return;
        foreach (var pane in LivePanes()) pane.Redraw();
    }

    // ---------- Frame cost ----------

    /// <summary>CPU time of the last draw of each surface redrawn together with the latest one, summed: what one frame of panning costs.</summary>
    public static double FrameMs => LastFrame().Sum(p => p.LastMs);
    public static double AverageMs { get; private set; }
    public static double PeakMs { get; private set; }
    public static int DrawnPanes => LastFrame().Count;

    private static List<Pane> LastFrame()
    {
        var panes = LivePanes().ToList();
        var latest = panes.Count == 0 ? 0 : panes.Max(p => p.LastDraw);
        return panes.Where(p => p.LastDraw > latest - Stopwatch.Frequency / 4).ToList();
    }
    public static event Action? Measured;

    private static void Record(double ms)
    {
        AverageMs = AverageMs == 0 ? ms : AverageMs * 0.9 + ms * 0.1;
        PeakMs = Math.Max(PeakMs * 0.995, ms);
        Measured?.Invoke();
    }

    // ---------- Pictures: decoded once, downscaled ----------

    private const int PictureSize = 384;
    private static readonly Dictionary<string, CanvasBitmap?> Pictures = [];

    /// <summary>A small copy of the picture at <paramref name="path"/>, or null while it loads (the glass redraws once it has).</summary>
    public static CanvasBitmap? Picture(string path)
    {
        if (Pictures.TryGetValue(path, out var bitmap)) return bitmap;
        if (Pictures.Count > 160) Pictures.Clear();
        Pictures[path] = null;
        _ = LoadPictureAsync(path);
        return null;
    }

    private static async Task LoadPictureAsync(string path)
    {
        try
        {
            var device = CanvasDevice.GetSharedDevice();
            using var source = Media.IsSvg(path)
                ? await CanvasBitmap.LoadAsync(device, new MemoryStream(await ImageTools.RasterizeSvgAsync(await File.ReadAllBytesAsync(path))).AsRandomAccessStream())
                : await CanvasBitmap.LoadAsync(device, path);
            var size = source.SizeInPixels;
            var scale = Math.Min(1, PictureSize / (double)Math.Max(size.Width, size.Height));
            var small = new CanvasRenderTarget(device, (float)Math.Max(1, size.Width * scale), (float)Math.Max(1, size.Height * scale), 96);
            using (var ds = small.CreateDrawingSession()) ds.DrawImage(source, new Rect(0, 0, small.Size.Width, small.Size.Height));
            Pictures[path] = small;
            Invalidate();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            // Unreadable pictures stay a plain card under the glass.
        }
    }

    // ---------- glasswork adapt.js ----------

    private static readonly double[] Xs = [0.18, 0.34, 0.5, 0.66, 0.82];
    private static readonly double[] Ys = [0.24, 0.5, 0.76];
    private static double Clamp01(double v) => Math.Clamp(v, 0, 1);
    private static double Smoothstep(double v) { var a = Clamp01(v); return a * a * (3 - 2 * a); }
    private static double Luma(Color c) => (c.R * 0.299 + c.G * 0.587 + c.B * 0.114) / 255;

    private record struct Patch(double Luminance, double Texture, double R, double G, double B);

    /// <summary>15 points under the pane, dropping the two darkest and two brightest.</summary>
    private static Patch Sample(Color[] pixels, int width, int height)
    {
        var grid = Ys.SelectMany(fy => Xs.Select(fx => pixels[Math.Clamp((int)(fy * height), 0, height - 1) * width + Math.Clamp((int)(fx * width), 0, width - 1)]))
            .OrderBy(Luma).ToList();
        var texture = Luma(grid[^1]) - Luma(grid[0]);
        var kept = grid.Skip(2).Take(grid.Count - 4).ToList();
        return new(kept.Average(Luma), texture, kept.Average(c => (double)c.R), kept.Average(c => (double)c.G), kept.Average(c => (double)c.B));
    }

    /// <summary>Light ink below the threshold, keeping the previous ink within a band around it.</summary>
    private static bool LightInk(double luminance, bool? previous, double threshold, double band = 0.05)
    {
        var shift = previous == true ? band : previous == false ? -band : 0;
        return luminance < threshold + shift;
    }

    private static (double Density, double Keep) Adapt(Patch patch, bool lightInk)
    {
        var near = lightInk ? Smoothstep((patch.Luminance - 0.12) / 0.4) : Smoothstep((0.8 - patch.Luminance) / 0.35);
        var chroma = (Math.Max(patch.R, Math.Max(patch.G, patch.B)) - Math.Min(patch.R, Math.Min(patch.G, patch.B))) / 255;
        return (Clamp01(near * 0.55 + patch.Texture * 0.7 + chroma * 0.3), Math.Clamp(1 - chroma * 0.75, 0.45, 1));
    }

    private static double Channel(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    private static double Relative(double gray) => Channel(gray);
    private static double RelativeRgb(double r, double g, double b) => 0.2126 * Channel(r / 255) + 0.7152 * Channel(g / 255) + 0.0722 * Channel(b / 255);
    private static double Contrast(double a, double b) => (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);

    /// <summary>The least tint opacity at which the ink keeps 4.5:1 over the brightest (light ink) or darkest (dark ink) part of the patch.</summary>
    private static double TintFloor(Patch patch, bool lightInk, double tintLevel)
    {
        const double maxFloor = 0.9;
        var spread = Clamp01(patch.Texture) / 2;
        var backdrop = lightInk ? Math.Min(1, patch.Luminance + spread) : Math.Max(0, patch.Luminance - spread);
        var ink = lightInk ? RelativeRgb(245, 245, 247) : RelativeRgb(29, 29, 31);
        bool Reads(double alpha) => Contrast(ink, Relative(Math.Round(Clamp01(backdrop + (tintLevel - backdrop) * alpha) * 255) / 255)) >= 4.5;
        if (Reads(0)) return 0;
        if (!Reads(maxFloor)) return maxFloor;
        var (low, high) = (0.0, maxFloor);
        for (var i = 0; i < 16; i++)
        {
            var middle = (low + high) / 2;
            if (Reads(middle)) high = middle; else low = middle;
        }
        return high;
    }

    public static Color ParseColor(string hex, Color fallback)
    {
        var h = hex.TrimStart('#');
        return h.Length == 6 && uint.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out var v)
            ? Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v) : fallback;
    }

    /// <summary>
    /// One glass surface: a Win2D control placed behind the border's content, reaching past it by <see cref="ShadowMargin"/> for the shadow.
    /// </summary>
    private sealed class Pane
    {
        private const double ShadowMargin = 28;
        private readonly Border _border;
        private CanvasControl? _control;
        private CanvasRenderTarget? _scene, _probe;
        private PixelShaderEffect<GlassShader>? _effect;
        private GaussianBlurEffect? _blur;
        private Rect _lastBounds;
        private bool? _lightInk;
        private ThemeShadow? _shadow;
        private ContentPresenter? _ink;
        private long _lastAdapt;
        private DispatcherQueueTimer? _adaptTimer;
        private (double Density, double Keep, double Floor) _adapted = (0.35, 1, 0);
        public long LastDraw;
        public double LastMs;

        public Pane(Border border)
        {
            _border = border;
            border.Loaded += (_, _) => { Install(); Apply(); };
            border.LayoutUpdated += (_, _) => CheckBounds();
        }

        /// <summary>Moves the border's content into a grid with the glass control underneath it.</summary>
        private void Install()
        {
            if (_control is not null) return;
            _control = new CanvasControl { IsHitTestVisible = false, ClearColor = Microsoft.UI.Colors.Transparent };
            _control.Draw += Draw;
            var content = _border.Child;
            _border.Child = null;
            var grid = new Grid();
            grid.Children.Add(_control);
            if (content is not null) grid.Children.Add(content);
            // The presenter carries the ink to plain text, which otherwise takes its colour from the nearest control outside the glass.
            _ink = new ContentPresenter { Content = grid };
            _border.Child = _ink;
        }

        public void Apply()
        {
            if (_control is null) return;
            var on = GlassSettings.Current.Enabled;
            if (on)
            {
                var inset = _border.Padding;
                var (b, m) = (_border.BorderThickness, ShadowMargin);
                _control.Margin = new Thickness(-inset.Left - b.Left - m, -inset.Top - b.Top - m, -inset.Right - b.Right - m, -inset.Bottom - b.Bottom - m);
                _border.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                _border.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                _border.CornerRadius = new CornerRadius(0);
                // The shader draws the shadow; a ThemeShadow would double it.
                if (_border.Shadow is ThemeShadow shadow) _shadow = shadow;
                _border.Shadow = null;
                _control.Visibility = Visibility.Visible;
                Redraw();
            }
            else
            {
                _border.ClearValue(Border.BackgroundProperty);
                _border.ClearValue(Border.BorderBrushProperty);
                _border.ClearValue(Border.CornerRadiusProperty);
                _border.RequestedTheme = ElementTheme.Default;
                _ink?.ClearValue(ContentPresenter.ForegroundProperty);
                if (_shadow is not null) _border.Shadow = _shadow;
                _lightInk = null;
                _control.Visibility = Visibility.Collapsed;
            }
        }

        public void Redraw() => _control?.Invalidate();

        private void CheckBounds()
        {
            if (_control is null || !GlassSettings.Current.Enabled || Scene?.SceneRoot is not { } root || _border.Visibility != Visibility.Visible) return;
            Rect bounds;
            try { bounds = _control.TransformToVisual(root).TransformBounds(new Rect(0, 0, _control.ActualWidth, _control.ActualHeight)); }
            catch (ArgumentException) { return; }
            if (bounds == _lastBounds) return;
            _lastBounds = bounds;
            var scale = (float)Math.Clamp(bounds.Width / Math.Max(1, _control.ActualWidth), 0.5, 3);
            if (Math.Abs(_control.DpiScale - scale) > 0.01) _control.DpiScale = scale;
            _control.Invalidate();
        }

        private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            var settings = GlassSettings.Current;
            if (!settings.Enabled || Scene is not { SceneRoot: { } root } scene) return;
            var start = Stopwatch.GetTimestamp();
            var (w, h) = ((float)sender.ActualWidth, (float)sender.ActualHeight);
            if (w <= 2 * ShadowMargin || h <= 2 * ShadowMargin) return;
            Rect bounds;
            try { bounds = sender.TransformToVisual(root).TransformBounds(new Rect(0, 0, w, h)); }
            catch (ArgumentException) { return; }
            _lastBounds = bounds;
            var k = bounds.Width / w;

            // What lies under the pane, in the control's own DIPs (scaled panes see the scene shrunk back to their unscaled units).
            if (_scene is null || _scene.Size.Width != w || _scene.Size.Height != h || _scene.Dpi != sender.Dpi)
            {
                _scene?.Dispose();
                _scene = new CanvasRenderTarget(sender, w, h);
            }
            using (var ds = _scene.CreateDrawingSession())
            {
                ds.Transform = Matrix3x2.CreateTranslation((float)-bounds.X, (float)-bounds.Y) * Matrix3x2.CreateScale((float)(1 / k));
                scene.DrawGlassScene(ds, bounds);
            }

            var m = (float)ShadowMargin;
            var (bw, bh) = (w - 2 * m, h - 2 * m);
            var dark = Material(settings, scene, bw, bh, m);
            var material = dark ? settings.Dark : settings.Light;
            var veil = Math.Min(1, material.Opacity);
            var tintA = Math.Min(0.95, Math.Max(_adapted.Floor, (material.Thin + (material.Dense - material.Thin) * (settings.AutoDensity ? _adapted.Density : settings.Density)) * material.Opacity));
            var tint = ParseColor(material.Tint, dark ? Color.FromArgb(255, 30, 30, 34) : Microsoft.UI.Colors.White);

            // glasswork lens.js: the rim narrows to 1/3.5 of the short side, and surfaces under 32 px get no lens.
            var px = sender.Dpi / 96f;
            var rim = (float)Math.Round(Math.Max(4, Math.Min(settings.Rim, Math.Min(bw, bh) / 3.5)));
            var lens = settings.Lens && Math.Min(bw, bh) >= 32;
            var radius = (float)Math.Min(settings.Radius, Math.Min(bw, bh) / 2);
            var corner = radius >= bh / 2 - 0.5f || radius >= bw / 2 - 0.5f ? 2f : (float)Math.Pow(2, settings.Smoothing);

            _blur ??= new GaussianBlurEffect { BorderMode = EffectBorderMode.Hard, Optimization = EffectOptimization.Speed };
            _blur.Source = _scene;
            _blur.BlurAmount = (float)material.Blur;
            _effect ??= new PixelShaderEffect<GlassShader>();
            _effect.Sources[0] = _blur;
            _effect.ConstantBuffer = new GlassShader(
                new float4(m * px, m * px, bw * px, bh * px),
                new float4(radius * px, corner, rim * px, lens ? (float)settings.Edge : 0),
                new float4((float)material.Saturation, (float)(1 + (material.Contrast - 1) * veil), (float)(1 + (material.Brightness - 1) * veil), (float)_adapted.Keep),
                new float4(tint.R / 255f, tint.G / 255f, tint.B / 255f, (float)tintA),
                new float4((float)material.RimLight, (float)(material.Bevel * veil), (float)material.Shadow, px),
                new float4((float)settings.Power, lens ? (float)settings.Dispersion : 0, dark ? 1 : 0, 0));
            args.DrawingSession.DrawImage(_effect);

            LastMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            LastDraw = Stopwatch.GetTimestamp();
            Record(LastMs);
        }

        /// <summary>
        /// Reads the scene under the pane (at most every 100 ms, with a catch-up redraw), picks the ink and the material and adapts the
        /// tint density, the saturation kept and the legibility floor. Returns true for the dark material.
        /// </summary>
        private bool Material(GlassSettings settings, CanvasView scene, float bw, float bh, float m)
        {
            var themeDark = scene.ActualTheme == ElementTheme.Dark;
            var needsPatch = settings.Ink == "auto" || settings.AutoDensity || settings.Legible;
            if (needsPatch && _scene is not null)
            {
                var now = Stopwatch.GetTimestamp();
                if (now - _lastAdapt >= Stopwatch.Frequency / 10)
                {
                    _lastAdapt = now;
                    _probe ??= new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 20, 12, 96);
                    using (var ds = _probe.CreateDrawingSession()) ds.DrawImage(_scene, new Rect(0, 0, 20, 12), new Rect(m, m, bw, bh));
                    var patch = Sample(_probe.GetPixelColors(), 20, 12);
                    var light = settings.Ink switch
                    {
                        "auto" => LightInk(patch.Luminance, _lightInk, settings.Threshold),
                        "light" => true,
                        "dark" => false,
                        _ => themeDark,
                    };
                    var (density, keep) = settings.AutoDensity ? Adapt(patch, light) : (settings.Density, 1);
                    var material = light ? settings.Dark : settings.Light;
                    var floor = settings.Legible ? TintFloor(patch, light, Luma(ParseColor(material.Tint, Microsoft.UI.Colors.White))) : 0;
                    _adapted = (density, keep, floor);
                    SetInk(light, settings);
                }
                else
                {
                    if (_adaptTimer is null)
                    {
                        _adaptTimer = _border.DispatcherQueue.CreateTimer();
                        _adaptTimer.Interval = TimeSpan.FromMilliseconds(120);
                        _adaptTimer.IsRepeating = false;
                        _adaptTimer.Tick += (_, _) => Redraw();
                    }
                    if (!_adaptTimer.IsRunning) _adaptTimer.Start();
                }
            }
            else
            {
                _adapted = (settings.Density, 1, 0);
                SetInk(settings.Ink switch { "light" => true, "dark" => false, _ => themeDark }, settings);
            }
            return _lightInk ?? themeDark;
        }

        /// <summary>Light ink means dark glass: the content follows through RequestedTheme, so every theme brush inside switches with it.</summary>
        private void SetInk(bool light, GlassSettings settings)
        {
            _lightInk = light;
            var theme = settings.Ink == "theme" ? ElementTheme.Default : light ? ElementTheme.Dark : ElementTheme.Light;
            if (_border.RequestedTheme == theme) return;
            _border.RequestedTheme = theme;
            // glasswork's inks: rgba(255,255,255,.94) on dark glass, rgba(0,0,0,.86) on light glass.
            if (theme == ElementTheme.Default) _ink?.ClearValue(ContentPresenter.ForegroundProperty);
            else if (_ink is not null) _ink.Foreground = new SolidColorBrush(light ? Color.FromArgb(240, 255, 255, 255) : Color.FromArgb(219, 0, 0, 0));
        }
    }
}
