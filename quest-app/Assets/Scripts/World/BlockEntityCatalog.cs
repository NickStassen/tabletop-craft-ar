// Block entities (chests, shulker boxes, heads, banners, bells, decorated pots, conduits, copper golem statues, books,
// end portals, signs, beacons, spawners, campfires, shelves, vaults) drawn the way vanilla 26.3's BlockEntityRenderers
// do: which model layers (entity_models.json, generated from the user's own client jar), which pack textures, and the
// renderer's block-space transform, per block state and per server data (block_entities, PROTOCOL.md 4.29).
// Engine-free: Tests~ checks the transforms and layer choices; BlockEntityRenderer draws the parts as GPU instances.
//
// Conventions. A part's mesh is baked in figure space like every entity mesh here (EntityModels: game model space
// point-reflected, 1 unit = 1 model pixel). Vanilla draws a model vertex p (pixels) at blockPos + M * (p / 16), so a
// figure-space vertex f = -p lands at blockPos + M * S(-1/16) * f. BlockEntityRenderer then mirrors Z for Unity.
// All rotations are the game's numbers (right-handed maths, radians); M4 is a plain affine 4x4.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    /// <summary>A small affine 4x4 matrix (row-major, last row 0 0 0 1), right-handed maths like JOML.</summary>
    public struct M4
    {
        public float A00, A01, A02, A03, A10, A11, A12, A13, A20, A21, A22, A23;

        public static readonly M4 Identity = new M4 { A00 = 1, A11 = 1, A22 = 1 };

        public static M4 operator *(M4 a, M4 b) => new M4
        {
            A00 = a.A00 * b.A00 + a.A01 * b.A10 + a.A02 * b.A20, A01 = a.A00 * b.A01 + a.A01 * b.A11 + a.A02 * b.A21,
            A02 = a.A00 * b.A02 + a.A01 * b.A12 + a.A02 * b.A22, A03 = a.A00 * b.A03 + a.A01 * b.A13 + a.A02 * b.A23 + a.A03,
            A10 = a.A10 * b.A00 + a.A11 * b.A10 + a.A12 * b.A20, A11 = a.A10 * b.A01 + a.A11 * b.A11 + a.A12 * b.A21,
            A12 = a.A10 * b.A02 + a.A11 * b.A12 + a.A12 * b.A22, A13 = a.A10 * b.A03 + a.A11 * b.A13 + a.A12 * b.A23 + a.A13,
            A20 = a.A20 * b.A00 + a.A21 * b.A10 + a.A22 * b.A20, A21 = a.A20 * b.A01 + a.A21 * b.A11 + a.A22 * b.A21,
            A22 = a.A20 * b.A02 + a.A21 * b.A12 + a.A22 * b.A22, A23 = a.A20 * b.A03 + a.A21 * b.A13 + a.A22 * b.A23 + a.A23,
        };

        public static M4 T(float x, float y, float z) => new M4 { A00 = 1, A11 = 1, A22 = 1, A03 = x, A13 = y, A23 = z };
        public static M4 S(float x, float y, float z) => new M4 { A00 = x, A11 = y, A22 = z };
        public static M4 S(float s) => S(s, s, s);
        public static M4 RotX(float r) { float c = (float)Math.Cos(r), s = (float)Math.Sin(r); return new M4 { A00 = 1, A11 = c, A12 = -s, A21 = s, A22 = c }; }
        public static M4 RotY(float r) { float c = (float)Math.Cos(r), s = (float)Math.Sin(r); return new M4 { A00 = c, A02 = s, A11 = 1, A20 = -s, A22 = c }; }
        public static M4 RotZ(float r) { float c = (float)Math.Cos(r), s = (float)Math.Sin(r); return new M4 { A00 = c, A01 = -s, A10 = s, A11 = c, A22 = 1 }; }
        public static M4 RotYDeg(float d) => RotY(d * (float)Math.PI / 180f);
        /// <summary>The game's ModelPart rotation: Z * Y * X (radians).</summary>
        public static M4 RotZYX(float x, float y, float z) => RotZ(z) * RotY(y) * RotX(x);
        /// <summary>JOML rotateAround: T(o) * R * T(-o).</summary>
        public static M4 Around(M4 r, float ox, float oy, float oz) => T(ox, oy, oz) * r * T(-ox, -oy, -oz);

        public Vector3 Point(Vector3 p) => new Vector3(A00 * p.x + A01 * p.y + A02 * p.z + A03, A10 * p.x + A11 * p.y + A12 * p.z + A13, A20 * p.x + A21 * p.y + A22 * p.z + A23);
        public Vector3 Dir(Vector3 d) => new Vector3(A00 * d.x + A01 * d.y + A02 * d.z, A10 * d.x + A11 * d.y + A12 * d.z, A20 * d.x + A21 * d.y + A22 * d.z);
        public float Det => A00 * (A11 * A22 - A12 * A21) - A01 * (A10 * A22 - A12 * A20) + A02 * (A10 * A21 - A11 * A20);
        public Vector3 Translation => new Vector3(A03, A13, A23);
    }

    public enum BlockEntityKind : byte
    {
        None, Chest, ShulkerBox, Skull, Banner, Bell, DecoratedPot, Conduit, CopperGolemStatue, EndPortal, EndGateway,
        Sign, HangingSign, EnchantingTable, Lectern, Beacon, Spawner, TrialSpawner, Campfire, Shelf, Vault,
    }

    /// <summary>One absolute part-pose value set by a renderer's setupAnim (a jaw angle, the statue's flipped root...).</summary>
    public struct PoseValue
    {
        public string Part;     // "" = the root part
        public int Field;       // 0..2 pivot x/y/z (pixels), 3..5 rotation x/y/z (radians)
        public float Value;
        public PoseValue(string part, int field, float value) { Part = part; Field = field; Value = value; }
    }

    /// <summary>One model drawn for a block entity: a layer (or one part of it), its pose, texture and block-space transform.</summary>
    public struct BEPart
    {
        public string Model;        // entity_models.json layer, e.g. "chest", "standing_banner#flag"
        public string Only;         // draw only this part (and its children), e.g. a decorated pot side; null = all
        public string Pose;         // BlockEntityCatalog.Poses key, null = the layer's default pose
        public string Texture;      // texture key (BlockEntityCatalog: pack path, "skin:..", "banner:..")
        public M4 M;                // vanilla block-space transform (blocks, origin = the block's min corner)

        /// <summary>Key of the baked mesh (layer + part + pose): equal keys share one mesh.</summary>
        public string MeshKey => Model + "|" + Only + "|" + Pose;
    }

    public static class BlockEntityCatalog
    {
        public const float Pi = (float)Math.PI;

        // ---------- classification ----------

        static readonly string[] HeadTypes = { "zombie", "player", "creeper", "dragon", "piglin" };

        /// <summary>Which vanilla block entity renderer draws a block (by block id path, e.g. "oak_wall_sign").</summary>
        public static BlockEntityKind KindOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return BlockEntityKind.None;
            switch (path)
            {
                case "chest": case "trapped_chest": case "ender_chest": return BlockEntityKind.Chest;
                case "bell": return BlockEntityKind.Bell;
                case "decorated_pot": return BlockEntityKind.DecoratedPot;
                case "conduit": return BlockEntityKind.Conduit;
                case "end_portal": return BlockEntityKind.EndPortal;
                case "end_gateway": return BlockEntityKind.EndGateway;
                case "enchanting_table": return BlockEntityKind.EnchantingTable;
                case "lectern": return BlockEntityKind.Lectern;
                case "beacon": return BlockEntityKind.Beacon;
                case "spawner": return BlockEntityKind.Spawner;
                case "trial_spawner": return BlockEntityKind.TrialSpawner;
                case "campfire": case "soul_campfire": return BlockEntityKind.Campfire;
                case "vault": return BlockEntityKind.Vault;
            }
            if (path.EndsWith("copper_chest")) return BlockEntityKind.Chest;
            if (path.EndsWith("shulker_box")) return BlockEntityKind.ShulkerBox;
            if (path.EndsWith("copper_golem_statue")) return BlockEntityKind.CopperGolemStatue;
            if (path.EndsWith("_banner")) return BlockEntityKind.Banner;
            if (path.EndsWith("_hanging_sign")) return BlockEntityKind.HangingSign;
            if (path.EndsWith("_sign")) return BlockEntityKind.Sign;
            if (path.EndsWith("_shelf")) return BlockEntityKind.Shelf;
            if (path.EndsWith("_skull")) return BlockEntityKind.Skull;
            if (path.EndsWith("_head"))
                foreach (var t in HeadTypes) if (path == t + "_head" || path == t + "_wall_head") return BlockEntityKind.Skull;
            return BlockEntityKind.None;
        }

        public static BlockEntityKind KindOfState(string state) => KindOf(BlockRules.Path(state));

        /// <summary>
        /// Blocks whose look vanilla draws entirely in code (their block model only names a particle texture): the
        /// terrain mesher leaves them out and BlockEntityRenderer draws them. Bells draw only their body in code (the
        /// frame is a block model). Signs, shelves, beds, lecterns... are real block models with extras on top.
        /// </summary>
        public static bool CodeDrawn(string path)
        {
            switch (KindOf(path))
            {
                case BlockEntityKind.Chest: case BlockEntityKind.ShulkerBox: case BlockEntityKind.Skull: case BlockEntityKind.Banner:
                case BlockEntityKind.Bell: case BlockEntityKind.DecoratedPot: case BlockEntityKind.Conduit: case BlockEntityKind.CopperGolemStatue:
                case BlockEntityKind.EndPortal: case BlockEntityKind.EndGateway:
                    return true;
                default: return false;
            }
        }

        // ---------- directions (net.minecraft.core.Direction) ----------

        /// <summary>Direction.toYRot: south 0, west 90, north 180, east 270.</summary>
        public static float YRot(string facing)
        {
            switch (facing) { case "west": return 90; case "north": return 180; case "east": return 270; default: return 0; }
        }

        static readonly string[] Horizontal = { "south", "west", "north", "east" };   // get2DDataValue order

        public static int Data2D(string facing) { int i = Array.IndexOf(Horizontal, facing); return i < 0 ? 0 : i; }
        public static string From2D(int i) => Horizontal[((i % 4) + 4) % 4];
        public static string Opposite(string f) => f == "north" ? "south" : f == "south" ? "north" : f == "east" ? "west" : f == "west" ? "east" : f == "up" ? "down" : "up";
        public static string ClockWise(string f) => f == "north" ? "east" : f == "east" ? "south" : f == "south" ? "west" : "north";
        public static int StepX(string f) => f == "east" ? 1 : f == "west" ? -1 : 0;
        public static int StepZ(string f) => f == "south" ? 1 : f == "north" ? -1 : 0;

        /// <summary>Direction.getRotation (the shulker box lid's direction from up).</summary>
        public static M4 DirectionRotation(string d)
        {
            float h = Pi / 2;
            switch (d)
            {
                case "down": return M4.RotX(Pi);
                case "north": return M4.RotX(h) * M4.RotZ(Pi);
                case "south": return M4.RotX(h);
                case "west": return M4.RotX(h) * M4.RotZ(h);
                case "east": return M4.RotX(h) * M4.RotZ(-h);
                default: return M4.Identity;
            }
        }

        static string Prop(Dictionary<string, string> p, string k, string def) => p.TryGetValue(k, out var v) ? v : def;
        static int PropInt(Dictionary<string, string> p, string k) => p.TryGetValue(k, out var v) && int.TryParse(v, out int n) ? n : 0;

        // ---------- the renderers' transforms ----------

        /// <summary>ChestRenderer.modelTransformation: turned about the block's vertical centre line.</summary>
        public static M4 ChestTransform(string facing) => M4.Around(M4.RotYDeg(-YRot(facing)), 0.5f, 0, 0.5f);

        /// <summary>ShulkerBoxRenderer.createModelTransform.</summary>
        public static M4 ShulkerTransform(string facing) =>
            M4.T(0.5f, 0.5f, 0.5f) * M4.S(0.9995f) * DirectionRotation(facing) * M4.S(1, -1, -1) * M4.T(0, -1, 0);

        /// <summary>SkullBlockRenderer: floor (rotation 0-15) or wall (facing).</summary>
        public static M4 SkullTransform(bool wall, string facing, int rotation) => wall
            ? M4.T(0.5f - StepX(facing) * 0.25f, 0.25f, 0.5f - StepZ(facing) * 0.25f) * M4.RotYDeg(-YRot(Opposite(facing))) * M4.S(-1, -1, 1)
            : M4.T(0.5f, 0, 0.5f) * M4.RotYDeg(-rotation * 22.5f) * M4.S(-1, -1, 1);

        /// <summary>BannerRenderer.modelTransformation (2/3 scale, model Y and Z flipped).</summary>
        public static M4 BannerTransform(bool wall, string facing, int rotation) =>
            M4.T(0.5f, 0, 0.5f) * M4.RotYDeg(-(wall ? YRot(facing) : rotation * 22.5f)) * M4.S(2f / 3, -2f / 3, -2f / 3);

        /// <summary>DecoratedPotRenderer: turned by 180 - facing about the block centre.</summary>
        public static M4 PotTransform(string facing) => M4.Around(M4.RotYDeg(180 - YRot(facing)), 0.5f, 0.5f, 0.5f);

        /// <summary>CopperGolemStatueBlockRenderer: faces away from its facing (the statue looks at the placer).</summary>
        public static M4 StatueTransform(string facing) => M4.T(0.5f, 0, 0.5f) * M4.RotYDeg(-YRot(Opposite(facing)));

        /// <summary>LecternRenderer: the open book on the slope.</summary>
        public static M4 LecternBookTransform(string facing) =>
            M4.T(0.5f, 1.0625f, 0.5f) * M4.RotYDeg(-YRot(ClockWise(facing))) * M4.RotZ(67.5f * Pi / 180) * M4.T(0, -0.125f, 0);

        /// <summary>EnchantTableRenderer: the floating book at game time t (ticks), turned by yRot (radians).</summary>
        public static M4 EnchantBookTransform(float ticks, float yRot) =>
            M4.T(0.5f, 0.75f, 0.5f) * M4.T(0, 0.1f + (float)Math.Sin(ticks * 0.1f) * 0.01f, 0) * M4.RotY(-yRot) * M4.RotZ(80 * Pi / 180);

        /// <summary>SpawnerRenderer.submitEntityInSpawner: the spinning mob (spin in degrees, scale from its size).</summary>
        public static M4 SpawnerMobTransform(float spinDeg, float width, float height)
        {
            float s = 0.53125f, m = Math.Max(width, height);
            if (m > 1) s /= m;
            return M4.T(0.5f, 0.4f, 0.5f) * M4.RotYDeg(spinDeg) * M4.T(0, -0.2f, 0) * M4.RotX(-30 * Pi / 180) * M4.S(s);
        }

        /// <summary>CampfireRenderer: item slot 0-3 lying on the logs (before the item's "fixed" display transform).</summary>
        public static M4 CampfireItemTransform(string facing, int slot)
        {
            string d = From2D(slot + Data2D(facing));
            return M4.T(0.5f, 0.44921875f, 0.5f) * M4.RotYDeg(-YRot(d)) * M4.RotX(Pi / 2) * M4.T(-0.3125f, -0.3125f, 0) * M4.S(0.375f);
        }

        /// <summary>ShelfRenderer: item slot 0-2 (before the item's "on_shelf" display transform), items centred.</summary>
        public static M4 ShelfItemTransform(string facing, int slot, bool alignBottom = false) =>
            M4.T(0.5f, 0.5f, 0.5f) * M4.RotYDeg(-YRot(facing)) * M4.T((slot - 1) * 0.3125f, alignBottom ? -0.25f : 0, -0.25f) * M4.S(0.25f);

        /// <summary>VaultRenderer: the displayed item, spinning (before the item's "ground" display transform).</summary>
        public static M4 VaultItemTransform(float spinDeg) => M4.T(0.5f, 0.4f, 0.5f) * M4.RotYDeg(spinDeg);

        /// <summary>An item model's display transform (ItemTransform.apply), then the model's centring: translate(-0.5).
        /// Item meshes here are figure space (-16 * (q - 0.5)), so the centring becomes S(-1/16).</summary>
        public static M4 ItemDisplayTransform(ItemDisplay d)
        {
            if (!d.Set) d = ItemDisplay.Identity;
            float r = Pi / 180;
            return M4.T(d.Tx / 16f, d.Ty / 16f, d.Tz / 16f) * M4.RotX(d.Rx * r) * M4.RotY(d.Ry * r) * M4.RotZ(d.Rz * r) * M4.S(d.Sx, d.Sy, d.Sz) * M4.S(-1f / 16);
        }

        // ---------- poses (the renderers' setupAnim, as absolute part values) ----------

        static readonly Dictionary<string, PoseValue[]> PoseTable = new Dictionary<string, PoseValue[]>
        {
            // DragonHeadModel: jaw = (sin(0) + 1) * 0.2 with no redstone animation.
            ["dragon"] = new[] { new PoseValue("jaw", 3, 0.2f) },
            // PiglinHeadModel: ears at -(cos(0) + 2.5) * 0.2 and +(cos(0) + 2.5) * 0.2.
            ["piglin"] = new[] { new PoseValue("left_ear", 5, -0.7f), new PoseValue("right_ear", 5, 0.7f) },
            // CopperGolemStatueModel: the root at y 0, turned upright (zRot pi).
            ["statue"] = new[] { new PoseValue("", 1, 0), new PoseValue("", 5, Pi) },
            // BannerFlagModel: (-0.0125 + 0.01 cos(2 pi phase)) * pi; the flag hangs at the middle of its sway.
            ["flag"] = new[] { new PoseValue("flag", 3, -0.0125f * Pi) },
        };

        /// <summary>BookModel.setupAnim for an openness angle (forAnimation's (sin(t 0.02) 0.1 + 1.25) * open) and page flips.</summary>
        public static PoseValue[] BookPose(float angle, float flip1, float flip2)
        {
            float sx = (float)Math.Sin(angle);
            return new[]
            {
                new PoseValue("left_lid", 4, Pi + angle), new PoseValue("right_lid", 4, -angle),
                new PoseValue("left_pages", 4, angle), new PoseValue("right_pages", 4, -angle),
                new PoseValue("flip_page1", 4, angle - angle * 2 * flip1), new PoseValue("flip_page2", 4, angle - angle * 2 * flip2),
                new PoseValue("left_pages", 0, sx), new PoseValue("right_pages", 0, sx), new PoseValue("flip_page1", 0, sx), new PoseValue("flip_page2", 0, sx),
            };
        }

        static BlockEntityCatalog()
        {
            PoseTable["book_lectern"] = BookPose(1.25f * 1.2f, 0.1f, 0.9f);   // LecternRenderer: forAnimation(0, 0.1, 0.9, 1.2)
            PoseTable["book_closed"] = BookPose(0, 0, 0);
        }

        /// <summary>A pose by key (null when the key is null or unknown).</summary>
        public static PoseValue[] Pose(string key) => key != null && PoseTable.TryGetValue(key, out var p) ? p : null;

        // ---------- textures ----------

        /// <summary>Texture keys: a pack texture path ("entity/chest/normal"), "skin:url|uuid|name" (a player head),
        /// "banner:base|pattern=color|..." (a banner flag composited from its layers).</summary>
        public const string SkinPrefix = "skin:", BannerPrefix = "banner:";

        static string ChestTexture(string path, string type)
        {
            string m;
            if (path == "trapped_chest") m = "trapped";
            else if (path == "ender_chest") return "entity/chest/ender";
            else if (path.EndsWith("copper_chest"))
            {
                string p = path.StartsWith("waxed_") ? path.Substring(6) : path;
                m = p.StartsWith("exposed_") ? "copper_exposed" : p.StartsWith("weathered_") ? "copper_weathered" : p.StartsWith("oxidized_") ? "copper_oxidized" : "copper";
            }
            else m = "normal";
            return "entity/chest/" + m + (type == "left" ? "_left" : type == "right" ? "_right" : "");
        }

        static readonly string[] DyeNames =
        {
            "white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray", "light_gray", "cyan", "purple", "blue",
            "brown", "green", "red", "black",
        };

        /// <summary>The dye colour a block id starts with ("light_blue_banner" -> "light_blue"), or null.</summary>
        public static string DyePrefix(string path)
        {
            string best = null;
            foreach (var d in DyeNames) if (path.StartsWith(d + "_") && (best == null || d.Length > best.Length)) best = d;
            return best;
        }

        static string SkullType(string path)
        {
            int cut = path.IndexOf("_wall_", StringComparison.Ordinal);
            string t = cut >= 0 ? path.Substring(0, cut) : path.Substring(0, path.LastIndexOf('_'));
            return t;   // skeleton, wither_skeleton, zombie, player, creeper, dragon, piglin
        }

        static string SkullLayer(string type) =>
            type == "skeleton" ? "skeleton_skull" : type == "wither_skeleton" ? "wither_skeleton_skull" : type == "dragon" ? "dragon_skull" : type + "_head";

        static string SkullTexture(string type)
        {
            switch (type)
            {
                case "skeleton": return "entity/skeleton/skeleton";
                case "wither_skeleton": return "entity/skeleton/wither_skeleton";
                case "zombie": return "entity/zombie/zombie";
                case "creeper": return "entity/creeper/creeper";
                case "dragon": return "entity/enderdragon/dragon";
                case "piglin": return "entity/piglin/piglin";
                default: return null;
            }
        }

        /// <summary>A sherd item (or brick) -> the decorated pot side texture.</summary>
        public static string SherdTexture(string item)
        {
            string p = item ?? "";
            int c = p.IndexOf(':'); if (c >= 0) p = p.Substring(c + 1);
            return p.EndsWith("_pottery_sherd") ? "entity/decorated_pot/" + p.Substring(0, p.Length - "_sherd".Length) + "_pattern" : "entity/decorated_pot/decorated_pot_side";
        }

        static string StatueTexture(string path)
        {
            string p = path.StartsWith("waxed_") ? path.Substring(6) : path;
            string w = p.StartsWith("exposed_") ? "_exposed" : p.StartsWith("weathered_") ? "_weathered" : p.StartsWith("oxidized_") ? "_oxidized" : "";
            return "entity/copper_golem/copper_golem" + w;
        }

        /// <summary>The texture key of a player head: its skin URL when the server sent one, else the default skin
        /// the game picks for the profile's UUID (or Steve without a profile).</summary>
        public static string SkinKey(BlockEntityData d) => SkinPrefix + (d?.Skin ?? "") + "|" + (d?.SkinUuid ?? "") + "|" + (d?.SkinName ?? "");

        /// <summary>The banner flag texture key: base colour plus the pattern layers (at most 16, as vanilla).</summary>
        public static string BannerKey(string baseColor, BlockEntityData d)
        {
            var sb = new System.Text.StringBuilder(BannerPrefix).Append(baseColor);
            if (d?.Patterns != null)
                for (int i = 0; i < d.Patterns.Count && i < 16; i++) sb.Append('|').Append(d.Patterns[i].Pattern).Append('=').Append(d.Patterns[i].Color);
            return sb.ToString();
        }

        // ---------- parts ----------

        /// <summary>The static model parts of a block entity (state + optional server data). Dynamic extras (sign text,
        /// beams, spinning items and mobs, the enchanting table's book, an active conduit) are drawn by the renderer.</summary>
        public static void Parts(string state, BlockEntityData data, List<BEPart> into)
        {
            into.Clear();
            string path = BlockRules.Path(state);
            var kind = KindOf(path);
            if (kind == BlockEntityKind.None) return;
            var p = BlockModelBaker.Props(state);
            string facing = Prop(p, "facing", "north");
            switch (kind)
            {
                case BlockEntityKind.Chest:
                {
                    string type = Prop(p, "type", "single");
                    string model = type == "left" ? "double_chest_left" : type == "right" ? "double_chest_right" : "chest";
                    into.Add(new BEPart { Model = model, Texture = ChestTexture(path, type), M = ChestTransform(facing) });
                    break;
                }
                case BlockEntityKind.ShulkerBox:
                {
                    string dye = DyePrefix(path);
                    into.Add(new BEPart { Model = "shulker_box", Texture = "entity/shulker/shulker" + (dye != null ? "_" + dye : ""), M = ShulkerTransform(Prop(p, "facing", "up")) });
                    break;
                }
                case BlockEntityKind.Skull:
                {
                    string type = SkullType(path);
                    bool wall = path.Contains("_wall_");
                    var m = SkullTransform(wall, facing, PropInt(p, "rotation"));
                    string tex = type == "player" ? SkinKey(data) : SkullTexture(type);
                    into.Add(new BEPart { Model = SkullLayer(type), Texture = tex, M = m, Pose = type == "dragon" ? "dragon" : type == "piglin" ? "piglin" : null });
                    break;
                }
                case BlockEntityKind.Banner:
                {
                    bool wall = path.EndsWith("_wall_banner");
                    var m = BannerTransform(wall, facing, PropInt(p, "rotation"));
                    string layer = wall ? "wall_banner" : "standing_banner";
                    into.Add(new BEPart { Model = layer, Texture = "entity/banner/banner_base", M = m });
                    into.Add(new BEPart { Model = layer + "#flag", Texture = BannerKey(DyePrefix(path) ?? "white", data), M = m, Pose = "flag" });
                    break;
                }
                case BlockEntityKind.Bell:
                    into.Add(new BEPart { Model = "bell", Texture = "entity/bell/bell_body", M = M4.Identity });
                    break;
                case BlockEntityKind.DecoratedPot:
                {
                    var m = PotTransform(facing);
                    into.Add(new BEPart { Model = "decorated_pot_base", Texture = "entity/decorated_pot/decorated_pot_base", M = m });
                    string[] sides = { "back", "left", "right", "front" };   // PotDecorations order
                    for (int i = 0; i < 4; i++)
                        into.Add(new BEPart { Model = "decorated_pot_sides", Only = sides[i], Texture = SherdTexture(data?.Sherds != null && i < data.Sherds.Length ? data.Sherds[i] : null), M = m });
                    break;
                }
                case BlockEntityKind.Conduit:
                    // Active conduits (cage, wind, eye) are animated by the renderer; the closed shell is static.
                    if (data == null || !data.Active) into.Add(new BEPart { Model = "conduit#shell", Texture = "entity/conduit/base", M = M4.T(0.5f, 0.5f, 0.5f) });
                    break;
                case BlockEntityKind.CopperGolemStatue:
                {
                    string pose = Prop(p, "copper_golem_pose", "standing");
                    string layer = pose == "running" ? "copper_golem_running" : pose == "sitting" ? "copper_golem_sitting" : pose == "star" ? "copper_golem_star" : "copper_golem";
                    into.Add(new BEPart { Model = layer, Texture = StatueTexture(path), M = StatueTransform(facing), Pose = "statue" });
                    break;
                }
                case BlockEntityKind.Lectern:
                    if (Prop(p, "has_book", "false") == "true")
                        into.Add(new BEPart { Model = "book", Texture = "entity/enchantment/enchanting_table_book", M = LecternBookTransform(facing), Pose = "book_lectern" });
                    break;
            }
        }

        /// <summary>The kinds with something drawn on top of their block model or by code over time.</summary>
        public static bool HasExtras(BlockEntityKind k) =>
            k == BlockEntityKind.Sign || k == BlockEntityKind.HangingSign || k == BlockEntityKind.EnchantingTable || k == BlockEntityKind.Beacon ||
            k == BlockEntityKind.Spawner || k == BlockEntityKind.TrialSpawner || k == BlockEntityKind.Campfire || k == BlockEntityKind.Shelf ||
            k == BlockEntityKind.Vault || k == BlockEntityKind.Conduit || k == BlockEntityKind.EndPortal || k == BlockEntityKind.EndGateway;

        /// <summary>Sign text: StandingSignRenderer/HangingSignRenderer text transformation for one side, mapping text
        /// pixels (x right, y down, the font's units) into block space.</summary>
        public static M4 SignTextTransform(string state, bool front)
        {
            string path = BlockRules.Path(state);
            var p = BlockModelBaker.Props(state);
            bool hanging = path.EndsWith("_hanging_sign");
            bool wall = path.EndsWith("_wall_sign") || path.EndsWith("_wall_hanging_sign");
            float angle = wall ? YRot(Prop(p, "facing", "north")) : PropInt(p, "rotation") * 22.5f;
            M4 m;
            if (hanging)
            {
                m = M4.T(0.5f, 0.9375f, 0.5f) * M4.RotYDeg(-angle) * M4.T(0, -0.3125f, 0);
                if (!front) m = m * M4.RotYDeg(180);
                const float s = 0.0140625f;
                return m * M4.T(0, -0.32f, 0.073f) * M4.S(s, -s, s);
            }
            m = M4.T(0.5f, 0.5f, 0.5f) * M4.RotYDeg(-angle);
            if (wall) m = m * M4.T(0, -0.3125f, -0.4375f);
            if (!front) m = m * M4.RotYDeg(180);
            const float k = 0.010416667f;
            return m * M4.T(0, 1f / 3, 0.046666667f) * M4.S(k, -k, k);
        }

        /// <summary>SignBlockEntity line height and wrap width: 10 / 90 px, hanging signs 9 / 60.</summary>
        public static void SignMetrics(string state, out int lineHeight, out int maxWidth)
        {
            bool hanging = BlockRules.Path(state).EndsWith("_hanging_sign");
            lineHeight = hanging ? 9 : 10;
            maxWidth = hanging ? 60 : 90;
        }
    }
}
