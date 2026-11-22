using ComputeSharp;
using ComputeSharp.D2D1;

namespace Tabula.App.Board;

/// <summary>
/// The canvas's top fade (as in XMind): a band along the window's top edge in which the scene is blurred progressively, most at the edge
/// and not at all at the band's bottom, and washed towards the canvas colour. Input 0 is the scene, edge-clamped, in output pixels.
/// <paramref name="band"/>: band height and largest blur radius (window DIPs), wash, strength. <paramref name="color"/>: canvas colour.
/// <paramref name="map"/>: window DIPs per output pixel and the window y of output row 0, so a glass surface can run the same fade on its
/// own scene; z = 1 for an opaque result (the glass's input) instead of a band laid over the canvas; w > 0 for a band along the bottom edge
/// (w is the control's height in DIPs, and the fade grows towards the bottom instead of the top).
/// </summary>
[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DInputDescription(0, D2D1Filter.MinMagMipLinear)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
public readonly partial struct FadeShader(float4 band, float4 color, float4 map) : ID2D1PixelShader
{
    public float4 Execute()
    {
        float y = map.W > 0 ? map.W - D2D.GetScenePosition().Y * map.X : D2D.GetScenePosition().Y * map.X + map.Y;
        // 1 at the window's top edge, 0 at the band's bottom.
        float t = Hlsl.Clamp(1 - y / Hlsl.Max(band.X, 1), 0, 1);
        float r = Hlsl.Max(band.Y * t * t / Hlsl.Max(map.X, 0.0001f), 0.01f);
        float3 sharp = D2D.SampleInputAtOffset(0, new float2(0, 0)).RGB;
        float3 sum = sharp;
        float total = 1;
        // A Gaussian-weighted golden-angle spiral, as refract.js blurs (24 taps): the radius varies per row, which no fixed blur can do.
        for (int i = 0; i < 24; i++)
        {
            float d = r * 2.2f * Hlsl.Sqrt((i + 0.5f) / 24);
            float a = i * 2.39996f;
            float w = Hlsl.Exp(-(d * d) / (2 * r * r));
            sum += D2D.SampleInputAtOffset(0, new float2(Hlsl.Cos(a), Hlsl.Sin(a)) * d).RGB * w;
            total += w;
        }
        float3 c = Hlsl.Lerp(sum / total, color.RGB, band.Z * t * t);
        // Opaque as soon as the blur shows (t > 0.15 is under a pixel of radius below that): a half-transparent blurred copy over the sharp
        // canvas read as a double image. Only the bottom sliver, where both are alike, fades into the real canvas.
        float alpha = Hlsl.SmoothStep(0, 0.15f, t) * band.W;
        float4 result = new float4(c * alpha, alpha);
        if (map.Z > 0.5f) result = new float4(Hlsl.Lerp(sharp, c, alpha), 1);
        return result;
    }
}
