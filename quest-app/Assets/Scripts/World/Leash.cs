// [living] Lead ropes and fishing lines as 26.3 draws them (engine-free; Tests~ checks them against the game's formulas).
//
//   Leads (EntityRenderer.extractRenderState + LeashFeatureRenderer): the rope starts at the leashed entity's position
//   plus its leash offset turned by its body yaw (Leashable.getLeashOffset: eye height up and 0.4 x width forward by
//   default, per-type factors below) and ends at the holder's rope hold point (Entity.getRopeHoldPosition: 0.7 x eye
//   height up; a knot 0.2 up; a player's hand; a villager's front). 24 steps, two crossed ribbons (0.05 blocks), the
//   pairs alternating dark (0.7) and light brown (0.5, 0.4, 0.3), sagging (y = dy t^2 when the holder is higher, else
//   dy - dy (1 - t)^2), each pair lit by the light lerped between the two ends. Boats and camels held by a happy ghast
//   hang from 4 ropes (createQuadLeashOffsets), taut.
//   Fishing line (FishingHookRenderer): 16 segments from the bobber (0.25 up) to the rod hand of its owner
//   (getPlayerHandPos), y = dy (t^2 + t) / 2, black.
// Coordinates are Minecraft world coordinates (x east, y up, z south), angles in degrees as the protocol sends them.
using System.Collections.Generic;
using UnityEngine;

namespace TabletopCraft.World
{
    public static class Leash
    {
        public const int Steps = 24;
        public const float Width = 0.05f;

        /// <summary>Leashable.getLeashOffset before the body-yaw turn: (0, y, z) in the entity's frame (z = forward).</summary>
        public static Vector3 Offset(string type, float eye, float width, float height)
        {
            string t = type != null && type.StartsWith("minecraft:") ? type.Substring(10) : type ?? "";
            if (t.EndsWith("_boat") || t.EndsWith("_raft")) return new Vector3(0, 0.88f * height, 0.64f * width);   // AbstractBoat
            switch (t)
            {
                case "allay": return new Vector3(0, 0.6f * eye, 0.1f * width);
                case "bee": return new Vector3(0, 0.5f * eye, 0.2f * width);
                case "llama": case "trader_llama": return new Vector3(0, 0.75f * eye, 0.5f * width);
                case "ocelot": case "parrot": return new Vector3(0, 0.5f * eye, 0.4f * width);
                case "fox": return new Vector3(0, 0.55f * eye, 0.4f * width);
                case "copper_golem": return new Vector3(0, 0.75f * eye, 0);
                case "iron_golem": return new Vector3(0, 0.875f * eye, 0.4f * width);
                case "snow_golem": return new Vector3(0, 0.75f * eye, 0.4f * width);
                case "pig": case "rabbit": case "wolf": case "strider": return new Vector3(0, 0.6f * eye, 0.4f * width);
                case "happy_ghast": return Vector3.zero;
                case "sulfur_cube": return new Vector3(0, height / 2f, 0);
                case "camel": case "camel_husk": return new Vector3(0, height - 0.6f, 0.56f * width);   // body anchor, standing (approximation)
                default: return new Vector3(0, eye, 0.4f * width);   // Leashable default
            }
        }

        /// <summary>Vec3.yRot(-bodyYaw) of an offset in the entity's frame: world coordinates relative to the entity.</summary>
        public static Vector3 Turn(Vector3 v, float bodyYawDeg)
        {
            float a = -bodyYawDeg * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
            return new Vector3(v.x * c + v.z * s, v.y, v.z * c - v.x * s);
        }

        /// <summary>Leads that hang from 4 points: boats and camels (supportQuadLeash) held by a happy ghast
        /// (supportQuadLeashAsHolder).</summary>
        public static bool QuadLeashed(string type) { string t = Path(type); return t.EndsWith("_boat") || t.EndsWith("_raft") || t == "camel" || t == "camel_husk"; }
        public static bool QuadHolder(string type) => Path(type) == "happy_ghast";

        static string Path(string type) => type != null && type.StartsWith("minecraft:") ? type.Substring(10) : type ?? "";

        /// <summary>Leashable.createQuadLeashOffsets(entity, a, b, c, d): the 4 corners in the entity's frame.</summary>
        public static Vector3[] QuadOffsets(float width, float height, float a, float b, float c, float d)
        {
            float da = a * width, db = b * width, dc = c * width, dd = d * height;
            return new[] { new Vector3(-dc, dd, db + da), new Vector3(-dc, dd, -db + da), new Vector3(dc, dd, -db + da), new Vector3(dc, dd, db + da) };
        }

        /// <summary>The leashed side's 4 corners (AbstractBoat 0, 0.64, 0.382, 0.88; Camel 0.02, 0.48, 0.25, 0.82).</summary>
        public static Vector3[] QuadLeashedOffsets(string type, float width, float height) =>
            Path(type) == "camel" || Path(type) == "camel_husk" ? QuadOffsets(width, height, 0.02f, 0.48f, 0.25f, 0.82f) : QuadOffsets(width, height, 0f, 0.64f, 0.382f, 0.88f);

