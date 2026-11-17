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
        Scene?.InvalidateTopFade();
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

    private const int PictureSize = 384, ThumbSize = 16;
    private static readonly Dictionary<string, CanvasBitmap?> Pictures = [];
    private static readonly Dictionary<string, Color[]> Thumbs = [];

    /// <summary>
    /// A 16 × 16 colour grid of the picture (stretched), read back once when it loads, so the adaptive tint samples pictures on the CPU
    /// instead of reading the GPU every few frames.
    /// </summary>
    public static Color[]? Thumb(string path) => Thumbs.GetValueOrDefault(path);
    public const int ThumbSide = ThumbSize;

    /// <summary>A small copy of the picture at <paramref name="path"/>, or null while it loads (the glass redraws once it has).</summary>
    public static CanvasBitmap? Picture(string path)
    {
        if (Pictures.TryGetValue(path, out var bitmap)) return bitmap;
        if (Pictures.Count > 160) { Pictures.Clear(); Thumbs.Clear(); }
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
            using (var thumb = new CanvasRenderTarget(device, ThumbSize, ThumbSize, 96))
            {
                using (var ds = thumb.CreateDrawingSession()) ds.DrawImage(small, new Rect(0, 0, ThumbSize, ThumbSize), new Rect(0, 0, small.Size.Width, small.Size.Height));
                Thumbs[path] = thumb.GetPixelColors();
            }
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
    private static Patch Sample(IEnumerable<Color> points)
    {
        var grid = points.OrderBy(Luma).ToList();
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
        private CanvasRenderTarget? _scene;
        private PixelShaderEffect<GlassShader>? _effect;
        private GaussianBlurEffect? _blur;
        private BorderEffect? _fadeClamp;
        private PixelShaderEffect<FadeShader>? _fade;
        private Rect _lastBounds;
        private bool? _lightInk;
        private ThemeShadow? _shadow;
        private ContentPresenter? _ink;
        private long _lastEase;
        private DispatcherQueueTimer? _easeTimer;
        /// <summary>Eased values: tint density, saturation kept, legibility floor, and how dark the material is (0 light, 1 dark).</summary>
        private (double Density, double Keep, double Floor, double Dark) _adapted = (0.35, 1, 0, 0);
        private double? _luma;
        public long LastDraw;
        public double LastMs;
        private static int _count;
        private readonly int _id = ++_count;
        private int _draws;
        private int _reach = -1;
        private double _adaptMs;
        private CanvasRenderTarget? _gpuProbe;

        public Pane(Border border)
        {
            _border = border;
            border.Loaded += (_, _) => { Install(); Apply(); };
            border.LayoutUpdated += (_, _) => CheckBounds();
        }

        /// <summary>Moves the border's content into a grid with the glass control underneath it.</summary>
        /// <summary>FloatingPanel surfaces (text-heavy) versus GlassCapsule toolbars.</summary>
        private bool IsPanel => _border.Style == (Style)Application.Current.Resources["FloatingPanel"];
        private Thickness? _basePadding;

        private void Install()
        {
            if (_control is not null) return;
            if (IsPanel) AddPanelResources(_border.Resources);
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
                // Panels: the content moves in as far as the rounded corner cuts in at 45 degrees, so nothing sits in the curve.
                _basePadding ??= _border.Padding;
                var basePad = _basePadding.Value;
                var cut = IsPanel ? Math.Min(24, GlassSettings.Current.Radius * (1 - Math.Pow(2, -1 / Math.Pow(2, GlassSettings.Current.Smoothing))) * 0.9) : 0;
                _border.Padding = new Thickness(basePad.Left + cut, basePad.Top + cut * 0.6, basePad.Right + cut, basePad.Bottom + cut * 0.6);
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
                if (_basePadding is { } padding) _border.Padding = padding;
                _border.RequestedTheme = ElementTheme.Default;
                _ink?.ClearValue(ContentPresenter.ForegroundProperty);
                if (_shadow is not null) _border.Shadow = _shadow;
                _lightInk = null;
                _control.Visibility = Visibility.Collapsed;
            }
        }

        public void Redraw()
        {
            GlassTrace.Write($"I p{_id}");
            _control?.Invalidate();
        }

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
            GlassTrace.Write($"d p{_id}");
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
            var tScene = Stopwatch.GetTimestamp();
            using (var ds = _scene.CreateDrawingSession())
            {
                ds.Transform = Matrix3x2.CreateTranslation((float)-bounds.X, (float)-bounds.Y) * Matrix3x2.CreateScale((float)(1 / k));
                scene.DrawGlassScene(ds, bounds);
            }

            var sceneMs = Stopwatch.GetElapsedTime(tScene).TotalMilliseconds;
            var m = (float)ShadowMargin;
            var (bw, bh) = (w - 2 * m, h - 2 * m);
            var inset = ShadowMargin * k;
            Material(settings, scene, new Rect(bounds.X + inset, bounds.Y + inset, Math.Max(1, bounds.Width - 2 * inset), Math.Max(1, bounds.Height - 2 * inset)));
            // Between the two materials while the ink changes, as glass.css glides them.
            var mix = _adapted.Dark;
            var dark = mix > 0.5;
            var (lm, dm) = (settings.Light, settings.Dark);
            double Mix(Func<GlassMaterial, double> pick) => pick(lm) + (pick(dm) - pick(lm)) * mix;
            var opacity = Mix(x => x.Opacity);
            var veil = Math.Min(1, opacity);
            var tintA = Math.Min(0.95, Math.Max(_adapted.Floor, (Mix(x => x.Thin) + (Mix(x => x.Dense) - Mix(x => x.Thin)) * _adapted.Density) * opacity));
            var (lt, dt) = (ParseColor(lm.Tint, Microsoft.UI.Colors.White), ParseColor(dm.Tint, Color.FromArgb(255, 30, 30, 34)));
            var tint = (R: lt.R + (dt.R - lt.R) * mix, G: lt.G + (dt.G - lt.G) * mix, B: lt.B + (dt.B - lt.B) * mix);

            // glasswork lens.js: the rim narrows to 1/3.5 of the short side, and surfaces under 32 px get no lens.
            var px = sender.Dpi / 96f;
            var rim = (float)Math.Round(Math.Max(4, Math.Min(settings.Rim, Math.Min(bw, bh) / 3.5)));
            var lens = settings.Lens && Math.Min(bw, bh) >= 32;
            // Toolbars are capsules; panels take the radius setting.
            var radius = IsPanel ? (float)Math.Min(settings.Radius, Math.Min(bw, bh) / 2) : Math.Min(bw, bh) / 2;
            var corner = radius >= bh / 2 - 0.5f || radius >= bw / 2 - 0.5f ? 2f : (float)Math.Pow(2, settings.Smoothing);
            if (IsPanel) tintA = Math.Min(0.95, Math.Max(tintA, settings.PanelTint));

            _blur ??= new GaussianBlurEffect { BorderMode = EffectBorderMode.Hard, Optimization = EffectOptimization.Speed };
            // Over the top fade, the glass refracts the faded canvas, as the band shows around it.
            var fade = scene.GlassFade;
            if (fade.On && bounds.Y + fade.Above < fade.Height)
            {
                _fadeClamp ??= new BorderEffect { ExtendX = CanvasEdgeBehavior.Clamp, ExtendY = CanvasEdgeBehavior.Clamp };
                _fadeClamp.Source = _scene;
                _fade ??= new PixelShaderEffect<FadeShader> { TransformMapper = ComputeSharp.D2D1.Interop.D2D1DrawTransformMapper<FadeShader>.Inflate(200) };
                _fade.Sources[0] = _fadeClamp;
                var pxLocal = sender.Dpi / 96f;
                _fade.ConstantBuffer = new FadeShader(
                    new float4((float)fade.Height, (float)settings.FadeBlur, (float)settings.FadeWash, (float)settings.FadeOpacity),
                    new float4(fade.Background.R / 255f, fade.Background.G / 255f, fade.Background.B / 255f, 1),
                    new float4((float)(k / pxLocal), (float)(bounds.Y + fade.Above), 1, 0));
                _blur.Source = _fade;
            }
            else _blur.Source = _scene;
            _blur.BlurAmount = (float)(Mix(x => x.Blur) + (IsPanel ? settings.PanelBlur : 0));
            _effect ??= new PixelShaderEffect<GlassShader>();
            // The bend reads up to edge × rim × (1 + dispersion) inward; Direct2D must hand each tile that much input around it, or the
            // rim samples transparent pixels at tile edges (dark patches that came and went as the canvas moved).
            var reach = (int)Math.Ceiling(settings.Edge * rim * (1 + settings.Dispersion) * px) + 4;
            if (reach != _reach) { _reach = reach; _effect.TransformMapper = ComputeSharp.D2D1.Interop.D2D1DrawTransformMapper<GlassShader>.Inflate(reach); }
            _effect.Sources[0] = _blur;
            _effect.ConstantBuffer = new GlassShader(
                new float4(m * px, m * px, bw * px, bh * px),
                new float4(radius * px, corner, rim * px, lens ? (float)settings.Edge : 0),
                new float4((float)Mix(x => x.Saturation), (float)(1 + (Mix(x => x.Contrast) - 1) * veil), (float)(1 + (Mix(x => x.Brightness) - 1) * veil), (float)_adapted.Keep),
                new float4((float)(tint.R / 255), (float)(tint.G / 255), (float)(tint.B / 255), (float)tintA),
                new float4((float)Mix(x => x.RimLight), (float)(Mix(x => x.Bevel) * veil), (float)Mix(x => x.Shadow), px),
                new float4((float)settings.Power, lens ? (float)settings.Dispersion : 0, (float)mix, 0));
            var tDraw = Stopwatch.GetTimestamp();
            args.DrawingSession.DrawImage(_effect);
            var drawMs = Stopwatch.GetElapsedTime(tDraw).TotalMilliseconds;

            LastMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (GlassTrace.On)
            {
                GlassTrace.Write($"D p{_id} vx={scene.Viewport.X:0.0} bx={bounds.X:0.0} cpu={LastMs:0.00} scene={sceneMs:0.00} fx={drawMs:0.00} adapt={_adaptMs:0.00} tint={tintA:0.000} dens={_adapted.Density:0.000} floor={_adapted.Floor:0.000} dark={dark} size={w:0}x{h:0}");
                _adaptMs = 0;
                // Every 15th draw: the GPU cost of this pane, timed by rendering the same graph offscreen and reading one pixel back.
                if (++_draws % 15 == 0 && Environment.GetEnvironmentVariable("INFINITE_CANVAS_GLASS_TRACE_GPU") is not null)
                {
                    var gpu = Stopwatch.GetTimestamp();
                    if (_gpuProbe is null || _gpuProbe.Size != _scene.Size || _gpuProbe.Dpi != _scene.Dpi) { _gpuProbe?.Dispose(); _gpuProbe = new CanvasRenderTarget(sender, w, h); }
                    using (var probe = _gpuProbe.CreateDrawingSession()) probe.DrawImage(_effect);
                    _gpuProbe.GetPixelColors(0, 0, 1, 1);
                    GlassTrace.Write($"G p{_id} gpu={Stopwatch.GetElapsedTime(gpu).TotalMilliseconds:0.00}");
                }
            }
            LastDraw = Stopwatch.GetTimestamp();
            Record(LastMs);
        }

        /// <summary>
        /// glasswork adapt.js on every draw: 15 points under the pane (<paramref name="box"/>, canvas-area DIPs) read on the CPU from the
        /// document and the picture thumbnails, then the ink, tint density, saturation kept and legibility floor. The numbers and the
        /// light / dark material ease towards their targets (time constant 0.2 s, as glass.css transitions them) instead of jumping,
        /// which made the tint flicker whenever a picture crossed the pane.
        /// </summary>
        private void Material(GlassSettings settings, CanvasView scene, Rect box)
        {
            var start = Stopwatch.GetTimestamp();
            var themeDark = scene.ActualTheme == ElementTheme.Dark;
            var first = _lastEase == 0;
            var step = first ? 1 : 1 - Math.Exp(-(start - _lastEase) / (double)Stopwatch.Frequency / 0.2);
            _lastEase = start;
            bool light;
            (double Density, double Keep, double Floor) target;
            if (settings.Ink == "auto" || settings.AutoDensity || settings.Legible)
            {
                var patch = Sample(Ys.SelectMany(fy => Xs.Select(fx => scene.GlassColor(new Windows.Foundation.Point(box.X + box.Width * fx, box.Y + box.Height * fy)))));
                _luma = _luma is { } luma && !first ? luma + (patch.Luminance - luma) * step : patch.Luminance;
                light = settings.Ink switch { "auto" => LightInk(_luma.Value, _lightInk, settings.Threshold), "light" => true, "dark" => false, _ => themeDark };
                var (density, keep) = settings.AutoDensity ? Adapt(patch, light) : (settings.Density, 1);
                var floor = settings.Legible ? TintFloor(patch, light, Luma(ParseColor((light ? settings.Dark : settings.Light).Tint, Microsoft.UI.Colors.White))) : 0;
                target = (density, keep, floor);
            }
            else
            {
                light = settings.Ink switch { "light" => true, "dark" => false, _ => themeDark };
                target = (settings.Density, 1, 0);
            }
            var dark = light ? 1.0 : 0.0;
            double Ease(double from, double to) => first ? to : from + (to - from) * step;
            var next = (Ease(_adapted.Density, target.Density), Ease(_adapted.Keep, target.Keep), Ease(_adapted.Floor, target.Floor), Ease(_adapted.Dark, dark));
            var moving = Math.Abs(next.Item1 - target.Density) + Math.Abs(next.Item2 - target.Keep) + Math.Abs(next.Item3 - target.Floor) + Math.Abs(next.Item4 - dark) > 0.004;
            _adapted = moving ? next : (target.Density, target.Keep, target.Floor, dark);
            SetInk(light, settings);
            _adaptMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (!moving) return;
            // Keep easing after the canvas stops: one more frame shortly.
            if (_easeTimer is null)
            {
                _easeTimer = _border.DispatcherQueue.CreateTimer();
                _easeTimer.Interval = TimeSpan.FromMilliseconds(16);
                _easeTimer.IsRepeating = false;
                _easeTimer.Tick += (_, _) => Redraw();
            }
            if (!_easeTimer.IsRunning) _easeTimer.Start();
        }

        /// <summary>
        /// Controls inside glass panels: rounder corners to match the glass, and inputs that let the glass through instead of
        /// sitting on it as opaque Fluent boxes.
        /// </summary>
        private static void AddPanelResources(ResourceDictionary resources)
        {
            resources["ControlCornerRadius"] = new CornerRadius(16);
            resources["OverlayCornerRadius"] = new CornerRadius(20);
            resources.ThemeDictionaries["Light"] = Wells(dark: false);
            resources.ThemeDictionaries["Dark"] = Wells(dark: true);
        }

        /// <summary>
        /// Inputs and buttons as small glass wells: a faint fill of the ink's opposite, brighter while hovered or focused, and a rim
        /// light (bright along the top, a soft shade along the bottom) instead of Fluent's opaque boxes with a dark underline.
        /// </summary>
        private static ResourceDictionary Wells(bool dark)
        {
            var dictionary = new ResourceDictionary();
            Brush Fill(byte alpha) => new SolidColorBrush(dark ? Color.FromArgb(alpha, 255, 255, 255) : Color.FromArgb(alpha, 255, 255, 255));
            Brush Rim(byte top, byte bottom, Color? tint = null) => new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(top, 255, 255, 255), Offset = 0 },
                    new GradientStop { Color = tint is { } t ? Color.FromArgb(bottom, t.R, t.G, t.B) : Color.FromArgb(bottom, 0, 0, 0), Offset = 1 },
                },
            };
            var accent = Application.Current.Resources.TryGetValue("SystemAccentColor", out var value) && value is Color c ? c : Color.FromArgb(255, 15, 108, 189);
            var (rest, hover, focus) = dark ? ((byte)22, (byte)34, (byte)44) : ((byte)70, (byte)120, (byte)160);
            var (rimTop, rimBottom) = dark ? ((byte)70, (byte)60) : ((byte)230, (byte)26);
            foreach (var prefix in new[] { "TextControl", "ComboBox", "Button", "ToggleButton", "RepeatButton" })
            {
                dictionary[prefix + "Background"] = Fill(rest);
                dictionary[prefix + "BackgroundPointerOver"] = Fill(hover);
                dictionary[prefix + "BackgroundPressed"] = Fill(rest);
                dictionary[prefix + "BackgroundFocused"] = Fill(focus);
                dictionary[prefix + "BorderBrush"] = Rim(rimTop, rimBottom);
                dictionary[prefix + "BorderBrushPointerOver"] = Rim(rimTop, rimBottom);
                dictionary[prefix + "BorderBrushPressed"] = Rim(rimTop, rimBottom);
                dictionary[prefix + "BorderBrushFocused"] = Rim(rimTop, 200, accent);
            }
            // The accent button keeps its colour as tinted glass with the same rim light.
            dictionary["AccentButtonBorderBrush"] = Rim(150, 60);
            dictionary["AccentButtonBorderBrushPointerOver"] = Rim(170, 60);
            dictionary["ComboBoxDropDownBackground"] = new SolidColorBrush(dark ? Color.FromArgb(240, 44, 44, 48) : Color.FromArgb(245, 250, 250, 252));
            return dictionary;
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
