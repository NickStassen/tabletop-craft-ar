// Unlit player skin for the figures on the table: 64x64 skin texture (point filtered), alpha-tested for the second
// layer, both sides drawn like the game's entity render types, and the game's fixed two-light entity shading so
// the boxes read as 3D without a scene light. Built-in render pipeline, single-pass instanced stereo friendly.
// Shipped through Assets/Resources/TabletopPlayer.mat (Resources are always in the build).
Shader "TabletopCraft/PlayerSkin"
{
    Properties { _MainTex ("Skin", 2D) = "white" {} _Cutoff ("Alpha cutoff", Range(0,1)) = 0.1 }
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" }
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "TabletopClip.cginc"   // [nav] clip volume: figures outside the visible box are hidden
            sampler2D _MainTex;
            float _Cutoff;
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; half shade : TEXCOORD1; TABLETOP_CLIP_COORDS(7) UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert (appdata v)
            {
                v2f o; UNITY_SETUP_INSTANCE_ID(v); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv;
                TABLETOP_TRANSFER_CLIP(o, v.vertex)   // [nav] clip volume
                // Lighting.setupFor3DItems-style entity light: two fixed directions, 0.4 ambient + 0.6 diffuse.
                float3 n = normalize(UnityObjectToWorldNormal(v.normal));
                float3 l0 = normalize(float3(0.2, 1.0, -0.7)), l1 = normalize(float3(-0.2, 1.0, 0.7));
                float s = min(1.0, 0.4 + 0.6 * (max(0.0, dot(n, l0)) + max(0.0, dot(n, l1))));
                // The texture is sampled as linear; the shade factor is a display-space (sRGB) multiplier.
                #ifndef UNITY_COLORSPACE_GAMMA
                s = GammaToLinearSpaceExact(s);
                #endif
                o.shade = s;
                return o;
            }
            fixed4 frag (v2f i) : SV_Target
            {
                TABLETOP_CLIP(i)   // [nav] clip volume
                fixed4 c = tex2D(_MainTex, i.uv);
                clip(c.a - _Cutoff);
                return fixed4(c.rgb * i.shade, 1);
            }
            ENDCG
        }
    }
}
