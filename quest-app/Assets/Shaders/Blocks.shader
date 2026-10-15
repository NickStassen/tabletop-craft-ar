// Unlit block shader: Texture2DArray layer from uv.z (layer < 0 => vertex colour only), tint from vertex colour (sRGB).
// Built-in render pipeline, single-pass instanced stereo friendly.
// [gpu] Variants (keywords) instead of runtime branches: TABLETOP_SOLID (material: faces whose textures are opaque, no
// alpha test, so the GPU keeps early depth rejection; TabletopRenderer.SolidMaterial), TABLETOP_FOG (eye in a fluid),
// TABLETOP_DAYLIGHT (not noon in clear weather), TABLETOP_CLIP_PLANES (hardware clip planes for the [nav] box).
Shader "TabletopCraft/Blocks"
{
    Properties { _Blocks ("Blocks", 2DArray) = "" {} _HasTex ("Has Texture", Float) = 0 _Cutoff ("Alpha cutoff", Range(0,1)) = 0.3
                 _LightRatio ("World light ratio (1 terrain, 0 lit on the CPU)", Float) = 1 _Tint ("Tint", Color) = (1,1,1,1) }
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma require 2darray
            #pragma multi_compile_local _ TABLETOP_SOLID
            #pragma multi_compile _ TABLETOP_FOG
            #pragma multi_compile _ TABLETOP_DAYLIGHT
            #pragma multi_compile _ TABLETOP_CLIP_PLANES
            #define TABLETOP_DAYLIGHT_KEYWORD
            #include "UnityCG.cginc"
            #include "TabletopFog.cginc"   // eye-in-fluid fog (EyeEffects.cs)
            #include "TabletopClip.cginc"   // [nav] clip volume (XR/NavController)
            #include "TabletopBlocks.cginc"   // _Blocks sampling: [random] per-block rotation, crisp texels up close, mips far away
            #include "TabletopDayLight.cginc"   // [living] time of day and weather: the light code in normal.w
            float _HasTex, _Cutoff;
            struct appdata { float4 vertex : POSITION; float4 normal : NORMAL; float3 uv : TEXCOORD0; fixed4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float3 uv : TEXCOORD0; half4 color : COLOR; TABLETOP_FOG_COORDS(5) TABLETOP_CLIP_VARYING(7) TABLETOP_RANDOM_COORDS(1, 2) UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert (appdata v)
            {
                v2f o; UNITY_SETUP_INSTANCE_ID(v); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 wp = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0)).xyz;
                o.pos = mul(UNITY_MATRIX_VP, float4(wp, 1.0)); o.uv = v.uv;
                TABLETOP_TRANSFER_FOG(o, wp)   // eye-in-fluid fog
                TABLETOP_TRANSFER_CLIP_WORLD(o, wp)   // [nav] clip volume
                TABLETOP_TRANSFER_RANDOM(o, v)   // [random] uv.z = layer + 4096 * table -> o.uv.z, o.randomTable, o.cellPos
                // Vanilla face shading (CardinalLighting.DEFAULT): up 1.0, down 0.5, north/south 0.8, east/west 0.6.
                // Faces not aligned to an axis (cross plants, 45 degree elements; zero normals on double-sided quads)
                // stay unshaded, as vanilla draws them. Z is mirrored in Unity, which doesn't change |n.z|.
                float3 n = abs(v.normal.xyz);
                float shade = n.y > 0.9 ? (v.normal.y > 0 ? 1.0 : 0.5) : (n.z > 0.9 ? 0.8 : (n.x > 0.9 ? 0.6 : 1.0));
                o.color = v.color; o.color.rgb *= shade * TabletopDayLight(v.normal.w);   // [living] night, weather, flashes
                // Tints, flat colours and shading are sRGB values; vertex colours aren't converted in linear colour space.
                #ifndef UNITY_COLORSPACE_GAMMA
                o.color.rgb = GammaToLinearSpace(o.color.rgb);
                #endif
                return o;
            }
            fixed4 frag (v2f i) : SV_Target
            {
                TABLETOP_BLOCKS_GRADIENTS(i)   // uv gradients for the mips, before the clip discards anything
                TABLETOP_CLIP_FRAG(i)   // [nav] clip volume (nothing here with TABLETOP_CLIP_PLANES: the vertex stage cut it)
                fixed4 c = i.color;
                if (_HasTex > 0.5 && i.uv.z >= 0) c *= TabletopSampleBlocks(i.uv, i.cellPos, i.randomTable, tbDx, tbDy);   // [random], mips
                #if !defined(TABLETOP_SOLID)
                clip(c.a - _Cutoff);
                #endif
                c.a = 1;   // [passthrough] a cutout texel that survives the clip is opaque (mips leave alpha below 1)
                TABLETOP_APPLY_FOG(c.rgb, i)   // eye-in-fluid fog
                return c;
            }
            ENDCG
        }
    }
}
