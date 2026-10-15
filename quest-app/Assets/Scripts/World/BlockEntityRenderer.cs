// [blocks] Draws the region's block entities like vanilla 26.3's BlockEntityRenderers (ARCHITECTURE.md "Block
// entities"): chests, shulker boxes, heads, banners with their patterns, bells, decorated pots with sherds, conduits,
// copper golem statues, lectern and enchanting table books, end portals and gateways, sign text, beacon beams, the
// spinning mob in spawners, and the items on campfires, shelves and vaults.
//
// Where they are comes from the grid: a budgeted main-thread scan of the sections a snapshot brought (all of them, or
// only the new and changed ones after a glide or resize) plus WorldModel.CellChanged for block_update and local edits.
// What the block state doesn't say comes from block_entities (BlockEntityStore, PROTOCOL.md 4.29).
//
// Static models are GPU instances: each (mesh, material) pair is one group drawn with Graphics.RenderMeshInstanced,
// so a village of chests is one draw call per chest texture. Instances are keyed by world position; their matrices
// are region-local (Z mirrored like the terrain) under this transform (a child of Blocks), so a glide or a resize only
// recomputes matrices. Each instance takes the lightmap colour of its own cell, as vanilla lights block entities.
// Sign text, end portal faces and beacon beams are a few dynamic meshes; spawner mobs are EntityFigures.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using TabletopCraft.Net;
using UnityEngine;
using UnityEngine.Rendering;

namespace TabletopCraft.World
{
    public sealed class BlockEntityRenderer : MonoBehaviour
    {
        public WorldModel Model;
        public EntityTextures Textures;
        public ResourcePackAtlas Atlas;
        public TabletopRenderer Terrain;     // its Staging flag: an old region is still on screen
        public float ScanBudgetMs = 0.5f;    // main-thread time per frame for scanning new sections
        public readonly BlockEntityStore Store = new BlockEntityStore();
        public const int Chunk = 256;        // instances per draw call (stereo instancing doubles them)

        // ---------- instances and groups ----------

        enum Anim : byte { None, VaultItem, EnchantBook, ConduitCage, ConduitEye }

        sealed class Inst
        {
            public Vector3Int World;
            public string State;
            public BlockEntityKind Kind;
            public readonly List<Member> Members = new List<Member>();
            public Color Light = Color.white;
            public bool Confirmed = true;
            public EntityFigure Mob;
            public string MobType;
            public float MobW, MobH, Spin, Age;
            public List<BeamSection> Beam;
            public bool BeamDirty = true;
        }

        sealed class Member
        {
            public Inst Inst;
            public Group Group;
            public M4 Part;                 // block space (vanilla transform x S(-1/16), or an item transform)
            public Color Base = Color.white;
            public bool FullBright;
            public Anim Anim;
        }

        sealed class Group
        {
            public Mesh Mesh;
            public Material Mat;
            public readonly List<Member> Members = new List<Member>();
            public bool Dirty = true, Animated;
            public Matrix4x4[] Local = new Matrix4x4[0];
            public Matrix4x4[][] World = new Matrix4x4[0][];   // per draw call (Chunk instances each)
            public Vector4[][] Colors = new Vector4[0][];
            public MaterialPropertyBlock[] Props = new MaterialPropertyBlock[0];
        }

        readonly Dictionary<Vector3Int, Inst> _insts = new Dictionary<Vector3Int, Inst>();
        readonly Dictionary<(Mesh, Material), Group> _groups = new Dictionary<(Mesh, Material), Group>();
        readonly Dictionary<string, Mesh> _meshes = new Dictionary<string, Mesh>();
        readonly Dictionary<string, Texture2D> _tex = new Dictionary<string, Texture2D>();
        readonly List<BEPart> _parts = new List<BEPart>();
        readonly List<Inst> _tmpInsts = new List<Inst>();
        readonly List<Vector3Int> _cells = new List<Vector3Int>();
        readonly List<Vector3Int> _changed = new List<Vector3Int>();
        readonly HashSet<Vector3Int> _dirtyCells = new HashSet<Vector3Int>();   // region-local
        readonly Queue<Vector3Int> _scan = new Queue<Vector3Int>();               // region-local sections still to scan
        readonly HashSet<Vector3Int> _scanSet = new HashSet<Vector3Int>();
        bool[] _mask = new bool[0];
        int _maskCount = -1, _modelsGen = -1;
        bool _fullScan, _textDirty, _portalDirty;
        Matrix4x4 _lastL2W;
        readonly Stopwatch _sw = new Stopwatch();
        PlayerSkins _skins;
        BitmapFont _font;
        bool _fontTried;
        Texture2D _fontTex;
        Material _textMat, _beamCoreMat, _beamGlowMat, _portalMat, _gatewayMat;
        Mesh _textMesh, _portalMesh, _gatewayMesh, _beamMesh;
        float _ticks;   // game ticks since start (animations)

        // Stats (status line, AREA_LOADED-style logs, measurements).
        public int Count => _insts.Count;
        public int DrawCalls { get; private set; }
        public int Instances { get; private set; }
        public long Triangles { get; private set; }
        public int Groups => _groups.Count;
        public bool Scanning => _scan.Count > 0;
        public int TextQuads => _textMesh != null ? _textMesh.vertexCount / 4 : 0;
        public int PortalFaces => (_portalMesh != null ? _portalMesh.vertexCount / 4 : 0) + (_gatewayMesh != null ? _gatewayMesh.vertexCount / 4 : 0);
        public int BeamQuads => _bv.Count / 4;

        /// <summary>The region-local matrix of the first model at a world position (Editor checks).</summary>
        public bool TryInstanceMatrix(Vector3Int world, out Matrix4x4 local)
        {
            local = Matrix4x4.identity;
            if (!_insts.TryGetValue(world, out var i) || i.Members.Count == 0) return false;
            local = LocalOf(i.Members[0]);
            return true;
        }

        void Awake() { _skins = new PlayerSkins(); }

        void OnDestroy()
        {
            Clear();
            _skins?.Dispose();
            foreach (var t in _tex.Values) if (t != null) Destroy(t);
            foreach (var m in _meshes.Values) if (m != null) Destroy(m);
            foreach (var m in new Mesh[] { _textMesh, _portalMesh, _gatewayMesh, _beamMesh }) if (m != null) Destroy(m);
        }

        public void Clear()
        {
            foreach (var i in _insts.Values) i.Mob?.Destroy();
            _insts.Clear();
            _groups.Clear();
            _beacons.Clear(); _gateways = 0;
            _scan.Clear(); _scanSet.Clear();
            _textDirty = _portalDirty = true;
        }

