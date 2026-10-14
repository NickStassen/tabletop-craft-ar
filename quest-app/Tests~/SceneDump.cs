// Offline visual check of the mesher with real block models: builds a demo scene of odd-shaped blocks (or crops a
// saved chunk_snapshot), meshes it with the pack, and writes the mesh as JSON for tools/render_mesh.py.
//   dotnet run -- --jar CLIENT.jar --scene OUT.json [--snapshot FILE.json x0,y0,z0,dx,dy,dz]
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

static class SceneDump
{
    /// <summary>Demo scene: rows of blocks on a grass floor, each "name[props]" at (x, y, z).</summary>
    static readonly (int x, int y, int z, string s)[] Demo =
    {
        // stairs: facings, upside down, corners
        (1, 1, 1, "oak_stairs[facing=east,half=bottom,shape=straight]"), (2, 1, 1, "oak_stairs[facing=south,half=bottom,shape=straight]"),
        (3, 1, 1, "oak_stairs[facing=west,half=bottom,shape=straight]"), (4, 1, 1, "oak_stairs[facing=north,half=bottom,shape=straight]"),
        (5, 1, 1, "oak_stairs[facing=south,half=top,shape=straight]"), (6, 1, 1, "stone_brick_stairs[facing=south,half=bottom,shape=outer_left]"),
        (7, 1, 1, "stone_brick_stairs[facing=south,half=bottom,shape=inner_right]"), (8, 1, 1, "oak_slab[type=bottom]"), (9, 1, 1, "oak_slab[type=top]"),
        (10, 1, 1, "stone_slab[type=double]"),
        // fences, walls, panes, bars
        (1, 1, 3, "oak_fence[east=true,north=false,south=false,west=false]"), (2, 1, 3, "oak_fence[east=true,north=false,south=true,west=true]"),
        (3, 1, 3, "oak_fence[east=false,north=false,south=false,west=true]"), (2, 1, 4, "oak_fence[east=false,north=true,south=false,west=false]"),
        (5, 1, 3, "cobblestone_wall[east=low,north=none,south=none,up=true,west=none]"), (6, 1, 3, "cobblestone_wall[east=low,north=none,south=tall,up=false,west=low]"),
        (7, 1, 3, "cobblestone_wall[east=none,north=none,south=none,up=true,west=low]"), (6, 1, 4, "cobblestone_wall[east=none,north=tall,south=none,up=true,west=none]"),
        (9, 1, 3, "glass_pane[east=true,north=false,south=false,west=false]"), (10, 1, 3, "glass_pane[east=false,north=false,south=true,west=true]"),
        (10, 1, 4, "red_stained_glass_pane[east=false,north=true,south=false,west=false]"), (12, 1, 3, "iron_bars[east=true,north=false,south=false,west=false]"),
        (13, 1, 3, "iron_bars[east=false,north=false,south=false,west=true]"), (14, 1, 3, "oak_fence_gate[facing=south,in_wall=false,open=false]"),
        // logs and oriented cubes, torches
        (1, 1, 6, "oak_log[axis=x]"), (2, 1, 6, "oak_log[axis=x]"), (3, 1, 6, "oak_log[axis=z]"), (4, 1, 6, "oak_log[axis=y]"), (5, 1, 6, "furnace[facing=south,lit=false]"),
        (6, 1, 6, "furnace[facing=east,lit=true]"), (7, 1, 6, "crafting_table"), (8, 1, 6, "stone"), (8, 1, 7, "wall_torch[facing=south]"), (9, 1, 6, "stone"),
        (10, 1, 6, "wall_torch[facing=east]"), (11, 1, 6, "torch"), (12, 1, 6, "lantern[hanging=false]"), (13, 2, 6, "oak_planks"), (13, 1, 6, "lantern[hanging=true]"),
        (14, 1, 6, "hay_block[axis=x]"), (15, 1, 6, "bookshelf"),
        // plants
        (1, 1, 9, "poppy"), (2, 1, 9, "dandelion"), (3, 1, 9, "short_grass"), (4, 1, 9, "tall_grass[half=lower]"), (4, 2, 9, "tall_grass[half=upper]"),
        (5, 1, 9, "wheat[age=7]"), (6, 1, 9, "sugar_cane[age=0]"), (6, 2, 9, "sugar_cane[age=0]"), (7, 1, 9, "oak_sapling[stage=0]"), (8, 1, 9, "sweet_berry_bush[age=3]"),
        (9, 1, 9, "potted_poppy"), (10, 1, 9, "red_carpet"), (11, 1, 9, "rail[shape=north_south]"), (12, 1, 9, "rail[shape=south_east]"),
        (13, 1, 9, "redstone_wire[east=side,north=none,power=15,south=side,west=none]"), (14, 1, 9, "pink_petals[facing=north,flower_amount=4]"), (15, 1, 9, "oak_leaves[distance=1,persistent=true]"),
        // doors, trapdoors, ladders, vines, snow
        (1, 1, 12, "oak_door[facing=south,half=lower,hinge=left,open=false]"), (1, 2, 12, "oak_door[facing=south,half=upper,hinge=left,open=false]"),
        (2, 1, 12, "oak_trapdoor[facing=south,half=bottom,open=false]"), (3, 1, 12, "oak_trapdoor[facing=south,half=bottom,open=true]"),
        (4, 1, 12, "stone"), (4, 1, 13, "ladder[facing=south]"), (5, 1, 12, "stone"), (5, 1, 13, "vine[east=false,north=true,south=false,up=false,west=false]"),
        (6, 1, 12, "snow[layers=1]"), (7, 1, 12, "snow[layers=3]"), (8, 1, 12, "snow[layers=5]"), (9, 1, 12, "snow[layers=7]"), (10, 1, 12, "snow[layers=8]"),
        (11, 1, 12, "grass_block[snowy=false]"), (12, 1, 12, "dirt_path"), (13, 1, 12, "farmland[moisture=7]"), (14, 1, 12, "cauldron"), (15, 1, 12, "hopper[enabled=true,facing=down]"),
        // block entities and furniture
        (1, 1, 15, "chest[facing=south,type=single]"), (2, 1, 15, "chest[facing=south,type=right]"), (3, 1, 15, "chest[facing=south,type=left]"),
        (4, 1, 15, "red_banner[rotation=0]"), (5, 1, 15, "skeleton_skull[rotation=2]"), (6, 1, 15, "red_bed[facing=north,part=foot]"), (6, 1, 14, "red_bed[facing=north,part=head]"),
        (7, 1, 15, "oak_sign[rotation=0]"), (8, 1, 15, "bell[attachment=floor,facing=south]"), (9, 1, 15, "anvil[facing=east]"), (10, 1, 15, "enchanting_table"),
        (11, 1, 15, "lectern[facing=south,has_book=false]"), (12, 1, 15, "campfire[facing=south,lit=true]"), (13, 1, 15, "decorated_pot[facing=south]"),
        (14, 1, 15, "brewing_stand[has_bottle_0=true,has_bottle_1=false,has_bottle_2=true]"), (15, 1, 15, "white_shulker_box[facing=up]"),
    };

