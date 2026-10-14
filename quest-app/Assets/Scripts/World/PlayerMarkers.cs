// Draws Java players from player_positions as Minecraft-style figures (PlayerFigure) with their skins and name
// labels. Positions, yaw and pitch ease toward the 10 Hz samples; PlayerAnimation turns the eased motion into limb
// swing, body yaw lag and the sneak pose. Works in Minecraft world coordinates and converts to RegionRoot-local
// Unity metres (Z mirrored) at the end, so a region change doesn't make figures jump.
using System.Collections.Generic;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class PlayerMarkers : MonoBehaviour
    {
        public WorldModel Model;
        public float BlockSizeM = 0.01f;
        public float LerpRate = 12f;
        [Tooltip("Unlit/Color material (no longer used by the figures; kept for TabletopApp).")]
        public Material MarkerMaterial;
        [Tooltip("TabletopCraft/PlayerSkin material; loaded from Resources/TabletopPlayer if empty.")]
        public Material SkinMaterial;
        /// <summary>Source of the default skins (assets/minecraft/textures/entity/player/... in the user's pack.zip).</summary>
        public ResourcePackAtlas Atlas;

        sealed class Marker
        {
            public PlayerFigure Fig;
            public readonly PlayerAnimation Anim = new PlayerAnimation();
            public Vector3 Target, Shown;          // Minecraft world coordinates of the feet
            public float TargetYaw, Yaw, TargetPitch, Pitch;
            public bool Sneaking;
            public string SkinUrl;                 // URL the figure wears (or is downloading)
        }

        readonly Dictionary<string, Marker> _markers = new Dictionary<string, Marker>();
        // The server sends skin/skin_model only the first time it lists a UUID on this connection; keep them so a
        // player who walks out of range and back keeps their skin.
        readonly Dictionary<string, (string url, bool slim)> _skinInfo = new Dictionary<string, (string, bool)>();
        readonly HashSet<string> _seen = new HashSet<string>();
        readonly List<string> _gone = new List<string>();
        PlayerSkins _skins;

        void Awake()
        {
            if (!SkinMaterial) SkinMaterial = Resources.Load<Material>("TabletopPlayer");
            if (!SkinMaterial) Debug.LogWarning("[Tabletop] Resources/TabletopPlayer material missing; player figures fall back to Unlit/Texture");
            if (!SkinMaterial) SkinMaterial = new Material(Shader.Find("Unlit/Texture"));
        }

        public void Apply(List<PlayerInfo> players)
        {
            if (_skins == null) _skins = new PlayerSkins { Atlas = Atlas };
            _seen.Clear();
            foreach (var p in players)
            {
                if (string.IsNullOrEmpty(p.Uuid)) continue;
                _seen.Add(p.Uuid);
                if (p.HasSkin) _skinInfo[p.Uuid] = (p.Skin, p.SkinModel == "slim");
                if (!_markers.TryGetValue(p.Uuid, out var m))
                {
                    m = new Marker { Shown = p.Pos, Yaw = p.Yaw, Pitch = p.Pitch };
                    var tex = _skins.DefaultFor(p.Uuid, out bool slim);
                    m.Fig = new PlayerFigure(transform, p.Name, SkinMaterial, tex, slim);
                    _markers[p.Uuid] = m;
                }
                m.Target = p.Pos; m.TargetYaw = p.Yaw; m.TargetPitch = p.Pitch; m.Sneaking = p.Sneaking;
                if (_skinInfo.TryGetValue(p.Uuid, out var s) && s.url != m.SkinUrl && !string.IsNullOrEmpty(s.url))
                {
                    m.SkinUrl = s.url;
                    var fig = m.Fig; string url = s.url; bool slimSkin = s.slim;
                    // Download off the critical path; the figure keeps its default skin until then.
                    _skins.Fetch(url, t => { if (fig.Root != null && m.SkinUrl == url) fig.SetSkin(t, slimSkin); });
                }
            }
            _gone.Clear();
            foreach (var kv in _markers) if (!_seen.Contains(kv.Key)) _gone.Add(kv.Key);
            foreach (var k in _gone) { _markers[k].Fig.Destroy(); _markers.Remove(k); }
        }

        /// <summary>World feet position -> RegionRoot-local Unity metres (Z mirrored).</summary>
        Vector3 ToLocal(Vector3 world)
        {
            if (Model == null) return Vector3.zero;
            Vector3 l = world - (Vector3)Model.Origin;
            return new Vector3(l.x, l.y, -l.z) * BlockSizeM;
        }

        void Update()
        {
            var cam = Camera.main;
            float dt = Time.deltaTime, k = 1 - Mathf.Exp(-LerpRate * dt);
            float scale = BlockSizeM / 16f;
            foreach (var m in _markers.Values)
            {
                Vector3 before = m.Shown;
                m.Shown = Vector3.Lerp(m.Shown, m.Target, k);
                m.Yaw = Mathf.LerpAngle(m.Yaw, m.TargetYaw, k);
                m.Pitch = Mathf.Lerp(m.Pitch, m.TargetPitch, k);
                Vector3 d = m.Shown - before;
                // A teleport shouldn't make the figure sprint across the table.
                if ((m.Target - m.Shown).sqrMagnitude > 16f * 16f) { m.Shown = m.Target; d = Vector3.zero; }
                var pose = m.Anim.Step(dt, d.x, d.z, m.Yaw, m.Pitch, m.Sneaking);
                var root = m.Fig.Root.transform;
                root.localPosition = ToLocal(m.Shown);
                // Minecraft yaw 0 faces +Z (south) = Unity -Z after the mirror (Euler Y 180); yaw 90 faces west = Unity -X.
                root.localRotation = Quaternion.Euler(0, 180f + pose.BodyYaw, 0);
                root.localScale = Vector3.one * scale;
                m.Fig.Apply(pose);
                if (cam != null) m.Fig.Label.transform.rotation = Quaternion.LookRotation(m.Fig.Label.transform.position - cam.transform.position);
            }
        }

        void OnDestroy() { _skins?.Dispose(); }
    }
}
