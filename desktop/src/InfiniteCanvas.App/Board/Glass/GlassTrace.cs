using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml.Media;

namespace InfiniteCanvas.App.Board;

/// <summary>
/// Diagnostic trace of the liquid glass timing, on only when INFINITE_CANVAS_GLASS_TRACE names a file: every composition frame,
/// viewport change and glass draw is written with a millisecond timestamp and the frame it fell in.
/// </summary>
public static class GlassTrace
{
    private static readonly string? FilePath = Environment.GetEnvironmentVariable("INFINITE_CANVAS_GLASS_TRACE") is { Length: > 0 } path ? path : null;
    private static readonly StringBuilder Buffer = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static bool _started;

    public static bool On => FilePath is not null;
    /// <summary>Composition frames seen since tracing started (CompositionTarget.Rendering).</summary>
    public static int Frame { get; private set; }

    public static void Start()
    {
        if (!On || _started) return;
        _started = true;
        CompositionTarget.Rendering += (_, _) =>
        {
            Frame++;
            Write("R");
            if (Frame % 60 == 0) Flush();
        };
    }

    public static void Write(string line)
    {
        if (!On) return;
        Buffer.Append(Clock.Elapsed.TotalMilliseconds.ToString("0.00")).Append(" f").Append(Frame).Append(' ').AppendLine(line);
    }

    public static void Flush()
    {
        if (!On || Buffer.Length == 0) return;
        try { File.AppendAllText(FilePath!, Buffer.ToString()); }
        catch (IOException) { }
        Buffer.Clear();
    }
}
