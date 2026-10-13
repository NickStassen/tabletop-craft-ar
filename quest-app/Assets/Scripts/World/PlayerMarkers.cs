// Draws Java players from player_positions as small capsules with name labels, lerped toward the 10 Hz samples.
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

        sealed class Marker { public GameObject Go; public TextMesh Label; public Vector3 Target; public float Yaw; public float LastSeen; }
        readonly Dictionary<string, Marker> _markers = new Dictionary<string, Marker>();

        public void Apply(List<PlayerInfo> players)
        {
            var seen = new HashSet<string>();
            foreach (var p in players)
            {
                seen.Add(p.Uuid);
                if (!_markers.TryGetValue(p.Uuid, out var m)) { m = Create(p.Name); _markers[p.Uuid] = m; m.Go.transform.localPosition = ToLocal(p.Pos); }
                m.Target = ToLocal(p.Pos);
                m.Yaw = p.Yaw;
                m.LastSeen = Time.time;
                m.Go.transform.localScale = new Vector3(0.6f, p.Sneaking ? 0.75f : 0.9f, 0.6f) * BlockSizeM;
            }
            var gone = new List<string>();
            foreach (var kv in _markers) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var k in gone) { Destroy(_markers[k].Go); _markers.Remove(k); }
        }

        /// <summary>World feet position -> RegionRoot-local Unity metres (Z mirrored); capsule centre is 0.9 blocks up.</summary>
        Vector3 ToLocal(Vector3 world)
        {
            if (Model == null) return Vector3.zero;
            Vector3 l = world - (Vector3)Model.Origin;
            return new Vector3(l.x, l.y + 0.9f, -l.z) * BlockSizeM;
        }

        Marker Create(string name)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Destroy(go.GetComponent<Collider>());
            go.name = "player " + name;
            go.transform.SetParent(transform, false);
            go.GetComponent<Renderer>().material.color = new Color(1f, 0.55f, 0.1f);
            var labelGo = new GameObject("label");
            labelGo.transform.SetParent(go.transform, false);
            labelGo.transform.localPosition = new Vector3(0, 1.6f, 0);
            var t = labelGo.AddComponent<TextMesh>();
            t.text = name; t.anchor = TextAnchor.LowerCenter; t.characterSize = 0.1f; t.fontSize = 48; t.color = Color.white;
            return new Marker { Go = go, Label = t };
        }

        void Update()
        {
            var cam = Camera.main;
            foreach (var m in _markers.Values)
            {
                m.Go.transform.localPosition = Vector3.Lerp(m.Go.transform.localPosition, m.Target, 1 - Mathf.Exp(-LerpRate * Time.deltaTime));
                // Minecraft yaw 0 faces +Z (south) = Unity -Z after the mirror (Euler Y 180); yaw 90 faces west = Unity -X (Euler Y 270).
                m.Go.transform.localRotation = Quaternion.Euler(0, 180f + m.Yaw, 0);
                if (cam != null) m.Label.transform.rotation = Quaternion.LookRotation(m.Label.transform.position - cam.transform.position);
            }
        }
    }
}
