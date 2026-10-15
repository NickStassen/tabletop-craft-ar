// Clip volume for the tabletop box (XR/NavController sets _TabletopClip every frame). Geometry outside the visible
// window is discarded, so terrain sliding in from the view margin never hangs off the table. _TabletopClip maps
// world space to box space, where the box is [-1, 1] on every axis. Unset (all zeros) means no clipping.
// Usage in a block shader (single-pass instanced stereo safe: world positions don't depend on the eye):
//   #include "TabletopClip.cginc"
//   v2f gets TABLETOP_CLIP_COORDS(7)            (an unused TEXCOORD index)
//   vert: TABLETOP_TRANSFER_CLIP(o, v.vertex);  (after UNITY_SETUP_INSTANCE_ID)
//   frag: TABLETOP_CLIP(i);                     (first line)
#ifndef TABLETOP_CLIP_INCLUDED
#define TABLETOP_CLIP_INCLUDED

float4x4 _TabletopClip;

#define TABLETOP_CLIP_COORDS(idx) float3 tabletopClip : TEXCOORD##idx;
#define TABLETOP_TRANSFER_CLIP(o, vertex) o.tabletopClip = mul(_TabletopClip, mul(unity_ObjectToWorld, float4((vertex).xyz, 1.0))).xyz;
#define TABLETOP_CLIP(i) clip(1.0 - max(abs(i.tabletopClip.x), max(abs(i.tabletopClip.y), abs(i.tabletopClip.z))));

// [gpu] The block shaders cut the box with six hardware clip planes instead: `#pragma multi_compile _ TABLETOP_CLIP_PLANES`
// (a global keyword, tabletop.json "clip_planes", NavController). The vertex stage writes the distances to the box's faces
// and the fragment shader has no discard: opaque terrain keeps early depth rejection (a discard turns it off for the
// whole draw) and needs no clip varying. Without the keyword it is the discard above. Usage (world position computed once):
//   v2f gets TABLETOP_CLIP_VARYING(7); vert: TABLETOP_TRANSFER_CLIP_WORLD(o, worldPos); frag: TABLETOP_CLIP_FRAG(i)
#if defined(TABLETOP_CLIP_PLANES)
#define TABLETOP_CLIP_VARYING(idx) float4 tabletopClip0 : SV_ClipDistance0; float2 tabletopClip1 : SV_ClipDistance1;
#define TABLETOP_TRANSFER_CLIP_WORLD(o, worldPos) { float3 c_ = mul(_TabletopClip, float4((worldPos), 1.0)).xyz; o.tabletopClip0 = 1.0 + float4(-c_.x, c_.x, -c_.y, c_.y); o.tabletopClip1 = 1.0 + float2(-c_.z, c_.z); }
#define TABLETOP_CLIP_FRAG(i)
#else
#define TABLETOP_CLIP_VARYING(idx) TABLETOP_CLIP_COORDS(idx)
#define TABLETOP_TRANSFER_CLIP_WORLD(o, worldPos) o.tabletopClip = mul(_TabletopClip, float4((worldPos), 1.0)).xyz;
#define TABLETOP_CLIP_FRAG(i) TABLETOP_CLIP(i)
#endif

#endif