        bool Ready => Model != null && Model.Loaded && Atlas != null && Atlas.HasPack;

        // ---------- inputs ----------

        /// <summary>A block_entities message (PROTOCOL.md 4.29).</summary>
        public void Apply(Dictionary<string, object> m)
        {
            _changed.Clear();
            Store.Apply(m, _changed);
            if (Model == null || !Model.Loaded) return;
            foreach (var w in _changed)
            {
                var l = w - Model.Origin;
                if (Model.InBounds(l.x, l.y, l.z)) _dirtyCells.Add(l);
                if (_insts.TryGetValue(w, out var inst)) Rebuild(inst);
            }
        }

        /// <summary>Call right after TabletopRenderer.OnSnapshot: keeps instances where they are in the world, drops the
        /// ones outside the new region, and scans the sections the snapshot brought (all of them without a re-key).</summary>
        public void OnSnapshot(SectionPlan plan, Vector3Int oldOrigin, Vector3Int newOrigin)
        {
            if (Model == null) return;
            Model.CellChanged = OnCellChanged;
            Store.Prune(Model.Origin, Model.Dims);
            _tmpInsts.Clear();
            foreach (var i in _insts.Values) { var l = i.World - Model.Origin; if (!Model.InBounds(l.x, l.y, l.z)) _tmpInsts.Add(i); }
            foreach (var i in _tmpInsts) Remove(i);
            foreach (var g in _groups.Values) g.Dirty = true;   // matrices are region-local
            _textDirty = _portalDirty = true;
            foreach (var i in _insts.Values) i.BeamDirty = true;
            _dirtyCells.Clear();
            bool rekey = plan != null && plan.Rekey;
            var n = Model.SectionCount;
            if (!rekey)
            {
                _scan.Clear(); _scanSet.Clear();
                foreach (var i in _insts.Values) i.Confirmed = false;
                for (int y = 0; y < n.y; y++) for (int z = 0; z < n.z; z++) for (int x = 0; x < n.x; x++) Queue(new Vector3Int(x, y, z));
                _fullScan = true;
            }
            else
            {
                // Pending sections from before keep their place in the new grid (shifted by whole sections).
                var pending = new List<Vector3Int>(_scan);
                _scan.Clear(); _scanSet.Clear();
                var sh = plan.ShiftSections;
                foreach (var s in pending) { var k = new Vector3Int(s.x - sh.x, s.y - sh.y, s.z - sh.z); if (k.x >= 0 && k.y >= 0 && k.z >= 0 && k.x < n.x && k.y < n.y && k.z < n.z) Queue(k); }
                foreach (var s in plan.Mesh) Queue(s);
                const int S = WorldModel.Section;
                foreach (var i in _insts.Values)
                {
                    var l = i.World - Model.Origin;
                    if (_scanSet.Contains(new Vector3Int(l.x / S, l.y / S, l.z / S))) i.Confirmed = false;
                }
            }
            _mask = BlockEntityScan.Mask(Model.Palette);
            _maskCount = Model.Palette.Count;
        }

        void Queue(Vector3Int s) { if (_scanSet.Add(s)) _scan.Enqueue(s); }

        void OnCellChanged(int x, int y, int z) => _dirtyCells.Add(new Vector3Int(x, y, z));

        // ---------- the scan ----------

        void UpdateMask()
        {
            if (_maskCount == Model.Palette.Count) return;
            _mask = BlockEntityScan.Mask(Model.Palette);
            _maskCount = Model.Palette.Count;
        }

        void ScanStep()
        {
            if (_scan.Count == 0) return;
            UpdateMask();
            _sw.Restart();
            var one = new Vector3Int[1];
            while (_scan.Count > 0 && _sw.Elapsed.TotalMilliseconds < ScanBudgetMs)
            {
                one[0] = _scan.Dequeue();
                _scanSet.Remove(one[0]);
                _cells.Clear();
                BlockEntityScan.Cells(Model.Grid, Model.Dims, _mask, one, _cells);
                foreach (var c in _cells) Ensure(c);
            }
            if (_scan.Count == 0)
            {
                _tmpInsts.Clear();
                foreach (var i in _insts.Values) if (!i.Confirmed) _tmpInsts.Add(i);
                foreach (var i in _tmpInsts) Remove(i);
                _fullScan = false;
            }
        }

        /// <summary>A cell whose block or light changed: create, rebuild, relight or remove its instance.</summary>
        void Recheck(Vector3Int l)
        {
            const int S = WorldModel.Section;
            if (!Model.InBounds(l.x, l.y, l.z)) return;
            var w = l + Model.Origin;
            // Beams pass through the cells above a beacon; an end gateway's faces depend on its neighbours.
            foreach (var b in _beacons) if (b.World.x == w.x && b.World.z == w.z && b.World.y < w.y) b.BeamDirty = true;
            if (_gateways > 0) _portalDirty = true;
            if (_scanSet.Contains(new Vector3Int(l.x / S, l.y / S, l.z / S)) && !_insts.ContainsKey(w)) return;   // the scan will find it
            UpdateMask();
            ushort id = Model.Grid[Model.Index(l.x, l.y, l.z)];
            if (id < _mask.Length && _mask[id]) Ensure(l);
            else if (_insts.TryGetValue(w, out var gone)) Remove(gone);
        }

        void Ensure(Vector3Int l)
        {
            var w = l + Model.Origin;
            string state = Model.State(Model.Grid[Model.Index(l.x, l.y, l.z)]);
            if (_insts.TryGetValue(w, out var inst))
            {
                inst.Confirmed = true;
                var light = LightAt(l);
                if (inst.State == state)
                {
                    if (light != inst.Light) { inst.Light = light; foreach (var m in inst.Members) m.Group.Dirty = true; if (IsSign(inst.Kind)) _textDirty = true; }
                    return;
                }
                Forget(inst);
                inst.State = state; inst.Light = light;
                inst.Kind = BlockEntityCatalog.KindOfState(state);
                Track(inst);
                Rebuild(inst);
                return;
            }
            inst = new Inst { World = w, State = state, Kind = BlockEntityCatalog.KindOfState(state), Light = LightAt(l) };
            _insts[w] = inst;
            Track(inst);
            Rebuild(inst);
        }

        void Remove(Inst inst)
        {
            ClearMembers(inst);
            inst.Mob?.Destroy(); inst.Mob = null; inst.MobType = null;
            _insts.Remove(inst.World);
            Forget(inst);
            if (IsSign(inst.Kind)) _textDirty = true;
            if (inst.Kind == BlockEntityKind.EndPortal || inst.Kind == BlockEntityKind.EndGateway) _portalDirty = true;
        }

