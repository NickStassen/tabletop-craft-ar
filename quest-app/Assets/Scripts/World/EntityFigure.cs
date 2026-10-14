// One entity on the table: the GameObjects for its look (EntityCatalog), built from shared meshes and per-texture
// materials. Box models get one transform per model part (animated by EntityAnimation through per-part rotation deltas),
// items spin and bob, frames and paintings hang on their wall, unknown types are a box of their size. All in figure
// space (1 unit = 1 model pixel, the same frame as PlayerFigure); the renderer places, turns and scales the root.
using System.Collections.Generic;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class EntityFigure
    {
        public readonly GameObject Root;
        public readonly EntityLook Look;
        public TextMesh Label;
        /// <summary>Model pixels from the feet to the top of the figure (where the name label sits).</summary>
        public float TopPx;

        readonly EModel _model;
        readonly EntityRig _rig;
        Transform _modelRoot;
        Transform[] _parts;
        Quaternion[] _defaults;
        Vector3[] _defaultRad;
        float[] _rot;
        Transform _spin;                 // items: spinning node
        EntityInfo _info;                // model figures: the entity they were built for (armour stand pose, invisibility)
        readonly List<MeshRenderer> _renderers = new List<MeshRenderer>();
        readonly List<MeshRenderer> _body = new List<MeshRenderer>();   // the model and its second layer, not gear
        readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();
        Color _color = Color.white, _overlay = new Color(1, 0, 0, 0);
        EntityGear _gear;

        public EntityFigure(Transform parent, EntityInfo e, EntityLook look, EntityTextures tex)
        {
            Look = look;
            Root = new GameObject($"entity {e.Path} {e.Id}");
            Root.transform.SetParent(parent, false);
            switch (look.Kind)
            {
                case EntityKind.Model:
                    _model = EntityModels.Get(look.Model);
                    if (_model == null) { BuildBox(e, tex); break; }
                    _rig = EntityRig.For(_model);
                    _info = e;
                    BuildModel(tex);
                    break;
                case EntityKind.Item: BuildItem(e, tex); break;
                case EntityKind.Block: BuildBlock(look.Item, tex, 16f, new Vector3(0, 8, 0)); TopPx = 16; break;
                case EntityKind.Frame: BuildFrame(e, tex); break;
                case EntityKind.Painting: BuildPainting(e, tex); break;
                default: BuildBox(e, tex); break;
            }
            if (!string.IsNullOrEmpty(e.Name)) AddLabel(e.Name);
        }

        // ---------- box models ----------

        void BuildModel(EntityTextures tex)
        {
            var mat = tex.Material(tex.Get(Look.Texture, Look.Overlays, Look.Tint.r == 255 && Look.Tint.g == 255 && Look.Tint.b == 255 ? (Color32?)null : Look.Tint));
            _modelRoot = new GameObject("model").transform;
            _modelRoot.SetParent(Root.transform, false);
            // The game renderer's transforms before the model (EntityCatalog.RootOffset/RootRotation); minecarts and
            // projectiles follow their pitch in Animate.
            _modelRoot.localPosition = EntityCatalog.RootOffset(Look.Root);
            _modelRoot.localRotation = RootRotation(Look.Root, 0);
            int n = _model.Parts.Count;
            _parts = new Transform[n];
            _defaults = new Quaternion[n];
            _defaultRad = new Vector3[n];
            _rot = new float[3 * n];
            foreach (var p in _model.Parts)
            {
                var t = p.Parent == null ? _modelRoot : new GameObject(p.Name).transform;
                if (p.Parent != null)
                {
                    t.SetParent(_parts[p.Parent.Index], false);
                    EntityModels.Pivot(p, out float x, out float y, out float z);
                    t.localPosition = new Vector3(x, y, z);
                }
                else
                {
                    // The root part's own pose (villagers, horses: offset and scale for the size the game draws them at).
                    EntityModels.Pivot(p, out float x, out float y, out float z);
                    var holder = new GameObject("root").transform;
                    holder.SetParent(_modelRoot, false);
                    holder.localPosition = new Vector3(x, y, z);
                    t = holder;
                }
                _defaultRad[p.Index] = new Vector3(p.Rx, p.Ry, p.Rz);
                _defaults[p.Index] = Rot(p.Rx, p.Ry, p.Rz);
                t.localRotation = _defaults[p.Index];
                t.localScale = new Vector3(p.Sx, p.Sy, p.Sz);
                _parts[p.Index] = t;
                bool hidden = Look.HideArms && (p.Name == "right_arm" || p.Name == "left_arm");
                // Armour stands: arms and base plate only when the stand has them, never the humanoid hat.
                if (Look.ArmorStand && EntityCatalog.StandPart(_info, p.Name, 0, out _, out bool shown) && !shown) hidden = true;
                var mesh = EntityMeshes.Part(_model, p);
                if (mesh != null && !hidden) { AddRenderer(t.gameObject, mesh, mat); _body.Add(_renderers[_renderers.Count - 1]); }
            }
            if (Look.ExtraModel != null)
            {
                // A second, inflated layer on the same parts (sheep wool, stray/drowned/bogged clothes, slime shell).
                var extra = EntityModels.Get(Look.ExtraModel);
                var etex = tex.Get(Look.ExtraTexture, null, Look.ExtraTint.r == 255 && Look.ExtraTint.g == 255 && Look.ExtraTint.b == 255 ? (Color32?)null : Look.ExtraTint);
                var emat = tex.Material(etex, Look.ExtraTranslucent ? 1 : 0);
                if (extra != null)
                    foreach (var p in extra.Parts)
                    {
                        var mesh = EntityMeshes.Part(extra, p);
                        if (mesh == null) continue;
                        var host = p.Parent == null ? _parts[0] : Part(p.Name);
                        if (host == null) continue;
                        var go = new GameObject(p.Name + " layer");
                        go.transform.SetParent(host, false);
                        // The layer's own pivot can differ from the host part's only by its default rotation (wool body).
                        var hp = _model.Find(p.Name);
                        if (hp != null && (hp.Rx != p.Rx || hp.Ry != p.Ry || hp.Rz != p.Rz))
                            go.transform.localRotation = Quaternion.Inverse(_defaults[hp.Index]) * Rot(p.Rx, p.Ry, p.Rz);
                        AddRenderer(go, mesh, emat);
                        _body.Add(_renderers[_renderers.Count - 1]);
                    }
            }
            if (Look.Contents != null) BuildBlock(Look.Contents, tex, 12f, new Vector3(0, 10.5f, 0));
            if (Look.ArmorStand) PoseStand(0);
            TopPx = Look.Root == RootKind.Living ? Height() : 16;
            // LivingEntityRenderer: an invisible mob's model and its layers (wool, clothes, slime shell) aren't drawn;
            // its armour and held items are (EntityGear hangs them on the same, still animated, part transforms).
            if (_info.Invisible) foreach (var r in _body) r.enabled = false;
        }

        /// <summary>Armour stands: the pose replaces the animated rotations (EntityCatalog.StandPart).</summary>
        void PoseStand(float yaw)
        {
            foreach (var p in _model.Parts)
                if (EntityCatalog.StandPart(_info, p.Name, yaw, out var deg, out _))
                    _parts[p.Index].localRotation = Rot(deg.x * Mathf.Deg2Rad, deg.y * Mathf.Deg2Rad, deg.z * Mathf.Deg2Rad);
        }

        /// <summary>Game rotation Z * Y * X (radians) as a Unity quaternion (same numbers, see PlayerModel).</summary>
        static Quaternion Rot(float x, float y, float z) =>
            Quaternion.AngleAxis(z * Mathf.Rad2Deg, Vector3.forward) * Quaternion.AngleAxis(y * Mathf.Rad2Deg, Vector3.up) * Quaternion.AngleAxis(x * Mathf.Rad2Deg, Vector3.right);

        static Quaternion RootRotation(RootKind k, float pitch)
        {
            EntityCatalog.RootRotation(k, pitch, out float yaw, out float roll);
            return Quaternion.AngleAxis(yaw, Vector3.up) * Quaternion.AngleAxis(roll, Vector3.forward);
        }

        static readonly Vector3 CrystalAxis = new Vector3(1, 0, 1).normalized;

        /// <summary>End crystal glass: spin and bob (CrystalAnimation, EndCrystalModel.setupAnim).</summary>
        void AnimateCrystal(float age)
        {
            var outer = _model.Find("outer_glass");
            if (outer == null) return;
            float spin = CrystalAnimation.SpinDeg(age);
            var tilt = Quaternion.AngleAxis(CrystalAnimation.TiltDeg, CrystalAxis);
            var t = _parts[outer.Index];
            EntityModels.Pivot(outer, out float x, out float y, out float z);
            t.localPosition = new Vector3(x, y - CrystalAnimation.BobPx(age), z);   // figure space: the pivot is point-reflected
            t.localRotation = Quaternion.AngleAxis(spin, Vector3.up) * tilt;
            foreach (var name in new[] { "inner_glass", "cube" })
                if (_model.Find(name) is EModelPart p) _parts[p.Index].localRotation = tilt * Quaternion.AngleAxis(spin, Vector3.up);
        }

        float Height()
        {
            float top = 0;
            foreach (var r in _renderers)
            {
                var b = r.GetComponent<MeshFilter>().sharedMesh.bounds;
                var w = Root.transform.InverseTransformPoint(r.transform.TransformPoint(b.center));
                top = Mathf.Max(top, w.y + b.extents.y);
            }
            return top;
        }

        public Transform Part(string name)
        {
            var p = _model?.Find(name);
            return p == null ? null : _parts[p.Index];
        }

        public bool Humanoid => _rig != null && _rig.Humanoid;

        /// <summary>Applies one frame of animation (rotation deltas on the default pose of each animated part).</summary>
        public void Animate(in EntityAnimInput a, float pitchDeg)
        {
            if (_rig != null && Look.ArmorStand) { PoseStand(a.BodyYaw); return; }
            if (_rig != null)
            {
                EntityAnimation.Pose(_rig, a, _rot);
                for (int i = 0; i < _parts.Length; i++)
                {
                    if (_rig.Roles[i] == PartRole.None) continue;
                    var d = _defaultRad[i];
                    _parts[i].localRotation = Rot(d.x + _rot[3 * i], d.y + _rot[3 * i + 1], d.z + _rot[3 * i + 2]);
                }
                if (Look.Root == RootKind.Minecart || Look.Root == RootKind.Projectile || Look.Root == RootKind.Trident)
                    _modelRoot.localRotation = RootRotation(Look.Root, pitchDeg);
                if (Look.Root == RootKind.Crystal) AnimateCrystal(a.AgeTicks);
            }
            if (_spin != null)
            {
                // ItemEntityRenderer: bob 0.1 * sin(age / 10) + 0.1 blocks, spin age / 20 radians.
                float bob = (Mathf.Sin(a.AgeTicks / 10f) * 0.1f + 0.1f) * 16f;
                _spin.localPosition = new Vector3(0, bob, 0);
                _spin.localRotation = Quaternion.AngleAxis(-a.AgeTicks / 20f * Mathf.Rad2Deg, Vector3.up);
            }
        }

        // ---------- items, blocks, frames, paintings, boxes ----------

        void BuildItem(EntityInfo e, EntityTextures tex)
        {
            _spin = new GameObject("spin").transform;
            _spin.SetParent(Root.transform, false);
            var v = tex.Item(Look.Item);
            var d = v.Ground.Set ? v.Ground : new ItemDisplay { Sx = 0.5f, Sy = 0.5f, Sz = 0.5f, Ty = 2 };
            // ItemEntityRenderer lifts the item by a quarter of its ground scale, then applies the ground display.
            int copies = e.Count >= 49 ? 5 : e.Count >= 33 ? 4 : e.Count >= 17 ? 3 : e.Count > 1 ? 2 : 1;
            for (int i = 0; i < copies; i++)
            {
                var node = ItemNode(_spin, v, tex, true);
                if (node == null) break;
                // Copies spread a little (the game uses a seeded random per stack).
                float ox = i == 0 ? 0 : ((i * 37) % 7 - 3) * 0.5f, oz = i == 0 ? 0 : ((i * 53) % 7 - 3) * 0.5f;
                // Centre: a quarter of the ground scale up plus the display translation (the model is centred on it).
                node.localPosition = new Vector3(ox + d.Tx, 4f * d.Sy + d.Ty, oz - d.Tz);
                node.localScale = new Vector3(d.Sx, d.Sy, d.Sz);
            }
            TopPx = 16f * d.Sy + 8;
        }

        /// <summary>A sprite or cube for an item under parent (centred, 16 px). gameFrame: the parent is Z-mirrored game
        /// space (items on the ground, frames), so the point-reflected mesh is turned 180 degrees about Z to undo it.</summary>
        public static Transform ItemNode(Transform parent, ItemVisual v, EntityTextures tex, bool gameFrame, List<MeshRenderer> renderers = null)
        {
            Mesh mesh; Texture2D t;
            if (v.Kind == ItemVisualKind.Sprite) { mesh = EntityMeshes.Sprite; t = tex.Sprite(v); }
            else if (v.Kind == ItemVisualKind.Cube) { mesh = EntityMeshes.ItemCube; t = tex.Strip(v); }
            else return null;
            if (t == null) return null;
            var node = new GameObject("item " + v.Item).transform;
            node.SetParent(parent, false);
            var meshGo = gameFrame ? new GameObject("mesh") : node.gameObject;
            if (gameFrame) { meshGo.transform.SetParent(node, false); meshGo.transform.localRotation = Quaternion.AngleAxis(180, Vector3.forward); }
            var mf = meshGo.AddComponent<MeshFilter>(); mf.sharedMesh = mesh;
            var mr = meshGo.AddComponent<MeshRenderer>(); mr.sharedMaterial = tex.Material(t, v.Kind == ItemVisualKind.Sprite ? 2 : 0);
            Quiet(mr);
            renderers?.Add(mr);
            return node;
        }

        void BuildBlock(string item, EntityTextures tex, float sizePx, Vector3 centre)
        {
            var v = tex.Item(item);
            if (v.Kind != ItemVisualKind.Cube) v = tex.Item("minecraft:oak_planks");
            var node = ItemNode(Root.transform, v, tex, true, _renderers);
            if (node == null) return;
            node.localPosition = centre;
            node.localScale = Vector3.one * (sizePx / 16f);
        }

        /// <summary>Unity direction of a hanging entity's facing (game +Z = south -> Unity -Z).</summary>
        static Vector3 FacingDir(string facing)
        {
            switch (facing)
            {
                case "north": return new Vector3(0, 0, 1);
                case "south": return new Vector3(0, 0, -1);
                case "east": return new Vector3(1, 0, 0);
                case "west": return new Vector3(-1, 0, 0);
                case "up": return Vector3.up;
                case "down": return Vector3.down;
                default: return new Vector3(0, 0, -1);
            }
        }

        static Quaternion Facing(string facing)
        {
            var n = FacingDir(facing);
            return Quaternion.LookRotation(-n, Mathf.Abs(n.y) > 0.5f ? new Vector3(0, 0, 1) : Vector3.up);   // the quad's front (-Z local) faces n
        }

        void BuildFrame(EntityInfo e, EntityTextures tex)
        {
            // ItemFrameRenderer: a 12x12 px frame, 1 px deep, against the wall; the item at half size in front of it.
            var holder = new GameObject("frame").transform;
            holder.SetParent(Root.transform, false);
            holder.localRotation = Facing(e.Facing);
            if (!e.Invisible)
            {
                var frame = new GameObject("board");
                frame.transform.SetParent(holder, false);
                frame.transform.localScale = new Vector3(12f / 16f, 12f / 16f, 1f / 16f);
                frame.transform.localRotation = Quaternion.AngleAxis(180, Vector3.forward);   // undo the mesh's point reflection
                AddRenderer(frame, EntityMeshes.Unit, tex.Material(tex.Get(Look.Texture)));
            }
            var v = tex.Item(Look.Item);
            if (v.Kind != ItemVisualKind.None)
            {
                var spin = new GameObject("rotation").transform;
                spin.SetParent(holder, false);
                spin.localPosition = new Vector3(0, 0, e.Invisible ? 0 : -0.6f);   // the entity sits on the wall; local -Z points out
                spin.localRotation = Quaternion.AngleAxis(e.Rotation * 45f, Vector3.forward);
                var node = ItemNode(spin, v, tex, true, _renderers);
                if (node != null) node.localScale = Vector3.one * (v.Kind == ItemVisualKind.Cube ? 0.25f : 0.5f);
            }
            TopPx = 8;
        }

        void BuildPainting(EntityInfo e, EntityTextures tex)
        {
            var t = tex.Get(Look.Texture);
            float w = t != null ? t.width : 16, h = t != null ? t.height : 16;   // vanilla art: 16 texture px per block
            var holder = new GameObject("painting").transform;
            holder.SetParent(Root.transform, false);
            holder.localRotation = Facing(e.Facing);
            var board = new GameObject("canvas");
            board.transform.SetParent(holder, false);
            board.transform.localScale = new Vector3(w / 16f, h / 16f, 1f / 16f);
            board.transform.localRotation = Quaternion.AngleAxis(180, Vector3.forward);   // upright: undo the mesh's point reflection
            AddRenderer(board, EntityMeshes.Unit, tex.Material(t));
            TopPx = h / 2;
        }

        void BuildBox(EntityInfo e, EntityTextures tex)
        {
            float w = Mathf.Max(0.1f, e.Width) * 16f, h = Mathf.Max(0.1f, e.Height) * 16f;
            var go = new GameObject("box");
            go.transform.SetParent(Root.transform, false);
            go.transform.localPosition = new Vector3(0, h / 2, 0);
            go.transform.localScale = new Vector3(w / 16f, h / 16f, w / 16f);
            AddRenderer(go, EntityMeshes.Unit, tex.Material(tex.White));
            // Without its model (no entity_models.json, or an unknown type) a box of the server's bounding box, in the
            // average colour of the entity's pack texture when there is one (a pig is pink, a boat wooden), else a
            // stable colour per type.
            var avg = Look.Texture != null ? tex.Average(Look.Texture) : null;
            _boxColor = avg ?? Look.BoxColor;
            TopPx = h;
        }

        void AddLabel(string text)
        {
            var go = new GameObject("label");
            go.transform.SetParent(Root.transform, false);
            go.transform.localPosition = new Vector3(0, TopPx + 6f, 0);
            Label = go.AddComponent<TextMesh>();
            TabletopApp.SetBuiltinFont(Label);
            Label.text = text; Label.anchor = TextAnchor.LowerCenter; Label.characterSize = 1.44f; Label.fontSize = 48; Label.color = Color.white;
        }

        void AddRenderer(GameObject go, Mesh mesh, Material mat)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            Quiet(r);
            _renderers.Add(r);
        }

        public static void Quiet(MeshRenderer r)
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        // ---------- colours, gear ----------

        /// <summary>Tint (world light; box colour) and overlay (hurt flash). Only touches the renderers when it changes.</summary>
        public void SetColors(Color light, Color overlay)
        {
            var c = new Color(light.r * _base.r, light.g * _base.g, light.b * _base.b, 1);
            if (c == _color && overlay == _overlay && _applied) return;
            _color = c; _overlay = overlay; _applied = true;
            _mpb.Clear();
            _mpb.SetColor(EntityTextures.ColorId, c);
            _mpb.SetColor(EntityTextures.OverlayId, overlay);
            foreach (var r in _renderers) if (r != null) r.SetPropertyBlock(_mpb);
            _gear?.SetColors(c, overlay);
        }
        bool _applied;
        Color _boxColor = Color.white;
        Color _base => _boxColor;

        /// <summary>Held items and armour of humanoid mobs (rebuilt when the equipment changes).</summary>
        public void SetEquipment(Dictionary<string, string> eq, EntityTextures tex)
        {
            if (_model == null || !Humanoid) return;
            if (_gear == null) _gear = new EntityGear(tex, Part);
            _gear.Set(eq);
            _applied = false;
        }

        public void Destroy() { _gear?.Clear(); if (Root != null) Object.Destroy(Root); }
    }
}
