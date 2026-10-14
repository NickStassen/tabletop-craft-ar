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

#endif
