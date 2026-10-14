// Tint over the passthrough while the eye is in water/lava/powder snow/inside a block (EyeEffects.cs). Drawn on a box
// around the head in the Background queue, before any block, with ZTest Always and no depth write. Blocks then draw
// over it (they carry their own fog), so it only tints what is behind the miniature: the passthrough, through the
// eye buffer's alpha. Premultiplied output (rgb * a, a): over the cleared buffer the compositor sees the tint at
// opacity a. Built-in render pipeline, single-pass instanced stereo friendly.
// Shipped through Assets/Resources/TabletopEyeOverlay.mat (Resources are always in the build).
Shader "TabletopCraft/EyeOverlay"
{
    Properties { _Tint ("Tint (rgb in render colour space, a = opacity)", Vector) = (0, 0, 0, 0) }
    SubShader
    {
        Tags { "Queue"="Background+10" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Cull Off ZWrite Off ZTest Always
        Blend One OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _Tint;
            struct appdata { float4 vertex : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert (appdata v)
            {
                v2f o; UNITY_SETUP_INSTANCE_ID(v); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }
            fixed4 frag (v2f i) : SV_Target { return fixed4(_Tint.rgb * _Tint.a, _Tint.a); }
            ENDCG
        }
    }
}
