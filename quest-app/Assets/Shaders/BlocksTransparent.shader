// Unlit block shader: Texture2DArray layer from uv.z (layer < 0 => vertex colour only), tint from vertex colour.
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
            UNITY_DECLARE_TEX2DARRAY(_Blocks);
            float _HasTex, _Cutoff;
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float3 uv : TEXCOORD0; fixed4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float3 uv : TEXCOORD0; fixed4 color : COLOR; float shade : TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert (appdata v)
            {
                v2f o; UNITY_SETUP_INSTANCE_ID(v); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; o.color = v.color;
                float3 n = abs(v.normal);
                o.shade = n.y > 0.5 ? (v.normal.y > 0 ? 1.0 : 0.5) : (n.x > 0.5 ? 0.8 : 0.65); // Minecraft-like face shading
                return o;
            }
            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = i.color;
                if (_HasTex > 0.5 && i.uv.z >= 0) c *= UNITY_SAMPLE_TEX2DARRAY(_Blocks, i.uv);
                clip(c.a - 0.01);
                c.rgb *= i.shade;
                return c;
            }
            ENDCG
        }
    }
}
