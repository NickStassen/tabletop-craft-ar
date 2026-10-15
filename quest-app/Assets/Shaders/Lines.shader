// [living] Untextured, vertex-coloured geometry on the table: lead ropes (vanilla's leash render type: vertex colour x
// the light at its ends), fishing lines, lightning bolts (additive: _SrcBlend SrcAlpha, _DstBlend One) and text display
// backgrounds (alpha blended). _Color multiplies (the world light), both sides drawn, clipped by the visible window like
// the blocks and entities. Shipped through Assets/Resources/TabletopLines.mat; materials are instanced per use.
Shader "TabletopCraft/Lines"
{
    Properties
    {
        _Color ("Tint", Color) = (1,1,1,1)
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst blend", Float) = 0
        _ZWrite ("ZWrite", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Off
        Blend [_SrcBlend] [_DstBlend]
        ZWrite [_ZWrite]
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            #include "TabletopFog.cginc"
            #include "TabletopClip.cginc"   // [nav] clip volume
            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(fixed4, _Color)
            UNITY_INSTANCING_BUFFER_END(Props)
            struct appdata { float4 vertex : POSITION; fixed4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; float3 fogPos : TEXCOORD5; TABLETOP_CLIP_COORDS(7) UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert (appdata v)
            {
                v2f o; UNITY_SETUP_INSTANCE_ID(v); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                TABLETOP_TRANSFER_CLIP(o, v.vertex)
                o.fogPos = TabletopFogPos(v.vertex);
                fixed4 c = v.color;   // sRGB vertex colour; _Color arrives linear (SetColor converts)
                #ifndef UNITY_COLORSPACE_GAMMA
                c.rgb = GammaToLinearSpace(c.rgb);
                #endif
                o.color = c * UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
                return o;
            }
            fixed4 frag (v2f i) : SV_Target
            {
                TABLETOP_CLIP(i)
                return fixed4(TabletopFog(i.color.rgb, i.fogPos), i.color.a);
            }
            ENDCG
        }
    }
}
