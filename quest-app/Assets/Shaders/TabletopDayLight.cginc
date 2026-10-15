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

inline half3 TabletopDayLight(float w)
{
    half3 k = half3(1, 1, 1);
    if (_TabletopLightRatioOn > 0.5 && _LightRatio > 0.5)
    {
        float c = fmod(round(w * 127.0) + 128.0 - 15.0 + 256.0, 256.0);   // LightCode.FromSnorm
        float sky = floor(c / 16.0), blk = c - sky * 16.0;
        k = tex2Dlod(_TabletopLightRatio, float4((blk + 0.5) / 16.0, (sky + 0.5) / 16.0, 0, 0)).rgb * _TabletopLightRatioScale;
    }
    return k * _Tint.rgb;
}

#endif
