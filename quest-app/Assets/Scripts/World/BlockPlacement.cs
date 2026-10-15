// Where a placed block goes and which state it gets, after vanilla 26.3 (engine-free, tested in Tests~/EditTests.cs):
//   - replaceable cells (BlockBehaviour.canBeReplaced): aiming at short grass, a single snow layer, vines... places
//     into that cell, and placing next to one replaces it;
//   - the state from the clicked face, the hit point and the look direction (each block's getStateForPlacement and
//     StandingAndWallBlockItem: wall torches/signs/banners/heads/coral fans on side faces, button/lever/grindstone
//     attachment, bells, top slabs/stairs/trapdoors, rods, buds, hoppers, observers, pistons, doors and their hinge,
//     beds, double plants, lanterns, vines...);
//   - the second half of doors, beds and tall plants (the server places it too, PROTOCOL.md 4.20).
// What the server also does after placing (fence and stair connections, leaf distance) is not guessed here: its
// block_update brings it.
//
// Directions are BlockRaycast's face indices: 0=west(-X) 1=east(+X) 2=down 3=up 4=north(-Z) 5=south(+Z), and the
// "look" is the controller ray in block space (+X east, +Y up, +Z south, as in Minecraft), which plays the part of
// the player's view direction.
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace TabletopCraft.World
{
    public static class Dir
    {
        public const int West = 0, East = 1, Down = 2, Up = 3, North = 4, South = 5;
        public static string Name(int d) => BlockRaycast.FaceNames[d];
        public static int Opposite(int d) => d ^ 1;
        public static bool Horizontal(int d) => d != Down && d != Up;
        public static int Axis(int d) => d / 2;
        public static Vector3Int Step(int d) => BlockRaycast.Normal(d);
        /// <summary>Clockwise seen from above (north, east, south, west); up and down are kept.</summary>
        public static int Clockwise(int d) => d == North ? East : d == East ? South : d == South ? West : d == West ? North : d;
        public static int CounterClockwise(int d) => d == North ? West : d == West ? South : d == South ? East : d == East ? North : d;
    }

    /// <summary>The user's look as vanilla reads it off the player (Entity yaw, Player.getDirection, Direction.orderedByNearest).</summary>
    public readonly struct Look
    {
        /// <summary>Minecraft yaw in degrees: 0 = south, 90 = west, 180 = north, -90 = east.</summary>
        public readonly float Yaw;
        /// <summary>Horizontal direction the user faces (Player.getDirection).</summary>
        public readonly int Horizontal;
        readonly int _n0, _n1, _n2;
        public readonly float Dy;

        Look(float yaw, int horizontal, int n0, int n1, int n2, float dy) { Yaw = yaw; Horizontal = horizontal; _n0 = n0; _n1 = n1; _n2 = n2; Dy = dy; }

        /// <summary>From a direction in block space (any length).</summary>
        public static Look From(Vector3 d)
        {
            float yaw = MathF.Atan2(-d.x, d.z) * (180f / MathF.PI);
            if (Math.Abs(d.x) < 1e-9f && Math.Abs(d.z) < 1e-9f) yaw = 0;
            int[] byYaw = { Dir.South, Dir.West, Dir.North, Dir.East };   // Direction.fromYRot / from2DDataValue
            int h = byYaw[(int)MathF.Floor(yaw / 90f + 0.5f) & 3];
            // Direction.orderedByNearest: the three signed axes by how much the look points along them.
            int x = d.x > 0 ? Dir.East : Dir.West, y = d.y > 0 ? Dir.Up : Dir.Down, z = d.z > 0 ? Dir.South : Dir.North;
            float ax = Math.Abs(d.x), ay = Math.Abs(d.y), az = Math.Abs(d.z);
            int a, b, c;
            if (ax > az) { if (ay > ax) { a = y; b = x; c = z; } else if (az > ay) { a = x; b = z; c = y; } else { a = x; b = y; c = z; } }
            else if (ay > az) { a = y; b = z; c = x; }
            else if (ax > ay) { a = z; b = x; c = y; }
            else { a = z; b = y; c = x; }
            return new Look(yaw, h, a, b, c, d.y);
        }

        /// <summary>The six directions, nearest to the look first: [a, b, c, -c, -b, -a] (Direction.makeDirectionArray).</summary>
        public int[] Nearest() => new[] { _n0, _n1, _n2, Dir.Opposite(_n2), Dir.Opposite(_n1), Dir.Opposite(_n0) };
        public int NearestOne => _n0;
    }

    /// <summary>One placement, as vanilla's BlockPlaceContext sees it.</summary>
    public sealed class PlaceContext
    {
        public WorldModel World;
        public Vector3Int Pos;          // the cell the block goes into (region-local)
        public Vector3Int Clicked;      // the block the ray hit
        public int Face;                // the clicked face
        public bool ReplaceClicked;     // the hit block itself is replaced (Pos == Clicked)
        public Vector3 Hit;             // where the ray hit, block space (region-local)
        public Look Look;

        public float FracX => Hit.x - Pos.x;
        public float FracY => Hit.y - Pos.y;
        public float FracZ => Hit.z - Pos.z;

        /// <summary>State in a cell, or null outside the region.</summary>
        public string At(Vector3Int c) => World != null && World.InBounds(c.x, c.y, c.z) ? World.State(World.Get(c.x, c.y, c.z)) : null;

        /// <summary>BlockPlaceContext.getNearestLookingDirections: the look order, with the side facing the clicked
        /// block first unless the clicked block is the one being replaced.</summary>
        public int[] NearestLookingDirections()
        {
            var dirs = Look.Nearest();
            if (ReplaceClicked) return dirs;
            int toward = Dir.Opposite(Face), i = Array.IndexOf(dirs, toward);
            if (i > 0) { Array.Copy(dirs, 0, dirs, 1, i); dirs[0] = toward; }
            return dirs;
        }

        /// <summary>Something can hang or stand on this cell's block. The clicked block always can (the user aimed at
        /// it); other cells need a full block, a slab, stairs, a fence or a wall (vanilla's sturdy-face checks, from names).</summary>
        public bool Supports(Vector3Int c)
        {
            if (!ReplaceClicked && c == Clicked) return true;
            string s = At(c);
            if (s == null || BlockPlacement.Replaceable(s, null)) return false;
            string p = BlockRules.Path(s);
            var shape = BlockRules.Classify(s);
            return shape == BlockShape.Cube || shape == BlockShape.TransparentCube || shape == BlockShape.Slab ||
                   p.EndsWith("_fence") || p.EndsWith("_wall") || p.EndsWith("_stairs");
        }

        /// <summary>A block whose collision box fills the cell (isCollisionShapeFullBlock, from names; for door hinges).</summary>
        public bool Full(Vector3Int c)
        {
            string s = At(c);
            if (s == null) return false;
            var shape = BlockRules.Classify(s);
            return (shape == BlockShape.Cube || shape == BlockShape.TransparentCube) && !BlockPlacement.Replaceable(s, null) && !s.Contains("type=bottom") && !s.Contains("type=top");
        }
    }

    public static class BlockPlacement
    {
        /// <summary>Blocks vanilla 26.3 marks replaceable (BlockBehaviour.Properties#replaceable), plus sculk vein,
        /// which MultifaceBlock#canBeReplaced lets other blocks replace too. Paths in the minecraft namespace.</summary>
        public static readonly HashSet<string> ReplaceableIds = new HashSet<string>
        {
            "air", "cave_air", "void_air", "structure_void", "light", "water", "lava", "bubble_column", "fire", "soul_fire",
            "short_grass", "tall_grass", "fern", "large_fern", "dead_bush", "bush", "red_shrub", "short_dry_grass", "tall_dry_grass",
            "seagrass", "tall_seagrass", "vine", "glow_lichen", "sculk_vein", "resin_clump", "hanging_roots", "crimson_roots",
            "warped_roots", "nether_sprouts", "snow", "leaf_litter",
        };

        /// <summary>
        /// Vanilla's canBeReplaced for placing <paramref name="placing"/> (null = any other block): a replaceable block,
        /// but a snow cover only when it's a single layer, and never by its own block (stacking snow, merging slabs or
        /// adding petals is not supported from the headset). The plugin applies the same rule.
        /// </summary>
        public static bool Replaceable(string state, string placing)
        {
            if (string.IsNullOrEmpty(state)) return false;
            string p = BlockRules.Path(state);
            if (!ReplaceableIds.Contains(p)) return false;
            if (placing != null && BlockRules.Path(placing) == p && BlockRules.Name(placing) == BlockRules.Name(state)) return false;
            if (p == "snow") return BlockEditRules.Prop(state, "layers") == "1";
            return true;
        }

        static readonly string[] WallSuffixes = { "hanging_sign", "torch", "sign", "banner", "skull", "head", "fan" };

        /// <summary>
        /// The wall form placed with the same item (StandingAndWallBlockItem): "minecraft:torch" -> "minecraft:wall_torch",
        /// "minecraft:oak_sign" -> "minecraft:oak_wall_sign", "minecraft:tube_coral_fan" -> "minecraft:tube_coral_wall_fan".
        /// <paramref name="known"/> checks the id exists (the block_registry's variants); null when there is none.
        /// </summary>
        public static string WallVariant(string state, Func<string, bool> known)
        {
            string name = BlockRules.Name(state), p = BlockRules.Path(state);
            if (p.Contains("wall_") || p == "piston_head") return null;
            string ns = name.Length > p.Length ? name.Substring(0, name.Length - p.Length) : "minecraft:";
            foreach (var suffix in WallSuffixes)
            {
                string wall = null;
                if (suffix == "fan") { if (p.EndsWith("_coral_fan")) wall = p.Substring(0, p.Length - 3) + "wall_fan"; }
                else if (p == suffix) wall = "wall_" + suffix;
                else if (p.EndsWith("_" + suffix)) wall = p.Substring(0, p.Length - suffix.Length) + "wall_" + suffix;
                if (wall == null) continue;
                string id = ns + wall;
                return known == null || known(id) ? id : null;
            }
            return null;
        }

        /// <summary>The state with a different block id and properties: drops <paramref name="drop"/>, sets <paramref name="set"/>
        /// (key, value pairs), properties sorted by name as BlockData#getAsString prints them.</summary>
        public static string Rebuild(string state, string id, string[] drop, params string[] set)
        {
            var props = new SortedDictionary<string, string>(StringComparer.Ordinal);
            int open = state.IndexOf('[');
            if (open >= 0)
                foreach (var kv in state.Substring(open + 1, state.Length - open - 2).Split(','))
                {
                    int eq = kv.IndexOf('=');
                    if (eq > 0) props[kv.Substring(0, eq)] = kv.Substring(eq + 1);
                }
            if (drop != null) foreach (var k in drop) props.Remove(k);
            for (int i = 0; i + 1 < set.Length; i += 2) props[set[i]] = set[i + 1];
            if (props.Count == 0) return id;
            var sb = new StringBuilder(id).Append('[');
            bool first = true;
            foreach (var kv in props) { if (!first) sb.Append(','); sb.Append(kv.Key).Append('=').Append(kv.Value); first = false; }
            return sb.Append(']').ToString();
        }

        static string P(string s, string k) => BlockEditRules.Prop(s, k);
        static string With(string s, string k, string v) => BlockEditRules.WithProp(s, k, v);
        static string Label(string s) => BlockEditRules.Label(s);

        /// <summary>16-step rotation (RotationSegment.convertToSegment): round(deg * 16 / 360) & 15, Java's rounding.</summary>
        public static int Segment(float degrees) => (int)MathF.Floor(degrees * 16f / 360f + 0.5f) & 15;

        static int SegmentOf(int dir) => dir == Dir.South ? 0 : dir == Dir.West ? 4 : dir == Dir.North ? 8 : 12;

        /// <summary>
        /// The state vanilla would place for the selected block <paramref name="sel"/> (a default state from the block
        /// menu) in context <paramref name="c"/>, or null when it can't be placed there (<paramref name="why"/> says why,
        /// e.g. a torch with nothing to stand on or hang from, or a door without room above).
        /// </summary>
        public static string StateFor(string sel, PlaceContext c, Func<string, bool> known, out string why)
        {
            why = null;
            string p = BlockRules.Path(sel), s = sel;
            string wall = WallVariant(sel, known);
            if (wall != null) return StandingOrWall(sel, wall, c, out why);

            if (P(s, "face") != null && (p.EndsWith("_button") || p == "lever" || p == "grindstone"))
            {
                // FaceAttachedHorizontalDirectionalBlock: floor/ceiling facing the way the user looks, or on a wall.
                foreach (int d in c.NearestLookingDirections())
                {
                    string cand = Dir.Axis(d) == 1
                        ? With(With(s, "face", d == Dir.Up ? "ceiling" : "floor"), "facing", Dir.Name(c.Look.Horizontal))
                        : With(With(s, "face", "wall"), "facing", Dir.Name(Dir.Opposite(d)));
                    if (p == "grindstone" || c.Supports(c.Pos + Dir.Step(d))) return cand;
                }
                why = "nothing to attach the " + Label(s) + " to"; return null;
            }
            if (p == "bell" && P(s, "attachment") != null) return Bell(s, c, out why);
            if (p.EndsWith("_slab") && P(s, "type") != null)
                return With(s, "type", TopHalf(c) ? "top" : "bottom");
            if (p.EndsWith("_stairs"))
                return With(With(s, "facing", Dir.Name(c.Look.Horizontal)), "half", TopHalf(c) ? "top" : "bottom");
            if (p.EndsWith("_trapdoor"))
            {
                if (!c.ReplaceClicked && Dir.Horizontal(c.Face)) return With(With(s, "facing", Dir.Name(c.Face)), "half", c.FracY > 0.5f ? "top" : "bottom");
                return With(With(s, "facing", Dir.Name(Dir.Opposite(c.Look.Horizontal))), "half", c.Face == Dir.Up ? "bottom" : "top");
            }
            if (p.EndsWith("_door") && P(s, "hinge") != null)
            {
                if (!RoomAbove(c, s)) { why = "no room for the " + Label(s); return null; }
                int f = c.Look.Horizontal;
                s = With(With(With(s, "facing", Dir.Name(f)), "hinge", Hinge(c, f)), "half", "lower");
                return With(With(s, "open", "false"), "powered", "false");
            }
            if (P(s, "part") != null && p.EndsWith("_bed"))
            {
                int f = c.Look.Horizontal;
                string head = c.At(c.Pos + Dir.Step(f));
                if (head == null || !Replaceable(head, s)) { why = "no room for the " + Label(s); return null; }
                return With(With(s, "facing", Dir.Name(f)), "part", "foot");
            }
            string half = P(s, "half");
            if ((half == "lower" || half == "upper") && p != "pitcher_crop")
            {
                // DoublePlantBlock (tall grass, sunflower, pitcher plant, small dripleaf...): needs room above.
                if (!RoomAbove(c, s)) { why = "no room for the " + Label(s); return null; }
                s = With(s, "half", "lower");
                if (p == "small_dripleaf") s = With(s, "facing", Dir.Name(Dir.Opposite(c.Look.Horizontal)));
                return s;
            }
            if (P(s, "hanging") != null && p.EndsWith("lantern"))
            {
                foreach (int d in c.NearestLookingDirections())
                    if (Dir.Axis(d) == 1 && c.Supports(c.Pos + Dir.Step(d))) return With(s, "hanging", d == Dir.Up ? "true" : "false");
                why = "nothing to put the " + Label(s) + " on"; return null;
            }
            if (p == "vine" || p == "glow_lichen" || p == "sculk_vein" || p == "resin_clump")
            {
                // MultifaceBlock / VineBlock: the face toward a block it can cling to, nearest to the look first.
                foreach (int d in c.NearestLookingDirections())
                    if (P(s, Dir.Name(d)) != null && c.Supports(c.Pos + Dir.Step(d))) return With(s, Dir.Name(d), "true");
                why = "nothing for the " + Label(s) + " to cling to"; return null;
            }
            if (P(s, "axis") != null && p != "nether_portal")
                s = With(s, "axis", c.Face < 2 ? "x" : c.Face < 4 ? "y" : "z");
            if (P(s, "vertical_direction") != null)   // pointed dripstone: tip away from the look's vertical part
                s = With(s, "vertical_direction", c.Look.Dy < 0 ? "up" : "down");
            if (P(s, "orientation") != null && p == "crafter")
            {
                int front = Dir.Opposite(c.Look.NearestOne);
                int top = front == Dir.Down ? Dir.Opposite(c.Look.Horizontal) : front == Dir.Up ? c.Look.Horizontal : Dir.Up;
                s = With(s, "orientation", Dir.Name(front) + "_" + Dir.Name(top));
            }
            if (P(s, "shape") != null && p.EndsWith("rail"))
                s = With(s, "shape", Dir.Axis(c.Look.Horizontal) == 0 ? "east_west" : "north_south");
            if (P(s, "facing") != null) s = Facing(s, p, c, out why);
            if (s != null && p.EndsWith("_leaves")) s = With(s, "persistent", "true");   // placed leaves never decay
            return s;
        }

        /// <summary>Slabs and stairs: the top half from the underside, or from the upper half of a side.</summary>
        static bool TopHalf(PlaceContext c) => c.Face != Dir.Up && (c.Face == Dir.Down || c.FracY > 0.5f);

        static bool RoomAbove(PlaceContext c, string s)
        {
            string above = c.At(c.Pos + Dir.Step(Dir.Up));
            return above != null && Replaceable(above, s);
        }

        static string Facing(string s, string p, PlaceContext c, out string why)
        {
            why = null;
            // Facing the clicked face: end rods (away from a rod they're put on), lightning rods, amethyst, shulker boxes.
            if (p.EndsWith("amethyst_bud") || p == "amethyst_cluster" || p.EndsWith("lightning_rod") || p.EndsWith("shulker_box"))
                return With(s, "facing", Dir.Name(c.Face));
            if (p == "end_rod")
            {
                string behind = c.At(c.Pos + Dir.Step(Dir.Opposite(c.Face)));
                bool onRod = behind != null && BlockRules.Path(behind) == "end_rod" && P(behind, "facing") == Dir.Name(c.Face);
                return With(s, "facing", Dir.Name(onRod ? Dir.Opposite(c.Face) : c.Face));
            }
            if (p == "hopper")
            {
                int f = Dir.Opposite(c.Face);
                return With(s, "facing", Dir.Name(Dir.Horizontal(f) ? f : Dir.Down));
            }
            if (p == "observer") return With(s, "facing", Dir.Name(c.Look.NearestOne));
            if (p == "piston" || p == "sticky_piston" || p == "dispenser" || p == "dropper" || p == "barrel" || p.EndsWith("command_block"))
                return With(s, "facing", Dir.Name(Dir.Opposite(c.Look.NearestOne)));
            string cur = P(s, "facing");
            if (cur == "up" || cur == "down") return s;   // another block with a vertical default: keep it
            if (p == "cocoa" || p == "tripwire_hook" || p == "ladder" || p.EndsWith("shelf_mushroom"))
            {
                // Hung on a side: cocoa faces the log; hooks and ladders face away from the block behind them.
                foreach (int d in c.NearestLookingDirections())
                    if (Dir.Horizontal(d) && c.Supports(c.Pos + Dir.Step(d))) return With(s, "facing", Dir.Name(p == "cocoa" ? d : Dir.Opposite(d)));
                why = "nothing to hang the " + Label(s) + " on"; return null;
            }
            int h = c.Look.Horizontal;
            if (p.EndsWith("_door") || p.EndsWith("_fence_gate") || p.EndsWith("_bed") || p.EndsWith("campfire") || p == "calibrated_sculk_sensor" || p == "decorated_pot")
                return With(s, "facing", Dir.Name(h));   // the way the user looks
            if (p.EndsWith("anvil")) return With(s, "facing", Dir.Name(Dir.Clockwise(h)));
            return With(s, "facing", Dir.Name(Dir.Opposite(h)));   // most blocks face the user (furnaces, chests, pumpkins...)
        }

        /// <summary>StandingAndWallBlockItem: the standing form on top (hanging signs: under), the wall form on a side.</summary>
        static string StandingOrWall(string sel, string wall, PlaceContext c, out string why)
        {
            why = null;
            string p = BlockRules.Path(sel);
            bool hanging = p.EndsWith("_hanging_sign");
            int attach = hanging ? Dir.Up : Dir.Down;
            foreach (int d in c.NearestLookingDirections())
            {
                if (d == Dir.Opposite(attach)) continue;
                string cand = d == attach ? Standing(sel, c) : WallState(sel, wall, c, hanging);
                if (cand != null) return cand;
            }
            why = "nothing to attach the " + Label(sel) + " to";
            return null;
        }

        static string Standing(string s, PlaceContext c)
        {
            string p = BlockRules.Path(s);
            var below = c.Pos + Dir.Step(Dir.Down);
            if (p.EndsWith("_hanging_sign"))
            {
                // CeilingHangingSignBlock: under a full block it snaps to the 4 directions (not "attached"), else 16.
                var above = c.Pos + Dir.Step(Dir.Up);
                string a = c.At(above);
                if (a == null || Replaceable(a, null) || BlockRules.Classify(a) == BlockShape.Air) return null;
                bool full = c.Full(above);
                int rot = full ? SegmentOf(Dir.Opposite(c.Look.Horizontal)) : Segment(c.Look.Yaw + 180f);
                return With(With(s, "attached", full ? "false" : "true"), "rotation", rot.ToString());
            }
            if (p.EndsWith("_skull") || p.EndsWith("_head")) return With(s, "rotation", Segment(c.Look.Yaw).ToString());   // no support needed
            if (!c.Supports(below)) return null;
            if (P(s, "rotation") != null) return With(s, "rotation", Segment(c.Look.Yaw + 180f).ToString());   // signs, banners
            return s;   // torches, coral fans
        }

        static readonly string[] StandingOnly = { "rotation", "attached" };

        static string WallState(string sel, string wall, PlaceContext c, bool hanging)
        {
            bool skull = wall.EndsWith("_wall_skull") || wall.EndsWith("_wall_head");
            foreach (int d in c.NearestLookingDirections())
            {
                if (!Dir.Horizontal(d)) continue;
                if (hanging && Dir.Axis(d) == Dir.Axis(c.Face)) continue;   // WallHangingSignBlock: along the wall
                int facing = Dir.Opposite(d);
                bool ok = hanging
                    ? c.Supports(c.Pos + Dir.Step(Dir.Clockwise(facing))) || c.Supports(c.Pos + Dir.Step(Dir.CounterClockwise(facing)))
                    : skull ? !Replaceable(c.At(c.Pos + Dir.Step(d)) ?? BlockEditRules.Air, null) : c.Supports(c.Pos + Dir.Step(d));
                if (ok) return Rebuild(sel, wall, StandingOnly, "facing", Dir.Name(facing));
            }
            return null;
        }

        static string Bell(string s, PlaceContext c, out string why)
        {
            why = null;
            int face = c.Face;
            if (Dir.Axis(face) == 1)
            {
                string st = With(With(s, "attachment", face == Dir.Down ? "ceiling" : "floor"), "facing", Dir.Name(c.Look.Horizontal));
                if (c.Supports(c.Pos + Dir.Step(face == Dir.Down ? Dir.Up : Dir.Down))) return st;
            }
            else
            {
                int a = Dir.Axis(face) == 0 ? Dir.West : Dir.North;
                bool both = c.Supports(c.Pos + Dir.Step(a)) && c.Supports(c.Pos + Dir.Step(Dir.Opposite(a)));
                string st = With(With(s, "facing", Dir.Name(Dir.Opposite(face))), "attachment", both ? "double_wall" : "single_wall");
                if (c.Supports(c.Pos + Dir.Step(Dir.Opposite(face)))) return st;
                bool floor = c.Supports(c.Pos + Dir.Step(Dir.Down));
                st = With(st, "attachment", floor ? "floor" : "ceiling");
                if (c.Supports(c.Pos + Dir.Step(floor ? Dir.Down : Dir.Up))) return st;
            }
            why = "nothing to hang the bell from"; return null;
        }

        /// <summary>DoorBlock.getHinge: away from full blocks and towards a door next to it, else from the hit point.</summary>
        static string Hinge(PlaceContext c, int dir)
        {
            var pos = c.Pos; var above = pos + Dir.Step(Dir.Up);
            int ccw = Dir.CounterClockwise(dir), cw = Dir.Clockwise(dir);
            Vector3Int l = pos + Dir.Step(ccw), la = above + Dir.Step(ccw), r = pos + Dir.Step(cw), ra = above + Dir.Step(cw);
            int balance = (c.Full(l) ? -1 : 0) + (c.Full(la) ? -1 : 0) + (c.Full(r) ? 1 : 0) + (c.Full(ra) ? 1 : 0);
            bool doorL = IsLowerDoor(c.At(l)), doorR = IsLowerDoor(c.At(r));
            if ((!doorL || doorR) && balance <= 0)
            {
                if ((!doorR || doorL) && balance >= 0)
                {
                    var st = Dir.Step(dir);
                    float cx = c.FracX, cz = c.FracZ;
                    return (st.x >= 0 || !(cz < 0.5f)) && (st.x <= 0 || !(cz > 0.5f)) && (st.z >= 0 || !(cx > 0.5f)) && (st.z <= 0 || !(cx < 0.5f)) ? "left" : "right";
                }
                return "left";
            }
            return "right";
        }

        static bool IsLowerDoor(string s) => s != null && BlockRules.Path(s).EndsWith("_door") && P(s, "half") == "lower";

        /// <summary>
        /// The other half of a two-block structure whose one half is <paramref name="state"/>: doors and double plants
        /// (half=lower/upper) one cell up or down, beds (part=foot/head) one cell along their facing. False otherwise.
        /// </summary>
        public static bool Partner(string state, out Vector3Int offset, out string partner)
        {
            offset = default; partner = null;
            if (string.IsNullOrEmpty(state)) return false;
            string p = BlockRules.Path(state), half = P(state, "half"), part = P(state, "part");
            if ((half == "lower" || half == "upper") && p != "pitcher_crop")
            {
                offset = Dir.Step(half == "lower" ? Dir.Up : Dir.Down);
                partner = With(state, "half", half == "lower" ? "upper" : "lower");
                return true;
            }
            if ((part == "foot" || part == "head") && p.EndsWith("_bed"))
            {
                int f = Array.IndexOf(BlockRaycast.FaceNames, P(state, "facing"));
                if (f < 0) return false;
                offset = Dir.Step(part == "foot" ? f : Dir.Opposite(f));
                partner = With(state, "part", part == "foot" ? "head" : "foot");
                return true;
            }
            return false;
        }
    }
}
