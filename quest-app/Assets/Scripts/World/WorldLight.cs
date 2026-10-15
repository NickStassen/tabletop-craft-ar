// [living] Time of day and weather on the table (PROTOCOL.md "World state: time and weather"; DayLight.cs). Keeps the
// server's world_state (WorldClock: the clock runs between messages, rain and thunder fade like vanilla's), turns it into
// the lightmap inputs every frame and, when they change, (1) sets BlockShading.Environment so entities and players take
// the new light and (2) uploads the 16x16 ratio table the block shaders multiply their noon colours by
// (TabletopDayLight.cginc). Before the first world_state, and at noon in clear weather, the shaders skip the lookup.
using System.Collections.Generic;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class WorldLight : MonoBehaviour
    {
        /// <summary>tabletop.json "weather_fx": rain, snow and lightning bolts on the table (the light follows the time of
        /// day and the weather either way).</summary>
        public static bool WeatherFx = true;

        public readonly WorldClock Clock = new WorldClock();
        public LightEnv Current { get; private set; } = LightEnv.Noon;

        static readonly int RatioId = Shader.PropertyToID("_TabletopLightRatio"), OnId = Shader.PropertyToID("_TabletopLightRatioOn"),
                            ScaleId = Shader.PropertyToID("_TabletopLightRatioScale");
        Texture2D _ratio;
        readonly float[] _rgb = new float[256 * 3];
        bool _half, _applied;
        float _brightness = -1, _minLight = -2;

        void Awake()
        {
            _half = SystemInfo.SupportsTextureFormat(TextureFormat.RGBAHalf);
            _ratio = new Texture2D(16, 16, _half ? TextureFormat.RGBAHalf : TextureFormat.RGBA32, false, true)
            { name = "tabletop light ratio", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            Shader.SetGlobalTexture(RatioId, _ratio);
            Shader.SetGlobalFloat(ScaleId, _half ? 1f : 4f);
            Shader.SetGlobalFloat(OnId, 0f);
        }

        /// <summary>A world_state message.</summary>
        public void Apply(Dictionary<string, object> m) => Clock.Apply(m);

        /// <summary>A lightning bolt appeared: the sky flashes (vanilla's 2 ticks).</summary>
        public void Flash() => Clock.Flash();

        /// <summary>The link went down or the server changed: back to noon until the next world_state.</summary>
        public void Reset() { Clock.Known = false; }

        void Update()
        {
            Clock.Step(Time.deltaTime);
            var env = Clock.Env;
            bool settings = !Mathf.Approximately(_brightness, BlockShading.Brightness) || !Mathf.Approximately(_minLight, BlockShading.MinLight);
            if (_applied && !settings && env.SameAs(Current)) return;
            _applied = true; _brightness = BlockShading.Brightness; _minLight = BlockShading.MinLight;
            Current = env;
            BlockShading.Environment = env;
            bool noon = env.SameAs(LightEnv.Noon);
            Shader.SetGlobalFloat(OnId, noon ? 0f : 1f);
            if (noon) return;
            DayLight.Ratios(env, _rgb);
            var px = new Color[256];
            float k = _half ? 1f : 0.25f;
            for (int c = 0; c < 256; c++)
                px[c] = new Color(_rgb[c * 3] * k, _rgb[c * 3 + 1] * k, _rgb[c * 3 + 2] * k, 1);   // row = sky, column = block
            _ratio.SetPixels(px);
            _ratio.Apply(false, false);
        }

        void OnDestroy()
        {
            Shader.SetGlobalFloat(OnId, 0f);
            if (_ratio != null) Destroy(_ratio);
        }
    }
}