        readonly HashSet<Inst> _beacons = new HashSet<Inst>();
        int _gateways;

        void Track(Inst i)
        {
            if (i.Kind == BlockEntityKind.Beacon) _beacons.Add(i);
            if (i.Kind == BlockEntityKind.EndGateway) _gateways++;
        }

        void Forget(Inst i)
        {
            if (i.Kind == BlockEntityKind.Beacon) _beacons.Remove(i);
            if (i.Kind == BlockEntityKind.EndGateway) _gateways--;
        }

        static bool IsSign(BlockEntityKind k) => k == BlockEntityKind.Sign || k == BlockEntityKind.HangingSign;

        Color LightAt(Vector3Int l)
        {
            if (Model.Light == null || !Model.InBounds(l.x, l.y, l.z)) return Color.white;
            byte v = Model.Light[Model.Index(l.x, l.y, l.z)];
            var c = BlockShading.Lightmap(v >> 4, v & 15);
            return new Color(c.x, c.y, c.z, 1);
        }

        // ---------- members ----------

        void ClearMembers(Inst inst)
        {
            foreach (var m in inst.Members) { m.Group.Members.Remove(m); m.Group.Dirty = true; }
            inst.Members.Clear();
        }

        void AddMember(Inst inst, Mesh mesh, Material mat, M4 part, Anim anim = Anim.None, bool fullBright = false, Color? baseColor = null)
        {
            if (mesh == null || mat == null) return;
            if (!_groups.TryGetValue((mesh, mat), out var g)) { g = new Group { Mesh = mesh, Mat = mat }; _groups[(mesh, mat)] = g; }
            var m = new Member { Inst = inst, Group = g, Part = part, Anim = anim, FullBright = fullBright, Base = baseColor ?? Color.white };
            g.Members.Add(m);
            if (anim != Anim.None) g.Animated = true;
            g.Dirty = true;
            inst.Members.Add(m);
        }

        /// <summary>(Re)creates an instance's models, items and extras from its state and server data.</summary>
        void Rebuild(Inst inst)
        {
            ClearMembers(inst);
            if (inst.Mob != null) { inst.Mob.Destroy(); inst.Mob = null; inst.MobType = null; }
            var data = Store.Get(inst.World);
            BlockEntityCatalog.Parts(inst.State, data, _parts);
            bool fallback = false;
            foreach (var p in _parts)
            {
                var em = EntityModels.Get(p.Model);
                var mesh = em != null ? MeshFor(p, em) : null;
                if (mesh == null) { fallback = true; continue; }
                var tex = TextureFor(p.Texture, em.TexW, em.TexH);
                AddMember(inst, mesh, Textures.Material(tex, 0), BlockEntityModels.ToBlock(p.M));
            }
            if (fallback) AddFallback(inst);
            var p0 = BlockModelBaker.Props(inst.State);
            p0.TryGetValue("facing", out var facing);
            facing = facing ?? "north";
            switch (inst.Kind)
            {
                case BlockEntityKind.Campfire:
                    if (data?.Items != null)
                        for (int i = 0; i < data.Items.Length; i++) AddItem(inst, data.Items[i], BlockEntityCatalog.CampfireItemTransform(facing, i), 0);
                    break;
                case BlockEntityKind.Shelf:
                    if (data?.Items != null)
                        for (int i = 0; i < data.Items.Length; i++) AddItem(inst, data.Items[i], BlockEntityCatalog.ShelfItemTransform(facing, i), 1);
                    break;
                case BlockEntityKind.Vault:
                    if (data?.Item != null) AddItem(inst, data.Item, M4.Identity, 2, Anim.VaultItem);
                    break;
                case BlockEntityKind.EnchantingTable:
                {
                    var em = EntityModels.Get("book");
                    var part = new BEPart { Model = "book", Pose = "book_closed", Texture = "entity/enchantment/enchanting_table_book" };
                    var mesh = em != null ? MeshFor(part, em) : null;
                    if (mesh != null) AddMember(inst, mesh, Textures.Material(TextureFor(part.Texture, em.TexW, em.TexH), 0), M4.Identity, Anim.EnchantBook);
                    break;
                }
                case BlockEntityKind.Conduit:
                    if (data != null && data.Active) AddActiveConduit(inst);
                    break;
                case BlockEntityKind.Spawner:
                case BlockEntityKind.TrialSpawner:
                    SetMob(inst, data);
                    break;
                case BlockEntityKind.Beacon:
                    inst.BeamDirty = true;
                    break;
                case BlockEntityKind.Sign:
                case BlockEntityKind.HangingSign:
                    _textDirty = true;
                    break;
                case BlockEntityKind.EndPortal:
                case BlockEntityKind.EndGateway:
                    _portalDirty = true;
                    break;
            }
        }

        Mesh MeshFor(BEPart p, EModel em)
        {
            string key = p.MeshKey;
            if (_meshes.TryGetValue(key, out var mesh)) return mesh;
            var fm = BlockEntityModels.Bake(em, BlockEntityCatalog.Pose(p.Pose), p.Only);
            mesh = fm != null ? EntityMeshes.Upload(fm, "block entity " + key) : null;
            _meshes[key] = mesh;
            return mesh;
        }

        /// <summary>Without entity_models.json: a box of the old placeholder size in the texture's average colour.</summary>
        void AddFallback(Inst inst)
        {
            _parts.Clear();
            BlockEntityCatalog.Parts(inst.State, Store.Get(inst.World), _parts);
            string tex = _parts.Count > 0 ? _parts[_parts.Count - 1].Texture : null;
            Color c = Color.white;
            if (tex != null && !tex.StartsWith(BlockEntityCatalog.SkinPrefix) && !tex.StartsWith(BlockEntityCatalog.BannerPrefix)) { var a = Textures.Average(tex); if (a.HasValue) c = a.Value; }
            else if (tex != null && tex.StartsWith(BlockEntityCatalog.BannerPrefix)) c = Dye(BlockEntityCatalog.DyePrefix(BlockRules.Path(inst.State)) ?? "white");
            else c = new Color32(190, 140, 110, 255);
            Vector3 min, max;
            switch (inst.Kind)
            {
                case BlockEntityKind.Chest: min = new Vector3(1, 0, 1); max = new Vector3(15, 14, 15); break;
                case BlockEntityKind.Skull: min = new Vector3(4, 0, 4); max = new Vector3(12, 8, 12); break;
                case BlockEntityKind.Banner: min = new Vector3(2, 0, 7); max = new Vector3(14, 28, 9); break;
                case BlockEntityKind.Bell: min = new Vector3(5, 4, 5); max = new Vector3(11, 13, 11); break;
                case BlockEntityKind.Conduit: min = new Vector3(5, 5, 5); max = new Vector3(11, 11, 11); break;
                case BlockEntityKind.CopperGolemStatue: min = new Vector3(3, 0, 4); max = new Vector3(13, 15, 12); break;
                case BlockEntityKind.Lectern: return;
                default: min = new Vector3(1, 0, 1); max = new Vector3(15, 16, 15); break;
            }
            var centre = (min + max) * (0.5f / 16); var size = (max - min) * (1f / 16);
            // The unit mesh is a 16 px cube centred in figure space: block = centre + size * (-f / 16).
            var part = M4.T(centre.x, centre.y, centre.z) * M4.S(size.x, size.y, size.z) * M4.S(-1f / 16);
            AddMember(inst, EntityMeshes.Unit, Textures.Material(Textures.White, 0), part, Anim.None, false, c);
        }

