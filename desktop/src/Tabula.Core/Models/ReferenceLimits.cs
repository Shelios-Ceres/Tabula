using Tabula.Core.Settings;

namespace Tabula.Core.Models;

/// <summary>A vendor's documented ceiling for reference images: per image, per request (all inline bytes), and pixels.</summary>
public sealed record ReferenceLimit(long MaxBytes, long TotalBytes = long.MaxValue, long MaxPixels = long.MaxValue);

/// <summary>
/// Reference images are only shrunk when they break these documented limits, and only to just under them (95%),
/// so a picture that fits is always sent untouched.
/// </summary>
public static class ReferenceLimits
{
    private const long MB = 1_000_000, MiB = 1024 * 1024;
    public const double Margin = 0.95;

    public static ReferenceLimit For(Vendor vendor) => vendor switch
    {
        // Image API edits: each image under 50MB.
        Vendor.OpenAI => new(50 * MB),
        // Interactions: inline data counts toward a 100MB request (the Files API is only needed above that), and base64 adds a third.
        Vendor.Google => new(long.MaxValue, TotalBytes: 100 * MB * 3 / 4),
        // Seedream: each image up to 30MB and 6000×6000 pixels.
        Vendor.Ark => new(30 * MB, MaxPixels: 6000L * 6000),
        // Image understanding / editing: 20MiB per image.
        Vendor.XAI => new(20 * MiB),
        // DashScope image input: each image up to 10MB.
        Vendor.Qwen => new(10 * MB),
        _ => new(long.MaxValue),
    };

    /// <summary>Byte budget per image for a request carrying <paramref name="count"/> references.</summary>
    public static long Budget(ReferenceLimit limit, int count)
    {
        var share = limit.TotalBytes == long.MaxValue ? limit.MaxBytes : Math.Min(limit.MaxBytes, limit.TotalBytes / Math.Max(1, count));
        return share == long.MaxValue ? long.MaxValue : (long)(share * Margin);
    }

    /// <summary>Pixel ceiling with the same margin, or unlimited.</summary>
    public static long PixelBudget(ReferenceLimit limit) => limit.MaxPixels == long.MaxValue ? long.MaxValue : (long)(limit.MaxPixels * Margin);
}
