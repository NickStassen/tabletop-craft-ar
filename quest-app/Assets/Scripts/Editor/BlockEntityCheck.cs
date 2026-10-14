// [blocks] Drives the real BlockEntityRenderer in batch mode over a small world with every kind of block entity: the
// scan, block_entities data, instanced groups, sign text, end portals, beacon beams, spawner mobs, a glide (re-key) and
// an edit that removes a chest. Needs the user's client jar (or pack zip) and entity_models.json, given by path:
//   TABLETOP_PACK=~/.minecraft/versions/26.3/26.3.jar TABLETOP_ENTITY_MODELS=entity_models.json \
//   Unity -batchmode -nographics -quit -projectPath quest-app -buildTarget Android \
//     -executeMethod TabletopCraft.EditorTools.BlockEntityCheck.Run -logFile be.log    (grep BLOCKENTITYCHECK)
// With graphics (no -nographics) and TABLETOP_SHOT=out.png it also renders the scene to a PNG.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEditor;
using UnityEngine;

namespace TabletopCraft.EditorTools
{
    public static class BlockEntityCheck
    {
        static readonly (Vector3Int pos, string state)[] Placed =
        {
            (new Vector3Int(1, 1, 1), "minecraft:chest[facing=south,type=single,waterlogged=false]"),
            (new Vector3Int(3, 1, 1), "minecraft:chest[facing=south,type=left,waterlogged=false]"),
            (new Vector3Int(4, 1, 1), "minecraft:chest[facing=south,type=right,waterlogged=false]"),
            (new Vector3Int(6, 1, 1), "minecraft:ender_chest[facing=south,waterlogged=false]"),
            (new Vector3Int(8, 1, 1), "minecraft:oxidized_copper_chest[facing=south,type=single,waterlogged=false]"),
            (new Vector3Int(10, 1, 1), "minecraft:magenta_shulker_box[facing=up]"),
            (new Vector3Int(12, 1, 1), "minecraft:shulker_box[facing=east]"),
            (new Vector3Int(1, 1, 3), "minecraft:skeleton_skull[powered=false,rotation=2]"),
            (new Vector3Int(2, 1, 3), "minecraft:wither_skeleton_skull[powered=false,rotation=0]"),
            (new Vector3Int(3, 1, 3), "minecraft:zombie_head[powered=false,rotation=0]"),
            (new Vector3Int(4, 1, 3), "minecraft:creeper_head[powered=false,rotation=0]"),
            (new Vector3Int(5, 1, 3), "minecraft:dragon_head[powered=false,rotation=0]"),
            (new Vector3Int(7, 1, 3), "minecraft:piglin_head[powered=false,rotation=0]"),
            (new Vector3Int(8, 1, 3), "minecraft:player_head[powered=false,rotation=0]"),
            (new Vector3Int(9, 2, 3), "minecraft:player_wall_head[facing=south,powered=false]"),
            (new Vector3Int(1, 1, 5), "minecraft:white_banner[rotation=0]"),
            (new Vector3Int(3, 2, 5), "minecraft:blue_wall_banner[facing=south]"),
            (new Vector3Int(5, 1, 5), "minecraft:bell[attachment=floor,facing=south,powered=false]"),
            (new Vector3Int(7, 1, 5), "minecraft:decorated_pot[cracked=false,facing=south,waterlogged=false]"),
            (new Vector3Int(9, 1, 5), "minecraft:conduit[waterlogged=false]"),
            (new Vector3Int(11, 1, 5), "minecraft:conduit[waterlogged=true]"),
            (new Vector3Int(13, 1, 5), "minecraft:copper_golem_statue[copper_golem_pose=star,facing=south,waterlogged=false]"),
            (new Vector3Int(1, 1, 7), "minecraft:lectern[facing=south,has_book=true,powered=false]"),
            (new Vector3Int(3, 1, 7), "minecraft:enchanting_table"),
            (new Vector3Int(5, 1, 7), "minecraft:end_portal"),
            (new Vector3Int(6, 1, 7), "minecraft:end_portal"),
            (new Vector3Int(8, 1, 7), "minecraft:end_gateway"),
            (new Vector3Int(1, 1, 9), "minecraft:oak_sign[rotation=0,waterlogged=false]"),
            (new Vector3Int(3, 2, 9), "minecraft:spruce_wall_sign[facing=south,waterlogged=false]"),
            (new Vector3Int(5, 2, 9), "minecraft:cherry_hanging_sign[attached=false,rotation=0,waterlogged=false]"),
            (new Vector3Int(7, 1, 9), "minecraft:beacon"),
            (new Vector3Int(7, 3, 9), "minecraft:red_stained_glass"),
            (new Vector3Int(9, 1, 9), "minecraft:spawner"),
            (new Vector3Int(11, 1, 9), "minecraft:campfire[facing=south,lit=true,signal_fire=false,waterlogged=false]"),
            (new Vector3Int(13, 1, 9), "minecraft:oak_shelf[facing=south,powered=false,side_chain=unconnected,waterlogged=false]"),
            (new Vector3Int(1, 1, 11), "minecraft:vault[facing=south,ominous=false,vault_state=active]"),
            (new Vector3Int(3, 1, 11), "minecraft:stone"),
        };