        /// <summary>An item on a campfire (0, "fixed" display), a shelf (1, "on_shelf") or in a vault (2, "ground").</summary>
        void AddItem(Inst inst, string item, M4 slot, int display, Anim anim = Anim.None)
        {
            if (string.IsNullOrEmpty(item)) return;
            var v = Textures.Item(item);
            Mesh mesh; Texture2D t; int flags;
            if (v.Kind == ItemVisualKind.Sprite) { mesh = EntityMeshes.Sprite; t = Textures.Sprite(v); flags = 2; }
            else if (v.Kind == ItemVisualKind.Cube) { mesh = EntityMeshes.ItemCube; t = Textures.Strip(v); flags = 0; }
            else return;
            if (t == null) return;
            var d = display == 0 ? v.Fixed : display == 1 ? v.OnShelf : v.Ground;
            AddMember(inst, mesh, Textures.Material(t, flags), slot * BlockEntityCatalog.ItemDisplayTransform(d), anim);
        }

        void AddActiveConduit(Inst inst)
        {
            foreach (var (layer, texture, anim, part, flags) in new[]
            {
                ("conduit#cage", "entity/conduit/cage", Anim.ConduitCage, M4.Identity, 0),
                ("conduit#wind", "entity/conduit/wind", Anim.None, M4.T(0.5f, 0.5f, 0.5f), 1),
                ("conduit#wind", "entity/conduit/wind", Anim.None, M4.T(0.5f, 0.5f, 0.5f) * M4.S(0.875f) * M4.RotX(BlockEntityCatalog.Pi) * M4.RotZ(BlockEntityCatalog.Pi), 1),
                ("conduit#eye", "entity/conduit/open_eye", Anim.ConduitEye, M4.Identity, 0),
            })
            {
                var em = EntityModels.Get(layer);
                if (em == null) continue;
                var p = new BEPart { Model = layer, Texture = texture };
                AddMember(inst, MeshFor(p, em), Textures.Material(TextureFor(texture, em.TexW, em.TexH), flags), BlockEntityModels.ToBlock(part), anim, anim == Anim.ConduitEye);
            }
        }

        void SetMob(Inst inst, BlockEntityData data)
        {
            // Rebuilt with the instance (also when entity_models.json arrives), so a figure never outlives its models.
            if (inst.Mob != null) { inst.Mob.Destroy(); inst.Mob = null; inst.MobType = null; }
            if (data?.Entity == null) return;
            var info = new EntityInfo { Id = -1, Type = data.Entity, Width = data.EntityWidth > 0 ? data.EntityWidth : 0.6f, Height = data.EntityHeight > 0 ? data.EntityHeight : 1.8f };
            inst.MobW = info.Width; inst.MobH = info.Height; inst.MobType = data.Entity;
            inst.Mob = new EntityFigure(transform, info, EntityCatalog.Resolve(info), Textures);
            inst.Mob.Root.name = "spawner mob " + data.Entity;
            if (inst.Mob.Label != null) inst.Mob.Label.gameObject.SetActive(false);
        }

        // ---------- textures ----------

        /// <summary>A texture key (BlockEntityCatalog) as a Texture2D: pack texture, player skin or banner flag.</summary>
        Texture2D TextureFor(string key, int texW, int texH)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (_tex.TryGetValue(key, out var t)) return t;
            if (key.StartsWith(BlockEntityCatalog.SkinPrefix)) t = Skin(key);
            else if (key.StartsWith(BlockEntityCatalog.BannerPrefix)) t = Banner(key);
            else t = Pack(key, texW, texH, TextureWrapMode.Clamp);
            _tex[key] = t;
            return t;
        }

        Texture2D Skin(string key)
        {
            var parts = key.Substring(BlockEntityCatalog.SkinPrefix.Length).Split('|');
            string url = parts[0], uuid = parts.Length > 1 ? parts[1] : "";
            _skins.Atlas = Atlas;
            var t = _skins.DefaultFor(string.IsNullOrEmpty(uuid) ? null : uuid, out _);
            if (!string.IsNullOrEmpty(url))
                _skins.Fetch(url, skin =>
                {
                    _tex[key] = skin;
                    foreach (var i in _insts.Values) if (i.Kind == BlockEntityKind.Skull) Rebuild(i);
                });
            return t;
        }

        Texture2D Banner(string key)
        {
            var layers = new List<BannerLayer>();
            BannerImage.ParseKey(key, out string baseColor, layers);
            var solid = Pixels("entity/banner/banner_base", out int w, out int h);
            if (solid == null) return null;
            var list = new List<(Color32[], Color32)>();
            list.Add((Pixels("entity/banner/base", out int bw, out int bh) is Color32[] b && bw == w && bh == h ? b : null, Dye(baseColor)));
            foreach (var l in layers)
                list.Add((Pixels(BannerImage.PatternTexture(l.Pattern), out int pw, out int ph) is Color32[] p && pw == w && ph == h ? p : null, Dye(l.Color)));
            return Make(BannerImage.Compose(solid, w, h, list), w, h, key, TextureWrapMode.Clamp);
        }

        static Color32 Dye(string name)
        {
            if (EntityModels.Dye(name, out int rgb)) return new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
            var c = BlockRules.FlatColor("minecraft:" + (name ?? "white") + "_wool");
            return c;
        }

        static Color32 TextDye(string name)
        {
            if (EntityModels.TextDye(name, out int rgb)) return new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
            return name == "black" ? new Color32(0, 0, 0, 255) : Dye(name);
        }

        readonly Dictionary<string, (Color32[] px, int w, int h)> _pixels = new Dictionary<string, (Color32[], int, int)>();

