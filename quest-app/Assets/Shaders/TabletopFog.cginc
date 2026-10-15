// Eye-in-fluid fog (water, lava, powder snow, head inside a block), shared by the block and player shaders.
// EyeEffects.cs sets the globals every frame; they default to 0 (strength 0 = no fog) until it does. Distances are
// metres from the centre eye (one fog for both eyes; keeps single-pass instanced stereo simple and consistent).
// Usage: v2f gets `float3 fogPos : TEXCOORD5;` = TabletopFogPos(v.vertex) in vert; frag: c.rgb = TabletopFog(c.rgb, i.fogPos).
#ifndef TABLETOP_FOG_INCLUDED
#define TABLETOP_FOG_INCLUDED

float4 _TabletopFogColor;    // rgb: fog colour, in the render colour space (linear when the project is linear)
float4 _TabletopFogParams;   // x: start (m), y: 1 / (end - start) (1/m), z: strength (max fog, 0 = off)
float4 _TabletopFogEye;      // xyz: centre eye, world space

inline float3 TabletopFogPos(float4 objectVertex) { return mul(unity_ObjectToWorld, objectVertex).xyz; }

inline half3 TabletopFog(half3 col, float3 worldPos)
{
    float f = saturate((distance(worldPos, _TabletopFogEye.xyz) - _TabletopFogParams.x) * _TabletopFogParams.y) * _TabletopFogParams.z;
    return lerp(col, (half3)_TabletopFogColor.rgb, (half)f);
}

// [gpu] The block shaders compile the fog only while the eye is in a fluid: `#pragma multi_compile _ TABLETOP_FOG` (EyeEffects
// sets the global keyword with the strength), then TABLETOP_FOG_COORDS(5) in v2f, TABLETOP_TRANSFER_FOG(o, worldPos) in vert
// and TABLETOP_APPLY_FOG(c.rgb, i) in frag. Without the keyword there is no fog varying and no per-pixel distance.
#if defined(TABLETOP_FOG)
#define TABLETOP_FOG_COORDS(idx) float3 fogPos : TEXCOORD##idx;
#define TABLETOP_TRANSFER_FOG(o, worldPos) o.fogPos = (worldPos);
#define TABLETOP_APPLY_FOG(col, i) col = TabletopFog(col, (i).fogPos);
#else
#define TABLETOP_FOG_COORDS(idx)
#define TABLETOP_TRANSFER_FOG(o, worldPos)
#define TABLETOP_APPLY_FOG(col, i)
#endif

#endif
