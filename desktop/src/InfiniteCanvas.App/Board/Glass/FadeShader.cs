using ComputeSharp;
using ComputeSharp.D2D1;

namespace InfiniteCanvas.App.Board;

/// <summary>
/// The canvas's top fade (as in XMind): a band along the top edge in which the scene is blurred progressively, most at the edge and not
/// at all at the band's bottom, and washed towards the canvas colour. Input 0 is the scene, edge-clamped, in output pixels.
/// <paramref name="band"/> is height, largest blur radius, wash and the band's opacity at the top; <paramref name="color"/> is the
/// canvas colour.
/// </summary>
[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DInputDescription(0, D2D1Filter.MinMagMipLinear)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
public readonly partial struct FadeShader(float4 band, float4 color) : ID2D1PixelShader
{
    public float4 Execute()
    {
        float y = D2D.GetScenePosition().Y;
        // 1 at the top edge, 0 at the band's bottom.
        float t = Hlsl.Clamp(1 - y / Hlsl.Max(band.X, 1), 0, 1);
        float radius = band.Y * t * t;
        float3 sum = D2D.SampleInputAtOffset(0, new float2(0, 0)).RGB;
        float total = 1;
        // A Gaussian-weighted golden-angle spiral, as refract.js blurs (24 taps): the radius varies per row, which no fixed blur can do.
        float r = Hlsl.Max(radius, 0.01f);
        for (int i = 0; i < 24; i++)
        {
            float d = r * 2.2f * Hlsl.Sqrt((i + 0.5f) / 24);
            float a = i * 2.39996f;
            float w = Hlsl.Exp(-(d * d) / (2 * r * r));
            sum += D2D.SampleInputAtOffset(0, new float2(Hlsl.Cos(a), Hlsl.Sin(a)) * d).RGB * w;
            total += w;
        }
        float3 c = Hlsl.Lerp(sum / total, color.RGB, band.Z * t * t);
        // Clear at the bottom so the real canvas (with card text and buttons) shows through, opaque towards the top.
        float alpha = Hlsl.SmoothStep(0, 0.7f, t) * band.W;
        return new float4(c * alpha, alpha);
    }
}
