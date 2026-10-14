// Unlit block shader: Texture2DArray layer from uv.z (layer < 0 => vertex colour only), tint from vertex colour (sRGB).
// Built-in render pipeline, single-pass instanced stereo friendly.
Shader "TabletopCraft/BlocksTransparent"
{
    Properties { _Blocks ("Blocks", 2DArray) = "" {} _HasTex ("Has Texture", Float) = 0 _Cutoff ("Alpha cutoff", Range(0,1)) = 0.3 }
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
            #include "TabletopClip.cginc"   // [nav] clip volume (XR/NavController)
            UNITY_DECLARE_TEX2DARRAY(_Blocks);
            float _HasTex, _Cutoff;
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float3 uv : TEXCOORD0; fixed4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float3 uv : TEXCOORD0; half4 color : COLOR; TABLETOP_CLIP_COORDS(7) UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert (appdata v)
            {
                v2f o; UNITY_SETUP_INSTANCE_ID(v); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv;
                TABLETOP_TRANSFER_CLIP(o, v.vertex)   // [nav] clip volume
                // Vanilla face shading (CardinalLighting.DEFAULT): up 1.0, down 0.5, north/south 0.8, east/west 0.6.
                // Faces not aligned to an axis (cross plants, 45 degree elements; zero normals on double-sided quads)
                // stay unshaded, as vanilla draws them. Z is mirrored in Unity, which doesn't change |n.z|.
                float3 n = abs(v.normal);
                float shade = n.y > 0.9 ? (v.normal.y > 0 ? 1.0 : 0.5) : (n.z > 0.9 ? 0.8 : (n.x > 0.9 ? 0.6 : 1.0));
                o.color = v.color; o.color.rgb *= shade;
                // Tints, flat colours and shading are sRGB values; vertex colours aren't converted in linear colour space.
                #ifndef UNITY_COLORSPACE_GAMMA
                o.color.rgb = GammaToLinearSpace(o.color.rgb);
                #endif
                return o;
            }
            fixed4 frag (v2f i) : SV_Target
            {
                TABLETOP_CLIP(i)   // [nav] clip volume
                fixed4 c = i.color;
                if (_HasTex > 0.5 && i.uv.z >= 0) c *= UNITY_SAMPLE_TEX2DARRAY(_Blocks, i.uv);
                clip(c.a - 0.01);
                return c;
            }
            ENDCG
        }
    }
}
