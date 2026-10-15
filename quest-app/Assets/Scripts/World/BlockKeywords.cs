// [gpu] Global shader keywords of the block shaders (Shaders/Blocks.shader, BlocksTransparent.shader): features that
// used to be runtime branches in every vertex or pixel are compiled in only while they are needed.
//   TABLETOP_FOG          eye in a fluid (EyeEffects): the fog varying and the per-pixel distance
//   TABLETOP_DAYLIGHT     not noon in clear weather (WorldLight): the light-ratio lookup per vertex
//   TABLETOP_CLIP_PLANES  the visible window cut by hardware clip planes instead of a discard (NavController,
//                         tabletop.json "clip_planes"): opaque terrain keeps early depth rejection
// Other shaders that include the same .cginc files keep their runtime paths (they don't declare the keywords).
using UnityEngine;

namespace TabletopCraft.World
{
    public static class BlockKeywords
    {
        public const string Fog = "TABLETOP_FOG", DayLight = "TABLETOP_DAYLIGHT", ClipPlanes = "TABLETOP_CLIP_PLANES";

        /// <summary>tabletop.json "clip_planes" (default on). Off: the fragment discard of 0.8.0.</summary>
        public static bool UseClipPlanes = true;

        /// <summary>Sets a global keyword when it changes (Shader.EnableKeyword is cheap, but this runs every frame).</summary>
        public static void Set(string keyword, bool on)
        {
            if (Shader.IsKeywordEnabled(keyword) == on) return;
            if (on) Shader.EnableKeyword(keyword); else Shader.DisableKeyword(keyword);
        }
    }
}
