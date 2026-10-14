// What stays in view without the old full-width status text (docs/QUEST_SETUP.md "What's where"):
//   - toasts: short, small, fading messages low in the view (link lost/restored, server errors, "area set"...),
//     from World/HudState.cs's ToastQueue;
//   - the area-setup panel: while area setup (Y) runs, its size, estimate, warnings and "A confirm  B cancel" on a
//     small panel above the left hand instead of head-locked;
//   - optionally ("Show HUD" in the menu's Info tab, or tabletop.json "show_hud": true) a small head-locked status,
//     low and to the left, about a third of the old text's size.
// The full status is in the block menu's Info tab (XR/BlockMenu.cs).
using System;
using TabletopCraft.World;
using UnityEngine;

namespace TabletopCraft.XR
{
    public sealed class Hud : MonoBehaviour
    {
        public Transform Head, LeftHand;
        public Func<string> StatusText;       // the full status (shown small when ShowHud)
        public Func<bool> AreaActive;
        public Func<string> AreaText;
        public bool ShowHud;
        public readonly ToastQueue Toasts = new ToastQueue();

        TextMesh _status, _toasts, _area;
        Transform _areaPanel, _areaBg;
        MeshRenderer _areaRenderer;
        float _statusAt;
        int _toastVersion = -1;

        void Start()
        {
            if (Head)
            {
                // About a third of the old status text's area (0.008 -> 0.0045 character size), low and to the left.
                _status = NewText("HudStatus", Head, new Vector3(-0.40f, -0.20f, 0.8f), 0.0045f, TextAnchor.UpperLeft, new Color(0.6f, 1f, 1f, 0.9f));
                _toasts = NewText("Toasts", Head, new Vector3(0f, -0.30f, 0.8f), 0.0045f, TextAnchor.LowerCenter, Color.white);
                _toasts.richText = true;
                _toasts.alignment = TextAlignment.Center;
            }
            _areaPanel = new GameObject("AreaSetupPanel").transform;
            var bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(bg.GetComponent<Collider>());
            bg.name = "Background";
            bg.transform.SetParent(_areaPanel, false);
            bg.transform.localPosition = new Vector3(0, 0, 0.001f);
            bg.transform.localScale = new Vector3(0.24f, 0.085f, 1f);
            bg.GetComponent<MeshRenderer>().sharedMaterial = new Material(Shader.Find("Sprites/Default")) { color = new Color(0.06f, 0.07f, 0.09f, 0.85f), renderQueue = 3000 };
            _area = NewText("AreaSetupText", _areaPanel, Vector3.zero, 0.0016f, TextAnchor.MiddleCenter, new Color(0.6f, 1f, 1f));
            _area.alignment = TextAlignment.Center;
            _areaBg = bg.transform;
            _areaRenderer = _area.GetComponent<MeshRenderer>();
            _areaPanel.gameObject.SetActive(false);
        }

        static TextMesh NewText(string name, Transform parent, Vector3 pos, float size, TextAnchor anchor, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            var t = go.AddComponent<TextMesh>();
            TabletopApp.SetBuiltinFont(t);
            var mat = new Material(t.GetComponent<MeshRenderer>().sharedMaterial) { renderQueue = 3002 };   // over the panel backgrounds
            t.GetComponent<MeshRenderer>().sharedMaterial = mat;
            t.characterSize = size; t.fontSize = 48; t.anchor = anchor; t.color = color;
            return t;
        }

        /// <summary>life 0 = the kind's default; a key updates that toast in place ([comfort] "Loading area 40 %").</summary>
        public void Toast(string text, ToastKind kind = ToastKind.Info, double life = 0, string key = null) => Toasts.Push(text, kind, Time.unscaledTimeAsDouble, life, key);

        void Update()
        {
            float now = Time.unscaledTime;
            if (_status)
            {
                if (_status.gameObject.activeSelf != ShowHud) _status.gameObject.SetActive(ShowHud);
                if (ShowHud && now >= _statusAt) { _statusAt = now + 0.2f; _status.text = StatusText?.Invoke() ?? ""; }
            }
            if (_toasts)
            {
                var list = Toasts.Visible(Time.unscaledTimeAsDouble);
                // Rebuilt every frame while any toast fades; one rich-text line per toast, newest at the bottom.
                if (list.Count > 0 || _toastVersion != Toasts.Version)
                {
                    _toastVersion = Toasts.Version;
                    var sb = new System.Text.StringBuilder();
                    foreach (var t in list)
                    {
                        var c = t.Kind == ToastKind.Error ? new Color(1f, 0.55f, 0.45f) : t.Kind == ToastKind.Warn ? new Color(1f, 0.85f, 0.4f) : new Color(0.7f, 1f, 1f);
                        c.a = t.Alpha;
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGBA(c)).Append('>').Append(Escape(t.Text)).Append("</color>");
                    }
                    _toasts.text = sb.ToString();
                }
            }
            bool area = AreaActive?.Invoke() ?? false;
            if (_areaPanel.gameObject.activeSelf != area) _areaPanel.gameObject.SetActive(area);
            if (area && LeftHand && Head)
            {
                string text = AreaText?.Invoke() ?? "";
                if (_area.text != text) _area.text = text;
                var b = _areaRenderer.localBounds;   // the background hugs the text (the mesh may update a frame late)
                _areaBg.localPosition = new Vector3(b.center.x, b.center.y, 0.001f);
                _areaBg.localScale = new Vector3(Mathf.Max(0.05f, b.size.x + 0.012f), Mathf.Max(0.02f, b.size.y + 0.01f), 1f);
                _areaPanel.position = LeftHand.position + Vector3.up * 0.10f;
                _areaPanel.rotation = Quaternion.LookRotation(_areaPanel.position - Head.position, Vector3.up);
            }
        }

        /// <summary>TextMesh rich text has no escaping: keep '&lt;' out of server messages.</summary>
        static string Escape(string s) => s.Replace('<', '(').Replace('>', ')');
    }
}
