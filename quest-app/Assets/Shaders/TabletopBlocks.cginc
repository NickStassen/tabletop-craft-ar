// Block texture sampling for the block shaders (Blocks, BlocksTransparent): the Texture2DArray _Blocks (one layer per
// texture, UVs in block units with Repeat wrap, so greedy quads tile).
//  - [random] Per-block texture rotation of greedy cubes (World/BlockModels.cs RandomUv). Vanilla turns or mirrors the
//    textures of stone, dirt, grass tops, sand, netherrack... per block; a greedy quad spans many blocks, so the
//    variant is picked here per block cell. The mesher encodes uv.z = layer + 4096 * table (table 0 = none, layer < 0
//    = flat colour).
//  - Mips (ResourcePackAtlas: vanilla's chains, trilinear) for distant faces, but texel centres up close so blocks stay
//    crisp like vanilla's NEAREST magnification. Explicit gradients from the continuous uv keep the mip level steady
//    across block edges where a rotation changes the uv.
// Usage in a block shader:
//   #include "TabletopBlocks.cginc"                   (declares _Blocks)
//   v2f gets TABLETOP_RANDOM_COORDS(1, 2)             (two unused TEXCOORD indices)
//   vert: TABLETOP_TRANSFER_RANDOM(o, v)               (after o.uv = v.uv; needs v.vertex, v.normal, v.uv)
//   frag: TABLETOP_BLOCKS_GRADIENTS(i)                (first line: before any clip/discard, while the 2x2 quad is whole)
//         fixed4 t = TabletopSampleBlocks(i.uv, i.cellPos, i.randomTable, tbDx, tbDy);
#ifndef TABLETOP_BLOCKS_INCLUDED
#define TABLETOP_BLOCKS_INCLUDED

UNITY_DECLARE_TEX2DARRAY(_Blocks);
float4 _TabletopRandomUv[64];   // global, per table id: x = entries, y = 3-bit codes of entries 0-7, z = entries 8-15
float4 _TabletopCell0;          // per renderer (MaterialPropertyBlock): world block of the mesh origin; 0 without one
float _TabletopBlocksSize;      // global: texels per block (the atlas tile size); 0 until the atlas exists

#define TABLETOP_RANDOM_COORDS(a, b) float3 cellPos : TEXCOORD##a; nointerpolation float randomTable : TEXCOORD##b;
#define TABLETOP_BLOCKS_GRADIENTS(i) float2 tbDx = ddx((i).uv.xy), tbDy = ddy((i).uv.xy);
#define TABLETOP_TRANSFER_RANDOM(o, v) { float t_ = (v).uv.z >= 0 ? floor(((v).uv.z + 0.5) / 4096.0) : 0; o.uv.z = (v).uv.z - t_ * 4096.0; o.randomTable = t_; o.cellPos = (v).vertex.xyz - (v).normal.xyz * 0.5; }

// [gpu] Batches upload a 20-byte PackedVertex (SectionBatches.cs; the block shaders' TABLETOP_PACKED variant): SNorm16
// position in 1/64 blocks with w = light code + 256 * face direction (0..5: +x, -x, +y, -y, +z, -z; 6: none), the
// colour with the face shade multiplied in, SNorm16 uv in 1/1024 blocks, the layer (-1 = flat colour) and the table.
// Unpacks what the classic path reads from the normal and uv.z: object position, light code, uv + layer, table, cellPos.
inline void TabletopUnpack(float4 vertex, float4 uv, out float3 p, out float code, out float3 uvl, out float table, out float3 cellPos)
{
    p = vertex.xyz * (32767.0 / 64.0);
    float meta = round(vertex.w * 32767.0);
    float dir = floor(meta * (1.0 / 256.0));
    code = meta - dir * 256.0;
    float axis = floor(dir * 0.5);
    float3 n = float3(axis == 0, axis == 1, axis == 2) * (1.0 - 2.0 * (dir - 2.0 * axis));
    cellPos = p - 0.5 * n;
    uvl = float3(uv.xy * (32767.0 / 1024.0), round(uv.z * 32767.0));
    table = round(uv.w * 32767.0);
}

inline uint TabletopHash(int3 c)
{
    uint h = (uint)c.x * 0x8da6b343u ^ (uint)c.y * 0xd8163841u ^ (uint)c.z * 0xcb1ab31fu;
    h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16;
    return h;
}

// The block texture at uv (u, v in block units, z = layer). A face with a random table samples its block's variant:
// the in-block coordinate turned/mirrored by the variant's code (bit0 swap, bit1 negate u, bit2 negate v). The block
// is the cell just behind the face (cellPos, object space; meshes are Z mirrored).
inline fixed4 TabletopSampleBlocks(float3 uv, float3 cellPos, float randomTable, float2 dx, float2 dy)
{
    float2 st = uv.xy;
    float layer = uv.z;
    if (randomTable > 63.5)
    {
        // [flow] Table 64 (FluidRules.QuadTable): a texture twice the tile size kept as 4 layers (TL, TR, BL, BR);
        // the 2-block cell picks one, so water_flow/lava_flow keep their full resolution. Unity v is up: top = q.y 1.
        float2 q = floor(frac(uv.xy * 0.5) * 2.0);
        layer += q.x + 2.0 * (1.0 - q.y);
    }
    else if (randomTable >= 0.5)
    {
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
        st = floor(uv.xy) + f + 0.5;
    }
    // Magnified (a texel spans more than a pixel): the texel's centre, which bilinear filtering returns unblended.
    float n = _TabletopBlocksSize;
    if (n > 0 && max(dot(dx, dx), dot(dy, dy)) * n * n < 1.0) st = (floor(st * n) + 0.5) / n;
    return _Blocks.SampleGrad(sampler_Blocks, float3(st, layer), dx, dy);
}

#endif
