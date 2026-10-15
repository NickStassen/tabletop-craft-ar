// Unlit block shader: Texture2DArray layer from uv.z (layer < 0 => vertex colour only), tint from vertex colour (sRGB).
// Built-in render pipeline, single-pass instanced stereo friendly.
Shader "TabletopCraft/BlocksTransparent"
{
    Properties { _Blocks ("Blocks", 2DArray) = "" {} _HasTex ("Has Texture", Float) = 0 _Cutoff ("Alpha cutoff", Range(0,1)) = 0.3
                 _LightRatio ("World light ratio (1 terrain, 0 lit on the CPU)", Float) = 1 _Tint ("Tint", Color) = (1,1,1,1) }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma require 2darray
            #include "UnityCG.cginc"
            #include "TabletopFog.cginc"   // eye-in-fluid fog (EyeEffects.cs)
            #include "TabletopClip.cginc"   // [nav] clip volume (XR/NavController)
            #include "TabletopBlocks.cginc"   // _Blocks sampling: [random] per-block rotation, crisp texels up close, mips far away
            #include "TabletopDayLight.cginc"   // [living] time of day and weather: the light code in normal.w
            float _HasTex, _Cutoff;
            struct appdata { float4 vertex : POSITION; float4 normal : NORMAL; float3 uv : TEXCOORD0; fixed4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float3 uv : TEXCOORD0; half4 color : COLOR; float3 fogPos : TEXCOORD5; TABLETOP_CLIP_COORDS(7) TABLETOP_RANDOM_COORDS(1, 2) UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert (appdata v)
            {
                v2f o; UNITY_SETUP_INSTANCE_ID(v); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv;
                o.fogPos = TabletopFogPos(v.vertex);   // eye-in-fluid fog
                TABLETOP_TRANSFER_CLIP(o, v.vertex)   // [nav] clip volume
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
                TABLETOP_CLIP(i)   // [nav] clip volume
                fixed4 c = i.color;
                if (_HasTex > 0.5 && i.uv.z >= 0) c *= TabletopSampleBlocks(i.uv, i.cellPos, i.randomTable, tbDx, tbDy);   // [random], mips
                clip(c.a - 0.01);
                c.rgb = TabletopFog(c.rgb, i.fogPos);   // eye-in-fluid fog
                return c;
            }
            ENDCG
        }
    }
}
