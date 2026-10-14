// [comfort] Markers on the table itself (docs/QUEST_SETUP.md "Finding your way"):
//   - a small "N" just outside the visible window's north edge (Minecraft north = -Z = the anchor's +Z after the
//     mirror), turned to face you;
//   - an "Offline: reconnecting..." badge on the window's edge toward you while the link is down (players and entities
//     are hidden meanwhile; the terrain stays as it was).
// Children of the TabletopAnchor, which sits at the window's bottom centre (NavController.Place), so they follow
// drags, turns and zoom.
using System;
using TabletopCraft.World;
using UnityEngine;

namespace TabletopCraft.XR
{
    public sealed class TableMarkers : MonoBehaviour
    {
        public Transform Anchor, Head;
        /// <summary>Half the visible window on the table in metres (x, z); zero while nothing is loaded.</summary>
        public Func<Vector2> HalfSize;
        /// <summary>The badge text, or null to hide it.</summary>
        public Func<string> Badge;

        Transform _north, _badge, _badgeBg;
        TextMesh _northText, _badgeText;
        MeshRenderer _badgeRenderer;

        void Start()
        {
            _north = new GameObject("NorthMarker").transform;
            _north.SetParent(Anchor, false);
            _northText = NewText(_north, 0.0032f, new Color(1f, 0.35f, 0.3f));
            _northText.text = "N";
            _northText.fontStyle = FontStyle.Bold;
            _badge = new GameObject("OfflineBadge").transform;
            _badge.SetParent(Anchor, false);
            var bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(bg.GetComponent<Collider>());
            bg.transform.SetParent(_badge, false);
            bg.GetComponent<MeshRenderer>().sharedMaterial = new Material(Shader.Find("Sprites/Default")) { color = new Color(0.35f, 0.08f, 0.05f, 0.85f), renderQueue = 3000 };
            _badgeBg = bg.transform;
            _badgeText = NewText(_badge, 0.0016f, new Color(1f, 0.85f, 0.6f));
            _badgeRenderer = _badgeText.GetComponent<MeshRenderer>();
            _north.gameObject.SetActive(false);
            _badge.gameObject.SetActive(false);
        }

        static TextMesh NewText(Transform parent, float size, Color c)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMesh>();
            TabletopApp.SetBuiltinFont(t);
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = new Material(r.sharedMaterial) { renderQueue = 3002 };
            t.characterSize = size; t.fontSize = 48; t.anchor = TextAnchor.LowerCenter; t.alignment = TextAlignment.Center; t.color = c;
            return t;
        }

        /// <summary>Turn about the anchor's up axis to face the head.</summary>
        void Face(Transform t)
        {
            if (!Head) return;
            Vector3 d = Vector3.ProjectOnPlane(t.position - Head.position, Anchor.up);
            if (d.sqrMagnitude > 1e-6f) t.rotation = Quaternion.LookRotation(d, Anchor.up);
        }

        void LateUpdate()
        {
            Vector2 half = HalfSize != null ? HalfSize() : Vector2.zero;
            bool loaded = half.x > 0 && half.y > 0;
            if (_north.gameObject.activeSelf != loaded) _north.gameObject.SetActive(loaded);
            if (loaded)
            {
                _north.localPosition = new Vector3(0, 0.002f, half.y + 0.008f);
                Face(_north);
            }
            string badge = Badge?.Invoke();
            bool show = loaded && !string.IsNullOrEmpty(badge);
            if (_badge.gameObject.activeSelf != show) _badge.gameObject.SetActive(show);
            if (!show || !Head) return;
            if (_badgeText.text != badge) _badgeText.text = badge;
            Vector3 h = Anchor.InverseTransformPoint(Head.position);
            var e = NavState.EdgePoint(half.x + 0.02f, half.y + 0.02f, h.x, h.z);
            _badge.localPosition = new Vector3(e.x, 0.01f, e.z);
            Face(_badge);
            var b = _badgeRenderer.localBounds;
            _badgeBg.localPosition = new Vector3(b.center.x, b.center.y, 0.001f);
            _badgeBg.localScale = new Vector3(b.size.x + 0.01f, b.size.y + 0.006f, 1f);
        }
    }
}
