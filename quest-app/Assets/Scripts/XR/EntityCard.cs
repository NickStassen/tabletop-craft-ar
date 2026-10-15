// [hands] The mob info card (docs/QUEST_SETUP.md "Using blocks, items and mobs"): entity_info drawn as a small card
// floating above the mob on the table, turned to the head, with Kill, Remove, Freeze / Unfreeze, Glow and Close buttons
// the right ray clicks (World/EntityCardModel.cs has the logic). While the ray is on the card it owns the right trigger,
// like the block menu. Each action goes out as entity_op and the card asks for entity_info again, so it stays current;
// it closes with Close, B, when the mob is gone, or after 30 s untouched.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

namespace TabletopCraft.XR
{
    [DefaultExecutionOrder(-40)]   // after the menus (they win the ray: RayTaken), before BlockEditMode reads CapturesRight
    public sealed class EntityCard : MonoBehaviour
    {
        public Transform Head, RightHand;
        public Func<bool> Blocked;                          // area setup owns the controllers: the card hides
        public Func<bool> RayTaken;                         // the block menu or operator panel has the right ray
        public Action<Dictionary<string, object>> Send;
        public Func<InteractWelcome> Interact;
        /// <summary>The top of the mob as drawn (BlockEditMode.TryEntityTop), Unity world space.</summary>
        public TryTop Top;
        public delegate bool TryTop(string uuid, out Vector3 top);
        public Action<string> Result;                       // what happened, for the edit status / toasts
        public float MaxRayM = 1.5f, LiftM = 0.03f;

        public readonly EntityCardModel Model = new EntityCardModel();
        public bool CapturesRight => Model.CapturesRight;

        static readonly Color32 Bg = new Color32(16, 18, 22, 225), Button = new Color32(44, 47, 56, 235), Hover = new Color32(86, 96, 112, 245),
                                Danger = new Color32(120, 40, 36, 240), Edge = new Color32(255, 220, 60, 255);
        static readonly Color TextMain = new Color(0.92f, 0.95f, 1f), TextTitle = new Color(1f, 0.9f, 0.4f);

        Transform _card;
        Mesh _bg;
        Material _bgMat, _textMat;
        Font _font;
        LineRenderer _laser;
        readonly PanelMesh _b = new PanelMesh();
        readonly List<TextMesh> _texts = new List<TextMesh>();
        int _drawn = -1;
        Vector3 _hitLocal;
        float _hapticOff = -1;

        void Start()
        {
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _textMat = new Material(_font.material) { renderQueue = 3012 };
            Font.textureRebuilt += OnFontRebuilt;
            _bgMat = PanelMesh.SpriteMat(3011);
            _card = new GameObject("EntityCard").transform;
            _bg = PanelMesh.Child(_card, "Background", _bgMat);
            _card.gameObject.SetActive(false);
            var lgo = new GameObject("CardLaser");
            lgo.transform.SetParent(transform, false);
            _laser = lgo.AddComponent<LineRenderer>();
            _laser.useWorldSpace = true; _laser.positionCount = 2; _laser.material = PanelMesh.SpriteMat(3013);
            _laser.startWidth = 0.002f; _laser.endWidth = 0.001f; _laser.numCapVertices = 0;
            _laser.startColor = new Color(1f, 0.9f, 0.3f, 0.25f); _laser.endColor = new Color(1f, 0.9f, 0.3f, 0.9f);
            _laser.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; _laser.receiveShadows = false;
            lgo.SetActive(false);
        }

        void OnFontRebuilt(Font f) { if (f == _font && _textMat) _textMat.mainTexture = f.material.mainTexture; }

        /// <summary>entity_info arrived: open (or refresh) the card.</summary>
        public void Show(EntityInfoMessage info) => Model.Show(info, Interact?.Invoke(), Time.unscaledTimeAsDouble);

