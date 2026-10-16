// [comfort] The head-locked setup card (docs/QUEST_SETUP.md Part D): shown when the app has had no link for about 5 s
// since it started (or since the server address changed), or at once when the server refused it for good (close 4001
// version mismatch, 4003 wrong token). It says which URL was tried, what happened, where tabletop.json lives and how to
// fix it (menu > Info > Server). It disappears when the link comes up and while the block menu is open. The text is
// engine-free (World/SessionState.cs SetupCard.Text); TabletopApp decides when. [setup] The same view shows the first-run
// card (searching the LAN) and, as a second instance, the pairing code (SetupCard.PairText, TextMesh rich text).
using System;
using UnityEngine;

namespace TabletopCraft.XR
{
    public sealed class SetupCardView : MonoBehaviour
    {
        public Transform Head;
        public Func<bool> Visible;
        public Func<string> Text;
        /// <summary>[setup] Where the card sits in front of the head (metres, head-local); null = centred at 0.85 m.</summary>
        public Func<Vector3> Offset;

        Transform _card, _bg;
        TextMesh _text;
        MeshRenderer _textRenderer;
        float _textAt;

        void Start()
        {
            _card = new GameObject("SetupCard").transform;
            if (Head) _card.SetParent(Head, false);
            _card.localPosition = new Vector3(0f, 0.02f, 0.85f);
            var bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(bg.GetComponent<Collider>());
            bg.name = "Background";
            bg.transform.SetParent(_card, false);
            bg.GetComponent<MeshRenderer>().sharedMaterial = new Material(Shader.Find("Sprites/Default")) { color = new Color(0.06f, 0.07f, 0.09f, 0.9f), renderQueue = 3000 };
            _bg = bg.transform;
            var go = new GameObject("SetupCardText");
            go.transform.SetParent(_card, false);
            _text = go.AddComponent<TextMesh>();
            TabletopApp.SetBuiltinFont(_text);
            _textRenderer = go.GetComponent<MeshRenderer>();
            _textRenderer.sharedMaterial = new Material(_textRenderer.sharedMaterial) { renderQueue = 3002 };
            _text.characterSize = 0.0026f; _text.fontSize = 48; _text.anchor = TextAnchor.MiddleCenter; _text.alignment = TextAlignment.Left;
            _text.color = new Color(1f, 0.9f, 0.6f);
            _card.gameObject.SetActive(false);
        }

        void Update()
        {
            bool show = Visible != null && Visible();
            if (_card.gameObject.activeSelf != show) _card.gameObject.SetActive(show);
            if (!show) return;
            if (Offset != null) _card.localPosition = Offset();
            if (Time.unscaledTime < _textAt) return;
            _textAt = Time.unscaledTime + 0.25f;
            string t = Text?.Invoke() ?? "";
            if (_text.text != t) _text.text = t;
            var b = _textRenderer.localBounds;   // the background hugs the text (the mesh may update a frame late)
            _bg.localPosition = new Vector3(b.center.x, b.center.y, 0.002f);
            _bg.localScale = new Vector3(Mathf.Max(0.1f, b.size.x + 0.03f), Mathf.Max(0.05f, b.size.y + 0.03f), 1f);
        }
    }
}
