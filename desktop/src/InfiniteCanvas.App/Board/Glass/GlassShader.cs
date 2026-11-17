// Liquid glass material, ported from kubai087/glasswork (MIT License, Copyright (c) kubai087),
// https://github.com/kubai087/glasswork: the rim lens of lens.js / refract.js (inward bend along the contour normal,
// (1 - depth)^power falloff, red bent further than blue), the tone of glass.css (saturate, contrast, brightness, keep),
// its tint, and its box-shadow rim light, bevel and shadow, evaluated per pixel against a superellipse rounded box.
using ComputeSharp;
using ComputeSharp.D2D1;

namespace InfiniteCanvas.App.Board;

/// <summary>
/// Input 0 is the scene under the pane, already blurred, in the same pixel space as the output.
/// Every length is in output pixels: <paramref name="box"/> is the pane (x, y, width, height); <paramref name="lens"/> is radius,
/// corner exponent, rim width and edge; <paramref name="tone"/> is saturation, contrast, brightness and the saturation kept;
/// <paramref name="tint"/> is the tint colour with its opacity; <paramref name="light"/> is rim light, bevel (times the veil),
/// shadow and one glasswork pixel (u); <paramref name="extra"/> is falloff power, dispersion and 1 for the dark material.
/// </summary>
[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DInputDescription(0, D2D1Filter.MinMagMipLinear)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
public readonly partial struct GlassShader(float4 box, float4 lens, float4 tone, float4 tint, float4 light, float4 extra) : ID2D1PixelShader
{
    /// <summary>Signed distance to a rounded box whose corners are superellipses of exponent n (2 is a circle).</summary>
    private static float Box(float2 p, float2 half, float r, float n)
    {
        float2 q = Hlsl.Abs(p) - half + r;
        float2 m = Hlsl.Max(q, 0);
        float corner = Hlsl.Pow(Hlsl.Pow(m.X, n) + Hlsl.Pow(m.Y, n), 1 / n);
        return Hlsl.Min(Hlsl.Max(q.X, q.Y), 0) + corner - r;
    }

    /// <summary>The CSS saturate() matrix.</summary>
    private static float3 Saturate(float3 c, float s)
    {
        return new float3(
            (0.213f + 0.787f * s) * c.R + (0.715f - 0.715f * s) * c.G + (0.072f - 0.072f * s) * c.B,
            (0.213f - 0.213f * s) * c.R + (0.715f + 0.285f * s) * c.G + (0.072f - 0.072f * s) * c.B,
            (0.213f - 0.213f * s) * c.R + (0.715f - 0.715f * s) * c.G + (0.072f + 0.928f * s) * c.B);
    }

    /// <summary>
    /// How much of an inset box-shadow (offset, blur, spread) covers a point: 1 where the shadow's hole (the box moved by the offset and
    /// shrunk by the spread) leaves it, fading over the blur.
    /// </summary>
    private static float Inset(float2 p, float2 half, float r, float n, float2 offset, float blur, float spread)
    {
        float d = Box(p - offset, half, r, n) + spread;
        float sigma = Hlsl.Max(blur * 0.5f, 0.5f);
        return Hlsl.SmoothStep(-2 * sigma, 2 * sigma, d);
    }

    private float3 Sample(float2 offset)
    {
        float4 c = D2D.SampleInputAtOffset(0, offset);
        return c.RGB / Hlsl.Max(c.A, 0.0001f);
    }

    public float4 Execute()
    {
        float2 position = D2D.GetScenePosition().XY;
        float2 half = box.ZW * 0.5f;
        float2 p = position - box.XY - half;
        float r = Hlsl.Min(lens.X, Hlsl.Min(half.X, half.Y));
        float n = lens.Y;
        float u = light.W;
        float dark = extra.Z;
        float d = Box(p, half, r, n);
        float cover = Hlsl.Clamp(0.5f - d, 0, 1);

        // Outer shadows (glass.css): a 0.5u hairline and a soft drop 4u down, only outside the pane.
        float hairline = Hlsl.Clamp(0.5f * u + 0.5f - d, 0, 1) * Hlsl.Lerp(0.12f, 0.28f, dark);
        float sigma = Hlsl.Lerp(8, 7, dark) * u;
        float drop = (1 - Hlsl.SmoothStep(-2 * sigma, 2 * sigma, Box(p - new float2(0, 4 * u), half, r, n))) * Hlsl.Lerp(0.1f, 0.22f, dark);
        float shade = (1 - (1 - hairline) * (1 - drop)) * light.Z;
        float4 shadow = new float4(0, 0, 0, Hlsl.Clamp(shade, 0, 1) * (1 - cover));
        if (cover <= 0) return shadow;

        // The rim lens: the scene bends inward along the contour normal, most at the outline and not at all past the rim.
        float e = 0.5f;
        float2 gradient = new float2(
            Box(p + new float2(e, 0), half, r, n) - Box(p - new float2(e, 0), half, r, n),
            Box(p + new float2(0, e), half, r, n) - Box(p - new float2(0, e), half, r, n));
        float2 inward = -gradient / Hlsl.Max(Hlsl.Length(gradient), 0.00001f);
        float rim = Hlsl.Max(lens.Z, 1);
        float depth = Hlsl.Clamp(-d / rim, 0, 1);
        float amount = lens.W * rim * Hlsl.Pow(1 - depth, extra.X);
        float split = extra.Y;
        float3 c;
        if (split > 0 && amount > 0.01f)
        {
            c = new float3(
                Sample(inward * amount * (1 + split)).R,
                Sample(inward * amount).G,
                Sample(inward * amount * (1 - split)).B);
        }
        else
        {
            c = Sample(inward * amount);
        }

        // Tone, then the saturation the backdrop lets the glass keep, then the tint.
        c = Saturate(c, tone.X);
        c = (c - 0.5f) * tone.Y + 0.5f;
        c *= tone.Z;
        c = Saturate(c, tone.W);
        c = Hlsl.Clamp(c, 0, 1);
        c = Hlsl.Lerp(c, tint.RGB, tint.A);

        // Inset box-shadows, the last listed in glass.css first: the bevel bands, then the rim light.
        float catchLight = light.X;
        float bevel = light.Y;
        float s6 = -6 * u;
        c = Hlsl.Lerp(c, 0, Inset(p, half, r, n, new float2(0, -6 * u), 8 * u, s6) * Hlsl.Lerp(0.06f, 0.08f, dark) * bevel);
        c = Hlsl.Lerp(c, 1, Inset(p, half, r, n, new float2(-4 * u, 0), 8 * u, s6) * Hlsl.Lerp(0.12f, 0.04f, dark) * bevel);
        c = Hlsl.Lerp(c, 1, Inset(p, half, r, n, new float2(4 * u, 0), 8 * u, s6) * Hlsl.Lerp(0.22f, 0.07f, dark) * bevel);
        c = Hlsl.Lerp(c, 1, Inset(p, half, r, n, new float2(0, 6 * u), 8 * u, s6) * Hlsl.Lerp(0.5f, 0.18f, dark) * bevel);
        c = Hlsl.Lerp(c, 1, Inset(p, half, r, n, new float2(0, 0), 0, 0.5f * u) * Hlsl.Lerp(0.3f, 0.06f, dark) * catchLight);
        float step = Hlsl.Lerp(1.5f, 1, dark) * u;
        float soft = Hlsl.Lerp(1, 0.5f, dark) * u;
        c = Hlsl.Lerp(c, 1, Inset(p, half, r, n, new float2(-step, -step), soft, -soft) * Hlsl.Lerp(0.6f, 0.18f, dark) * catchLight);
        c = Hlsl.Lerp(c, 1, Inset(p, half, r, n, new float2(step, step), soft, -soft) * Hlsl.Lerp(1, 0.46f, dark) * catchLight);

        c = Hlsl.Clamp(c, 0, 1);
        return new float4(c * cover, cover) + shadow;
    }
}
