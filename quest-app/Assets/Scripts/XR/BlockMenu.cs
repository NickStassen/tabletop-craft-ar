// The creative-style block menu (docs/QUEST_SETUP.md "Block menu"): a panel beside the left hand with the server's
// block categories, search and an Info tab, a 9-slot hotbar, and (when the app wires it) an area-size row. The logic
// is engine-free in World/BlockMenuModel.cs; this draws it (meshes with Sprites/Default, TextMesh, no uGUI) and maps
// the controllers onto it:
//
//   Left menu button (≡)      open / close (not while area setup runs)
//   Right ray on the panel    hover; the trigger clicks, the right stick scrolls the grid, and block editing, gliding,
//                             turning and scaling are paused until the ray leaves the panel or the menu closes
//   Right stick click         menu closed: next hotbar slot
//   B (BlockEditMode)         picks a block from the world into the selection and the hotbar
//
// While the menu is closed a small copy of the hotbar floats by the right controller. Icons: XR/BlockIconAtlas.cs.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

namespace TabletopCraft.XR
{
    [DefaultExecutionOrder(-50)]   // before BlockEditMode, NavController and TablePlacement read CapturesRight
    public sealed class BlockMenu : MonoBehaviour
    {
        public Transform Head, LeftHand, RightHand;
        public Func<bool> Blocked;            // area setup owns the controllers
        public Func<bool> CanEdit;            // the server allows editing (the hotbar by the right hand shows only then)
        public Func<string> InfoText;         // the full status for the Info tab
        public Action<string> SelectedChanged;
        public Action<bool> HudChanged;
        /// <summary>Optional area-size row (null = hidden): the preset sizes in blocks, the current size, and a request
        /// for one of the presets (called with the preset's value). Wired by the app; the menu only draws and forwards.</summary>
        public Func<int[]> AreaPresets;
        public Func<int> AreaCurrent;
        public Action<int> AreaRequest;
        /// <summary>[comfort] Info tab: Reset view, the Server field's LAN search and connect (a normalised ws:// URL).</summary>
        public Action ResetView, ServerFind;
        public Action<string> ServerConnect;
        /// <summary>[comfort] The line under the small hotbar: coordinates and facing (shown while the menu is closed).</summary>
        public Func<string> Readout;
        public BlockIconAtlas Icons;
        public float MaxRayM = 1.5f;
        public int UploadsPerFrame = 6;

        public readonly BlockMenuModel Model = new BlockMenuModel();
        public bool CapturesRight => Model.CapturesRight;
        public bool Open => Model.Open;

        // Look (sRGB, alpha).
        static readonly Color32 PanelBg = new Color32(16, 18, 22, 225), Slot = new Color32(44, 47, 56, 235), SlotHover = new Color32(86, 96, 112, 245),
            SlotSelected = new Color32(40, 104, 140, 245), Border = new Color32(235, 240, 245, 255), Tab = new Color32(40, 44, 54, 240),
            TabActive = new Color32(46, 112, 150, 250), Arrow = new Color32(200, 205, 215, 255), Track = new Color32(70, 74, 84, 220),
            Thumb = new Color32(170, 176, 190, 240), Placeholder = new Color32(70, 72, 80, 160), Cursor = new Color32(255, 255, 255, 230);
        static readonly Color TextMain = new Color(0.92f, 0.95f, 1f), TextDim = new Color(0.62f, 0.68f, 0.76f);

        Transform _panel, _mini;
        TextMesh _miniText;
        Mesh _bg, _icon, _over, _miniBg, _miniIcon;
        Material _bgMat, _iconMat, _overMat, _textMat;
        Font _font;
        LineRenderer _laser;
        MenuLayout _layout;
        readonly Dictionary<string, TextMesh> _texts = new Dictionary<string, TextMesh>();
        readonly HashSet<string> _used = new HashSet<string>();
        readonly MeshBuilder _b = new MeshBuilder(), _i = new MeshBuilder(), _o = new MeshBuilder();
        Vector3 _hitLocal; bool _placed, _prefetched, _namesApplied;
        float _hapticOff = -1, _infoAt;
        string _info = "";
        BlockRegistry _registry;