        static string Data(Vector3Int o) => "{\"type\":\"block_entities\",\"entities\":[" + string.Join(",", new[]
        {
            E(o, 1, 1, 9, "\"kind\":\"sign\",\"front\":{\"lines\":[\"Welcome\",\"to the\",\"table\",\"\"],\"color\":\"black\",\"glowing\":false},\"back\":{\"lines\":[\"\",\"\",\"\",\"\"],\"color\":\"black\",\"glowing\":false}"),
            E(o, 3, 2, 9, "\"kind\":\"sign\",\"front\":{\"lines\":[\"\",\"SHOP\",\"\",\"\"],\"color\":\"lime\",\"glowing\":true},\"back\":{\"lines\":[\"\",\"\",\"\",\"\"],\"color\":\"black\",\"glowing\":false}"),
            E(o, 5, 2, 9, "\"kind\":\"sign\",\"front\":{\"lines\":[\"Inn\",\"\",\"\",\"\"],\"color\":\"black\",\"glowing\":true},\"back\":{\"lines\":[\"\",\"\",\"\",\"\"],\"color\":\"black\",\"glowing\":false}"),
            E(o, 1, 1, 5, "\"kind\":\"banner\",\"patterns\":[{\"pattern\":\"minecraft:stripe_bottom\",\"color\":\"red\"},{\"pattern\":\"minecraft:creeper\",\"color\":\"black\"}]"),
            E(o, 7, 1, 5, "\"kind\":\"decorated_pot\",\"sherds\":[\"minecraft:brick\",\"minecraft:arms_up_pottery_sherd\",\"minecraft:brick\",\"minecraft:skull_pottery_sherd\"]"),
            E(o, 11, 1, 5, "\"kind\":\"conduit\",\"active\":true"),
            E(o, 7, 1, 9, "\"kind\":\"beacon\",\"tier\":4"),
            E(o, 9, 1, 9, "\"kind\":\"spawner\",\"entity\":\"minecraft:zombie\",\"size\":[0.6,1.95]"),
            E(o, 11, 1, 9, "\"kind\":\"campfire\",\"items\":[\"minecraft:beef\",null,\"minecraft:cod\",null]"),
            E(o, 13, 1, 9, "\"kind\":\"shelf\",\"items\":[\"minecraft:apple\",\"minecraft:diamond_sword\",\"minecraft:oak_log\"]"),
            E(o, 1, 1, 11, "\"kind\":\"vault\",\"item\":\"minecraft:diamond\""),
        }) + "],\"removed\":[]}";