    /// <summary>--list-shapes: how every blockstate's first variant is classified with the pack (debugging aid).</summary>
    public static void ListShapes(string jar)
    {
        using var assets = new ZipAssets(jar);
        using var zip = System.IO.Compression.ZipFile.OpenRead(jar);
        BlockRules.ResetShapes();
        var faces = new PackFaces(assets);
        var counts = new Dictionary<string, int>();
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("assets/minecraft/blockstates/") && e.FullName.EndsWith(".json")).OrderBy(e => e.FullName))
        {
            string block = e.Name.Substring(0, e.Name.Length - 5);
            var bs = assets.ReadJson(e.FullName);
            string props = bs.O("variants") is Dictionary<string, object> v && v.Count > 0 ? v.Keys.First() : "";
            string state = "minecraft:" + block + (props.Length > 0 ? "[" + props + "]" : "");
            faces.Face(state, 0);
            var byName = BlockRules.Classify(state);
            var shape = BlockRules.Shape(state);
            var m = faces.Model(state);
            string k = $"{byName}->{shape}";
            counts[k] = counts.TryGetValue(k, out int c) ? c + 1 : 1;
            if ((byName == BlockShape.Cube && shape != BlockShape.Cube) || (shape == BlockShape.Model && (m == null || m.Quads.Length == 0)))
                Console.WriteLine($"  {state}: {byName} -> {shape}, {m?.Quads.Length ?? -1} faces");
        }
        foreach (var kv in counts.OrderByDescending(kv => kv.Value)) Console.WriteLine($"{kv.Key}: {kv.Value}");
        BlockRules.ResetShapes();
    }

    public static void Run(string jar, string outPath, string[] args)
    {
        using var assets = new ZipAssets(jar);
        BlockRules.ResetShapes();
        var faces = new PackFaces(assets);
        var w = new WorldModel { SnapshotId = 1 };
        int snapIdx = Array.IndexOf(args, "--snapshot");
        if (snapIdx >= 0)
        {
            var src = new WorldModel();
            src.ApplySnapshot(ChunkSnapshot.From(Json.Parse(File.ReadAllText(args[snapIdx + 1])) as Dictionary<string, object>));
            var c = args[snapIdx + 2].Split(',').Select(int.Parse).ToArray();
            w.Dims = new Vector3Int(c[3], c[4], c[5]);
            w.Palette.AddRange(src.Palette);
            w.Grid = new ushort[c[3] * c[4] * c[5]];
            for (int y = 0; y < c[4]; y++) for (int z = 0; z < c[5]; z++) for (int x = 0; x < c[3]; x++) w.Grid[w.Index(x, y, z)] = src.Get(c[0] + x, c[1] + y, c[2] + z);
        }
        else
        {
            w.Dims = new Vector3Int(17, 4, 17);
            w.Palette.Add("minecraft:air"); w.Palette.Add("minecraft:grass_block[snowy=false]");
            w.Grid = new ushort[w.Dims.x * w.Dims.y * w.Dims.z];
            for (int z = 0; z < w.Dims.z; z++) for (int x = 0; x < w.Dims.x; x++) w.Grid[w.Index(x, 0, z)] = 1;
            int onlyIdx = Array.IndexOf(args, "--only");   // e.g. --only bed,chest: just those demo entries
            var only = onlyIdx >= 0 ? args[onlyIdx + 1].Split(',') : null;
            foreach (var (x, y, z, s) in Demo)
            {
                if (only != null && !only.Any(o => s.Contains(o))) continue;
                string st = "minecraft:" + s;
                int id = w.Palette.IndexOf(st);
                if (id < 0) { id = w.Palette.Count; w.Palette.Add(st); }
                w.Grid[w.Index(x, y, z)] = (ushort)id;
            }
        }
        faces.Prewarm(w);
        var all = new MeshData();
        var part = new MeshData();
        var n = w.SectionCount;
        for (int sx = 0; sx < n.x; sx++) for (int sy = 0; sy < n.y; sy++) for (int sz = 0; sz < n.z; sz++)
        {
            GreedyMesher.Build(w, new Vector3Int(sx, sy, sz), faces, part);
            if (args.Contains("--specials-only")) part.Clear();   // debugging: only one of the two paths
            if (!args.Contains("--greedy-only")) GreedyMesher.BuildSpecials(w, new Vector3Int(sx, sy, sz), faces, part);
            int b = all.Verts.Count;
            all.Verts.AddRange(part.Verts); all.Uvs.AddRange(part.Uvs); all.Colors.AddRange(part.Colors);
            all.Opaque.AddRange(part.Opaque.Select(i => i + b)); all.Transparent.AddRange(part.Transparent.Select(i => i + b));
        }
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("{\"dims\":[").Append(w.Dims.x).Append(',').Append(w.Dims.y).Append(',').Append(w.Dims.z).Append("],\"layers\":[");
        sb.Append(string.Join(",", assets.Names.Select(x => "\"" + x + "\""))).Append("],\"verts\":[");
        sb.Append(string.Join(",", all.Verts.Select(v => string.Format(inv, "{0:G6},{1:G6},{2:G6}", v.x, v.y, v.z)))).Append("],\"uvs\":[");
        sb.Append(string.Join(",", all.Uvs.Select(v => string.Format(inv, "{0:G6},{1:G6},{2}", v.x, v.y, (int)v.z)))).Append("],\"colors\":[");
        sb.Append(string.Join(",", all.Colors.Select(c => $"{c.r},{c.g},{c.b},{c.a}"))).Append("],\"opaque\":[");
        sb.Append(string.Join(",", all.Opaque)).Append("],\"transparent\":[").Append(string.Join(",", all.Transparent)).Append("]}");
        File.WriteAllText(outPath, sb.ToString());
        Console.WriteLine($"scene: {w.Dims} palette {w.Palette.Count}, {all.Verts.Count / 4} faces -> {outPath}");
        BlockRules.ResetShapes();
    }
}
