// Offline check of the entity figures (no headset): composes a few entities the way EntityFigure / EntityGear build
// their hierarchies (root yaw, model-root offset, part pivots and rotations, second layers, held items) and writes the
// mesh in tools/render_mesh.py's format, in Minecraft block coordinates. Textures are named, read from the jar by the
// renderer. Overlays (villager professions) are left out: render_mesh draws one texture per triangle.
//   dotnet run -- --jar CLIENT.jar --entity-scene OUT.json
//   uv run --no-project --with pillow --with numpy python tools/render_mesh.py OUT.json CLIENT.jar OUT.png --px 64
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

static class EntityScene
{
    sealed class Out
    {
        public readonly List<float> Verts = new List<float>(), Uvs = new List<float>();
        public readonly List<int> Colors = new List<int>(), Tris = new List<int>();
        public readonly List<string> Layers = new List<string>();
        public int Layer(string tex) { tex = tex.Contains(':') ? tex : "minecraft:" + tex; int i = Layers.IndexOf(tex); if (i < 0) { i = Layers.Count; Layers.Add(tex); } return i; }
    }

    // 4x4 row-major helpers (Unity's matrices, same numbers as the game's for rotations).
    internal static float[] Mul(float[] a, float[] b) { var r = new float[16]; for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) for (int k = 0; k < 4; k++) r[i * 4 + j] += a[i * 4 + k] * b[k * 4 + j]; return r; }
    internal static float[] I() => new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
    internal static float[] T(float x, float y, float z) { var m = I(); m[3] = x; m[7] = y; m[11] = z; return m; }
    internal static float[] S(float x, float y, float z) { var m = I(); m[0] = x; m[5] = y; m[10] = z; return m; }
    internal static float[] RX(float d) { var m = I(); float r = d * (float)(Math.PI / 180), c = (float)Math.Cos(r), s = (float)Math.Sin(r); m[5] = c; m[6] = -s; m[9] = s; m[10] = c; return m; }
    internal static float[] RY(float d) { var m = I(); float r = d * (float)(Math.PI / 180), c = (float)Math.Cos(r), s = (float)Math.Sin(r); m[0] = c; m[2] = s; m[8] = -s; m[10] = c; return m; }
    internal static float[] RZ(float d) { var m = I(); float r = d * (float)(Math.PI / 180), c = (float)Math.Cos(r), s = (float)Math.Sin(r); m[0] = c; m[1] = -s; m[4] = s; m[5] = c; return m; }
    static float[] Rot(float x, float y, float z) => Mul(RZ(z * (float)(180 / Math.PI)), Mul(RY(y * (float)(180 / Math.PI)), RX(x * (float)(180 / Math.PI))));

    /// <summary>Appends a figure-space mesh under Unity matrix m, converted to game block coordinates (Z mirrored back).</summary>
    static void Emit(Out o, float[] m, FigureMesh fm, int layer, Color32 tint)
    {
        int b = o.Verts.Count / 3;
        for (int i = 0; i < fm.VertexCount; i++)
        {
            float x = fm.Pos[3 * i], y = fm.Pos[3 * i + 1], z = fm.Pos[3 * i + 2];
            float ux = m[0] * x + m[1] * y + m[2] * z + m[3], uy = m[4] * x + m[5] * y + m[6] * z + m[7], uz = m[8] * x + m[9] * y + m[10] * z + m[11];
            o.Verts.Add(ux); o.Verts.Add(uy); o.Verts.Add(-uz);
            o.Uvs.Add(fm.Uv[2 * i]); o.Uvs.Add(fm.Uv[2 * i + 1]); o.Uvs.Add(layer);
            o.Colors.Add(tint.r); o.Colors.Add(tint.g); o.Colors.Add(tint.b); o.Colors.Add(255);
        }
        // The Z mirror back to the game frame flips the winding (as TabletopRenderer's mirror does the other way).
        for (int t = 0; t < fm.Tris.Count; t += 3) { o.Tris.Add(b + fm.Tris[t]); o.Tris.Add(b + fm.Tris[t + 2]); o.Tris.Add(b + fm.Tris[t + 1]); }
    }

    static readonly Color32 White = new Color32(255, 255, 255, 255);

    /// <summary>One entity like EntityFigure: returns the part matrices so gear can hang on them.</summary>
    static Dictionary<string, float[]> Figure(Out o, EntityInfo e, float x, float y, float z, Func<EModel, float[]> anim = null)
    {
        var look = EntityCatalog.Resolve(e);
        var model = EntityModels.Get(look.Model);
        var parts = new Dictionary<string, float[]>();
        if (model == null) return parts;
        float yaw = e.HasBodyYaw ? e.BodyYaw : e.Yaw;
        var root = Mul(T(x, y, -z), Mul(RY(EntityCatalog.RootTurn(look.Root, yaw)), S(look.Scale / 16f, look.Scale / 16f, look.Scale / 16f)));
        float[] mr = ModelRoot(look.Root, e.Pitch);
        var rot = anim?.Invoke(model) ?? new float[model.Parts.Count * 3];
        var world = new float[model.Parts.Count][];
        int layer = o.Layer(look.Texture);
        foreach (var p in model.Parts)
        {
            EntityModels.Pivot(p, out float px, out float py, out float pz);
            var local = Mul(T(px, py, pz), Mul(Rot(p.Rx + rot[3 * p.Index], p.Ry + rot[3 * p.Index + 1], p.Rz + rot[3 * p.Index + 2]), S(p.Sx, p.Sy, p.Sz)));
            world[p.Index] = Mul(p.Parent == null ? Mul(root, mr) : world[p.Parent.Index], local);
            if (!string.IsNullOrEmpty(p.Name) && !parts.ContainsKey(p.Name)) parts[p.Name] = world[p.Index];
            if (look.HideArms && (p.Name == "right_arm" || p.Name == "left_arm")) continue;
            var fm = new FigureMesh();
            EntityModels.BuildPart(model, p, fm);
            Emit(o, world[p.Index], fm, layer, look.Tint);
        }
        if (look.ExtraModel != null && EntityModels.Get(look.ExtraModel) is EModel extra)
        {
            int el = o.Layer(look.ExtraTexture);
            foreach (var p in extra.Parts)
            {
                var hp = model.Find(p.Name);
                if (hp == null) continue;
                // EntityFigure hangs the layer part on the host part, correcting only a different default rotation.
                var fix = Mul(Inverse(Rot(hp.Rx, hp.Ry, hp.Rz)), Rot(p.Rx, p.Ry, p.Rz));
                var fm = new FigureMesh();
                EntityModels.BuildPart(extra, p, fm);
                Emit(o, Mul(world[hp.Index], fix), fm, el, look.ExtraTint);
            }
        }
        return parts;
    }

    /// <summary>The figure's model root (EntityCatalog.RootOffset + RootRotation) as a matrix.</summary>
    internal static float[] ModelRoot(RootKind k, float pitch)
    {
        var ro = EntityCatalog.RootOffset(k);
        EntityCatalog.RootRotation(k, pitch, out float yaw, out float roll);
        return Mul(T(ro.x, ro.y, ro.z), Mul(RY(yaw), RZ(roll)));
    }

    internal static Vector3 Apply(float[] m, Vector3 p) =>
        new Vector3(m[0] * p.x + m[1] * p.y + m[2] * p.z + m[3], m[4] * p.x + m[5] * p.y + m[6] * p.z + m[7], m[8] * p.x + m[9] * p.y + m[10] * p.z + m[11]);

    static float[] Inverse(float[] r) { var t = I(); for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) t[i * 4 + j] = r[j * 4 + i]; return t; }   // rotations only

    /// <summary>An item held like EntityGear.Held.</summary>
    static void Held(Out o, float[] arm, ItemVisual v, bool left)
    {
        if (arm == null || v.Kind != ItemVisualKind.Sprite) return;
        var d = v.ThirdPersonRight;
        float side = left ? -1 : 1, tx = left ? -d.Tx : d.Tx, ry = left ? -d.Ry : d.Ry, rz = left ? -d.Rz : d.Rz;
        var m = Mul(arm, Mul(Mul(RX(-90), RY(180)), Mul(T(-side - tx, -2 - d.Ty, 10 - d.Tz), Mul(Mul(RX(d.Rx), Mul(RY(ry), RZ(rz))), S(d.Sx, d.Sy, d.Sz)))));
        var fm = new FigureMesh();
        // EntityMeshes.Sprite: the 16x16 px item square, point-reflected (both windings: the entity shader draws both sides).
        float[][] c = { new[] { 0f, 0 }, new[] { 1f, 0 }, new[] { 1f, 1 }, new[] { 0f, 1 } };
        for (int k = 0; k < 4; k++) { fm.Pos.Add(-16 * (c[k][0] - 0.5f)); fm.Pos.Add(-16 * (c[k][1] - 0.5f)); fm.Pos.Add(0); fm.Normal.AddRange(new[] { 0f, 0, -1 }); fm.Uv.Add(c[k][0]); fm.Uv.Add(c[k][1]); }
        fm.Tris.AddRange(new[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 });
        Emit(o, m, fm, o.Layer(v.Sprite), White);
    }

    /// <summary>Armour like EntityGear.Armor: the humanoid armour layer's parts on the wearer's parts.</summary>
    static void Armor(Out o, Dictionary<string, float[]> parts, string item, string slot, Func<string, Dictionary<string, object>> read)
    {
        string layer = slot == "head" ? "player#helmet" : slot == "chest" ? "player#chestplate" : slot == "legs" ? "player#leggings" : "player#boots";
        string tex = EntityCatalog.ArmorTexture(item, slot, out bool dyeable);
        var tint = dyeable ? ItemModels.Resolve(item, read).SpriteTint : White;   // leather: the item's default dye
        var model = EntityModels.Get(layer);
        if (tex == null || model == null) return;
        int l = o.Layer(tex);
        foreach (var p in model.Parts)
        {
            if (p.Cubes.Count == 0 || !parts.TryGetValue(p.Name == "hat" ? "head" : p.Name, out var host)) continue;
            var fm = new FigureMesh();
            EntityModels.BuildPart(model, p, fm);
            Emit(o, host, fm, l, tint);
        }
    }

    public static void Run(string jar, string outPath)
    {
        using var zip = ZipFile.OpenRead(jar);
        Dictionary<string, object> Read(string path)
        {
            var e = zip.GetEntry(path);
            if (e == null) return null;
            using var r = new StreamReader(e.Open());
            return Json.Parse(r.ReadToEnd()) as Dictionary<string, object>;
        }
        var o = new Out();
        // A grass floor 14 x 8 so the feet have something to stand on.
        var grass = new FigureMesh();
        grass.Pos.AddRange(new[] { 0f, 0, 0, 14, 0, 0, 14, 0, -8, 0, 0, -8 });
        grass.Normal.AddRange(new[] { 0f, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0 });
        grass.Uv.AddRange(new[] { 0f, 0, 14, 0, 14, 8, 0, 8 });
        grass.Tris.AddRange(new[] { 0, 1, 2, 0, 2, 3 });
        Emit(o, I(), grass, o.Layer("block/grass_block_top"), new Color32(124, 189, 107, 255));

        EntityInfo E(string type, float yaw = 0, string variant = null) => new EntityInfo { Type = "minecraft:" + type, Yaw = yaw, BodyYaw = yaw, HasBodyYaw = true, Variant = variant, Width = 1, Height = 1 };
        float[] Walk(EModel m, float pos, float headYaw)
        {
            var r = new float[m.Parts.Count * 3];
            EntityAnimation.Pose(EntityRig.For(m), new EntityAnimInput { WalkPos = pos, WalkSpeed = 1, HeadYaw = headYaw, OnGround = true }, r);
            return r;
        }
        // Row 1 (z 1.5): boat with a riding zombie, cow walking with its head turned, red sheep, chicken.
        var boat = E("oak_boat", 30); boat.HasBodyYaw = false;
        Figure(o, boat, 2f, 0, 1.5f);
        var rider = E("zombie", 30);
        var rp = Figure(o, rider, 2f, 0.45f, 1.5f, m => { var r = new float[m.Parts.Count * 3]; EntityAnimation.Pose(EntityRig.For(m), new EntityAnimInput { Riding = true, ArmsForward = true }, r); return r; });
        Held(o, rp.TryGetValue("right_arm", out var ra) ? ra : null, ItemModels.Resolve("minecraft:iron_sword", Read), false);
        Figure(o, E("cow", -45), 5.5f, 0, 1.5f, m => Walk(m, 2, 30));
        var sheep = E("sheep", 0); sheep.Color = "red";
        Figure(o, sheep, 8.5f, 0, 1.5f);
        Figure(o, E("chicken", 90), 11f, 0, 1.5f, m => Walk(m, 1, 0));
        Figure(o, E("pig", 180, "warm"), 13f, 0, 1.5f);
        // Row 2 (z 4.5): villager, horse, wolf, creeper, skeleton, armour stand.
        Figure(o, E("villager", 20, "plains"), 1.5f, 0, 4.5f);
        Figure(o, E("horse", -20, "chestnut"), 4f, 0, 4.5f, m => Walk(m, 3, 0));
        Figure(o, E("wolf", 0, "pale"), 6.5f, 0, 4.5f);
        Figure(o, E("creeper", 0), 8.5f, 0, 4.5f);
        var sk = Figure(o, E("skeleton", 0), 10.5f, 0, 4.5f);
        Held(o, sk.TryGetValue("right_arm", out var sa) ? sa : null, ItemModels.Resolve("minecraft:bow", Read), false);
        Figure(o, E("cat", 0, "calico"), 12.5f, 0, 4.5f);
        // Row 3 (z 7): minecart, spider, axolotl, baby cow.
        var cart = E("minecart", 90); cart.HasBodyYaw = false;
        Figure(o, cart, 2f, 0, 7f);
        Figure(o, E("spider", 0), 5f, 0, 7f, m => Walk(m, 1, 0));
        var baby = E("cow", 0, "warm"); baby.Baby = true;
        Figure(o, baby, 8f, 0, 7f);
        Figure(o, E("iron_golem", 0), 11f, 0, 7f);
        // A mannequin (player model) in armour, sword in the main hand and a torch in the off hand, walking.
        var man = Figure(o, E("mannequin", 0), 13f, 0, 7f);
        foreach (var (item, slot) in new[] { ("minecraft:iron_helmet", "head"), ("minecraft:diamond_chestplate", "chest"), ("minecraft:leather_leggings", "legs"), ("minecraft:golden_boots", "feet") })
            Armor(o, man, item, slot, Read);
        Held(o, man.TryGetValue("right_arm", out var mra) ? mra : null, ItemModels.Resolve("minecraft:diamond_sword", Read), false);
        Held(o, man.TryGetValue("left_arm", out var mla) ? mla : null, ItemModels.Resolve("minecraft:torch", Read), true);
        // Row 4 (z 10): an arrow stuck in the ground flying north-east and down, a trident flying east, an end crystal.
        EntityInfo P(string type, float yaw, float pitch) => new EntityInfo { Type = "minecraft:" + type, Yaw = yaw, BodyYaw = yaw, Pitch = pitch, Width = 0.5f, Height = 0.5f };
        Figure(o, P("arrow", 135, -40), 2f, 0.2f, 10f);
        Figure(o, P("trident", 90, 0), 5f, 1f, 10f);
        Figure(o, P("end_crystal", 0, 0), 9f, 1f, 10f);

        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("{\"layers\":[").Append(string.Join(",", o.Layers.Select(x => "\"" + x + "\""))).Append("],\"verts\":[");
        sb.Append(string.Join(",", o.Verts.Select(v => v.ToString("G6", inv)))).Append("],\"uvs\":[");
        sb.Append(string.Join(",", o.Uvs.Select(v => v.ToString("G6", inv)))).Append("],\"colors\":[");
        sb.Append(string.Join(",", o.Colors)).Append("],\"opaque\":[").Append(string.Join(",", o.Tris)).Append("],\"transparent\":[]}");
        File.WriteAllText(outPath, sb.ToString());
        Console.WriteLine($"entity scene: {o.Tris.Count / 6} faces, {o.Layers.Count} textures -> {outPath}");
    }
}
