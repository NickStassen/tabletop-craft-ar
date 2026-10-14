// What an item looks like, from the user's pack: assets/<ns>/items/<id>.json (26.x item model definitions: the first
// plain "model" reference, following select/condition/range_dispatch fallbacks and special models' "base") -> the
// model's parent chain -> a flat sprite (item/generated: layer0, layer1) or a block-like cube (the first element's
// face textures, or the particle texture for builtin/entity models such as chests). Engine-free; the Quest turns the
// result into a sprite quad or a small cube (EntityTextures), Tests~ checks it against the real client jar.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public enum ItemVisualKind { None, Sprite, Cube }

    /// <summary>A model's "display" entry: rotation (degrees, applied X then Y then Z), translation (pixels), scale.</summary>
    public struct ItemDisplay
    {
        public float Rx, Ry, Rz, Tx, Ty, Tz, Sx, Sy, Sz;
        public bool Set;
        public static readonly ItemDisplay Identity = new ItemDisplay { Sx = 1, Sy = 1, Sz = 1 };
    }

    public sealed class ItemVisual
    {
        public ItemVisualKind Kind;
        public string Item, Model;
        public string Sprite, Overlay;                 // Sprite: layer0 and optional layer1 texture paths
        public Color32 SpriteTint = EntityLook.White;  // a constant or default dye tint on layer0 (leather armour, potions)
        public bool Handheld;                          // tools: held diagonally (item/handheld)
        /// <summary>Cube: textures per game face, order down, up, north, south, west, east; Tinted = model tintindex.</summary>
        public readonly string[] Faces = new string[6];
        public readonly bool[] Tinted = new bool[6];
        /// <summary>Display transforms from the model chain: held in the right hand, lying on the ground, in a frame.</summary>
        public ItemDisplay ThirdPersonRight = ItemDisplay.Identity, Ground = ItemDisplay.Identity, Fixed = ItemDisplay.Identity;

        public string Key => Kind == ItemVisualKind.Sprite ? "sprite:" + Sprite + "+" + Overlay + "#" + SpriteTint.r + "," + SpriteTint.g + "," + SpriteTint.b
                           : Kind == ItemVisualKind.Cube ? "cube:" + string.Join(",", Faces) + "#" + string.Join("", Array.ConvertAll(Tinted, t => t ? "1" : "0")) : "none";
    }

    public static class ItemModels
    {
        static readonly string[] Dirs = { "down", "up", "north", "south", "west", "east" };

        /// <summary>Resolves an item id ("minecraft:diamond") with readJson(path in the pack). Never null.</summary>
        public static ItemVisual Resolve(string item, Func<string, Dictionary<string, object>> readJson)
        {
            var r = new ItemVisual { Item = item };
            if (string.IsNullOrEmpty(item) || readJson == null) return r;
            Split(item, out string ns, out string path);
            string model = null;
            var def = readJson($"assets/{ns}/items/{path}.json");
            if (def != null && def.O("model") is Dictionary<string, object> root) model = FindModel(root, ref r.SpriteTint, 0);
            if (model == null) model = $"{ns}:item/{path}";   // packs from before item model definitions
            r.Model = model;
            if (!FromModel(model, readJson, r) && def == null) FromModel($"{ns}:block/{path}", readJson, r);
            return r;
        }

        static void Split(string id, out string ns, out string path)
        {
            int c = id.IndexOf(':');
            ns = c >= 0 ? id.Substring(0, c) : "minecraft";
            path = c >= 0 ? id.Substring(c + 1) : id;
        }

        /// <summary>The first plain model reference in an item model definition (the default case of every switch).</summary>
        static string FindModel(Dictionary<string, object> node, ref Color32 tint, int depth)
        {
            if (node == null || depth > 8) return null;
            string type = node.S("type") ?? "";
            if (type.StartsWith("minecraft:")) type = type.Substring(10);
            if (type == "model")
            {
                if (node.A("tints") is List<object> tints && tints.Count > 0 && tints[0] is Dictionary<string, object> t0)
                {
                    object v = t0.TryGetValue("default", out var dv) ? dv : t0.TryGetValue("value", out var vv) ? vv : null;
                    if (v is double d) { long c = (long)d & 0xFFFFFF; tint = new Color32((byte)(c >> 16), (byte)(c >> 8), (byte)c, 255); }
                }
                return node.S("model");
            }
            if (type == "special") return node.S("base");
            foreach (var k in new[] { "fallback", "on_false", "on_true", "model" })
                if (node.O(k) is Dictionary<string, object> sub) { var m = FindModel(sub, ref tint, depth + 1); if (m != null) return m; }
            foreach (var k in new[] { "cases", "entries", "models" })
                if (node.A(k) is List<object> list)
                    foreach (var e in list)
                        if (e is Dictionary<string, object> ed)
                        {
                            var m = FindModel(ed.O("model") ?? ed, ref tint, depth + 1);
                            if (m != null) return m;
                        }
            return null;
        }

        /// <summary>Follows a model's parents; fills a sprite or cube visual. False when the model doesn't exist.</summary>
        static bool FromModel(string model, Func<string, Dictionary<string, object>> readJson, ItemVisual r)
        {
            var textures = new Dictionary<string, string>();
            List<object> elements = null;
            bool generated = false, builtinEntity = false, found = false;
            string cur = model;
            for (int depth = 0; depth < 16 && cur != null; depth++)
            {
                string id = cur.StartsWith("minecraft:") ? cur.Substring(10) : cur;
                if (id == "builtin/generated" || id == "item/generated") generated = true;
                if (id == "item/handheld" || id == "item/handheld_rod" || id == "item/handheld_mace") r.Handheld = true;
                if (id == "builtin/entity") { builtinEntity = true; break; }
                if (id.StartsWith("builtin/")) break;
                Split(cur, out string ns, out string path);
                var m = readJson($"assets/{ns}/models/{path}.json");
                if (m == null) break;
                found = true;
                if (m.O("display") is Dictionary<string, object> disp)
                {
                    Display(disp.O("thirdperson_righthand"), ref r.ThirdPersonRight);
                    Display(disp.O("ground"), ref r.Ground);
                    Display(disp.O("fixed"), ref r.Fixed);
                }
                if (m.O("textures") is Dictionary<string, object> t)
                    foreach (var kv in t) if (!textures.ContainsKey(kv.Key)) textures[kv.Key] = TextureRef(kv.Value);
                if (elements == null && m.A("elements") is List<object> el && el.Count > 0) elements = el;
                cur = m.S("parent");
            }
            if (!found) return false;
            if (elements != null && !generated)
            {
                var faces = (elements[0] as Dictionary<string, object>)?.O("faces");
                string any = null;
                for (int i = 0; i < 6; i++)
                {
                    var f = faces?.O(Dirs[i]);
                    if (f == null) continue;
                    string tr = f.S("texture");
                    if (tr != null && !tr.StartsWith("#") && textures.ContainsKey(tr)) tr = "#" + tr;   // "all" means "#all" too
                    r.Faces[i] = Lookup(textures, tr);
                    r.Tinted[i] = f.ContainsKey("tintindex");
                    any = any ?? r.Faces[i];
                }
                any = any ?? Lookup(textures, "#particle");
                if (any == null) return true;
                for (int i = 0; i < 6; i++) if (r.Faces[i] == null) r.Faces[i] = any;
                r.Kind = ItemVisualKind.Cube;
                return true;
            }
            string layer0 = Lookup(textures, "#layer0");
            if (layer0 != null)
            {
                r.Kind = ItemVisualKind.Sprite; r.Sprite = layer0; r.Overlay = Lookup(textures, "#layer1");
                return true;
            }
            string particle = Lookup(textures, "#particle");
            if (particle != null && (builtinEntity || !generated))
            {
                r.Kind = ItemVisualKind.Cube;   // chests, shields, beds, signs...: a cube of the particle texture
                for (int i = 0; i < 6; i++) r.Faces[i] = particle;
            }
            return true;
        }

        /// <summary>The first (most specific) display entry along the parent chain wins.</summary>
        static void Display(Dictionary<string, object> d, ref ItemDisplay into)
        {
            if (d == null || into.Set) return;
            var r = Vec(d, "rotation", 0); var t = Vec(d, "translation", 0); var s = Vec(d, "scale", 1);
            into = new ItemDisplay { Rx = r[0], Ry = r[1], Rz = r[2], Tx = t[0], Ty = t[1], Tz = t[2], Sx = s[0], Sy = s[1], Sz = s[2], Set = true };
        }

        static float[] Vec(Dictionary<string, object> d, string k, float def)
        {
            var a = d.A(k);
            return a != null && a.Count == 3 ? new[] { (float)(double)a[0], (float)(double)a[1], (float)(double)a[2] } : new[] { def, def, def };
        }

        static string TextureRef(object v) => v as string ?? (v as Dictionary<string, object>)?.S("sprite");

        /// <summary>Resolves "#var" references; texture ids come back namespaced ("block/dirt" -> "minecraft:block/dirt").</summary>
        static string Lookup(Dictionary<string, string> t, string r)
        {
            for (int i = 0; i < 8 && r != null && r.StartsWith("#"); i++) t.TryGetValue(r.Substring(1), out r);
            if (r == null || r.StartsWith("#")) return null;
            return r.IndexOf(':') >= 0 ? r : "minecraft:" + r;
        }

        /// <summary>"minecraft:item/diamond" or "block/stone" -> "assets/minecraft/textures/item/diamond.png".</summary>
        public static string TexturePath(string texture)
        {
            Split(texture, out string ns, out string path);
            return $"assets/{ns}/textures/{path}.png";
        }
    }
}
