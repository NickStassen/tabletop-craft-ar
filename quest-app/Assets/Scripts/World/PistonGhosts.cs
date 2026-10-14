// [piston] What to draw in a moving_piston cell (the cheap version of vanilla's PistonHeadRenderer / moving block entity):
// a moving_piston bakes to nothing, so pushed blocks and piston arms used to vanish for the 2 ticks of every push or
// pull. While a cell is moving_piston, WorldModel.MovingShown holds the state to draw there instead, worked out from
// the cells' states before and after the block_update that started the move (the server sends a tick's changes
// together). The block jumps instead of sliding, but nothing blinks. Engine-free; main thread (WorldModel).
//
// In 26.3 (PistonBaseBlock.moveBlocks / triggerEvent): extending sets the base to extended=true at once, the head cell
// and every pushed block's new cell to moving_piston, and the cells left behind to air; retracting sets the base cell
// itself to moving_piston and the head cell to air, or (sticky) to moving_piston with the pulled block, whose old cell
// becomes air. So, per moving cell C with the piston's facing F, in order:
//   1. C was a piston base: draw that (a retracting base keeps its extended look)
//   2. C - F is now a piston base extended towards F: C is its head, draw piston_head
//   3. C - F changed in this update and held a block: it was pushed into C, draw it
//   4. C + F changed in this update to something else and held a block: it was pulled into C, draw it
//   5. C held something before (a retracting head): keep drawing it
using System.Collections.Generic;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class PistonGhosts
    {
        readonly Dictionary<int, ushort> _old = new Dictionary<int, ushort>();

        /// <summary>A recorder for one block_update, or null when no piston can be involved (cheap path).</summary>
        public static PistonGhosts Begin(WorldModel w)
        {
            if (w.MovingShown.Count > 0) return new PistonGhosts();
            foreach (var s in w.Palette) if (IsMoving(s)) return new PistonGhosts();
            return null;
        }

        /// <summary>Call before a cell changes: keeps its first (pre-update) palette id.</summary>
        public void Record(int index, ushort id) { if (!_old.ContainsKey(index)) _old[index] = id; }

        /// <summary>After the update's changes are in the grid: sets or clears WorldModel.MovingShown for every changed cell.</summary>
        public void Apply(WorldModel w)
        {
            foreach (var kv in _old)
            {
                string now = w.State(w.Grid[kv.Key]);
                string shown = IsMoving(now) ? Shown(w, kv.Key, now) : null;
                if (shown != null) w.MovingShown[kv.Key] = shown;
                else w.MovingShown.TryRemove(kv.Key, out _);
            }
        }

        string Prev(WorldModel w, int index) => w.State(_old.TryGetValue(index, out var id) ? id : w.Grid[index]);
        bool Changed(int index) => _old.ContainsKey(index);

        string Shown(WorldModel w, int c, string moving)
        {
            var props = BlockModelBaker.Props(moving);
            props.TryGetValue("facing", out var facing);
            props.TryGetValue("type", out var type);
            var d = Step(facing);
            int dx = w.Dims.x, dz = w.Dims.z;
            int x = c % dx, z = c / dx % dz, y = c / (dx * dz);
            string prevC = Prev(w, c);
            if (IsBase(prevC)) return prevC;                                                              // 1
            if (w.InBounds(x - d.x, y - d.y, z - d.z))
            {
                int b = w.Index(x - d.x, y - d.y, z - d.z);
                string nowB = w.State(w.Grid[b]);
                if (IsBase(nowB))
                {
                    var bp = BlockModelBaker.Props(nowB);
                    if (bp.TryGetValue("extended", out var ext) && ext == "true" && bp.TryGetValue("facing", out var bf) && bf == facing)
                        return $"minecraft:piston_head[facing={facing},short=false,type={(type == "sticky" ? "sticky" : "normal")}]";   // 2
                }
                if (Changed(b) && Movable(Prev(w, b))) return Prev(w, b);                                // 3
            }
            if (w.InBounds(x + d.x, y + d.y, z + d.z))
            {
                int f = w.Index(x + d.x, y + d.y, z + d.z);
                if (Changed(f) && Movable(Prev(w, f)) && Prev(w, f) != w.State(w.Grid[f])) return Prev(w, f);   // 4
            }
            return IsAir(prevC) || IsMoving(prevC) ? null : prevC;                                        // 5
        }

        static Vector3Int Step(string facing)
        {
            switch (facing)
            {
                case "north": return new Vector3Int(0, 0, -1);
                case "south": return new Vector3Int(0, 0, 1);
                case "west": return new Vector3Int(-1, 0, 0);
                case "east": return new Vector3Int(1, 0, 0);
                case "up": return new Vector3Int(0, 1, 0);
                default: return new Vector3Int(0, -1, 0);
            }
        }

        public static bool IsMoving(string s) => BlockRules.Path(s) == "moving_piston";
        static bool IsBase(string s) { string p = BlockRules.Path(s); return p == "piston" || p == "sticky_piston"; }
        static bool IsAir(string s) { string p = BlockRules.Path(s); return p == "air" || p == "cave_air" || p == "void_air"; }

        /// <summary>A block a piston can carry (not air, fluid or a piston part).</summary>
        static bool Movable(string s)
        {
            string p = BlockRules.Path(s);
            return !IsAir(s) && p != "moving_piston" && p != "piston_head" && !IsBase(s) && p != "water" && p != "lava";
        }
    }
}
