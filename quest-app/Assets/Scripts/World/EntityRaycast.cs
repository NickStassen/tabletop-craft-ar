// [hands] Aiming at mobs and players, engine-free (Tests~): the right controller's ray (in block space, the same frame
// and units as BlockRaycast's DDA, so the distances compare) against the boxes of the entities as they are drawn (the
// eased positions from EntityRenderer and PlayerMarkers). The nearer of an entity and a block wins, as in vanilla.
// Boxes are the entity's size (at least half a block, so dropped items and chickens stay easy to hit); players are
// 0.6 x 1.8 blocks (1.5 sneaking). Only the part inside the visible window counts, like the terrain.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TabletopCraft.World
{
    /// <summary>One aimable entity: world feet position (eased, as drawn) and box size in blocks.</summary>
    public struct EntityTarget
    {
        public string Uuid, Type, Name;
        public int Id;
        public bool Player;
        public Vector3 Pos;
        public float Width, Height;

        /// <summary>"Pig "Rex"", a player's name, or the type ("Armor Stand").</summary>
        public string Label => Player ? Name : string.IsNullOrEmpty(Name) ? Net.BlockRegistry.DefaultLabel(Net.BlockRegistry.PathOf(Type ?? "?")) :
            Net.BlockRegistry.DefaultLabel(Net.BlockRegistry.PathOf(Type ?? "?")) + " \"" + Name + "\"";
    }

    public struct EntityHit
    {
        public bool Hit;
        public EntityTarget Target;
        public float Distance;    // along the ray, blocks
        public Vector3 Point;     // block space, region-local
    }

    public static class EntityRaycast
    {
        public const float MinSize = 0.5f, PlayerWidth = 0.6f, PlayerHeight = 1.8f, SneakHeight = 1.5f;

        /// <summary>The target's box in block space (region-local): centred on its feet, at least MinSize wide and tall.</summary>
        public static void Box(in EntityTarget t, Vector3Int origin, out Vector3 min, out Vector3 max)
        {
            float w = Math.Max(MinSize, t.Width), h = Math.Max(MinSize, t.Height);
            var p = t.Pos - (Vector3)origin;
            min = new Vector3(p.x - w / 2, p.y, p.z - w / 2);
            max = new Vector3(p.x + w / 2, p.y + h, p.z + w / 2);
        }

        /// <summary>Ray parameters where the ray is inside the box [lo, hi]; false when it misses.</summary>
        public static bool Slab(Vector3 o, Vector3 d, Vector3 lo, Vector3 hi, out float t0, out float t1)
        {
            t0 = float.NegativeInfinity; t1 = float.PositiveInfinity;
            for (int a = 0; a < 3; a++)
            {
                if (Math.Abs(d[a]) < 1e-9f)
                {
                    if (o[a] < lo[a] || o[a] > hi[a]) return false;
                    continue;
                }
                float ta = (lo[a] - o[a]) / d[a], tb = (hi[a] - o[a]) / d[a];
                if (ta > tb) { float s = ta; ta = tb; tb = s; }
                t0 = Math.Max(t0, ta); t1 = Math.Min(t1, tb);
                if (t0 > t1) return false;
            }
            return true;
        }

        /// <summary>
        /// The nearest target the ray from <paramref name="o"/> along <paramref name="dir"/> (block space, region-local; any
        /// length) hits within <paramref name="maxDist"/> blocks, counting only what is inside the window [lo, hi].
        /// <paramref name="skip"/>: a uuid to ignore (the one being carried).
        /// </summary>
        public static EntityHit Cast(IList<EntityTarget> targets, Vector3Int origin, Vector3 o, Vector3 dir, float maxDist, Vector3 lo, Vector3 hi, string skip = null)
        {
            var best = new EntityHit();
            float len = (float)Math.Sqrt(dir.x * dir.x + dir.y * dir.y + dir.z * dir.z);
            if (targets == null || len < 1e-9f || maxDist <= 0) return best;
            var d = dir * (1f / len);
            if (!Slab(o, d, lo, hi, out float w0, out float w1)) return best;
            w0 = Math.Max(0, w0); w1 = Math.Min(maxDist, w1);
            if (w0 > w1) return best;
            float bestT = float.PositiveInfinity;
            foreach (var t in targets)
            {
                if (skip != null && t.Uuid == skip) continue;
                Box(t, origin, out var min, out var max);
                if (!Slab(o, d, min, max, out float e0, out float e1)) continue;
                float t0 = Math.Max(e0, w0), t1 = Math.Min(e1, w1);
                if (t0 > t1 || t0 >= bestT) continue;
                bestT = t0;
                best = new EntityHit { Hit = true, Target = t, Distance = t0, Point = o + d * t0 };
            }
            return best;
        }

        /// <summary>The entity is what the ray hits first (an entity standing in front of a wall, not one behind it).</summary>
        public static bool EntityFirst(in EntityHit e, in BlockHit b) => e.Hit && (!b.Hit || e.Distance <= b.Distance + 1e-4f);

        /// <summary>
        /// Where a carried entity goes down, region-local block space: on top of the hit block at the exact aim point, or
        /// at the bottom centre of the cell in front of a side or bottom face. Null on a miss or outside the region.
        /// </summary>
        public static Vector3? DropPoint(WorldModel w, in BlockHit h)
        {
            if (w == null || !h.Hit) return null;
            if (h.Face == 3) return new Vector3(h.Point.x, h.Block.y + 1, h.Point.z);
            var a = h.Adjacent;
            if (!w.InBounds(a.x, a.y, a.z)) return null;
            return new Vector3(a.x + 0.5f, a.y, a.z + 0.5f);
        }

        /// <summary>A held mob dangles a block above where it would be dropped.</summary>
        public const float HoldLift = 1f;
    }
}
