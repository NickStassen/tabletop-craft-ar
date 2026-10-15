// [blocks] Unlit vertex-coloured texture for block-entity extras (BlockEntityRenderer): sign text from the pack's font
// (alpha-tested, pulled towards the camera like vanilla's POLYGON_OFFSET text), beacon beams (the core alpha-tested, the
// glow blended). Vertex colours are sRGB (lit text: dye colour x light) and alpha multiplies the texture's. Clipped by
// the nav window and fogged like the blocks. Shipped through Assets/Resources/TabletopBlockEntityFx.mat.
Shader "TabletopCraft/BlockEntityFx"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Cutoff ("Alpha cutoff", Range(0,1)) = 0.1
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst blend", Float) = 0
        _ZWrite ("ZWrite", Float) = 1
        _OffsetFactor ("Depth offset factor", Float) = 0
        _OffsetUnits ("Depth offset units", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" }
        Cull Off
        Blend [_SrcBlend] [_DstBlend]
        ZWrite [_ZWrite]
        Offset [_OffsetFactor], [_OffsetUnits]
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "TabletopFog.cginc"    // eye-in-fluid fog (EyeEffects.cs)
            #include "TabletopClip.cginc"   // [nav] clip volume (XR/NavController)
            sampler2D _MainTex;
            float _Cutoff;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; half4 color : COLOR; float3 fogPos : TEXCOORD5; TABLETOP_CLIP_COORDS(7) UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert (appdata v)
            {
                v2f o; UNITY_SETUP_INSTANCE_ID(v); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv;
                o.fogPos = TabletopFogPos(v.vertex);   // eye-in-fluid fog
                TABLETOP_TRANSFER_CLIP(o, v.vertex)   // [nav] clip volume
                o.color = v.color;
                #ifndef UNITY_COLORSPACE_GAMMA
                o.color.rgb = GammaToLinearSpace(o.color.rgb);   // colours are sRGB like the blocks' vertex colours
                #endif
                return o;
            }
            fixed4 frag (v2f i) : SV_Target
            {
                TABLETOP_CLIP(i)   // [nav] clip volume
                fixed4 c = tex2D(_MainTex, i.uv) * i.color;
                clip(c.a - _Cutoff);
                c.rgb = TabletopFog(c.rgb, i.fogPos);   // eye-in-fluid fog
                return c;
            }
            ENDCG
        }
    }
}