        /// <summary>Bottom-up pixels of a pack texture (cached), or null.</summary>
        Color32[] Pixels(string texture, out int w, out int h)
        {
            if (!_pixels.TryGetValue(texture, out var p))
            {
                byte[] png = null;
                try { png = Atlas.ReadBytes(ItemModels.TexturePath(texture)); } catch (Exception) { }
                int pw = 0, ph = 0;
                var px = png != null ? PngDecoder.Decode(png, out pw, out ph) : null;
                p = px != null ? (px, pw, ph) : (null, 0, 0);
                _pixels[texture] = p;
            }
            w = p.w; h = p.h;
            return p.px;
        }

        /// <summary>A pack texture; an animated strip (taller than the model's texture shape) keeps its first frame.</summary>
        Texture2D Pack(string texture, int texW, int texH, TextureWrapMode wrap)
        {
            var px = Pixels(texture, out int w, out int h);
            if (px == null) return null;
            if (texW > 0 && texH > 0)
            {
                int frameH = w * texH / texW;
                if (frameH > 0 && h > frameH && h % frameH == 0)
                {
                    var f = new Color32[w * frameH];
                    Array.Copy(px, (h - frameH) * w, f, 0, w * frameH);   // the top frame: last rows bottom-up
                    px = f; h = frameH;
                }
            }
            return Make(px, w, h, texture, wrap);
        }

