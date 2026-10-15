// [living] EntityFigure's living-world parts (LivingLooks.cs): gear layers on the model's parts, block models on the
// figure (snow golem pumpkin, mooshroom mushrooms, enderman block), a shulker's lid and attach face, experience orbs and
// the dragon fireball as camera-facing sprites, lightning bolts, and display entities (block, item and text displays
// with their transformation and billboard, DisplayRenderer).
using System.Collections.Generic;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed partial class EntityFigure
    {
        readonly List<MeshRenderer> _blockRenderers = new List<MeshRenderer>();   // block shaders: lit through _Tint
        readonly MaterialPropertyBlock _blockMpb = new MaterialPropertyBlock();
        Transform _billboard, _orient, _dT, _dL, _dS, _dR;
        MeshRenderer _billboardRenderer;
        Vector3 _lastLight = new Vector3(-1, 0, 0);

        static readonly int TintId = Shader.PropertyToID("_Tint"), LightRatioId = Shader.PropertyToID("_LightRatio");

        /// <summary>A gear layer (same root pose as the model): parts the model has are drawn on its part transforms, the
        /// others (the happy ghast's goggles) get their own transform under their parent.</summary>
        void AttachLayer(EModel layer, Material mat)
        {
            var map = new Transform[layer.Parts.Count];
            foreach (var p in layer.Parts)
            {
                var hp = p.Parent == null ? _model.Root : _model.Find(p.Name);
                var mesh = EntityMeshes.Part(layer, p);
                if (hp != null && (p.Parent == null) == (hp.Parent == null))
                {
                    var host = _parts[hp.Index];
                    map[p.Index] = host;
                    if (mesh == null) continue;
                    var go = new GameObject(p.Name + " gear");
                    go.transform.SetParent(host, false);
                    if (p.Parent != null && (hp.Rx != p.Rx || hp.Ry != p.Ry || hp.Rz != p.Rz))
                        go.transform.localRotation = Quaternion.Inverse(_defaults[hp.Index]) * Rot(p.Rx, p.Ry, p.Rz);
                    AddRenderer(go, mesh, mat);
                    _body.Add(_renderers[_renderers.Count - 1]);
                }
                else
                {
                    var parent = map[p.Parent.Index] ?? _parts[0];
                    var t = new GameObject(p.Name + " gear").transform;
                    t.SetParent(parent, false);
                    EntityModels.Pivot(p, out float x, out float y, out float z);
                    t.localPosition = new Vector3(x, y, z);
                    t.localRotation = Rot(p.Rx, p.Ry, p.Rz);
                    t.localScale = new Vector3(p.Sx, p.Sy, p.Sz);
                    map[p.Index] = t;
                    if (mesh == null) continue;
                    AddRenderer(t.gameObject, mesh, mat);
                    _body.Add(_renderers[_renderers.Count - 1]);
                }
            }
        }

        /// <summary>A block model through a render layer's model-space PoseStack ops: in figure space (point-reflected game
        /// model space, 1 unit = 1 pixel) translations are negated pixels, rotations and scales keep their numbers.</summary>
        void AttachBlock(Transform host, BlockAttach b, EntityTextures tex)
        {
            var mesh = BlockFigureMeshes.Get(b.State, tex, true);
            if (mesh == null) return;
            var t = host;
            foreach (var op in b.Ops)
            {
                var n = new GameObject("block op").transform;
                n.SetParent(t, false);
                switch (op.Kind)
                {
                    case 'T': n.localPosition = op.V * -16f; break;
                    case 'X': n.localRotation = Quaternion.AngleAxis(op.V.x, Vector3.right); break;
                    case 'Y': n.localRotation = Quaternion.AngleAxis(op.V.x, Vector3.up); break;
                    case 'S': n.localScale = op.V; break;
                }
                t = n;
            }
            AddBlockRenderer(t.gameObject, mesh, tex);
        }

        void AddBlockRenderer(GameObject go, Mesh mesh, EntityTextures tex)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = new[] { tex.BlockOpaque, tex.BlockTransparent };
            Quiet(r);
            _blockRenderers.Add(r);
        }

        void LivingColors(Color light)
        {
            if (_blockRenderers.Count == 0) return;
            var l = new Vector3(light.r, light.g, light.b);
            if (l == _lastLight) return;
            _lastLight = l;
            _blockMpb.Clear();
            _blockMpb.SetVector(TintId, new Vector4(light.r, light.g, light.b, 1));   // sRGB values, as the vertex colours
            _blockMpb.SetFloat(LightRatioId, 0f);                                     // lit here, not by the terrain's ratio
            foreach (var r in _blockRenderers) if (r != null) r.SetPropertyBlock(_blockMpb);
        }

        /// <summary>ShulkerRenderer: a shulker stuck to a wall or ceiling turns about its centre by the opposite face's
        /// Direction.getRotation (game frame, seen through the Z mirror).</summary>
        void AttachFace(string attach)
        {
            float[] q;
            switch (attach)   // getRotation of the opposite face
            {
                case "up": q = LivingLooks.AxisAngle(1, 0, 0, 180); break;                                           // down
                case "south": q = LivingLooks.Mul(LivingLooks.AxisAngle(1, 0, 0, 90), LivingLooks.AxisAngle(0, 0, 1, 180)); break;   // north
                case "north": q = LivingLooks.AxisAngle(1, 0, 0, 90); break;                                          // south
                case "east": q = LivingLooks.Mul(LivingLooks.AxisAngle(1, 0, 0, 90), LivingLooks.AxisAngle(0, 0, 1, 90)); break;     // west
                case "west": q = LivingLooks.Mul(LivingLooks.AxisAngle(1, 0, 0, 90), LivingLooks.AxisAngle(0, 0, 1, -90)); break;    // east
                default: return;
            }
            var m = LivingLooks.Mirror(q);
            var pivot = new GameObject("attach").transform;
            pivot.SetParent(Root.transform, false);
            pivot.localPosition = new Vector3(0, 8, 0);
            pivot.localRotation = new Quaternion(m[0], m[1], m[2], m[3]);
            var offset = _modelRoot.localPosition;
            _modelRoot.SetParent(pivot, false);
            _modelRoot.localPosition = offset - new Vector3(0, 8, 0);
        }

        // ---------- sprites: experience orbs, the dragon fireball ----------

        void BuildBillboard(EntityInfo e, EntityTextures tex)
        {
            var t = tex.Get(Look.Texture);
            _billboard = new GameObject("billboard").transform;
            _billboard.SetParent(Root.transform, false);
            float u0 = 0, v0 = 0, u1 = 1, v1 = 1;
            if (Look.Kind == EntityKind.Orb)
            {
                // ExperienceOrbRenderer: 0.1 up, facing the camera, 0.3 of a block, the value's icon.
                LivingLooks.OrbUv(Look.Orb, out u0, out v0, out u1, out v1);
                _billboard.localPosition = new Vector3(0, 1.6f, 0);
                _billboard.localScale = Vector3.one * 0.3f;
            }
            var go = new GameObject("sprite");
            go.transform.SetParent(_billboard, false);
            go.AddComponent<MeshFilter>().sharedMesh = BillboardMeshes.Get(u0, v0, u1, v1);
            _billboardRenderer = go.AddComponent<MeshRenderer>();
            _billboardRenderer.sharedMaterial = tex.Material(t, 1 | 2);   // translucent, flat
            Quiet(_billboardRenderer);
            _renderers.Add(_billboardRenderer);
            if (Look.Kind == EntityKind.Orb) _boxColor = (Color)LivingLooks.OrbColor(0);
            TopPx = 8;
        }

        /// <summary>Turns sprites to the camera (vanilla's camera orientation) and display entities' billboards.</summary>
        public void FaceCamera(Transform cam)
        {
            if (cam == null) return;
            if (_billboard != null) _billboard.rotation = Quaternion.LookRotation(_billboard.position - cam.position, cam.up);
            if (_orient != null && _info?.Display != null) PoseDisplay(_info, cam);
            if (_textBack != null && !_textSized)
            {
                var b = _text.GetComponent<MeshRenderer>().localBounds;
                if (b.size.x > 0)
                {
                    _textSized = true;
                    _textBack.localPosition = new Vector3(b.center.x, b.center.y, 0.05f);
                    _textBack.localScale = new Vector3(b.size.x + 0.8f, b.size.y + 0.8f, 1);   // a font pixel of padding
                }
            }
        }

        // ---------- lightning ----------

        void BuildLightning(EntityInfo e, EntityTextures tex)
        {
            var go = new GameObject("bolt");
            go.transform.SetParent(Root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = BillboardMeshes.Bolt(e.Id);
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = tex.Lightning;   // additive, no depth write; not lit (not in _renderers)
            Quiet(r);
            TopPx = 16;
        }

        // ---------- display entities ----------

        void BuildDisplay(EntityInfo e, EntityTextures tex)
        {
            // DisplayRenderer: orientation (billboard), then the transformation translation * left * scale * right.
            _orient = Node(Root.transform, "orient");
            _dT = Node(_orient, "translation"); _dL = Node(_dT, "left"); _dS = Node(_dL, "scale"); _dR = Node(_dS, "right");
            string t = e.Path;
            if (t == "block_display" && !string.IsNullOrEmpty(Look.State))
            {
                // BlockDisplayRenderer: the block's model with its corner at the entity (no centring), game frame.
                var mesh = BlockFigureMeshes.Get(Look.State, tex, false);
                if (mesh != null) AddBlockRenderer(Node(_dR, "block").gameObject, mesh, tex);
            }
            else if (t == "item_display" && !string.IsNullOrEmpty(Look.Item))
            {
                // ItemDisplayRenderer: rotY(pi), then the item model's display transform for the item_transform context.
                var turn = Node(_dR, "item turn");
                turn.localRotation = Quaternion.AngleAxis(180, Vector3.up);
                var v = tex.Item(Look.Item);
                var d = ItemContext(v, e.Display?.ItemTransform, out bool left);
                var ctx = Node(turn, "context");
                ctx.localPosition = new Vector3(left ? -d.Tx : d.Tx, d.Ty, -d.Tz);
                ctx.localRotation = Quaternion.AngleAxis(-d.Rx, Vector3.right) * Quaternion.AngleAxis(left ? d.Ry : -d.Ry, Vector3.up) * Quaternion.AngleAxis(left ? -d.Rz : d.Rz, Vector3.forward);
                ctx.localScale = new Vector3(d.Sx, d.Sy, d.Sz);
                ItemNode(ctx, v, tex, true, _renderers);
            }
            else if (t == "text_display" && e.Display?.Text != null) BuildText(e.Display, tex);
            TopPx = 16;
            PoseDisplay(e, null);
        }

        static Transform Node(Transform parent, string name)
        {
            var n = new GameObject(name).transform;
            n.SetParent(parent, false);
            return n;
        }

        /// <summary>The display transform an item_display context uses (ItemModels parses fixed, ground, gui and
        /// thirdperson_righthand; the left hand mirrors it; first person and head draw untransformed).</summary>
        static ItemDisplay ItemContext(ItemVisual v, string context, out bool left)
        {
            left = false;
            switch (context)
            {
                case "fixed": return v.Fixed.Set ? v.Fixed : ItemDisplay.Identity;
                case "ground": return v.Ground.Set ? v.Ground : ItemDisplay.Identity;
                case "gui": return v.Gui.Set ? v.Gui : ItemDisplay.Identity;
                case "thirdperson_lefthand": left = true; return v.ThirdPersonRight.Set ? v.ThirdPersonRight : ItemDisplay.Identity;
                case "thirdperson_righthand": return v.ThirdPersonRight.Set ? v.ThirdPersonRight : ItemDisplay.Identity;
                default: return ItemDisplay.Identity;
            }
        }

        TextMesh _text;
        Transform _textBack;
        bool _textSized;

        /// <summary>TextDisplayRenderer: lines of 0.025-block font pixels (a line is 10), standing on the entity
        /// position, with the background colour behind them (sized once the text mesh exists, SizeTextBackground).</summary>
        void BuildText(EntityDisplay d, EntityTextures tex)
        {
            var holder = Node(_dR, "text");
            var go = new GameObject("text");
            go.transform.SetParent(holder, false);
            _text = go.AddComponent<TextMesh>();
            TabletopApp.SetBuiltinFont(_text);
            _text.text = d.Text;
            _text.fontSize = 48;
            _text.characterSize = 0.72f;   // a 10-pixel line = 0.25 blocks = 4 figure pixels
            _text.anchor = d.Align == "left" ? TextAnchor.LowerLeft : d.Align == "right" ? TextAnchor.LowerRight : TextAnchor.LowerCenter;
            _text.alignment = d.Align == "left" ? TextAlignment.Left : d.Align == "right" ? TextAlignment.Right : TextAlignment.Center;
            _text.color = new Color(1, 1, 1, Mathf.Max(26, d.TextOpacity) / 255f);
            int bg = d.Background;
            float a = ((bg >> 24) & 255) / 255f;
            if (a <= 0.01f) return;
            _textBack = Node(holder, "background");
            _textBack.localScale = Vector3.zero;   // sized when the text mesh exists
            var quad = _textBack.gameObject.AddComponent<MeshFilter>();
            quad.sharedMesh = BillboardMeshes.Quad;
            var qr = _textBack.gameObject.AddComponent<MeshRenderer>();
            qr.sharedMaterial = tex.LinesTranslucent;
            Quiet(qr);
            var mpb = new MaterialPropertyBlock();
            mpb.SetColor(EntityTextures.ColorId, new Color(((bg >> 16) & 255) / 255f, ((bg >> 8) & 255) / 255f, (bg & 255) / 255f, a));
            qr.SetPropertyBlock(mpb);
        }

        /// <summary>The display's orientation (billboard toward the camera) and transformation from the latest state.</summary>
        void PoseDisplay(EntityInfo e, Transform cam)
        {
            var d = e.Display;
            if (d == null || _orient == null) return;
            float camYaw = 0, camPitch = 0;
            if (cam != null && d.Billboard != "fixed")
            {
                // The camera's look direction in the game frame (Root's parent is RegionRoot-local Unity: z mirrored).
                var f = Root.transform.parent.InverseTransformDirection(cam.forward);
                LivingLooks.YawPitch(new Vector3(f.x, f.y, -f.z), out camYaw, out camPitch);
            }
            var q = LivingLooks.Mirror(LivingLooks.Orientation(d.Billboard, e.Yaw, e.Pitch, camYaw, camPitch));
            _orient.localRotation = new Quaternion(q[0], q[1], q[2], q[3]);
            _dT.localPosition = new Vector3(d.Translation.x, d.Translation.y, -d.Translation.z) * 16f;
            var l = LivingLooks.Mirror(d.Left); var r = LivingLooks.Mirror(d.Right);
            _dL.localRotation = Normalized(l);
            _dS.localScale = d.Scale;
            _dR.localRotation = Normalized(r);
        }

        static Quaternion Normalized(float[] q)
        {
            float n = Mathf.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
            return n < 1e-6f ? Quaternion.identity : new Quaternion(q[0] / n, q[1] / n, q[2] / n, q[3] / n);
        }

        // ---------- per frame ----------

        readonly float _born = Time.time;

        void AnimateLiving(in EntityAnimInput a, float pitchDeg)
        {
            if (Look.Kind == EntityKind.Orb) { _boxColor = (Color)LivingLooks.OrbColor(a.AgeTicks); _applied = false; }
            if (_rig == null) return;
            if (Look.Root == RootKind.Fangs) AnimateFangs(Mathf.Clamp01((Time.time - _born) / 1f));
            if (Look.Root == RootKind.Skull && _model.Find("head") is EModelPart head)
                _parts[head.Index].localRotation = Rot(pitchDeg * Mathf.Deg2Rad, a.BodyYaw * Mathf.Deg2Rad, 0);   // SkullModel.setupAnim
            if (Look.Root == RootKind.Bullet)
            {
                // ShulkerBulletRenderer: rotY(sin(age 0.1) 180) rotX(cos(age 0.1) 180) rotZ(sin(age 0.15) 360), through the Z mirror.
                float t = a.AgeTicks;
                _modelRoot.localRotation = Quaternion.AngleAxis(-Mathf.Sin(t * 0.1f) * 180f, Vector3.up) * Quaternion.AngleAxis(-Mathf.Cos(t * 0.1f) * 180f, Vector3.right)
                                           * Quaternion.AngleAxis(Mathf.Sin(t * 0.15f) * 360f, Vector3.forward);
            }
            if (_info != null && _info.Path == "shulker" && _model.Find("lid") is EModelPart lid)
            {
                // ShulkerModel.setupAnim: the lid rises with the peek amount, jitters when wide open, and twists a little.
                float f = (0.5f + _info.Peek) * Mathf.PI, g = -1f + Mathf.Sin(f);
                float h = f > Mathf.PI ? Mathf.Sin(a.AgeTicks * 0.1f) * 0.7f : 0f;
                _parts[lid.Index].localPosition = new Vector3(0, -(16f + Mathf.Sin(f) * 8f + h), 0);
                float yRot = _info.Peek > 0.3f ? g * g * g * g * Mathf.PI * 0.125f : 0f;
                _parts[lid.Index].localRotation = Rot(lid.Rx, lid.Ry + yRot, lid.Rz);
            }
        }
    }

    public sealed partial class EntityFigure
    {
        /// <summary>EvokerFangsModel.setupAnim: the fangs rise out of the ground and snap shut over the bite (progress
        /// 0..1 over 20 ticks from when they appear; the attack starts when the server spawns them).</summary>
        void AnimateFangs(float bite)
        {
            LivingLooks.FangsPose(bite, out float baseY, out float upperZ, out float lowerZ);
            if (_model.Find("base") is EModelPart b) _parts[b.Index].localPosition = new Vector3(-b.Px, -baseY, -b.Pz);
            if (_model.Find("upper_jaw") is EModelPart u) _parts[u.Index].localRotation = Rot(u.Rx, u.Ry, upperZ);
            if (_model.Find("lower_jaw") is EModelPart l) _parts[l.Index].localRotation = Rot(l.Rx, l.Ry, lowerZ);
        }
    }

    /// <summary>[living] Shared meshes for sprites (orb icons, the dragon fireball), a unit quad and lightning bolts.</summary>
    public static class BillboardMeshes
    {
        static readonly Dictionary<(float, float, float, float), Mesh> Sprites = new Dictionary<(float, float, float, float), Mesh>();
        static readonly Dictionary<int, Mesh> Bolts = new Dictionary<int, Mesh>();
        static Mesh _quad;

        /// <summary>The orb/fireball quad of ExperienceOrbRenderer (x -0.5..0.5, y -0.25..0.75 blocks) in figure pixels,
        /// facing -Z (towards a camera it is turned to face), with the given texture rectangle.</summary>
        public static Mesh Get(float u0, float v0, float u1, float v1)
        {
            var k = (u0, v0, u1, v1);
            if (Sprites.TryGetValue(k, out var m) && m != null) return m;
            m = new Mesh { name = "sprite" };
            m.SetVertices(new List<Vector3> { new Vector3(-8, -4, 0), new Vector3(8, -4, 0), new Vector3(8, 12, 0), new Vector3(-8, 12, 0) });
            m.SetUVs(0, new List<Vector2> { new Vector2(u0, v0), new Vector2(u1, v0), new Vector2(u1, v1), new Vector2(u0, v1) });
            m.SetNormals(new List<Vector3> { Vector3.back, Vector3.back, Vector3.back, Vector3.back });
            m.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
            m.RecalculateBounds();
            Sprites[k] = m;
            return m;
        }

        /// <summary>A unit quad centred on the origin in the XY plane (text display backgrounds).</summary>
        public static Mesh Quad
        {
            get
            {
                if (_quad != null) return _quad;
                _quad = new Mesh { name = "quad" };
                _quad.SetVertices(new List<Vector3> { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0) });
                _quad.SetColors(new List<Color32> { Color.white, Color.white, Color.white, Color.white });
                _quad.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
                _quad.RecalculateBounds();
                return _quad;
            }
        }

        /// <summary>LightningBolt.Quads for a seed, in figure pixels (game x, y, -z times 16), vertex coloured.</summary>
        public static Mesh Bolt(int seed)
        {
            if (Bolts.TryGetValue(seed, out var m) && m != null) return m;
            var q = LightningBolt.Quads(seed);
            var verts = new List<Vector3>(q.Count); var cols = new List<Color32>(q.Count); var tris = new List<int>(q.Count * 3 / 2);
            foreach (var p in q) { verts.Add(new Vector3(p.x * 16f, p.y * 16f, -p.z * 16f)); cols.Add(LightningBolt.Color); }
            for (int i = 0; i < q.Count; i += 4) { tris.Add(i); tris.Add(i + 1); tris.Add(i + 2); tris.Add(i); tris.Add(i + 2); tris.Add(i + 3); }
            m = new Mesh { name = "lightning bolt" };
            m.SetVertices(verts); m.SetColors(cols); m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            if (Bolts.Count > 32) Bolts.Clear();
            Bolts[seed] = m;
            return m;
        }
    }

    /// <summary>[living] One-block meshes of block states through the real mesher (textures and models like the terrain),
    /// for blocks on entities and block displays. pointReflected: figure space (game model space negated, pixels); else
    /// the game frame through the Z mirror (pixels). Main thread (the atlas resolves new states here).</summary>
    public static class BlockFigureMeshes
    {
        static readonly Dictionary<(string, bool), Mesh> Cache = new Dictionary<(string, bool), Mesh>();

        public static Mesh Get(string state, EntityTextures tex, bool pointReflected)
        {
            var atlas = tex?.Atlas;
            if (string.IsNullOrEmpty(state) || atlas == null || tex.BlockOpaque == null) return null;
            if (Cache.TryGetValue((state, pointReflected), out var m) && m != null) return m;
            for (int dir = 0; dir < 6; dir++) atlas.Face(state, dir);
            atlas.Commit();
            if (atlas.Array != null)
                foreach (var mat in new[] { tex.BlockOpaque, tex.BlockTransparent }) { mat.SetTexture("_Blocks", atlas.Array); mat.SetFloat("_HasTex", 1); }
            var one = new WorldModel { SnapshotId = 0, World = "figure", Dims = new Vector3Int(1, 1, 1) };
            one.Palette.Add("minecraft:air"); one.Palette.Add(state);
            one.Grid = new ushort[] { 1 };
            var data = new MeshData();
            IFaceSource faces = atlas;
            GreedyMesher.Build(one, Vector3Int.zero, faces, data);
            GreedyMesher.BuildSpecials(one, Vector3Int.zero, faces, data);
            var verts = new List<Vector3>(data.Verts.Count);
            foreach (var v in data.Verts) verts.Add(pointReflected ? v * -16f : new Vector3(v.x * 16f, v.y * 16f, -v.z * 16f));
            m = new Mesh { name = "block " + state };
            m.SetVertices(verts);
            m.SetUVs(0, data.Uvs);
            m.SetColors(data.Colors);
            m.subMeshCount = 2;
            m.SetTriangles(Flip(data.Opaque), 0, false);   // both frames reverse the handedness: flip the winding
            m.SetTriangles(Flip(data.Transparent), 1, false);
            m.RecalculateNormals();
            m.RecalculateBounds();
            Cache[(state, pointReflected)] = m;
            return m;
        }

        static List<int> Flip(List<int> t)
        {
            var r = new List<int>(t.Count);
            for (int i = 0; i + 2 < t.Count; i += 3) { r.Add(t[i]); r.Add(t[i + 2]); r.Add(t[i + 1]); }
            return r;
        }
    }
}