        static string E(Vector3Int o, int x, int y, int z, string body) => $"{{\"pos\":[{o.x + x},{o.y + y},{o.z + z}],{body}}}";

        static WorldModel World(Vector3Int origin, int id)
        {
            var dims = new Vector3Int(32, 16, 32);
            var w = new WorldModel { SnapshotId = id, World = "world", Origin = origin, Dims = dims };
            var pal = new List<string> { "minecraft:air", "minecraft:grass_block[snowy=false]" };
            w.Grid = new ushort[dims.x * dims.y * dims.z];
            for (int z = 0; z < dims.z; z++) for (int x = 0; x < dims.x; x++) w.Grid[w.Index(x, 64 - origin.y, z)] = 1;   // the ground under them
            foreach (var (p, s) in Placed)
            {
                // Placed relative to world (16, 64, 16) so a glide by one section keeps them in the region.
                var l = new Vector3Int(16, 64, 16) + p - origin;
                if (!w.InBounds(l.x, l.y, l.z)) continue;
                int i = pal.IndexOf(s); if (i < 0) { i = pal.Count; pal.Add(s); }
                w.Grid[w.Index(l.x, l.y, l.z)] = (ushort)i;
            }
            w.Palette.AddRange(pal);
            return w;
        }

        static Net.ChunkSnapshot Snap(WorldModel w) => new Net.ChunkSnapshot { SnapshotId = w.SnapshotId, World = w.World, Origin = w.Origin, Dims = w.Dims, Palette = w.Palette.ToArray(), Grid = w.Grid };

