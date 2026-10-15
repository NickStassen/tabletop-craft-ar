// [talk] Chat outside the operator panel (docs/QUEST_SETUP.md Part L):
//   - the wrist overlay: the last 5 chat lines just above the left controller, turned to the head, each fading out
//     10 s after it arrived; hidden while the operator panel or the block menu is open;
//   - bubbles over player figures: a player's chat (and say/me) floats above their figure's name, matched by uuid,
//     up to 3 lines stacked, kept at a readable angular size whatever the table's scale (World/ChatLog.cs ChatBubbles).
using System;
using System.Collections.Generic;
using TabletopCraft.World;
using UnityEngine;

namespace TabletopCraft.XR
{
    public sealed class ChatView : MonoBehaviour
    {
        public Transform Head, LeftHand;
        public ChatLog Log;
        public ChatBubbles Bubbles;
        public Func<bool> Hidden;                      // a panel is open (the wrist overlay steps aside)
        public Func<string, Transform> FigureLabel;   // a player's name label by uuid (PlayerMarkers), or null
        /// <summary>Bubble line height per metre of distance from the head (0.01 = 5 mm at arm's length, like the panel).</summary>
        public float BubbleHeightPerMetre = 0.01f;

        TextMesh[] _wrist;
        Transform _wristRoot;
        readonly Dictionary<string, (TextMesh text, Transform bg)> _bubbles = new Dictionary<string, (TextMesh, Transform)>();
        readonly List<string> _gone = new List<string>();
        Material _bgMat;
        int _bubbleVersion = -1;

        void Start()
        {
            _wristRoot = new GameObject("ChatWrist").transform;
            _wrist = new TextMesh[ChatLog.WristCount];
            for (int i = 0; i < _wrist.Length; i++)
            {
                var go = new GameObject("WristLine" + i);
                go.transform.SetParent(_wristRoot, false);
                go.transform.localPosition = new Vector3(0, (_wrist.Length - 1 - i) * 0.0105f, 0);
                var t = go.AddComponent<TextMesh>();
                TabletopApp.SetBuiltinFont(t);
                t.GetComponent<MeshRenderer>().sharedMaterial = new Material(t.GetComponent<MeshRenderer>().sharedMaterial) { renderQueue = 3005 };
                t.fontSize = 48; t.characterSize = 0.0011f; t.anchor = TextAnchor.LowerLeft; t.richText = false;
                _wrist[i] = t;
            }
            _bgMat = new Material(Shader.Find("Sprites/Default")) { color = new Color(0.05f, 0.06f, 0.08f, 0.75f), renderQueue = 3004 };
        }

        void LateUpdate()
        {
            double now = Time.unscaledTimeAsDouble;
            Wrist(now);
            BubblesUpdate();
        }

        void Wrist(double now)
        {
            bool show = Log != null && LeftHand && Head && !(Hidden?.Invoke() ?? false);
            var lines = show ? Log.Wrist(now) : null;
            show &= lines != null && lines.Count > 0;
            if (_wristRoot.gameObject.activeSelf != show) _wristRoot.gameObject.SetActive(show);
            if (!show) return;
            // Just above and outside the left controller, readable when you glance at the wrist.
            Vector3 right = Vector3.Cross(Vector3.up, Flat(Head.forward));
            _wristRoot.position = LeftHand.position + Vector3.up * 0.06f - right * 0.02f;
            _wristRoot.rotation = Quaternion.LookRotation(_wristRoot.position - Head.position, Vector3.up);
            int first = _wrist.Length - lines.Count;
            for (int i = 0; i < _wrist.Length; i++)
            {
                var t = _wrist[i];
                bool on = i >= first;
                if (t.gameObject.activeSelf != on) t.gameObject.SetActive(on);
                if (!on) continue;
                var l = lines[i - first];
                string text = l.Text.Length > 60 ? l.Text.Substring(0, 59) + "…" : l.Text;
                if (t.text != text) t.text = text;
                Color c = ChatLog.ColorOf(l.Tone);
                c.a = l.Alpha;
                t.color = QualitySettings.activeColorSpace == ColorSpace.Linear ? c.linear : c;
            }
        }

        static Vector3 Flat(Vector3 f) { f.y = 0; return f.sqrMagnitude < 1e-4f ? Vector3.forward : f.normalized; }

        void BubblesUpdate()
        {
            if (Bubbles == null || Head == null) return;
            bool changed = _bubbleVersion != Bubbles.Version;
            _bubbleVersion = Bubbles.Version;
            _gone.Clear();
            foreach (var k in _bubbles.Keys) if (Bubbles.For(k).Count == 0) _gone.Add(k);
            foreach (var k in _gone) { Destroy(_bubbles[k].text.transform.parent.gameObject); _bubbles.Remove(k); }
            foreach (var uuid in Bubbles.Players)
            {
                var anchor = FigureLabel?.Invoke(uuid);
                if (!_bubbles.TryGetValue(uuid, out var b))
                {
                    var root = new GameObject("ChatBubble " + uuid).transform;
                    var bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    Destroy(bg.GetComponent<Collider>());
                    bg.transform.SetParent(root, false);
                    bg.GetComponent<MeshRenderer>().sharedMaterial = _bgMat;
                    var go = new GameObject("Text");
                    go.transform.SetParent(root, false);
                    var t = go.AddComponent<TextMesh>();
                    TabletopApp.SetBuiltinFont(t);
                    t.GetComponent<MeshRenderer>().sharedMaterial = new Material(t.GetComponent<MeshRenderer>().sharedMaterial) { renderQueue = 3005 };
                    t.fontSize = 48; t.anchor = TextAnchor.LowerCenter; t.alignment = TextAlignment.Center; t.richText = false; t.color = Color.white;
                    b = (t, bg.transform);
                    _bubbles[uuid] = b;
                    changed = true;
                }
                var rootT = b.text.transform.parent;
                bool visible = anchor != null && anchor.gameObject.activeInHierarchy;
                if (rootT.gameObject.activeSelf != visible) rootT.gameObject.SetActive(visible);
                if (!visible) continue;
                if (changed) b.text.text = Bubbles.Text(uuid);
                // Above the name label, facing the head, the same angular size at any distance or table scale.
                float dist = Mathf.Max(0.1f, Vector3.Distance(Head.position, anchor.position));
                float h = BubbleHeightPerMetre * dist;          // one line's height in metres
                rootT.position = anchor.position + Vector3.up * (h * 1.2f);
                rootT.rotation = Quaternion.LookRotation(rootT.position - Head.position, Vector3.up);
                b.text.characterSize = h / 4.8f;                 // TextMesh line height ~ characterSize * fontSize / 10
                var bounds = b.text.GetComponent<MeshRenderer>().localBounds;   // already scaled by characterSize
                b.bg.localPosition = new Vector3(bounds.center.x, bounds.center.y, 0.0005f);
                b.bg.localScale = new Vector3(bounds.size.x + h * 0.8f, bounds.size.y + h * 0.5f, 1f);
            }
        }

        void OnDestroy()
        {
            if (_wristRoot) Destroy(_wristRoot.gameObject);
            foreach (var b in _bubbles.Values) if (b.text) Destroy(b.text.transform.parent.gameObject);
        }
    }
}
