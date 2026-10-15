// Unlit entity shader for the miniature's boats, minecarts, mobs, items, armour, dig cracks: a point-filtered texture,
// alpha-tested (or blended for the slime's outer layer), both sides drawn like the game's entity render types, the
// game's fixed two-light entity shading (_Shade 0 turns it off for flat sprites), and two instanced per-object colours:
// _Color multiplies (tint such as wool dye, times the world light at the entity) and _Overlay mixes towards a colour
// (the red hurt flash, rgb + amount in a). Instancing-friendly: objects sharing a mesh and material batch even with
// different colours. Shipped through Assets/Resources/TabletopEntity.mat (Resources are always in the build).
Shader "TabletopCraft/Entity"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Cutoff ("Alpha cutoff", Range(0,1)) = 0.1
        _Shade ("Entity shading (0 = flat)", Float) = 1
        _Color ("Tint", Color) = (1,1,1,1)
        _Overlay ("Overlay (rgb, amount)", Color) = (1,0,0,0)
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst blend", Float) = 0
        _ZWrite ("ZWrite", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" }
        Cull Off
        Blend [_SrcBlend] [_DstBlend], One OneMinusSrcAlpha   // [passthrough] alpha "over": translucent pixels never lower the alpha behind them (the room would show through)
        ZWrite [_ZWrite]
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            #include "TabletopFog.cginc"    // eye-in-fluid fog (EyeEffects.cs), like the blocks and players
            #include "TabletopClip.cginc"   // entities outside the visible box are hidden, like the blocks
            sampler2D _MainTex;
            float _Cutoff, _Shade;
            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(fixed4, _Color)
                UNITY_DEFINE_INSTANCED_PROP(fixed4, _Overlay)
            UNITY_INSTANCING_BUFFER_END(Props)
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; half4 color : COLOR; half4 overlay : TEXCOORD1; float3 fogPos : TEXCOORD5; TABLETOP_CLIP_COORDS(7) UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert (appdata v)
            {
                v2f o; UNITY_SETUP_INSTANCE_ID(v); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv;
                TABLETOP_TRANSFER_CLIP(o, v.vertex)
                o.fogPos = TabletopFogPos(v.vertex);
                // Lighting.setupFor3DItems-style entity light: two fixed directions, 0.4 ambient + 0.6 diffuse.
                float3 n = normalize(UnityObjectToWorldNormal(v.normal));
                float3 l0 = normalize(float3(0.2, 1.0, -0.7)), l1 = normalize(float3(-0.2, 1.0, 0.7));
                float s = min(1.0, 0.4 + 0.6 * (max(0.0, dot(n, l0)) + max(0.0, dot(n, l1))));
                s = lerp(1.0, s, _Shade);
                // The shade factor is a display-space (sRGB) multiplier; _Color/_Overlay arrive linear (SetColor converts).
                #ifndef UNITY_COLORSPACE_GAMMA
                s = GammaToLinearSpaceExact(s);
                #endif
                fixed4 c = UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
                o.color = half4(c.rgb * s, c.a);
                o.overlay = UNITY_ACCESS_INSTANCED_PROP(Props, _Overlay);
                return o;
            }
            fixed4 frag (v2f i) : SV_Target
            {
                TABLETOP_CLIP(i);
                fixed4 t = tex2D(_MainTex, i.uv);
                clip(t.a - _Cutoff);
                fixed3 rgb = lerp(t.rgb, i.overlay.rgb, i.overlay.a) * i.color.rgb;
                return fixed4(TabletopFog(rgb, i.fogPos), t.a * i.color.a);
            }
            ENDCG
        }
    }
}