        public static void Run()
        {
            int errors = 0;
            void Check(bool ok, string what) { if (!ok) errors++; Debug.Log($"BLOCKENTITYCHECK {(ok ? "PASS" : "FAIL")} {what}"); }
            string pack = Environment.GetEnvironmentVariable("TABLETOP_PACK"), models = Environment.GetEnvironmentVariable("TABLETOP_ENTITY_MODELS");
            if (string.IsNullOrEmpty(pack) || string.IsNullOrEmpty(models)) { Debug.Log("BLOCKENTITYCHECK SKIP: set TABLETOP_PACK and TABLETOP_ENTITY_MODELS"); Exit(0); return; }
            var atlas = new ResourcePackAtlas();
            Check(atlas.TryLoad(pack), "pack loads: " + atlas.Status);
            Check(EntityModels.LoadFile(models), "entity models load: " + EntityModels.Status);
            var tex = new EntityTextures { Atlas = atlas };
            var blocks = new GameObject("Blocks").transform;
            var be = new GameObject("BlockEntities").AddComponent<BlockEntityRenderer>();
            be.transform.SetParent(blocks, false);
            be.Model = new WorldModel(); be.Atlas = atlas; be.Textures = tex;
            typeof(BlockEntityRenderer).GetMethod("Awake", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.Invoke(be, null);
            var update = typeof(BlockEntityRenderer).GetMethod("Update", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            void Pump(int frames) { for (int i = 0; i < frames; i++) update.Invoke(be, null); }

            var origin = new Vector3Int(0, 60, 0);
            var w = World(origin, 1);
            be.Model.ApplySnapshot(Snap(w));
            be.OnSnapshot(null, origin, origin);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Pump(3);
            be.Apply(Json.Parse(Data(new Vector3Int(16, 64, 16))) as Dictionary<string, object>);
            Pump(3);
            long ms = sw.ElapsedMilliseconds;
            int expected = Placed.Length - 2;   // glass and stone are no block entities
            Check(be.Count == expected, $"scan finds every block entity ({be.Count}/{expected}) in {ms} ms; {be.Stats()}");
            Check(be.Instances == 39 && be.DrawCalls >= 30, $"models, items and books as instances; one draw call per (mesh, texture) ({be.Stats()})");
            Check(be.TextQuads > 30, $"sign text quads ({be.TextQuads})");
            Check(be.PortalFaces == 4 + 5, $"end portal tops and bottoms, gateway sides except the one on the ground ({be.PortalFaces})");
            Check(be.BeamQuads > 0, $"beacon beam ({be.BeamQuads} quads)");
            Check(be.transform.Find("spawner mob minecraft:zombie") != null, "spawner mob figure");
            var chest = new Vector3Int(17, 65, 17);
            Check(be.TryInstanceMatrix(chest, out var m0), "chest instance");
            // A glide one section east (+16 on x): instances keep their world place, only the matrices move.
            var shifted = World(origin + new Vector3Int(16, 0, 0), 2);
            var plan = SectionPlan.Compute(be.Model, Snap(shifted), new HashSet<Vector3Int>(), null);
            var old = be.Model.Origin;
            be.Model.ApplySnapshot(Snap(shifted));
            be.OnSnapshot(plan, old, shifted.Origin);
            Pump(3);
            var m1 = Matrix4x4.identity;
            bool kept = be.TryInstanceMatrix(chest, out m1);
            Check(plan.Rekey && kept && Mathf.Abs(m1.m03 - (m0.m03 - 16)) < 1e-4f && Mathf.Abs(m1.m23 - m0.m23) < 1e-4f,
                  $"re-key: the chest stays in the world, 16 blocks further west in the region ({m0.m03} -> {m1.m03})");
            int before = be.Count;
            // An edit: the chest cell turns to air (WorldModel.SetLocal raises CellChanged, as block_update does).
            var l = chest - be.Model.Origin;
            be.Model.SetLocal(l.x, l.y, l.z, 0);
            Pump(2);
            Check(be.Count == before - 1 && !be.TryInstanceMatrix(chest, out _), $"a broken chest disappears ({before} -> {be.Count})");
            string shot = Environment.GetEnvironmentVariable("TABLETOP_SHOT");
            if (!string.IsNullOrEmpty(shot) && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null) Shot(be, blocks, shot);
            Debug.Log($"BLOCKENTITYCHECK done: {errors} errors");
            Exit(errors == 0 ? 0 : 1);
        }

        static void Shot(BlockEntityRenderer be, Transform blocks, string path)
        {
            var camGo = new GameObject("shot camera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.5f, 0.6f, 0.75f);
            var o = be.Model.Origin;
            var centre = new Vector3(16 + 7 - o.x, 64 + 1 - o.y, -(16 + 6 - o.z));
            camGo.transform.position = centre + new Vector3(0, 9, -13);
            camGo.transform.LookAt(centre);
            var probe = GameObject.CreatePrimitive(PrimitiveType.Cube); probe.transform.position = centre + new Vector3(-8, 0, 0);
            probe.GetComponent<MeshRenderer>().sharedMaterial = new Material(Shader.Find("Unlit/Color")) { color = Color.red };
            cam.fieldOfView = 50; cam.nearClipPlane = 0.05f; cam.farClipPlane = 200;
            var rt = new RenderTexture(1600, 1000, 24);
            cam.targetTexture = rt;
            // Update computes the frame; outside the player loop the draws reach the camera through a command buffer.
            typeof(BlockEntityRenderer).GetMethod("Update", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(be, null);
            var cb = new UnityEngine.Rendering.CommandBuffer { name = "block entities" };
            be.Fill(cb);
            cam.AddCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterForwardOpaque, cb);
            ShaderUtil.allowAsyncCompilation = false;   // the editor otherwise skips draws whose shader is still compiling
            cam.Render(); cam.Render();
            RenderTexture.active = rt;
            var t = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            t.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            t.Apply();
            System.IO.File.WriteAllBytes(path, t.EncodeToPNG());
            RenderTexture.active = null;
            Debug.Log("BLOCKENTITYCHECK shot " + path);
        }

        static void Exit(int code) { if (Application.isBatchMode) EditorApplication.Exit(code); }
    }
}
#endif