        void Start()
        {
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _textMat = new Material(_font.material) { renderQueue = 3002 };
            Font.textureRebuilt += OnFontRebuilt;
            _bgMat = SpriteMat(3000, Texture2D.whiteTexture);
            _iconMat = SpriteMat(3001, Icons != null ? Icons.Texture : Texture2D.whiteTexture);
            _overMat = SpriteMat(3003, Texture2D.whiteTexture);
            _panel = new GameObject("BlockMenu").transform;
            _bg = Child(_panel, "Background", _bgMat); _icon = Child(_panel, "Icons", _iconMat); _over = Child(_panel, "Overlay", _overMat);
            _panel.gameObject.SetActive(false);
            _mini = new GameObject("HotbarMini").transform;
            if (RightHand) _mini.SetParent(RightHand, false);
            _mini.localPosition = new Vector3(0f, 0.022f, 0.02f);
            _miniBg = Child(_mini, "Background", _bgMat); _miniIcon = Child(_mini, "Icons", _iconMat);
            _mini.gameObject.SetActive(false);
            var lgo = new GameObject("MenuLaser");
            lgo.transform.SetParent(transform, false);
            _laser = lgo.AddComponent<LineRenderer>();
            _laser.useWorldSpace = true; _laser.positionCount = 2; _laser.material = SpriteMat(3003, Texture2D.whiteTexture);
            _laser.startWidth = 0.002f; _laser.endWidth = 0.001f; _laser.numCapVertices = 0;
            _laser.startColor = new Color(0.6f, 0.9f, 1f, 0.25f); _laser.endColor = new Color(0.6f, 0.9f, 1f, 0.9f);
            _laser.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; _laser.receiveShadows = false;
            lgo.SetActive(false);
        }

        static Material SpriteMat(int queue, Texture tex) => new Material(Shader.Find("Sprites/Default")) { renderQueue = queue, mainTexture = tex };

        static Mesh Child(Transform parent, string name, Material mat)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            var mesh = new Mesh { name = name };
            mesh.MarkDynamic();
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            return mesh;
        }

        void OnFontRebuilt(Font f) { if (f == _font && _textMat) _textMat.mainTexture = f.material.mainTexture; }

        /// <summary>block_registry arrived: the menu's lists and the placement check.</summary>
        public void SetRegistry(BlockRegistry r)
        {
            _registry = r;
            _namesApplied = false;
            Model.SetRegistry(r);
            if (_prefetched && Icons != null) Icons.Prefetch(r.Entries, 10);
        }

        /// <summary>B pick from the world (BlockEditMode): selection + hotbar.</summary>
        public void OnPicked(string state)
        {
            if (Model.Picked(state)) SelectedChanged?.Invoke(Model.Selected);
        }

        void Update()
        {
            float now = Time.unscaledTime;
            if (_hapticOff >= 0 && now >= _hapticOff) { _hapticOff = -1; OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.RTouch); }
            ApplyNames();
            bool blocked = Blocked?.Invoke() ?? false;
            if (blocked && Model.Open) Model.SetOpen(false);
            if (!blocked && OVRInput.GetDown(OVRInput.Button.Start, OVRInput.Controller.LTouch))
            {
                Model.Toggle();
                _placed = false;
                if (Model.Open && !_prefetched && Icons != null && _registry != null) { _prefetched = true; Icons.Prefetch(_registry.Entries, 10); }
            }
            if (AreaPresets != null) { Model.AreaPresets = AreaPresets(); Model.AreaCurrent = AreaCurrent?.Invoke() ?? int.MinValue; }
            else Model.AreaPresets = null;