        static Texture2D Make(Color32[] px, int w, int h, string name, TextureWrapMode wrap)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = name, filterMode = FilterMode.Point, wrapMode = wrap, anisoLevel = 0 };
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        // ---------- per frame ----------

        void Update()
        {
            _ticks += Time.deltaTime * 20f;
            if (!Ready) return;
            if (Model.CellChanged == null) Model.CellChanged = OnCellChanged;
            if (_modelsGen != EntityModels.Generation)
            {
                // entity_models.json arrived (or changed): bake again and rebuild every instance.
                _modelsGen = EntityModels.Generation;
                foreach (var m in _meshes.Values) if (m != null) Destroy(m);
                _meshes.Clear();
                foreach (var i in _insts.Values) Rebuild(i);
            }
            ScanStep();
            if (_dirtyCells.Count > 0)
            {
                var cells = new List<Vector3Int>(_dirtyCells);
                _dirtyCells.Clear();
                foreach (var c in cells) Recheck(c);
            }
            bool staging = Terrain != null && Terrain.Staging;
            foreach (var i in _insts.Values) if (i.Mob != null) UpdateMob(i, staging);
            if (staging) return;
            Draw();
        }

        static readonly Matrix4x4 MirrorZ = Matrix4x4.Scale(new Vector3(1, 1, -1));

        static Matrix4x4 U(M4 m)
        {
            var r = Matrix4x4.identity;
            r.m00 = m.A00; r.m01 = m.A01; r.m02 = m.A02; r.m03 = m.A03;
            r.m10 = m.A10; r.m11 = m.A11; r.m12 = m.A12; r.m13 = m.A13;
            r.m20 = m.A20; r.m21 = m.A21; r.m22 = m.A22; r.m23 = m.A23;
            return r;
        }

        /// <summary>Region-local Unity matrix of a member (cell corner + its block-space transform, Z mirrored).</summary>
        Matrix4x4 LocalOf(Member m)
        {
            var l = m.Inst.World - Model.Origin;
            M4 part = m.Part;
            switch (m.Anim)
            {
                case Anim.VaultItem:
                    part = BlockEntityCatalog.VaultItemTransform(_ticks * 10f) * m.Part;
                    break;
                case Anim.EnchantBook:
                    part = BlockEntityModels.ToBlock(BlockEntityCatalog.EnchantBookTransform(_ticks, _ticks * 0.02f + (m.Inst.World.x * 7 + m.Inst.World.z * 13) % 7));
                    break;
                case Anim.ConduitCage:
                {
                    float h = (float)Math.Sin(_ticks * 0.1f) / 2 + 0.5f; h = h * h + h;
                    var axis = new Vector3(0.5f, 1, 0.5f); float len = (float)Math.Sqrt(1.5);
                    part = BlockEntityModels.ToBlock(M4.T(0.5f, 0.3f + h * 0.2f, 0.5f) * AxisAngle(axis * (1 / len), _ticks * -0.0375f));
                    break;
                }
                case Anim.ConduitEye:
                {
                    float h = (float)Math.Sin(_ticks * 0.1f) / 2 + 0.5f; h = h * h + h;
                    var cam = Camera.main;
                    var pos = new Vector3(l.x + 0.5f, l.y + 0.3f + h * 0.2f, -(l.z + 0.5f));   // Unity region-local
                    var rot = Quaternion.identity;
                    if (cam != null)
                    {
                        var toCam = transform.InverseTransformPoint(cam.transform.position) - pos;
                        if (toCam.sqrMagnitude > 1e-8f) rot = Quaternion.LookRotation(-toCam.normalized, Vector3.up);
                    }
                    return Matrix4x4.TRS(pos, rot, Vector3.one * (0.5f * 4f / 3f / 16f));
                }
            }
            return MirrorZ * Matrix4x4.Translate(new Vector3(l.x, l.y, l.z)) * U(part);
        }

        static M4 AxisAngle(Vector3 a, float r)
        {
            float c = (float)Math.Cos(r), s = (float)Math.Sin(r), t = 1 - c;
            return new M4
            {
                A00 = t * a.x * a.x + c, A01 = t * a.x * a.y - s * a.z, A02 = t * a.x * a.z + s * a.y,
                A10 = t * a.x * a.y + s * a.z, A11 = t * a.y * a.y + c, A12 = t * a.y * a.z - s * a.x,
                A20 = t * a.x * a.z - s * a.y, A21 = t * a.y * a.z + s * a.x, A22 = t * a.z * a.z + c,
            };
        }

        readonly List<(Mesh, Material)> _emptyGroups = new List<(Mesh, Material)>();
        static readonly int ColorId = Shader.PropertyToID("_Color"), OverlayId = Shader.PropertyToID("_Overlay");
        static Vector4[] _noOverlay;

        void Draw()
        {
            var l2w = transform.localToWorldMatrix;
            bool moved = l2w != _lastL2W;
            _lastL2W = l2w;
            var bounds = RegionBounds(l2w);
            int calls = 0, instances = 0; long tris = 0;
            if (_noOverlay == null) { _noOverlay = new Vector4[Chunk]; for (int i = 0; i < Chunk; i++) _noOverlay[i] = new Vector4(1, 0, 0, 0); }
            _emptyGroups.Clear();
            foreach (var kv in _groups) if (kv.Value.Members.Count == 0) _emptyGroups.Add(kv.Key);
            foreach (var k in _emptyGroups) _groups.Remove(k);
            foreach (var g in _groups.Values)
            {
                int n = g.Members.Count;
                if (g.Dirty)
                {
                    if (g.Local.Length < n) g.Local = new Matrix4x4[n];
                    int chunks = (n + Chunk - 1) / Chunk;
                    if (g.Colors.Length < chunks)
                    {
                        Array.Resize(ref g.Colors, chunks); Array.Resize(ref g.Props, chunks); Array.Resize(ref g.World, chunks);
                        for (int c = 0; c < chunks; c++)
                        {
                            if (g.Colors[c] == null) g.Colors[c] = new Vector4[Chunk];
                            if (g.Props[c] == null) g.Props[c] = new MaterialPropertyBlock();
                            if (g.World[c] == null) g.World[c] = new Matrix4x4[Chunk];
                        }
                    }
                    g.Animated = false;
                    for (int i = 0; i < n; i++)
                    {
                        var m = g.Members[i];
                        if (m.Anim != Anim.None) g.Animated = true;
                        g.Local[i] = LocalOf(m);
                        var c = m.FullBright ? m.Base : m.Base * m.Inst.Light;
                        g.Colors[i / Chunk][i % Chunk] = c.linear;   // SetVectorArray doesn't convert like SetColor does
                    }
                    for (int c = 0; c < chunks; c++) { g.Props[c].Clear(); g.Props[c].SetVectorArray(ColorId, g.Colors[c]); g.Props[c].SetVectorArray(OverlayId, _noOverlay); }
                }
                else if (g.Animated)
                    for (int i = 0; i < n; i++) if (g.Members[i].Anim != Anim.None) g.Local[i] = LocalOf(g.Members[i]);
                if (g.Dirty || moved || g.Animated)
                    for (int i = 0; i < n; i++) g.World[i / Chunk][i % Chunk] = l2w * g.Local[i];
                g.Dirty = false;
                for (int start = 0, c = 0; start < n; start += Chunk, c++)
                {
                    var rp = new RenderParams(g.Mat)
                    {
                        matProps = g.Props[c], worldBounds = bounds, shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false,
                        layer = gameObject.layer, lightProbeUsage = LightProbeUsage.Off, reflectionProbeUsage = ReflectionProbeUsage.Off,
                    };
                    int count = Math.Min(Chunk, n - start);
                    Graphics.RenderMeshInstanced(rp, g.Mesh, 0, g.World[c], count);
                    calls++; instances += count;
                    tris += (long)count * g.Mesh.GetIndexCount(0) / 3;
                }
            }
            calls += DrawText(l2w, bounds);
            calls += DrawPortals(l2w, bounds);
            calls += DrawBeams(l2w, bounds);
            DrawCalls = calls; Instances = instances; Triangles = tris;
        }

        Bounds RegionBounds(Matrix4x4 l2w)
        {
            var d = Model.Dims;
            var b = new Bounds(l2w.MultiplyPoint3x4(Vector3.zero), Vector3.zero);
            for (int k = 1; k < 8; k++) b.Encapsulate(l2w.MultiplyPoint3x4(new Vector3((k & 1) * d.x, ((k >> 1) & 1) * (d.y + 4), -((k >> 2) & 1) * d.z)));
            return b;
        }

        // ---------- spawner mobs ----------

        void UpdateMob(Inst i, bool hidden)
        {
            var fig = i.Mob;
            fig.Root.SetActive(!hidden);
            if (hidden) return;
            // SpawnerRenderer: about 1000 / (delay + 200) * 10 degrees per tick while a player is near; a mid delay.
            i.Spin += Time.deltaTime * 20f * (1000f / 600f) * 10f;
            i.Age += Time.deltaTime * 20f;
            var l = i.World - Model.Origin;
            var p = BlockEntityCatalog.SpawnerMobTransform(i.Spin, i.MobW, i.MobH);
            // Vanilla P acts on the entity in Minecraft space; the figure's own root turn (yaw 0) is Unity Y 180.
            var w = MirrorZ * Matrix4x4.Translate(new Vector3(l.x, l.y, l.z)) * U(p) * MirrorZ * Matrix4x4.Rotate(Quaternion.Euler(0, 180, 0)) * Matrix4x4.Scale(Vector3.one * (fig.Look.Scale / 16f));
            var t = fig.Root.transform;
            t.localPosition = w.GetColumn(3);
            t.localRotation = w.rotation;
            t.localScale = w.lossyScale;
            fig.Animate(new EntityAnimInput { AgeTicks = i.Age, OnGround = true }, 0);
            fig.SetColors(i.Light, new Color(1, 0, 0, 0));
        }

        // ---------- sign text ----------

        int DrawText(Matrix4x4 l2w, Bounds bounds)
        {
            if (_textDirty) { _textDirty = false; BuildText(); }
            if (_textMesh == null || _textMesh.vertexCount == 0 || _textMat == null) return 0;
            Graphics.RenderMesh(new RenderParams(_textMat) { worldBounds = bounds, shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false, layer = gameObject.layer }, _textMesh, 0, l2w);
            return 1;
        }

        readonly List<TextQuad> _quads = new List<TextQuad>();
        readonly List<Vector3> _tv = new List<Vector3>();
        readonly List<Vector2> _tuv = new List<Vector2>();
        readonly List<Color32> _tc = new List<Color32>();
        readonly List<int> _ti = new List<int>();

        void BuildText()
        {
            _tv.Clear(); _tuv.Clear(); _tc.Clear(); _ti.Clear();
            foreach (var inst in _insts.Values)
            {
                if (!IsSign(inst.Kind)) continue;
                var data = Store.Get(inst.World);
                if (data?.Front == null) continue;
                if (!EnsureFont()) return;
                BlockEntityCatalog.SignMetrics(inst.State, out int lh, out int mw);
                var l = inst.World - Model.Origin;
                for (int side = 0; side < 2; side++)
                {
                    var s = side == 0 ? data.Front : data.Back;
                    if (s == null || s.Empty) continue;
                    _quads.Clear();
                    SignTextLayout.Layout(_font, s, TextDye(s.Color), lh, mw, _quads);
                    var m = BlockEntityCatalog.SignTextTransform(inst.State, side == 0);
                    foreach (var q in _quads)
                    {
                        var col = q.Color;
                        if (!q.FullBright) { var li = inst.Light; col = new Color32((byte)(col.r * li.r), (byte)(col.g * li.g), (byte)(col.b * li.b), 255); }
                        int b = _tv.Count;
                        Vector3 P(float x, float y) { var v = m.Point(new Vector3(x, y, q.Z)); return new Vector3(l.x + v.x, l.y + v.y, -(l.z + v.z)); }
                        _tv.Add(P(q.X0, q.Y0)); _tv.Add(P(q.X1, q.Y0)); _tv.Add(P(q.X1, q.Y1)); _tv.Add(P(q.X0, q.Y1));
                        _tuv.Add(new Vector2(q.U0, q.V0)); _tuv.Add(new Vector2(q.U1, q.V0)); _tuv.Add(new Vector2(q.U1, q.V1)); _tuv.Add(new Vector2(q.U0, q.V1));
                        for (int k = 0; k < 4; k++) _tc.Add(col);
                        _ti.Add(b); _ti.Add(b + 1); _ti.Add(b + 2); _ti.Add(b); _ti.Add(b + 2); _ti.Add(b + 3);
                    }
                }
            }
            if (_textMesh == null) { _textMesh = new Mesh { name = "sign text", indexFormat = IndexFormat.UInt32 }; _textMesh.MarkDynamic(); }
            _textMesh.Clear();
            _textMesh.SetVertices(_tv); _textMesh.SetUVs(0, _tuv); _textMesh.SetColors(_tc); _textMesh.SetTriangles(_ti, 0);
            _textMesh.RecalculateBounds();
        }

        bool EnsureFont()
        {
            if (_font != null) return true;
            if (_fontTried) return false;
            _fontTried = true;
            var assets = Atlas as IModelAssets;
            _font = BitmapFont.Load(assets.ReadJson, path =>
            {
                byte[] png = null;
                try { png = Atlas.ReadBytes(path.EndsWith(".png") ? path : path + ".png"); } catch (Exception) { }
                int w = 0, h = 0;
                var px = png != null ? PngDecoder.Decode(png, out w, out h) : null;
                return px != null ? (px, w, h) : ((Color32[], int, int)?)null;
            });
            if (_font == null) { UnityEngine.Debug.LogWarning("[Tabletop] sign text: no bitmap font in the pack"); return false; }
            _fontTex = Make(_font.Atlas, _font.AtlasW, _font.AtlasH, "sign font", TextureWrapMode.Clamp);
            var baseMat = Resources.Load<Material>("TabletopBlockEntityFx");
            _textMat = baseMat != null ? new Material(baseMat) { name = "sign text" } : new Material(Shader.Find("Sprites/Default"));
            _textMat.mainTexture = _fontTex;
            _textMat.SetFloat("_Cutoff", 0.1f);
            _textMat.SetFloat("_OffsetFactor", -1); _textMat.SetFloat("_OffsetUnits", -1);   // POLYGON_OFFSET: text over the board
            UnityEngine.Debug.Log($"[Tabletop] sign font: {_font.GlyphCount} glyphs, atlas {_font.AtlasW}x{_font.AtlasH}");
            return true;
        }

        // ---------- end portals and gateways ----------

        int DrawPortals(Matrix4x4 l2w, Bounds bounds)
        {
            if (_portalDirty) { _portalDirty = false; BuildPortals(); }
            int calls = 0;
            if (_portalMesh != null && _portalMesh.vertexCount > 0 && PortalMaterials())
            { Graphics.RenderMesh(new RenderParams(_portalMat) { worldBounds = bounds, shadowCastingMode = ShadowCastingMode.Off, layer = gameObject.layer }, _portalMesh, 0, l2w); calls++; }
            if (_gatewayMesh != null && _gatewayMesh.vertexCount > 0 && PortalMaterials())
            { Graphics.RenderMesh(new RenderParams(_gatewayMat) { worldBounds = bounds, shadowCastingMode = ShadowCastingMode.Off, layer = gameObject.layer }, _gatewayMesh, 0, l2w); calls++; }
            return calls;
        }

        bool PortalMaterials()
        {
            if (_portalMat != null) return true;
            var baseMat = Resources.Load<Material>("TabletopEndPortal");
            if (baseMat == null) return false;
            var stars = Pack("entity/end_portal/end_portal", 0, 0, TextureWrapMode.Repeat);
            var sky = Pack("environment/end_sky", 0, 0, TextureWrapMode.Repeat);
            _portalMat = new Material(baseMat) { name = "end portal" };
            _portalMat.SetTexture("_MainTex", stars); _portalMat.SetTexture("_SkyTex", sky); _portalMat.SetFloat("_Layers", 15);
            _gatewayMat = new Material(_portalMat) { name = "end gateway" };
            _gatewayMat.SetFloat("_Layers", 16);
            return true;
        }

        void BuildPortals()
        {
            var pv = new List<Vector3>(); var pi = new List<int>();
            var gv = new List<Vector3>(); var gi = new List<int>();
            foreach (var inst in _insts.Values)
            {
                var l = inst.World - Model.Origin;
                if (inst.Kind == BlockEntityKind.EndPortal)
                {
                    // TheEndPortalRenderer: only the top (0.75) and bottom (0.375) faces.
                    Quad(pv, pi, l, 3, 0.75f); Quad(pv, pi, l, 2, 0.375f);
                }
                else if (inst.Kind == BlockEntityKind.EndGateway)
                    for (int d = 0; d < 6; d++)
                    {
                        // Block.shouldRenderFace: hidden by an opaque cube or another gateway.
                        int nx = l.x + (d == 0 ? -1 : d == 1 ? 1 : 0), ny = l.y + (d == 2 ? -1 : d == 3 ? 1 : 0), nz = l.z + (d == 4 ? -1 : d == 5 ? 1 : 0);
                        ushort id = Model.Get(nx, ny, nz);
                        if (id != 0xFFFF && (Model.ShapeOf(id) == BlockShape.Cube || BlockRules.Path(Model.State(id)) == "end_gateway")) continue;
                        Quad(gv, gi, l, d, d == 0 || d == 2 || d == 4 ? 0 : 1);
                    }
            }
            _portalMesh = Fill(_portalMesh, "end portals", pv, pi);
            _gatewayMesh = Fill(_gatewayMesh, "end gateways", gv, gi);
        }

        /// <summary>One face of a cell (dir 0..5 = west, east, down, up, north, south) at offset at along its axis, Unity space.</summary>
        static void Quad(List<Vector3> v, List<int> idx, Vector3Int l, int dir, float at)
        {
            int axis = dir / 2;
            var c = new Vector3[4];
            for (int k = 0; k < 4; k++)
            {
                float a = k == 1 || k == 2 ? 1 : 0, b = k >= 2 ? 1 : 0;
                var p = axis == 0 ? new Vector3(at, a, b) : axis == 1 ? new Vector3(a, at, b) : new Vector3(a, b, at);
                c[k] = new Vector3(l.x + p.x, l.y + p.y, -(l.z + p.z));
            }
            int s = v.Count;
            v.AddRange(c);
            idx.Add(s); idx.Add(s + 1); idx.Add(s + 2); idx.Add(s); idx.Add(s + 2); idx.Add(s + 3);
        }

        static Mesh Fill(Mesh m, string name, List<Vector3> v, List<int> idx)
        {
            if (m == null) { m = new Mesh { name = name, indexFormat = IndexFormat.UInt32 }; m.MarkDynamic(); }
            m.Clear();
            m.SetVertices(v); m.SetTriangles(idx, 0);
            m.RecalculateBounds();
            return m;
        }

        // ---------- beacon beams ----------

        readonly List<Vector3> _bv = new List<Vector3>();
        readonly List<Vector2> _buv = new List<Vector2>();
        readonly List<Color32> _bc = new List<Color32>();
        readonly List<int> _bCore = new List<int>(), _bGlow = new List<int>();

        int DrawBeams(Matrix4x4 l2w, Bounds bounds)
        {
            _bv.Clear(); _buv.Clear(); _bc.Clear(); _bCore.Clear(); _bGlow.Clear();
            float k = _ticks % 40f;
            foreach (var inst in _insts.Values)
            {
                if (inst.Kind != BlockEntityKind.Beacon) continue;
                var data = Store.Get(inst.World);
                if (data == null || data.Tier <= 0) continue;
                var l = inst.World - Model.Origin;
                if (inst.BeamDirty) { inst.Beam = BeaconBeam.Sections(Model, l.x, l.y, l.z, Dye); inst.BeamDirty = false; }
                if (inst.Beam == null) continue;
                for (int s = 0; s < inst.Beam.Count; s++)
                {
                    var sec = inst.Beam[s];
                    int height = s == inst.Beam.Count - 1 ? Math.Max(1, Model.Dims.y - (l.y + sec.Y)) : sec.Height;
                    BeamPart(l, sec.Y, height, sec.Color, 0.2f, k * 2.25f - 45f, true, k);
                    BeamPart(l, sec.Y, height, new Color32(sec.Color.r, sec.Color.g, sec.Color.b, 32), 0.25f, 0, false, k);
                }
            }
            if (_bv.Count == 0) return 0;
            if (_beamCoreMat == null)
            {
                var baseMat = Resources.Load<Material>("TabletopBlockEntityFx");
                if (baseMat == null) return 0;
                var tex = Pack("entity/beacon/beacon_beam", 0, 0, TextureWrapMode.Repeat);
                _beamCoreMat = new Material(baseMat) { name = "beacon beam", mainTexture = tex };
                _beamCoreMat.SetFloat("_Cutoff", -1);
                _beamGlowMat = new Material(_beamCoreMat) { name = "beacon beam glow" };
                _beamGlowMat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); _beamGlowMat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                _beamGlowMat.SetFloat("_ZWrite", 0); _beamGlowMat.renderQueue = 3000;
            }
            if (_beamMesh == null) { _beamMesh = new Mesh { name = "beacon beams", indexFormat = IndexFormat.UInt32 }; _beamMesh.MarkDynamic(); }
            _beamMesh.Clear();
            _beamMesh.SetVertices(_bv); _beamMesh.SetUVs(0, _buv); _beamMesh.SetColors(_bc);
            _beamMesh.subMeshCount = 2;
            _beamMesh.SetTriangles(_bCore, 0); _beamMesh.SetTriangles(_bGlow, 1);
            _beamMesh.RecalculateBounds();
            Graphics.RenderMesh(new RenderParams(_beamCoreMat) { worldBounds = bounds, shadowCastingMode = ShadowCastingMode.Off, layer = gameObject.layer }, _beamMesh, 0, l2w);
            Graphics.RenderMesh(new RenderParams(_beamGlowMat) { worldBounds = bounds, shadowCastingMode = ShadowCastingMode.Off, layer = gameObject.layer }, _beamMesh, 1, l2w);
            return 2;
        }

        /// <summary>BeaconRenderer.submitBeaconBeam: 4 sides around the beacon's centre line, the core turned and narrower,
        /// the texture scrolling up (v per block 0.5 / radius for the core, 1 for the glow).</summary>
        void BeamPart(Vector3Int l, int y0, int height, Color32 c, float r, float turnDeg, bool core, float k)
        {
            float m = k * 0.2f - (float)Math.Floor(k * 0.1f); m -= (float)Math.Floor(m);
            float v0 = -1 + m, v1 = height * (core ? 0.5f / r : 1f) + v0;
            float a = turnDeg * Mathf.Deg2Rad;
            var corners = new Vector2[4];
            for (int i = 0; i < 4; i++)
            {
                // core: a diamond (0,r) (r,0) (0,-r) (-r,0) turned by a; glow: an axis-aligned square
                float ang = core ? a + i * Mathf.PI / 2 : Mathf.PI / 4 + i * Mathf.PI / 2;
                float rr = core ? r : r * Mathf.Sqrt(2);
                corners[i] = new Vector2(Mathf.Sin(ang) * rr, Mathf.Cos(ang) * rr);
            }
            var tris = core ? _bCore : _bGlow;
            for (int f = 0; f < 4; f++)
            {
                var p = corners[f]; var q = corners[(f + 1) % 4];
                int s = _bv.Count;
                float ya = l.y + y0, yb = l.y + y0 + height;
                _bv.Add(new Vector3(l.x + 0.5f + p.x, ya, -(l.z + 0.5f + p.y))); _bv.Add(new Vector3(l.x + 0.5f + q.x, ya, -(l.z + 0.5f + q.y)));
                _bv.Add(new Vector3(l.x + 0.5f + q.x, yb, -(l.z + 0.5f + q.y))); _bv.Add(new Vector3(l.x + 0.5f + p.x, yb, -(l.z + 0.5f + p.y)));
                _buv.Add(new Vector2(0, v1)); _buv.Add(new Vector2(1, v1)); _buv.Add(new Vector2(1, v0)); _buv.Add(new Vector2(0, v0));
                for (int i = 0; i < 4; i++) _bc.Add(c);
                tris.Add(s); tris.Add(s + 1); tris.Add(s + 2); tris.Add(s); tris.Add(s + 2); tris.Add(s + 3);
            }
        }

        /// <summary>What is on screen (status line, logs).</summary>
        public string Stats() => $"block_entities={_insts.Count} groups={_groups.Count} instances={Instances} draw_calls={DrawCalls} tris={Triangles}" + (Scanning ? $" scanning={_scan.Count}" : "");
    }
}
