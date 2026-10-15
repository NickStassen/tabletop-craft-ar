// [motion] Ambient particles on the table (World/AmbientParticles.cs, World/AmbientParticleRenderer.cs). One static
// mesh: a quad per looping slot holds its emitter (spawn box, rate as a period and a spawn chance, lifetime, size,
// sprite range, light) and this shader does the rest: which cycle the slot is in (time / period), whether the slot
// spawned this cycle (a hash against the chance), where the particle is after `age` seconds (one motion per vanilla
// particle class: flame, smoke, campfire smoke, lava pop, portal, falling leaf, firefly, bubbles, drip), its sprite
// (the pack's textures/particle/*.png as a texture array), size and fade, turned to face the camera. Nothing on the
// CPU per frame. Clipped by the visible window like the blocks; lit like the blocks (the light code at the emitter
// through the day/night ratio table) unless full bright; fireflies fade in with the dark (_Fireflies).
// Shipped through Assets/Resources/TabletopParticles.mat (Resources are always in the build).
Shader "TabletopCraft/Particles"
{
    Properties
    {
        _ParticleTime ("Seconds", Float) = 0
        _Fireflies ("Firefly visibility 0..1", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+5" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha   // [passthrough] alpha "over": translucent pixels never lower the alpha behind them (the room would show through)
        ZWrite Off
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma require 2darray
            #include "UnityCG.cginc"
            #include "TabletopFog.cginc"
            #include "TabletopClip.cginc"       // [nav] clip volume: particles outside the visible window are hidden
            #include "TabletopDayLight.cginc"   // _TabletopLightRatio: time of day and weather on the emitter's light
            UNITY_DECLARE_TEX2DARRAY(_ParticleTex);
            float _ParticleTime, _Fireflies;

            // vertex: emitter spawn point (region-local blocks, Z not yet mirrored)
            // uv0: corner x, y (-0.5..0.5), motion, seed 0..1     uv1: period (s), spawn chance, longest life (s), size (blocks)
            // uv2: spawn half extents xyz, reach (blocks; portal: the axis it crosses)   uv3: first sprite, sprites, light code, bright
            struct appdata { float4 vertex : POSITION; float4 a : TEXCOORD0; float4 b : TEXCOORD1; float4 c : TEXCOORD2; float4 d : TEXCOORD3; fixed4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float3 uv : TEXCOORD0; fixed4 color : COLOR; float3 fogPos : TEXCOORD5; TABLETOP_CLIP_COORDS(7) UNITY_VERTEX_OUTPUT_STEREO };

            float4 Hash4(float2 p)   // Dave Hoskins' hash without sine
            {
                float4 p4 = frac(p.xyxy * float4(0.1031, 0.1030, 0.0973, 0.1099));
                p4 += dot(p4, p4.wzxy + 33.33);
                return frac((p4.xxyz + p4.yzzw) * p4.zywx);
            }

            v2f vert (appdata v)
            {
                v2f o; UNITY_SETUP_INSTANCE_ID(v); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float motion = v.a.z, seed = v.a.w, period = max(v.b.x, 0.05), chance = v.b.y, life = v.b.z, size = v.b.w;
                float3 spread = v.c.xyz; float reach = v.c.w;
                float sprite0 = v.d.x, sprites = max(v.d.y, 1.0), code = v.d.z;
                float tc = _ParticleTime / period + seed;
                float cycle = floor(tc), age = frac(tc) * period;
                if (motion > 9.5) { cycle = 0; age = _ParticleTime - v.b.x; chance = 1; }   // one-shot: uv1.x is its start time
                float4 r = Hash4(float2(seed * 4096.0 + 0.5, cycle));      // spawn chance, spawn offset
                float4 q = Hash4(float2(seed * 4096.0 + 17.25, cycle));    // lifetime, velocities, sprite
                float4 h = Hash4(float2(seed * 4096.0 + 41.75, cycle));    // more
                float3 p = v.vertex.xyz + (r.yzw * 2.0 - 1.0) * spread;
                float L = life, sz = size, spin = 0, alpha = 1, idx = floor(q.w * sprites * 0.9999);
                fixed4 col = v.color;
                int m = (int)(motion + 0.5);
                if (m == 0)        // FlameParticle: 8/(r*0.8+0.2)+4 ticks, still, shrinks to 1 - s^2/2
                {
                    L = (8.0 / (q.x * 0.8 + 0.2) + 4.0) / 20.0;
                    float s = saturate(age / L);
                    p.y += 0.03 * age;
                    sz *= 1.0 - s * s * 0.5;
                }
                else if (m == 1)   // SmokeParticle (BaseAshSmokeParticle): rises (gravity -0.1, friction 0.96), sprite by age
                {
                    L = life * 0.2 / (q.x * 0.8 + 0.2);
                    float s = saturate(age / L), k = 0.816;
                    p.y += 1.92 * (age - (1.0 - exp(-k * age)) / k);
                    p.xz += (q.yz - 0.5) * 0.2 * age;
                    sz *= saturate(s * 32.0);
                    idx = min(floor(s * (sprites - 1.0) + 0.0001), sprites - 1.0);
                    if (code < 254.5) col.rgb *= q.y * 0.6 + 0.4;   // vanilla smoke greys vary per particle
                }
                else if (m == 2)   // CampfireSmokeParticle: rises 0.07 blocks a tick, sways, fades over its last 3 s
                {
                    L = life * (0.85 + 0.15 * q.x);
                    p.y += 1.4 * age;
                    p.xz += float2(sin(age * 0.6 + h.x * 6.28), cos(age * 0.5 + h.y * 6.28)) * 0.04 * age;
                    sz *= 0.7 + 0.6 * q.y;
                    alpha = saturate((L - age) / 3.0);
                }
                else if (m == 3)   // LavaParticle: thrown up 0.05-0.45 blocks a tick, gravity 0.03 a tick^2, lands, shrinks
                {
                    L = 16.0 / (q.x * 0.8 + 0.2) / 20.0;
                    float vy = (q.y * 0.4 + 0.05) * 20.0, land = vy / 6.0, t = min(age, land);
                    p.y += vy * t - 6.0 * t * t;
                    p.xz += (h.xy - 0.5) * 1.2 * t;
                    float s = saturate(age / L);
                    sz *= (h.z * 2.0 + 0.2) * 0.5 * (1.0 - s * s);
                }
                else if (m == 4)   // PortalParticle: starts out at a random offset and is drawn back to the block, one up
                {
                    L = (q.x * 10.0 + 40.0) / 20.0;
                    float s = saturate(age / L), flip = h.x < 0.5 ? -1.0 : 1.0;
                    float3 d = (q.yzw - 0.5) * 0.5;
                    if (reach < 1.0) { p.x = v.vertex.x + 0.25 * flip; d.x = h.y * 2.0 * flip; }
                    else { p.z = v.vertex.z + 0.25 * flip; d.z = h.y * 2.0 * flip; }
                    float P = 1.0 - (-s + s * s * 2.0);
                    p += d * P + float3(0, 1.0 - P, 0);
                    float g = 1.0 - s; g = 1.0 - g * g;
                    sz *= (h.w * 0.2 + 0.5) / 0.6 * g;
                    float f = h.z * 0.6 + 0.4;
                    col.rgb = float3(0.9 * f, 0.3 * f, f);
                    idx = floor(h.w * sprites * 0.9999);
                }
                else if (m == 5)   // FallingLeavesParticle (26.x): drifts down swaying and turning, rests where it lands
                {
                    float fall = 0.8 + 0.4 * q.x, land = reach / fall;
                    L = min(life, land + 1.0);
                    float t = min(age, land);
                    p.y -= fall * t;
                    p.xz += float2(sin(t * 1.5 + h.x * 6.28) - sin(h.x * 6.28), cos(t * 1.1 + h.y * 6.28) - cos(h.y * 6.28)) * 0.25;
                    spin = t * (h.z - 0.5) * 4.0 + h.w * 6.28;
                    sz *= 0.8 + 0.4 * q.y;
                    alpha = saturate(L - age);
                }
                else if (m == 6)   // FireflyParticle: wanders, fades in and out, glows; only in the dark
                {
                    L = life * (0.6 + 0.4 * q.x);
                    float s = saturate(age / L);
                    p += float3(sin(age * 0.9 + h.x * 6.28), 0.5 * sin(age * 1.3 + h.y * 6.28), cos(age * 0.7 + h.z * 6.28)) * 0.6;
                    alpha = saturate(s * 5.0) * saturate((1.0 - s) * 3.0) * saturate(_Fireflies) * (0.75 + 0.25 * sin(age * 7.0 + h.w * 6.28));
                    sz *= 0.75;
                }
                else if (m == 7)   // BubbleColumnUpParticle: rises (terminal ~0.6 blocks/s), pops at the column's top
                {
                    float up = 0.6 * age + 0.2 * (1.0 - exp(-3.0 * age));
                    L = min(life * 0.2 / (q.x * 0.8 + 0.2), reach / 0.6);
                    p.y += up;
                    p.xz += float2(sin(age * 6.0 + h.x * 6.28), cos(age * 5.0 + h.y * 6.28)) * 0.04;
                    sz *= (q.y * 0.6 + 0.2) / 0.5;
                }
                else if (m == 8)   // WaterCurrentDownParticle: whirls down to the column's bottom
                {
                    L = min((q.x * 60.0 + 30.0) / 20.0, max(reach, 0.1) / 0.6);
                    float ang = age * 4.0 + h.x * 6.28;
                    p += float3(cos(ang) * 0.25, -0.6 * age, sin(ang) * 0.25);
                }
                else if (m == 10)  // HugeExplosionParticle: 6-9 ticks, its 16 frames by age, grey 0.4-1, 2-4 blocks wide
                {
                    L = (6.0 + floor(q.x * 4.0)) / 20.0;
                    float s = saturate(age / L);
                    idx = min(floor(s * sprites), sprites - 1.0);
                    sz *= 1.0 - q.y * 0.5;
                    col.rgb *= q.z * 0.6 + 0.4;
                }
                else if (m == 11)  // the flash: a white glow that fades in 0.15 s
                {
                    L = life;
                    alpha = saturate(1.0 - age / L);
                }
                else               // DripParticle: hangs under the ceiling for 2 s, then falls to the floor (drip_hang, drip_fall)
                {
                    float hang = 2.0, g = 8.0, fallT = sqrt(2.0 * max(reach, 0.05) / g);
                    L = hang + fallT;
                    float t = max(age - hang, 0.0);
                    p.y -= 0.5 * g * t * t;
                    idx = age < hang ? 0.0 : 1.0;
                    sz *= age < hang ? 0.5 + 0.5 * saturate(age / hang) : 1.0;
                }
                if (r.x >= chance || age >= L || age < 0) sz = 0;   // this slot didn't spawn in this cycle, or its particle is gone

                float3 c = float3(p.x, p.y, -p.z);   // region-local blocks, Z mirrored like the blocks
                float2 k = v.a.xy, cs = float2(cos(spin), sin(spin));
                k = float2(k.x * cs.x - k.y * cs.y, k.x * cs.y + k.y * cs.x);
                float3 right = normalize(mul((float3x3)unity_WorldToObject, UNITY_MATRIX_V[0].xyz));
                float3 upv = normalize(mul((float3x3)unity_WorldToObject, UNITY_MATRIX_V[1].xyz));
                float4 vert = float4(c + (right * k.x + upv * k.y) * sz, 1);
                o.pos = UnityObjectToClipPos(vert);
                TABLETOP_TRANSFER_CLIP(o, vert)
                o.fogPos = TabletopFogPos(vert);
                o.uv = float3(v.a.xy + 0.5, sprite0 + idx);
                if (code < 254.5 && _TabletopLightRatioOn > 0.5)
                {
                    float sky = floor(code / 16.0), blk = code - sky * 16.0;
                    col.rgb *= tex2Dlod(_TabletopLightRatio, float4((blk + 0.5) / 16.0, (sky + 0.5) / 16.0, 0, 0)).rgb * _TabletopLightRatioScale;
                }
                col.a *= alpha;
                #ifndef UNITY_COLORSPACE_GAMMA
                col.rgb = GammaToLinearSpace(col.rgb);
                #endif
                o.color = col;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                TABLETOP_CLIP(i)
                fixed4 c = UNITY_SAMPLE_TEX2DARRAY(_ParticleTex, i.uv) * i.color;
                clip(c.a - 0.1);
                c.rgb = TabletopFog(c.rgb, i.fogPos);
                return c;
            }
            ENDCG
        }
    }
}