            if (Model.Open)
            {
                _layout = Model.Layout();
                PlacePanel();
                Pointer();
                if (Model.CapturesRight)
                {
                    if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch)) Act(Model.Click());
                    Model.Stick(OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch).y, Time.unscaledTimeAsDouble);
                }
                if (Model.Tab == Model.InfoTab && now >= _infoAt) { _infoAt = now + 0.25f; _info = InfoText?.Invoke() ?? ""; }
            }
            else
            {
                Model.Point(MenuHit.Nothing, false);
                if (!blocked && OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.RTouch))
                {
                    var s = Model.CycleHotbar();
                    if (s != null) { SelectedChanged?.Invoke(s); Haptic(0.2f); }
                }
            }
            _panel.gameObject.SetActive(Model.Open);
            _laser.gameObject.SetActive(Model.CapturesRight);
            if (Model.Open) Draw();
            DrawMini(!Model.Open && !blocked, CanEdit?.Invoke() ?? true);
            Icons?.Update(UploadsPerFrame);
        }

        /// <summary>Pack names (en_us.json) replace the derived labels once the icon worker has read them.</summary>
        void ApplyNames()
        {
            if (_namesApplied || _registry == null || Icons == null || !Icons.Store.NamesReady) return;
            _namesApplied = true;
            var names = Icons.Store.Names;
            if (names != null) _registry.ApplyNames(k => names.TryGetValue(k, out var v) ? v : null);
        }

        void Act(MenuAction a)
        {
            switch (a.Kind)
            {
                case MenuActionKind.Select: case MenuActionKind.HotbarSlot: SelectedChanged?.Invoke(a.State); Haptic(0.35f); break;
                case MenuActionKind.Area: AreaRequest?.Invoke(a.Value); Haptic(0.35f); break;
                case MenuActionKind.HudToggle: HudChanged?.Invoke(a.Value != 0); Haptic(0.25f); break;
                case MenuActionKind.ResetView: ResetView?.Invoke(); Haptic(0.35f); break;
                case MenuActionKind.ServerEdit: case MenuActionKind.ServerFind: ServerFind?.Invoke(); Haptic(0.2f); break;   // opening the field searches at once
                case MenuActionKind.ServerConnect: ServerConnect?.Invoke(a.State); Haptic(0.35f); break;
                case MenuActionKind.None: break;
                default: Haptic(0.15f); break;
            }
        }

        void Haptic(float amplitude)
        {
            OVRInput.SetControllerVibration(0.6f, amplitude, OVRInput.Controller.RTouch);
            _hapticOff = Time.unscaledTime + 0.035f;
        }

        /// <summary>Beside the left hand: the bottom edge a few cm above the controller, reaching in toward the body,
        /// turned to face the head. Eased so hand tremor doesn't shake it.</summary>
        void PlacePanel()
        {
            if (!LeftHand || !Head) return;
            Vector3 f = Head.forward; f.y = 0;
            if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
            f.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, f);
            Vector3 target = LeftHand.position + Vector3.up * (_layout.Height / 2 + 0.05f) + right * (_layout.Width / 2 - 0.05f);
            if (!_placed || (target - _panel.position).sqrMagnitude > 0.25f) { _panel.position = target; _placed = true; }
            else _panel.position = Vector3.Lerp(_panel.position, target, 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime));
            Vector3 look = _panel.position - Head.position;
            if (look.sqrMagnitude > 1e-6f) _panel.rotation = Quaternion.LookRotation(look, Vector3.up);
        }

        /// <summary>The right controller's ray against the panel plane -> what it points at (layout coordinates).</summary>
        void Pointer()
        {
            if (!RightHand || _layout == null) { Model.Point(MenuHit.Nothing, false); return; }
            Vector3 o = RightHand.position, d = RightHand.forward, n = _panel.forward;
            float den = Vector3.Dot(d, n);
            var hit = MenuHit.Nothing;
            if (Mathf.Abs(den) > 1e-4f)
            {
                float t = Vector3.Dot(_panel.position - o, n) / den;
                if (t > 0 && t < MaxRayM)
                {
                    _hitLocal = _panel.InverseTransformPoint(o + d * t);
                    float x = _hitLocal.x + _layout.Width / 2, y = _layout.Height / 2 - _hitLocal.y;
                    hit = BlockMenuModel.HitTest(_layout, x, y);
                    // A 1 cm rim still counts as the panel, so the bindings don't flicker at its edge.
                    if (hit.Part == MenuPart.None && x > -0.01f && y > -0.01f && x < _layout.Width + 0.01f && y < _layout.Height + 0.01f) hit = new MenuHit(MenuPart.Panel, 0);
                    if (hit.Part != MenuPart.None) { _laser.SetPosition(0, o); _laser.SetPosition(1, o + d * t); }
                }
            }
            Model.Point(hit, hit.Part != MenuPart.None);
        }

        // ---------- drawing ----------

        Vector3 L(float x, float y, float z) => new Vector3(x - _layout.Width / 2, _layout.Height / 2 - y, z);

        void Box(MeshBuilder m, MenuRect r, Color32 c, float z = 0f, float inset = 0f) =>
            m.Quad(L(r.X + inset, r.Y + inset, z), L(r.X + r.W - inset, r.Y + inset, z), L(r.X + r.W - inset, r.Y + r.H - inset, z), L(r.X + inset, r.Y + r.H - inset, z), c, default);

        void Frame(MeshBuilder m, MenuRect r, Color32 c, float w, float z)
        {
            Box(m, new MenuRect(r.X, r.Y, r.W, w), c, z); Box(m, new MenuRect(r.X, r.Y + r.H - w, r.W, w), c, z);
            Box(m, new MenuRect(r.X, r.Y, w, r.H), c, z); Box(m, new MenuRect(r.X + r.W - w, r.Y, w, r.H), c, z);
        }

        void IconQuad(MeshBuilder m, MenuRect r, float inset, Rect uv, float z)
        {
            m.Quad(L(r.X + inset, r.Y + inset, z), L(r.X + r.W - inset, r.Y + inset, z), L(r.X + r.W - inset, r.Y + r.H - inset, z), L(r.X + inset, r.Y + r.H - inset, z),
                   new Color32(255, 255, 255, 255), uv);
        }

        /// <summary>Icon of a state (resident in the atlas) or a flat-colour placeholder while it's rendered.</summary>
        void Icon(MeshBuilder bg, MeshBuilder icons, MenuRect r, string state, string item, int prio, float inset)
        {
            if (Icons != null && Icons.Get(state, item ?? BlockRegistry.IdOf(state), prio, out var uv)) { IconQuad(icons, r, inset, uv, -0.0005f); return; }
            var c = Icons != null && Icons.HasPack ? Placeholder : BlockRules.FlatColor(state);
            if (c.a < 120) c.a = 120;
            Box(bg, r, c, -0.0003f, inset * 1.6f);
        }

        string ItemOf(string state) => _registry?.Find(state) is BlockEntry e && e.Id == BlockRegistry.IdOf(state) ? e.Item : BlockRegistry.IdOf(state);

        void Draw()
        {
            _b.Clear(); _i.Clear(); _o.Clear(); _used.Clear();
            var Lo = _layout;
            Box(_b, new MenuRect(0, 0, Lo.Width, Lo.Height), PanelBg, 0.0004f);
            string selId = BlockRegistry.IdOf(Model.Selected);
            bool names = Icons == null || !Icons.HasPack;
            foreach (var it in Lo.Items)
            {
                bool hover = Model.Hover.Part == it.Part && Model.Hover.Index == it.Index;
                switch (it.Part)
                {
                    case MenuPart.Tab:
                        Box(_b, it.R, it.Index == Model.Tab ? TabActive : hover ? SlotHover : Tab, 0, 0.0008f);
                        Label("tab" + it.Index, it.Label, it.R.CX, it.R.CY, 0.0017f, TextAnchor.MiddleCenter, it.Index == Model.Tab ? Color.white : TextMain);
                        break;
                    case MenuPart.Cell:
                    {
                        var e = Model.CellEntry(it.Index);
                        bool sel = e != null && e.Id == selId;
                        Box(_b, it.R, sel ? SlotSelected : hover && e != null ? SlotHover : Slot, 0, 0.001f);
                        if (sel) Frame(_b, new MenuRect(it.R.X + 0.001f, it.R.Y + 0.001f, it.R.W - 0.002f, it.R.H - 0.002f), Border, 0.0008f, -0.0002f);
                        if (e == null) break;
                        Icon(_b, _i, it.R, e.State, e.Item, 1, 0.0025f);
                        if (names) Label("cell" + it.Index, Short(e.Label, 12), it.R.CX, it.R.Y + it.R.H - 0.004f, 0.00085f, TextAnchor.LowerCenter, TextMain);
                        break;
                    }
                    case MenuPart.ScrollUp:
                    case MenuPart.ScrollDown:
                    {
                        bool can = it.Part == MenuPart.ScrollUp ? Model.Scroll > 0 : Model.Scroll < Model.MaxScroll;
                        Box(_b, it.R, hover && can ? SlotHover : Tab, 0, 0.001f);
                        float cx = it.R.CX, cy = it.R.CY, s = it.R.W * 0.25f, dir = it.Part == MenuPart.ScrollUp ? -1 : 1;
                        var c = can ? Arrow : Track;
                        _b.Tri(L(cx - s, cy - dir * s * 0.6f, -0.0003f), L(cx + s, cy - dir * s * 0.6f, -0.0003f), L(cx, cy + dir * s * 0.8f, -0.0003f), c);
                        break;
                    }
                    case MenuPart.Hotbar:
                    {
                        string s = Model.Hotbar[it.Index];
                        bool cur = it.Index == Model.Hotbar.Selected && s != null && BlockRegistry.IdOf(s) == selId;
                        Box(_b, it.R, cur ? SlotSelected : hover && s != null ? SlotHover : Slot, 0, 0.001f);
                        if (cur) Frame(_b, new MenuRect(it.R.X + 0.001f, it.R.Y + 0.001f, it.R.W - 0.002f, it.R.H - 0.002f), Border, 0.0009f, -0.0002f);
                        if (s != null) Icon(_b, _i, it.R, s, ItemOf(s), 0, 0.0025f);
                        break;
                    }
                    case MenuPart.Key:
                    {
                        // [comfort] shift lit while on, the layer key and enter as accents (World/KeyboardModel.cs)
                        var kb = Model.ActiveKeys;
                        bool on = kb != null && it.Index == KeyboardModel.KeyShift && kb.Shift != ShiftState.Off;
                        Box(_b, it.R, hover ? SlotHover : on || it.Index == KeyboardModel.KeyEnter ? TabActive : Tab, 0, 0.0012f);
                        Label("key" + it.Index, it.Label, it.R.CX, it.R.CY, it.Label.Length > 1 ? 0.0016f : 0.0021f, TextAnchor.MiddleCenter, TextMain);
                        break;
                    }
                    case MenuPart.AreaStep:
                    {
                        bool cur = Model.AreaPresets != null && it.Index < Model.AreaPresets.Length && Model.AreaPresets[it.Index] == Model.AreaCurrent;
                        Box(_b, it.R, cur ? TabActive : hover ? SlotHover : Tab, 0, 0.0012f);
                        Label("area" + it.Index, it.Label, it.R.CX, it.R.CY, 0.0018f, TextAnchor.MiddleCenter, TextMain);
                        break;
                    }
                    case MenuPart.HudToggle:
                        Box(_b, it.R, Model.ShowHud ? TabActive : hover ? SlotHover : Tab, 0, 0.001f);
                        Label("hud", it.Label, it.R.CX, it.R.CY, 0.0017f, TextAnchor.MiddleCenter, TextMain);
                        break;
                    // [comfort] Info tab buttons: Reset view, the Server button, and the open Server field's buttons
                    case MenuPart.ResetView: case MenuPart.ServerFind: case MenuPart.ServerConnect: case MenuPart.ServerCancel: case MenuPart.FoundServer:
                        Box(_b, it.R, hover ? SlotHover : it.Part == MenuPart.ServerConnect ? TabActive : Tab, 0, 0.001f);
                        Label(it.Part + "" + it.Index, Short(it.Label, 34), it.R.CX, it.R.CY, 0.0016f, TextAnchor.MiddleCenter, TextMain);
                        break;
                    case MenuPart.ServerField:
                        Box(_b, it.R, hover ? SlotHover : Slot, 0, 0.001f);
                        Label("serverfield", Short(it.Label, 60), it.R.X + 0.004f, it.R.CY, 0.0015f, TextAnchor.MiddleLeft, TextMain);
                        break;
                }
            }
            if (Lo.ServerText.W > 0)
            {
                Box(_b, Lo.ServerText, Slot, 0, 0.0008f);
                Label("servertext", "Server: " + Model.ServerKeys.Display(52), Lo.ServerText.X + 0.004f, Lo.ServerText.CY, 0.0017f, TextAnchor.MiddleLeft, TextMain);
                if (Model.Found.Count == 0) Label("foundnote", Model.FoundNote, Lo.FoundNote.CX, Lo.FoundNote.CY, 0.0016f, TextAnchor.MiddleCenter, TextDim);
            }
            if (Lo.ScrollTrack.H > 0 && Model.MaxScroll > 0)
            {
                Box(_b, Lo.ScrollTrack, Track, -0.0002f);
                float frac = Model.GridRows / (float)Model.RowCount, th = Mathf.Max(0.008f, Lo.ScrollTrack.H * frac);
                float ty = Lo.ScrollTrack.Y + (Lo.ScrollTrack.H - th) * Model.Scroll / Model.MaxScroll;
                Box(_b, new MenuRect(Lo.ScrollTrack.X - 0.001f, ty, Lo.ScrollTrack.W + 0.002f, th), Thumb, -0.0003f);
            }
            if (Model.Tab == Model.SearchTab)
            {
                Box(_b, Lo.SearchField, Slot, 0, 0.0008f);
                string q = Model.Query.Length == 0 ? "type to search" : Model.Query + "_";
                Label("search", "Search: " + q, Lo.SearchField.X + 0.004f, Lo.SearchField.CY, 0.0019f, TextAnchor.MiddleLeft, Model.Query.Length == 0 ? TextDim : TextMain);
                Label("count", Model.Items.Count + " blocks", Lo.SearchField.X + Lo.SearchField.W - 0.004f, Lo.SearchField.CY, 0.0015f, TextAnchor.MiddleRight, TextDim);
            }
            if (Model.Tab == Model.InfoTab) Label("info", _info, Lo.Info.X + 0.002f, Lo.Info.Y + 0.002f, 0.00135f, TextAnchor.UpperLeft, TextMain);
            else if (_registry == null) Label("wait", "waiting for the server's block list...", Lo.Grid.CX, Lo.Grid.CY, 0.0018f, TextAnchor.MiddleCenter, TextDim);
            Label("name", Short(Model.NameLine(), 48), Lo.Name.X + 0.002f, Lo.Name.CY, 0.0019f, TextAnchor.MiddleLeft, TextMain);
            if (Lo.AreaLabel.W > 0) Label("arealabel", "Area", Lo.AreaLabel.X + 0.002f, Lo.AreaLabel.CY, 0.0018f, TextAnchor.MiddleLeft, TextDim);
            if (Model.CapturesRight)
            {
                // Cursor dot where the ray meets the panel.
                float cx = _hitLocal.x + _layout.Width / 2, cy = _layout.Height / 2 - _hitLocal.y, s = 0.0025f;
                Box(_o, new MenuRect(cx - s, cy - s, 2 * s, 2 * s), Cursor, -0.002f);
            }
            _b.Apply(_bg); _i.Apply(_icon); _o.Apply(_over);
            foreach (var kv in _texts) if (!_used.Contains(kv.Key) && kv.Value.gameObject.activeSelf) kv.Value.gameObject.SetActive(false);
        }

        static string Short(string s, int n) => s == null ? "" : s.Length <= n ? s : s.Substring(0, n - 1) + "…";

        void Label(string key, string text, float x, float y, float size, TextAnchor anchor, Color color)
        {
            if (!_texts.TryGetValue(key, out var t))
            {
                var go = new GameObject("Text " + key);
                go.transform.SetParent(_panel, false);
                t = go.AddComponent<TextMesh>();
                TabletopApp.SetBuiltinFont(t);
                go.GetComponent<MeshRenderer>().sharedMaterial = _textMat;
                t.fontSize = 48;
                _texts[key] = t;
            }
            _used.Add(key);
            if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            if (t.text != text) t.text = text;
            t.characterSize = size; t.anchor = anchor;
            t.color = QualitySettings.activeColorSpace == ColorSpace.Linear ? color.linear : color;   // vertex colours, as in MeshBuilder
            t.transform.localPosition = L(x, y, -0.001f);
        }

        /// <summary>The hotbar by the right controller while the menu is closed: 9 small slots, the selected one framed
        /// (only when editing is allowed), and [comfort] the coordinates + facing line under it.</summary>
        void DrawMini(bool show, bool hotbar)
        {
            show &= RightHand;
            if (_mini.gameObject.activeSelf != show) _mini.gameObject.SetActive(show);
            if (!show) return;
            if (Head) _mini.rotation = Quaternion.LookRotation(_mini.position - Head.position, Vector3.up);
            if (!_miniText)
            {
                var go = new GameObject("Readout");
                go.transform.SetParent(_mini, false);
                go.transform.localPosition = new Vector3(0, -0.0075f, -0.0005f);
                _miniText = go.AddComponent<TextMesh>();
                TabletopApp.SetBuiltinFont(_miniText);
                go.GetComponent<MeshRenderer>().sharedMaterial = _textMat;
                _miniText.fontSize = 48; _miniText.characterSize = 0.0011f; _miniText.anchor = TextAnchor.UpperCenter;
                _miniText.color = QualitySettings.activeColorSpace == ColorSpace.Linear ? TextMain.linear : TextMain;
            }
            string readout = Readout?.Invoke() ?? "";
            if (_miniText.text != readout) _miniText.text = readout;
            const float cell = 0.011f, gap = 0.001f;
            float w = Hotbar.Size * cell, x0 = -w / 2;
            _b.Clear(); _i.Clear();
            if (!hotbar || Model.Hotbar.Count == 0) { _b.Apply(_miniBg); _i.Apply(_miniIcon); return; }
            string selId = BlockRegistry.IdOf(Model.Selected);
            for (int k = 0; k < Hotbar.Size; k++)
            {
                float x = x0 + k * cell;
                Vector3 P(float px, float py, float z) => new Vector3(px, py, z);
                string s = Model.Hotbar[k];
                bool cur = k == Model.Hotbar.Selected && s != null && BlockRegistry.IdOf(s) == selId;
                var c = cur ? SlotSelected : Slot;
                _b.Quad(P(x + gap, cell / 2 - gap, 0), P(x + cell - gap, cell / 2 - gap, 0), P(x + cell - gap, -cell / 2 + gap, 0), P(x + gap, -cell / 2 + gap, 0), c, default);
                if (cur)
                {
                    float b = 0.0006f;
                    _b.Quad(P(x, cell / 2, -0.0002f), P(x + cell, cell / 2, -0.0002f), P(x + cell, cell / 2 - b, -0.0002f), P(x, cell / 2 - b, -0.0002f), Border, default);
                    _b.Quad(P(x, -cell / 2 + b, -0.0002f), P(x + cell, -cell / 2 + b, -0.0002f), P(x + cell, -cell / 2, -0.0002f), P(x, -cell / 2, -0.0002f), Border, default);
                }
                if (s == null) continue;
                float i = 0.0012f;
                if (Icons != null && Icons.Get(s, ItemOf(s), 0, out var uv))
                    _i.Quad(P(x + i, cell / 2 - i, -0.0005f), P(x + cell - i, cell / 2 - i, -0.0005f), P(x + cell - i, -cell / 2 + i, -0.0005f), P(x + i, -cell / 2 + i, -0.0005f), new Color32(255, 255, 255, 255), uv);
                else
                {
                    var fc = BlockRules.FlatColor(s);
                    _b.Quad(P(x + 2 * i, cell / 2 - 2 * i, -0.0003f), P(x + cell - 2 * i, cell / 2 - 2 * i, -0.0003f), P(x + cell - 2 * i, -cell / 2 + 2 * i, -0.0003f), P(x + 2 * i, -cell / 2 + 2 * i, -0.0003f), fc, default);
                }
            }
            _b.Apply(_miniBg); _i.Apply(_miniIcon);
        }

        void OnDestroy()
        {
            Font.textureRebuilt -= OnFontRebuilt;
            foreach (var m in new[] { _bg, _icon, _over, _miniBg, _miniIcon }) if (m) Destroy(m);
            if (_panel) Destroy(_panel.gameObject);
        }

        /// <summary>Reused vertex lists for the panel meshes (no per-frame garbage beyond the mesh upload).</summary>
        sealed class MeshBuilder
        {
            readonly List<Vector3> _v = new List<Vector3>();
            readonly List<Color32> _c = new List<Color32>();
            readonly List<Vector2> _uv = new List<Vector2>();
            readonly List<int> _t = new List<int>();

            public void Clear() { _v.Clear(); _c.Clear(); _uv.Clear(); _t.Clear(); }

            // Vertex colours reach Sprites/Default unconverted; the project renders in linear space and the colours here
            // are sRGB (like the blocks' tints, which Blocks.shader converts itself).
            static readonly bool Linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            static Color32 Lin(Color32 c) => Linear ? (Color32)((Color)c).linear : c;

            /// <summary>a b c d clockwise from the top-left (as seen by the viewer); uv default = a white texel.</summary>
            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color32 col, Rect uv)
            {
                col = Lin(col);
                int n = _v.Count;
                _v.Add(a); _v.Add(b); _v.Add(c); _v.Add(d);
                for (int k = 0; k < 4; k++) _c.Add(col);
                if (uv.width > 0) { _uv.Add(new Vector2(uv.xMin, uv.yMax)); _uv.Add(new Vector2(uv.xMax, uv.yMax)); _uv.Add(new Vector2(uv.xMax, uv.yMin)); _uv.Add(new Vector2(uv.xMin, uv.yMin)); }
                else for (int k = 0; k < 4; k++) _uv.Add(new Vector2(0.5f, 0.5f));
                _t.Add(n); _t.Add(n + 1); _t.Add(n + 2); _t.Add(n); _t.Add(n + 2); _t.Add(n + 3);
            }

            public void Tri(Vector3 a, Vector3 b, Vector3 c, Color32 col)
            {
                col = Lin(col);
                int n = _v.Count;
                _v.Add(a); _v.Add(b); _v.Add(c);
                for (int k = 0; k < 3; k++) { _c.Add(col); _uv.Add(new Vector2(0.5f, 0.5f)); }
                _t.Add(n); _t.Add(n + 1); _t.Add(n + 2);
            }

            public void Apply(Mesh m)
            {
                m.Clear();
                m.SetVertices(_v); m.SetColors(_c); m.SetUVs(0, _uv); m.SetTriangles(_t, 0, true);
            }
        }
    }
}
