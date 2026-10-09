using System.Diagnostics;
using InfiniteCanvas.Core.Storage;
using Windows.Graphics.Imaging;

namespace InfiniteCanvas.App.Board;

/// <summary>
/// Local super-resolution with Real-ESRGAN-ncnn-vulkan from tools/realesrgan in the data folder:
/// runs on the GPU through Vulkan, nothing is uploaded. Every model upscales 4×; 2× is the 4× result scaled down.
/// </summary>
public static class Upscaler
{
    public static string Dir => Path.Combine(ProjectStore.Root, "tools", "realesrgan");
    private static string Exe => Path.Combine(Dir, "realesrgan-ncnn-vulkan.exe");

    private static readonly (string Name, string Label)[] Known =
    [
        ("realesrgan-x4plus", "写实（Real-ESRGAN 官方）"),
        ("high-fidelity-4x", "高保真写实（人像、皮肤与发丝）"),
        ("ultrasharp-4x", "锐利细节"),
        ("realesrgan-x4plus-anime", "插画 / 二次元"),
    ];

    /// <summary>Models whose files are installed, in menu order; empty when the tool is missing.</summary>
    public static List<(string Name, string Label)> Models() =>
        File.Exists(Exe) ? Known.Where(m => File.Exists(Path.Combine(Dir, "models", m.Name + ".param"))).ToList() : [];

    public static async Task<byte[]> RunAsync(string input, string model, int factor, CancellationToken ct)
    {
        var output = Path.Combine(Path.GetTempPath(), $"ic-upscale-{Guid.NewGuid():N}.png");
        try
        {
            var info = new ProcessStartInfo(Exe) { WorkingDirectory = Dir, CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true };
            foreach (var arg in new[] { "-i", input, "-o", output, "-n", model, "-s", "4", "-f", "png" }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            // Drain stderr (progress lines) so the pipe never fills up and blocks the tool.
            var log = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(ct); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            var lines = (await log).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(l => !l.EndsWith('%')).TakeLast(3);
            if (process.ExitCode != 0 || !File.Exists(output)) throw new InvalidOperationException($"本地放大失败：{string.Join(" ", lines)}");
            var bytes = await File.ReadAllBytesAsync(output, ct);
            return factor == 4 ? bytes : await ScaleAsync(bytes, factor / 4.0);
        }
        finally
        {
            try { File.Delete(output); } catch (IOException) { }
        }
    }

    private static async Task<byte[]> ScaleAsync(byte[] png, double scale)
    {
        using var input = new MemoryStream(png);
        var decoder = await BitmapDecoder.CreateAsync(input.AsRandomAccessStream());
        using var output = new MemoryStream();
        var encoder = await BitmapEncoder.CreateForTranscodingAsync(output.AsRandomAccessStream(), decoder);
        encoder.BitmapTransform.ScaledWidth = (uint)Math.Round(decoder.PixelWidth * scale);
        encoder.BitmapTransform.ScaledHeight = (uint)Math.Round(decoder.PixelHeight * scale);
        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        await encoder.FlushAsync();
        return output.ToArray();
    }
}