        /// <summary>The happy ghast's 4 holder corners (HappyGhast.getQuadLeashHolderOffsets).</summary>
        public static Vector3[] QuadHolderOffsets(float width, float height) => QuadOffsets(width, height, -0.03125f, 0.4375f, 0.46875f, 0.03125f);

        /// <summary>Entity.getRopeHoldPosition for non-players: a knot 0.2 up, villagers and wandering traders in front of
        /// their chest, everything else 0.7 x eye height up.</summary>
        public static Vector3 Hold(string type, Vector3 pos, float eye, float height, float bodyYawDeg)
        {
            string t = Path(type);
            if (t == "leash_knot") return pos + new Vector3(0, 0.2f, 0);
            if (t == "villager" || t == "wandering_trader") return pos + Turn(new Vector3(0, height - 1f, 0.2f), bodyYawDeg);
            return pos + new Vector3(0, eye * 0.7f, 0);
        }

        /// <summary>Player.getRopeHoldPosition (walking, sneaking; right-handed): the hand 0.22 to the left of the body
        /// centre, the box height minus 1 up, a little forward (back when sneaking).</summary>
        public static Vector3 PlayerHold(Vector3 feet, float bodyYawDeg, bool sneaking)
        {
            float height = sneaking ? 1.5f : 1.8f;
            return feet + Turn(new Vector3(-0.22f, height - 1f, sneaking ? -0.2f : 0.07f), bodyYawDeg);
        }

        /// <summary>FishingHookRenderer.getPlayerHandPos (third person, right-handed, holding the rod in the main hand).</summary>
        public static Vector3 RodHand(Vector3 feet, float bodyYawDeg, bool sneaking)
        {
            float j = bodyYawDeg * Mathf.Deg2Rad, d = Mathf.Sin(j), e = Mathf.Cos(j);
            float l = 0.35f, m = 0.8f, n = sneaking ? -0.1875f : 0f, eye = sneaking ? 1.27f : 1.62f;
            return feet + new Vector3(-e * l - d * m, eye + n - 0.45f, -d * l + e * m);
        }

        /// <summary>
        /// The rope from start to end as triangles (two ribbons, 2 x 24 quads), with vertex colours already lit:
        /// light(t) gives the lightmap colour at fraction t (vanilla lerps the block and sky levels of the two ends).
        /// Appends world-space vertices, colours and triangle indices.
        /// </summary>
        public static void Rope(Vector3 start, Vector3 end, bool slack, System.Func<float, Vector3> light, List<Vector3> verts, List<Color32> colors, List<int> tris, float thick = 1f)
        {
            float dx = end.x - start.x, dy = end.y - start.y, dz = end.z - start.z;
            float len = Mathf.Sqrt(dx * dx + dz * dz);
            float k = len > 1e-5f ? 0.025f * thick / len : 0f;   // thick: a minimum size on the table (1 = vanilla)
            float ox = dz * k, oz = dx * k;
            Strip(start, dx, dy, dz, ox, oz, Width * thick, false, slack, light, verts, colors, tris, Width * thick);
            Strip(start, dx, dy, dz, ox, oz, 0f, true, slack, light, verts, colors, tris, Width * thick);
        }

        static void Strip(Vector3 s, float dx, float dy, float dz, float ox, float oz, float width, bool back, bool slack, System.Func<float, Vector3> light,
                          List<Vector3> verts, List<Color32> colors, List<int> tris, float full)
        {
            int first = verts.Count;
            for (int n = 0; n <= Steps; n++)
            {
                int i = back ? Steps - n : n;   // the second ribbon walks back from the holder, as vanilla's strip does
                float t = i / (float)Steps;
                float shade = i % 2 == (back ? 1 : 0) ? 0.7f : 1f;
                var l = light != null ? light(t) : Vector3.one;
                var c = new Color32(B(0.5f * shade * l.x), B(0.4f * shade * l.y), B(0.3f * shade * l.z), 255);
                float x = dx * t, y = slack ? (dy > 0 ? dy * t * t : dy - dy * (1 - t) * (1 - t)) : dy * t, z = dz * t;
                verts.Add(s + new Vector3(x - ox, y + width, z + oz)); colors.Add(c);
                verts.Add(s + new Vector3(x + ox, y + full - width, z - oz)); colors.Add(c);
            }
            for (int n = 0; n < Steps; n++)
            {
                int a = first + 2 * n;   // strip pairs (a, a+1) and (a+2, a+3)
                tris.Add(a); tris.Add(a + 1); tris.Add(a + 2);
                tris.Add(a + 1); tris.Add(a + 3); tris.Add(a + 2);
            }
        }

        static byte B(float v) => (byte)Mathf.Clamp((int)(v * 255f + 0.5f), 0, 255);

        /// <summary>FishingHookRenderer.stringVertex: the line's 17 points from the bobber (0.25 up) to the hand.</summary>
        public static Vector3[] FishingLine(Vector3 hook, Vector3 hand)
        {
            var p = new Vector3[17];
            float dx = hand.x - hook.x, dy = hand.y - (hook.y + 0.25f), dz = hand.z - hook.z;
            for (int k = 0; k <= 16; k++)
            {
                float t = k / 16f;
                p[k] = new Vector3(hook.x + dx * t, hook.y + dy * (t * t + t) * 0.5f + 0.25f, hook.z + dz * t);
            }
            return p;
        }
    }
}
