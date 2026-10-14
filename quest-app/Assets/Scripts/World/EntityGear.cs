// What a player or humanoid mob holds and wears: item sprites/cubes in the hands, placed like the game's ItemInHandLayer
// (arm -> rotate X -90, Y 180 -> 1 px sideways, 2 px down, 10 px forward -> the item model's thirdperson_righthand
// display transform; mirrored for the left hand), and armour as the game's inflated humanoid armour layers
// ("player#helmet" etc. from entity_models.json) with the pack's equipment textures, hung on the wearer's parts by name.
// Works in figure space (point-reflected game model space), where the game's rotation numbers carry over unchanged.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class EntityGear
    {
        readonly EntityTextures _tex;
        readonly Func<string, Transform> _part;   // "head", "body", "right_arm", ... -> the wearer's part transform
        readonly List<GameObject> _objects = new List<GameObject>();
        readonly List<MeshRenderer> _renderers = new List<MeshRenderer>();
        readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();
        Dictionary<string, string> _current;
        int _generation = -1;
        Color _color = Color.white, _overlay = new Color(1, 0, 0, 0);

        static readonly (string slot, string layer)[] ArmorSlots = { ("head", "player#helmet"), ("chest", "player#chestplate"), ("legs", "player#leggings"), ("feet", "player#boots") };

        public EntityGear(EntityTextures tex, Func<string, Transform> part) { _tex = tex; _part = part; }

        /// <summary>Rebuilds when the equipment changed (slot -> item key; null = nothing).</summary>
        public void Set(Dictionary<string, string> eq)
        {
            if (Same(eq, _current) && _generation == EntityModels.Generation) return;
            _generation = EntityModels.Generation;   // armour layers come with entity_models.json: rebuild when it loads
            Clear();
            _current = eq == null ? null : new Dictionary<string, string>(eq);
            if (eq == null) return;
            foreach (var (slot, layer) in ArmorSlots)
                if (eq.TryGetValue(slot, out var item)) Armor(item, slot, layer);
            if (eq.TryGetValue("main_hand", out var main)) Held(main, false);
            if (eq.TryGetValue("off_hand", out var off)) Held(off, true);
            Apply();
        }

        static bool Same(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            if ((a == null || a.Count == 0) && (b == null || b.Count == 0)) return true;
            if (a == null || b == null || a.Count != b.Count) return false;
            foreach (var kv in a) if (!b.TryGetValue(kv.Key, out var v) || v != kv.Value) return false;
            return true;
        }

        void Held(string item, bool left)
        {
            var arm = _part(left ? "left_arm" : "right_arm");
            if (arm == null) return;
            var v = _tex.Item(item);
            if (v.Kind == ItemVisualKind.None) return;
            var turn = new GameObject(left ? "left hand" : "right hand").transform;
            turn.SetParent(arm, false);
            turn.localRotation = Quaternion.AngleAxis(-90, Vector3.right) * Quaternion.AngleAxis(180, Vector3.up);
            var d = v.ThirdPersonRight.Set ? v.ThirdPersonRight : new ItemDisplay { Sx = 0.55f, Sy = 0.55f, Sz = 0.55f, Ty = 3, Tz = 1 };
            float side = left ? -1 : 1, tx = left ? -d.Tx : d.Tx, ry = left ? -d.Ry : d.Ry, rz = left ? -d.Rz : d.Rz;
            var hold = new GameObject("display").transform;
            hold.SetParent(turn, false);
            // Game offsets (blocks) become -16x pixels in figure space; rotations keep their numbers.
            hold.localPosition = new Vector3(-side - tx, -2 - d.Ty, 10 - d.Tz);
            hold.localRotation = Quaternion.AngleAxis(d.Rx, Vector3.right) * Quaternion.AngleAxis(ry, Vector3.up) * Quaternion.AngleAxis(rz, Vector3.forward);
            hold.localScale = new Vector3(d.Sx, d.Sy, d.Sz);
            var node = EntityFigure.ItemNode(hold, v, _tex, false, _renderers);
            _objects.Add(turn.gameObject);
            if (node == null) return;
        }

        void Armor(string item, string slot, string layer)
        {
            string texture = EntityCatalog.ArmorTexture(item, slot, out bool dyeable);
            var model = EntityModels.Get(layer);
            if (texture == null || model == null) return;
            // Leather takes the item's dye colour (its default comes from the pack's items/*.json) under an undyed overlay.
            var tex = _tex.Get(texture, dyeable ? new List<string> { texture + "_overlay" } : null, dyeable ? _tex.Item(item).SpriteTint : (Color32?)null);
            if (tex == null) return;
            var mat = _tex.Material(tex);
            foreach (var p in model.Parts)
            {
                var mesh = EntityMeshes.Part(model, p);
                if (mesh == null) continue;
                var host = _part(p.Name == "hat" ? "head" : p.Name);
                if (host == null) continue;
                var go = new GameObject("armor " + slot + " " + p.Name);
                go.transform.SetParent(host, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = mat;
                EntityFigure.Quiet(r);
                _renderers.Add(r);
                _objects.Add(go);
            }
        }

        public void SetColors(Color c, Color overlay)
        {
            if (c == _color && overlay == _overlay) return;
            _color = c; _overlay = overlay;
            Apply();
        }

        void Apply()
        {
            if (_renderers.Count == 0) return;
            _mpb.Clear();
            _mpb.SetColor(EntityTextures.ColorId, _color);
            _mpb.SetColor(EntityTextures.OverlayId, _overlay);
            foreach (var r in _renderers) if (r != null) r.SetPropertyBlock(_mpb);
        }

        public void Clear()
        {
            foreach (var o in _objects) if (o != null) UnityEngine.Object.Destroy(o);
            _objects.Clear();
            _renderers.Clear();
            _current = null;
        }
    }
}
