// [living] Rain and snow over the miniature (World/WeatherRenderer.cs, World/Precipitation.cs). One static mesh: a quad
// per drop holds its column (x, ground, z in block units, Z already mirrored) and the shader moves it: each drop falls
// from the top of its column to the ground (the highest motion-blocking block, as vanilla's precipitation height) and
// starts again at the top, turned about Y to face the camera. Rain is a thin soft streak, snow a soft flake drifting
// sideways. No textures, cheap fragments; clipped by the visible window like the blocks, faded by the rain level.
// Shipped through Assets/Resources/TabletopWeather.mat (Resources are always in the build).
Shader "TabletopCraft/Weather"
{
    Properties
    {
        _RainColor ("Rain colour", Color) = (0.62, 0.70, 0.86, 0.55)
        _SnowColor ("Snow colour", Color) = (1, 1, 1, 0.9)
        _Strength ("Rain level 0..1", Float) = 1
        _Light ("Sky light at the surface (rgb)", Color) = (1, 1, 1, 1)
        _RainSize ("Rain width, length (blocks)", Vector) = (0.12, 1.6, 0, 0)
        _SnowSize ("Snow flake size (blocks)", Float) = 0.3
        _WeatherTime ("Seconds", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+10" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "TabletopFog.cginc"
            #include "TabletopClip.cginc"   // [nav] clip volume: drops outside the visible window are hidden
            fixed4 _RainColor, _SnowColor, _Light;
            float _Strength, _SnowSize, _WeatherTime;
            float4 _RainSize;
            // vertex: column base (x, ground y, z); uv0: corner (-0.5..0.5, 0..1); uv1: top y, phase 0..1, speed (blocks/s), kind (0 rain, 1 snow)
            struct appdata { float4 vertex : POSITION; float2 corner : TEXCOORD0; float4 drop : TEXCOORD1; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float2 corner : TEXCOORD0; fixed4 color : COLOR; float kind : TEXCOORD1; float3 fogPos : TEXCOORD5; TABLETOP_CLIP_COORDS(7) UNITY_VERTEX_OUTPUT_STEREO };

            v2f vert (appdata v)
            {
                v2f o; UNITY_SETUP_INSTANCE_ID(v); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float ground = v.vertex.y, top = v.drop.x, span = max(top - ground, 0.01);
                bool snow = v.drop.w > 0.5;
                float t = frac(_WeatherTime * v.drop.z / span + v.drop.y);
                float3 c = float3(v.vertex.x, top - t * span, v.vertex.z);
                if (snow)
                {
                    // WeatherEffectRenderer's snow sways sideways as it falls
                    float sway = _WeatherTime * 0.7 + v.drop.y * 31.0;
                    c.x += sin(sway) * 0.35; c.z += cos(sway * 0.8) * 0.35;
                }
                // Turn about Y to face the camera (object space; the parent is scaled uniformly).
                float3 cam = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1)).xyz;
                float2 d = cam.xz - c.xz;
                float len = max(length(d), 1e-4);
                float3 right = float3(d.y / len, 0, -d.x / len);
                float2 size = snow ? float2(_SnowSize, _SnowSize) : _RainSize.xy;
                float3 p = c + right * (v.corner.x * size.x) + float3(0, (v.corner.y - (snow ? 0.5 : 0.0)) * size.y, 0);
                float4 vert = float4(p, 1);
                o.pos = UnityObjectToClipPos(vert);
                TABLETOP_TRANSFER_CLIP(o, vert)
                o.fogPos = TabletopFogPos(vert);
                o.corner = v.corner;
                o.kind = snow ? 1 : 0;
                fixed4 col = snow ? _SnowColor : _RainColor;
                col.rgb *= _Light.rgb;
                col.a *= saturate(_Strength) * saturate(t * 8.0);   // fade in at the top
                #ifndef UNITY_COLORSPACE_GAMMA
                col.rgb = GammaToLinearSpace(col.rgb);
                #endif
                o.color = col;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                TABLETOP_CLIP(i)
                float a;
                if (i.kind > 0.5) { float2 q = float2(i.corner.x, i.corner.y - 0.5) * 2.0; a = saturate(1.0 - dot(q, q)); }   // soft round flake
                else a = saturate(1.0 - abs(i.corner.x) * 2.0) * saturate(i.corner.y * 3.0) * saturate((1.0 - i.corner.y) * 1.5);   // streak, tapered
                fixed4 c = i.color;
                c.a *= a;
                clip(c.a - 0.004);
                c.rgb = TabletopFog(c.rgb, i.fogPos);
                return c;
            }
            ENDCG
        }
    }
}
