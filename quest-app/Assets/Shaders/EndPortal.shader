// [blocks] End portal and end gateway surfaces (BlockEntityRenderer): a field of drifting star layers that sits at
// infinity behind the surface, like looking into the void. The look follows vanilla's idea (the pack's end sky plus
// layers of its end_portal star texture, turned, scaled and scrolling at different speeds, tinted from dark teal to
// blue) but it is this project's own shader: layers are placed by the view direction instead of the screen, so both
// eyes agree and the stars stay put when the head moves. Unlit (portals glow), clipped by the nav window, fogged.
// Shipped through Assets/Resources/TabletopEndPortal.mat.
Shader "TabletopCraft/EndPortal"
{
    Properties
    {
        _MainTex ("Stars (entity/end_portal/end_portal)", 2D) = "black" {}
        _SkyTex ("Sky (environment/end_sky)", 2D) = "black" {}
        _Layers ("Star layers", Range(1, 16)) = 15
        _Speed ("Drift speed", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry+10" }
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "TabletopFog.cginc"    // eye-in-fluid fog (EyeEffects.cs)
            #include "TabletopClip.cginc"   // [nav] clip volume (XR/NavController)
            sampler2D _MainTex, _SkyTex;
            float _Layers, _Speed;
            struct appdata { float4 vertex : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float3 view : TEXCOORD0; float3 fogPos : TEXCOORD5; TABLETOP_CLIP_COORDS(7) UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert (appdata v)
            {
                v2f o; UNITY_SETUP_INSTANCE_ID(v); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.view = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0)).xyz - _WorldSpaceCameraPos;   // per eye
                o.fogPos = TabletopFogPos(v.vertex);   // eye-in-fluid fog
                TABLETOP_TRANSFER_CLIP(o, v.vertex)   // [nav] clip volume
                return o;
            }
            float hash1(float n) { return frac(sin(n * 12.9898) * 43758.5453); }
            fixed4 frag (v2f i) : SV_Target
            {
                TABLETOP_CLIP(i)   // [nav] clip volume
                float3 d = normalize(i.view);
                // Continuous direction -> plane mapping (looking down into a portal is the common view).
                float2 base = d.xz / (abs(d.y) + 0.35);
                float t = _Time.y * _Speed;
                float3 deep = float3(0.025, 0.085, 0.095), bright = float3(0.11, 0.33, 0.62);
                float3 c = tex2D(_SkyTex, base * 0.5 + 0.25).rgb * deep;
                [loop] for (int k = 1; k <= 16; k++)
                {
                    if (k > _Layers) break;
                    float fk = k;
                    float a = hash1(fk) * 6.2831853;
                    float2x2 rot = float2x2(cos(a), -sin(a), sin(a), cos(a));
                    float scale = 0.6 + 0.22 * fk;
                    float2 uv = mul(rot, base) * scale + float2(hash1(fk + 7.0), t * (0.015 + 0.006 * fk));
                    float w = fk / 16.0;
                    c += tex2D(_MainTex, uv).rgb * lerp(deep, bright, w * w) * (0.9 + 0.5 * w);
                }
                c = TabletopFog(c, i.fogPos);   // eye-in-fluid fog
                return fixed4(c, 1);
            }
            ENDCG
        }
    }
}
