// [living] Time of day and weather for the block shaders (World/DayLight.cs, PROTOCOL.md "World state"). The meshers bake
// vertex colours with the noon lightmap and put the light each vertex was lit with (LightCode: sky * 16 + block) in the
// batch vertex normal's spare w byte. TabletopApp sets a 16x16 ratio table (current lightmap / noon lightmap, per
// channel; x = block light, y = sky light) as a global texture and the vertex colour is multiplied by its entry, so night,
// rain, thunder, lightning flashes and the Nether's ambient change the light without re-meshing anything.
// Meshes built with float3 normals read w = 1, which decodes to open sky (sky 15, block 0).
// Usage in a block shader:
//   #include "TabletopDayLight.cginc"
//   appdata: float4 normal : NORMAL   (w carries the code)
//   vert:    o.color.rgb *= TabletopDayLight(v.normal.w);   (before converting the sRGB colour to linear)
#ifndef TABLETOP_DAYLIGHT_INCLUDED
#define TABLETOP_DAYLIGHT_INCLUDED

sampler2D _TabletopLightRatio;   // global 16x16, point filtered: rgb = ratio / _TabletopLightRatioScale
float _TabletopLightRatioOn;     // global: 0 at noon in clear weather (and before any world_state): no lookup at all
float _TabletopLightRatioScale;  // global: what the texture's values are multiplied by (1 for half floats)
float _LightRatio;               // per material / renderer: 1 terrain, 0 meshes lit on the CPU (blocks held by entities)
fixed4 _Tint;                    // per material / renderer: colour multiplier (white for terrain)

// The ratio for light code c (0..255 = sky * 16 + block): texel (block, sky) of the 16x16 table.
inline half3 TabletopLightRatio(float c)
{
    float t = (c + 0.5) * 0.0625;   // u = (block + 0.5) / 16 = frac(t), v = (sky + 0.5) / 16
    return tex2Dlod(_TabletopLightRatio, float4(frac(t), (floor(t) + 0.5) * 0.0625, 0, 0)).rgb * _TabletopLightRatioScale;
}

// LightCode.FromSnorm: the code from the normal's w (snorm8: round(w * 127) is code - 113, wrapped to -128..127).
inline float TabletopLightCode(float w)
{
    float c = round(w * 127.0) + 113.0;
    return c < 0 ? c + 256.0 : c;
}

inline half3 TabletopDayLight(float w)
{
    half3 k = half3(1, 1, 1);
#if defined(TABLETOP_DAYLIGHT_KEYWORD)
    // [gpu] Block shaders: `#define TABLETOP_DAYLIGHT_KEYWORD` and `#pragma multi_compile _ TABLETOP_DAYLIGHT` (WorldLight sets the
    // global keyword when it isn't noon in clear weather): no lookup and no branch at all at noon.
    #if defined(TABLETOP_DAYLIGHT)
    if (_LightRatio > 0.5) k = TabletopLightRatio(TabletopLightCode(w));
    #endif
#else
    if (_TabletopLightRatioOn > 0.5 && _LightRatio > 0.5) k = TabletopLightRatio(TabletopLightCode(w));
#endif
    return k * _Tint.rgb;
}

#endif
