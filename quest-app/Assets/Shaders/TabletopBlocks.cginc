// [random] Per-block texture rotation of greedy cubes (World/BlockModels.cs RandomUv). Vanilla turns or mirrors the
// textures of stone, dirt, grass tops, sand, netherrack... per block; a greedy quad spans many blocks, so the variant is
// picked here per block cell. The mesher encodes uv.z = layer + 4096 * table (table 0 = none, layer < 0 = flat colour).
// Usage in a block shader:
//   #include "TabletopRandom.cginc"
//   v2f gets TABLETOP_RANDOM_COORDS(1, 2)             (two unused TEXCOORD indices)
//   vert: TABLETOP_TRANSFER_RANDOM(o, v)               (after o.uv = v.uv; needs v.vertex, v.normal, v.uv)
//   frag: fixed4 t = TabletopSampleBlocks(i.uv, i.cellPos, i.randomTable);
#ifndef TABLETOP_RANDOM_INCLUDED
#define TABLETOP_RANDOM_INCLUDED

UNITY_DECLARE_TEX2DARRAY(_Blocks);
float4 _TabletopRandomUv[64];   // global, per table id: x = entries, y = 3-bit codes of entries 0-7, z = entries 8-15
float4 _TabletopCell0;          // per renderer (MaterialPropertyBlock): world block of the mesh origin; 0 without one

#define TABLETOP_RANDOM_COORDS(a, b) float3 cellPos : TEXCOORD##a; nointerpolation float randomTable : TEXCOORD##b;
#define TABLETOP_TRANSFER_RANDOM(o, v) { float t_ = (v).uv.z >= 0 ? floor(((v).uv.z + 0.5) / 4096.0) : 0; o.uv.z = (v).uv.z - t_ * 4096.0; o.randomTable = t_; o.cellPos = (v).vertex.xyz - (v).normal * 0.5; }

inline uint TabletopHash(int3 c)
{
    uint h = (uint)c.x * 0x8da6b343u ^ (uint)c.y * 0xd8163841u ^ (uint)c.z * 0xcb1ab31fu;
    h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16;
    return h;
}

// The block texture at uv (u, v in block units, z = layer). A face with a table samples its block's variant: the
// in-block coordinate turned/mirrored by the variant's code (bit0 swap, bit1 negate u, bit2 negate v). The block is
// the cell just behind the face (cellPos, object space; meshes are Z mirrored). Gradients come from the continuous uv,
// so the mip level doesn't jump at block edges where the rotation changes (swaps and flips keep their lengths).
// randomTable is flat per triangle, so the branch is uniform in every 2x2 pixel quad.
inline fixed4 TabletopSampleBlocks(float3 uv, float3 cellPos, float randomTable)
{
    if (randomTable < 0.5) return UNITY_SAMPLE_TEX2DARRAY(_Blocks, uv);
    float2 dx = ddx(uv.xy), dy = ddy(uv.xy);
    float4 t = _TabletopRandomUv[(int)randomTable];
    int3 c = (int3)floor(cellPos);
    int3 w = int3(c.x, c.y, -c.z - 1) + (int3)_TabletopCell0.xyz;
    uint k = TabletopHash(w) % (uint)max(t.x, 1.0);
    uint codes = k < 8u ? (uint)t.y : (uint)t.z;
    uint code = (codes >> (3u * (k & 7u))) & 7u;
    float2 f = frac(uv.xy) - 0.5;
    if ((code & 1u) != 0u) f = f.yx;
    if ((code & 2u) != 0u) f.x = -f.x;
    if ((code & 4u) != 0u) f.y = -f.y;
    return _Blocks.SampleGrad(sampler_Blocks, float3(floor(uv.xy) + f + 0.5, uv.z), dx, dy);
}

#endif