        void Update()
        {
            float now = Time.unscaledTime;
            if (_hapticOff >= 0 && now >= _hapticOff) { _hapticOff = -1; OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.RTouch); }
            bool blocked = Blocked?.Invoke() ?? false;
            Vector3 top = default;
            bool shown = Model.Open && Top != null && Top(Model.Uuid, out top);
            Model.Tick(Time.unscaledTimeAsDouble, shown);
            if (Model.Open && OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch) && !Model.CapturesRight) Model.Close();   // B closes it too
            bool open = Model.Open && !blocked;
            _card.gameObject.SetActive(open);
            if (!open) { Model.Point(0, 0, false); _laser.gameObject.SetActive(false); return; }
            // Above the mob, facing the head.
            _card.position = top + Vector3.up * (LiftM + Model.Height / 2);
            if (Head) { var look = _card.position - Head.position; if (look.sqrMagnitude > 1e-6f) _card.rotation = Quaternion.LookRotation(look, Vector3.up); }
            if (RayTaken?.Invoke() ?? false) Model.Point(0, 0, false); else Pointer();
            if (Model.CapturesRight && OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch)) Click();
            if (_drawn != Model.Version || Model.CapturesRight) Draw();   // the cursor dot follows the ray
            _laser.gameObject.SetActive(Model.CapturesRight);
        }

        void Click()
        {
            string uuid = Model.Uuid, label = Model.Info?.Lines()[0];
            string op = Model.Click(Time.unscaledTimeAsDouble);
            if (op == null) return;
            OVRInput.SetControllerVibration(0.6f, 0.35f, OVRInput.Controller.RTouch);
            _hapticOff = Time.unscaledTime + 0.035f;
            if (op == "close") return;
            Send?.Invoke(InteractProtocol.EntityOp(uuid, op));
            Result?.Invoke(op + " " + label);
            if (op == "kill" || op == "remove") Model.Close();
            else Send?.Invoke(InteractProtocol.EntityOp(uuid, "info"));   // refresh what the card says
        }

        /// <summary>The right controller's ray against the card plane -> the button it points at.</summary>
        void Pointer()
        {
            if (!RightHand) { Model.Point(0, 0, false); return; }
            Vector3 o = RightHand.position, d = RightHand.forward, n = _card.forward;
            float den = Vector3.Dot(d, n);
            bool on = false; float x = 0, y = 0;
            if (Mathf.Abs(den) > 1e-4f)
            {
                float t = Vector3.Dot(_card.position - o, n) / den;
                if (t > 0 && t < MaxRayM)
                {
                    _hitLocal = _card.InverseTransformPoint(o + d * t);
                    x = _hitLocal.x + EntityCardModel.Width / 2; y = Model.Height / 2 - _hitLocal.y;
                    on = x > -0.005f && y > -0.005f && x < EntityCardModel.Width + 0.005f && y < Model.Height + 0.005f;
                    if (on) { _laser.SetPosition(0, o); _laser.SetPosition(1, o + d * t); }
                }
            }
            Model.Point(x, y, on);
        }

        Vector3 L(float x, float y, float z) => new Vector3(x - EntityCardModel.Width / 2, Model.Height / 2 - y, z);

        void Box(MenuRect r, Color32 c, float z, float inset = 0) =>
            _b.Quad(L(r.X + inset, r.Y + inset, z), L(r.X + r.W - inset, r.Y + inset, z), L(r.X + r.W - inset, r.Y + r.H - inset, z), L(r.X + inset, r.Y + r.H - inset, z), c);

        void Draw()
        {
            _drawn = Model.Version;
            _b.Clear();
            float w = EntityCardModel.Width, h = Model.Height;
            Box(new MenuRect(0, 0, w, h), Bg, 0.0004f);
            float e = 0.0008f;   // a thin yellow frame: the colour of the aimed mob's outline
            Box(new MenuRect(0, 0, w, e), Edge, 0.0002f); Box(new MenuRect(0, h - e, w, e), Edge, 0.0002f);
            Box(new MenuRect(0, 0, e, h), Edge, 0.0002f); Box(new MenuRect(w - e, 0, e, h), Edge, 0.0002f);
            int k = 0;
            var lines = Model.Lines();
            for (int i = 0; i < lines.Count; i++)
                Text(k++, lines[i], EntityCardModel.Margin, EntityCardModel.Margin + (i + 0.5f) * EntityCardModel.LineH, i == 0 ? 0.0016f : 0.00125f,
                     TextAnchor.MiddleLeft, i == 0 ? TextTitle : TextMain);
            for (int i = 0; i < Model.Buttons.Count; i++)
            {
                var r = Model.ButtonRect(i);
                string op = Model.Buttons[i].Op;
                Box(r, i == Model.Hover ? Hover : op == "kill" || op == "remove" ? Danger : Button, 0, 0.0005f);
                Text(k++, Model.Buttons[i].Label, r.CX, r.CY, 0.0013f, TextAnchor.MiddleCenter, TextMain);
            }
            if (Model.CapturesRight)
            {
                float cx = _hitLocal.x + w / 2, cy = h / 2 - _hitLocal.y, s = 0.0018f;
                Box(new MenuRect(cx - s, cy - s, 2 * s, 2 * s), new Color32(255, 255, 255, 230), -0.002f);
            }
            _b.Apply(_bg);
            for (int i = k; i < _texts.Count; i++) if (_texts[i].gameObject.activeSelf) _texts[i].gameObject.SetActive(false);
        }

        void Text(int i, string s, float x, float y, float size, TextAnchor anchor, Color c)
        {
            while (_texts.Count <= i)
            {
                var go = new GameObject("CardText");
                go.transform.SetParent(_card, false);
                var tm = go.AddComponent<TextMesh>();
                TabletopApp.SetBuiltinFont(tm);
                go.GetComponent<MeshRenderer>().sharedMaterial = _textMat;
                tm.fontSize = 48;
                _texts.Add(tm);
            }
            var t = _texts[i];
            if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            if (t.text != s) t.text = s;
            t.characterSize = size; t.anchor = anchor;
            t.color = QualitySettings.activeColorSpace == ColorSpace.Linear ? c.linear : c;
            t.transform.localPosition = L(x, y, -0.001f);
        }

        void OnDestroy()
        {
            Font.textureRebuilt -= OnFontRebuilt;
            if (_bg) Destroy(_bg);
            if (_card) Destroy(_card.gameObject);
        }
    }
}
