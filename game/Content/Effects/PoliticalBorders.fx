// Compile: dotnet tool run mgfxc game/Content/Effects/PoliticalBorders.fx game/Content/Effects/PoliticalBorders.mgfx /Profile:DirectX_11
#include "Simple.fxh"
texture Neighbors;
texture Regions;
texture ClaimantColors;
float2 RegionSize;
float2 PaletteSize;
float2 CellOrigin;
float PixelsPerCell;
float Minimap;
float StripeScale;
sampler Labels = sampler_state { Texture = (Texture); MinFilter = Point; MagFilter = Point; MipFilter = Point; AddressU = Clamp; AddressV = Clamp; };
sampler Opposite = sampler_state { Texture = (Neighbors); MinFilter = Point; MagFilter = Point; MipFilter = Point; AddressU = Clamp; AddressV = Clamp; };
sampler Metadata = sampler_state { Texture = (Regions); MinFilter = Point; MagFilter = Point; MipFilter = Point; AddressU = Clamp; AddressV = Clamp; };
sampler Palette = sampler_state { Texture = (ClaimantColors); MinFilter = Point; MagFilter = Point; MipFilter = Point; AddressU = Clamp; AddressV = Clamp; };
float Decode(float3 rgb) { return dot(floor(rgb * 255 + 0.5), float3(1,256,65536)); }
float2 Address(float index, float2 size) { return (float2(index - floor(index / size.x) * size.x, floor(index / size.x)) + 0.5) / size; }
float3 RegionColor(float id, float2 worldCell)
{
    float2 range = tex2D(Metadata, Address(id, RegionSize)).rg;
    float stripe = floor((worldCell.x + worldCell.y) * StripeScale / lerp(8, 2, Minimap));
    float index = stripe - floor(stripe / max(range.y,1)) * max(range.y,1);
    return tex2D(Palette, Address(range.x + index, PaletteSize)).rgb;
}
float4 Material(float id, float other, float distance, float2 worldCell)
{
    float halfWidth = lerp(1,0.5,Minimap);
    float core = (1-smoothstep(halfWidth-0.65,halfWidth+0.65,distance))*lerp(0.8,0.7,Minimap);
    float fill = id > 0 ? lerp(0.06,0.14,Minimap) : 0;
    float glow = id > 0 ? pow(saturate(1 - distance / 8), 2) * 0.18 * (1-Minimap) : 0;
    float alpha = 1 - (1-fill) * (1-core) * (1-glow);
    if (id == 0 && other == 0) alpha = 0;
    float3 color = RegionColor(id > 0 ? id : other, worldCell);
    float grain = frac(sin(dot(floor(worldCell), float2(12.9898,78.233))) * 43758.5453);
    color = saturate(color * (1 + (grain - 0.5) * 0.05 * (1-Minimap)*(1-saturate(core/0.8))));
    return float4(color * alpha, alpha);
}
float SignedDistance(float4 sample, float region)
{
    return sample.a*16*(Decode(sample.rgb) == region ? 1 : -1);
}
float4 BorderPixel(SimpleVSOutput input) : COLOR0
{
    if (!UseTexture) return input.Color;
    float2 p = input.TextureCoordinate * 320 - 0.5;
    float2 c = floor(p), f = frac(p);
    float2 worldCell = CellOrigin + p;
        // Reconstruct the subcell contour before shading at every LOD. Blending
        // pre-shaded cell centers quantizes line position and thickness. Region
        // IDs remain point sampled; only scalar signed distances interpolate.
        float2 uv = (floor(p+0.5)+0.5)/320;
        float id = Decode(tex2D(Labels,uv).rgb);
        float other = Decode(tex2D(Opposite,uv).rgb);
        float a = SignedDistance(tex2D(Labels,(c+0.5)/320),id);
        float b = SignedDistance(tex2D(Labels,(c+float2(1.5,0.5))/320),id);
        float d = SignedDistance(tex2D(Labels,(c+float2(0.5,1.5))/320),id);
        float e = SignedDistance(tex2D(Labels,(c+1.5)/320),id);
        float distance = lerp(lerp(a,b,f.x),lerp(d,e,f.x),f.y)*PixelsPerCell;
        if (distance < 0) { float swap = id; id = other; other = swap; }
        return Material(id,other,abs(distance),worldCell)*input.Color;
}
technique PoliticalBorders { pass P0 {
    VertexShader = compile vs_4_0 SimpleVertexShader();
    PixelShader = compile ps_4_0 BorderPixel();
} }
