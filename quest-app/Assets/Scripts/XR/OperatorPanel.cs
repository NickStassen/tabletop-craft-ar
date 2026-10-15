// [talk] The operator panel (docs/QUEST_SETUP.md Part L): chat, console, players, world and server status, beside the
// left hand like the block menu. The logic is engine-free in World/OperatorPanelModel.cs (+ ChatLog, ConsoleModel);
// this draws it (meshes with Sprites/Default, pooled TextMeshes, no uGUI), maps the controllers onto it, sends what
// it asks for and routes the server's replies:
//
//   X (left)                  tap: open / close; hold 0.6 s: open Chat with the input focused
//   Menu button (left)        while the panel is open: close it (the block menu opens instead); X does the reverse
//   Right ray on the panel    hover; the trigger clicks (tabs, buttons, keys of the on-panel keyboard), the right stick
//                             scrolls, the stick click accepts the first suggestion; block editing, gliding, turning and
//                             scaling pause meanwhile. The keyboard's enter sends the chat line, runs the command or kicks.
//
// "@ pointer" and the Here/Bolt buttons use the block the right controller last aimed at (BlockEditMode's target,
// remembered while the ray is on the panel). Commands go out with LinkClient.SendTracked so their results match.
// [operator] The Marks tab (worlds, spawn, typed coordinates, bookmarks), Follow / Go to on Players, and the container view
// and sign editor (opened by BlockEditMode's left-trigger hooks through OpenContainer / OpenSign) are drawn here too; slot
// icons come from the block menu's icon atlas. Jumps and follow requests go to the app (JumpTo, Follow); bookmark,
// container and sign messages the model queues in its Outbox are sent each frame.
using System;
using System.Collections.Generic;
using System.IO;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

namespace TabletopCraft.XR
{
    [DefaultExecutionOrder(-60)]   // before BlockMenu (-50): X and the menu button switch between the two panels
    public sealed class OperatorPanel : MonoBehaviour
    {
        public Transform Head, LeftHand, RightHand;
        public Func<bool> Blocked;                          // area setup owns the controllers
        public Func<bool> Online;
        public Func<Dictionary<string, object>, long> SendTracked;
        public Func<string> WorldName;                      // the region's world (for "at")
        public Func<Vector3Int?> AimedBlock;                // world coordinates of BlockEditMode's target this frame, or null
        public Action<string, ToastKind> Toast;
        // [operator] jumps (world, target) and follow requests ("follow" / "goto" / "stop", name, uuid) go to the app
        public Action<string, Vector3> JumpTo;
        public Action<string, string, string> Follow;
        public BlockIconAtlas Icons;                        // container slot icons (the block menu's atlas)
        public NavState Nav;                                // where the view is (the Marks tab's header, Add mark, ~ coordinates)
        public FollowState Following;                       // who is followed (Players' Follow / Stop, Marks' Stop follow)
        public Func<string> Selection;                      // the hotbar's selected block or item (the container view's Put)
        public Func<BlockRegistry> Registry;                // block -> item for Put, icons of block items
        public float MaxRayM = 1.5f;
        /// <summary>How long the last aimed block stays the pointer after the ray left the terrain.</summary>
        public float PointerSeconds = 60f;

        public readonly OperatorPanelModel Model = new OperatorPanelModel();
        public readonly ChatBubbles Bubbles = new ChatBubbles();
        public bool CapturesRight => Model.CapturesRight;
        public bool Open => Model.Open;
        public string Session;

        static readonly Color32 PanelBg = new Color32(16, 18, 22, 225), Slot = new Color32(44, 47, 56, 235), Hover = new Color32(86, 96, 112, 245),
            Active = new Color32(46, 112, 150, 250), Disabled = new Color32(34, 36, 42, 200), Field = new Color32(28, 30, 36, 240),
            Arrow = new Color32(200, 205, 215, 255), Dim = new Color32(70, 74, 84, 220), Cursor = new Color32(255, 255, 255, 230),
            Selected = new Color32(40, 104, 140, 245);
        static readonly Color TextMain = new Color(0.92f, 0.95f, 1f), TextDim = new Color(0.55f, 0.6f, 0.68f), TextErr = new Color(1f, 0.5f, 0.42f),
            TextOk = new Color(0.75f, 1f, 0.8f), TextCmd = new Color(0.65f, 0.85f, 1f), TextNote = new Color(1f, 0.85f, 0.5f);

        Transform _panel;
        Mesh _bg, _over, _icon;
        Material _bgMat, _overMat, _textMat, _iconMat;
        readonly PanelMesh _ic = new PanelMesh();   // [operator] slot icons
        Font _font;
        LineRenderer _laser;
        PanelLayout _layout;
        readonly Dictionary<string, TextMesh> _texts = new Dictionary<string, TextMesh>();
        readonly HashSet<string> _used = new HashSet<string>();
        readonly PanelMesh _b = new PanelMesh(), _o = new PanelMesh();
        readonly List<WristLine> _chatLines = new List<WristLine>();
        readonly List<ConsoleLine> _consoleLines = new List<ConsoleLine>();
        Vector3 _hitLocal; bool _placed;
        float _hapticOff = -1;
        Vector3Int? _pointer; string _pointerWorld; float _pointerAt = -999;
        string _historyPath; int _historySaved;
        readonly Dictionary<long, string> _queries = new Dictionary<long, string>();

        void Start()
        {
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _textMat = new Material(_font.material) { renderQueue = 3012 };
            Font.textureRebuilt += OnFontRebuilt;
            _bgMat = PanelMesh.SpriteMat(3010);
            _overMat = PanelMesh.SpriteMat(3013);
            _panel = new GameObject("OperatorPanel").transform;
            _bg = PanelMesh.Child(_panel, "Background", _bgMat);
            _iconMat = PanelMesh.SpriteMat(3011);   // [operator] slot icons between the boxes and the text
            if (Icons != null) _iconMat.mainTexture = Icons.Texture;
            _icon = PanelMesh.Child(_panel, "Icons", _iconMat);
            _over = PanelMesh.Child(_panel, "Overlay", _overMat);
            _panel.gameObject.SetActive(false);
            var lgo = new GameObject("OperatorLaser");
            lgo.transform.SetParent(transform, false);
            _laser = lgo.AddComponent<LineRenderer>();
            _laser.useWorldSpace = true; _laser.positionCount = 2; _laser.material = PanelMesh.SpriteMat(3013);
            _laser.startWidth = 0.002f; _laser.endWidth = 0.001f; _laser.numCapVertices = 0;
            _laser.startColor = new Color(1f, 0.85f, 0.5f, 0.25f); _laser.endColor = new Color(1f, 0.85f, 0.5f, 0.9f);
            _laser.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; _laser.receiveShadows = false;
            lgo.SetActive(false);
            _historyPath = Path.Combine(Application.persistentDataPath, ConsoleModel.HistoryFile);
            try { if (File.Exists(_historyPath)) Model.Console.LoadHistory(File.ReadAllText(_historyPath)); }
            catch (Exception e) { Debug.LogWarning("[Tabletop] console history: " + e.Message); }
            _historySaved = Model.Console.History.Count;
        }

        void OnFontRebuilt(Font f) { if (f == _font && _textMat) _textMat.mainTexture = f.material.mainTexture; }

        // ---------- messages from the server (TabletopApp routes them) ----------
        public void OnWelcome(Dictionary<string, object> welcome, IEnumerable<string> localQuick)
        {
            Session = welcome.S("session");
            Model.OnWelcome(ControlWelcome.From(welcome), localQuick);
            var w = Model.Welcome;
            Debug.Log($"[Tabletop] TALK chat {(w.Chat ? (w.ChatSend ? "on as " + w.ChatName : "read-only") : "off")}, commands {(w.Commands ? "on" : "off")}, status {(w.Status ? "on" : "off")}");
        }

        /// <summary>Handles chat, command and status messages; false for any other type.</summary>
        public bool OnMessage(Dictionary<string, object> m)
        {
            double now = Time.unscaledTimeAsDouble;
            switch (m.S("type"))
            {
                case "chat":
                {
                    var c = ChatMessage.From(m);
                    var e = Model.Log.Add(c, now, Session);
                    Bubbles.Add(c, now);
                    if (ChatLog.Toast(c.Kind) && !Model.Open) Toast?.Invoke(e.Display, c.Kind == "death" ? ToastKind.Warn : ToastKind.Info);
                    return true;
                }
                case "command_result":
                {
                    var r = CommandResult.From(m);
                    Model.Console.OnResult(r);
                    if (Model.Tab != OpTab.Console && Model.Open) Model.Note = (r.Ok ? "" : "failed: ") + (r.Lines.Count > 0 ? r.Lines[0] : r.Ok ? "done" : "");
                    return true;
                }
                case "command_output": Model.Console.OnOutput(CommandOutput.From(m)); return true;
                case "command_suggestions": Model.Console.OnSuggestions(CommandSuggestions.From(m)); return true;
                case "server_status": Model.OnStatus(ServerStatus.From(m)); return true;
                case "world_info": Model.OnWorld(WorldInfo.From(m)); return true;
                case "players_info": Model.OnPlayers(PlayersInfo.From(m)); return true;
                // [operator] the shared bookmarks; a container view's contents (or its end)
                case "bookmarks": Model.Marks.OnBookmarks(BookmarkList.From(m)); return true;
                case "container":
                {
                    var v = ContainerView.From(m);
                    bool wasOpen = Model.Mode == PanelMode.Container;
                    if (!Model.OnContainer(v) && !v.Closed) Send(OperatorProtocol.ContainerClose(v.Pos));   // a stale view: tell the server we're not looking
                    if (wasOpen && Model.Mode != PanelMode.Container) Toast?.Invoke(Model.Note, ToastKind.Info);
                    return true;
                }
            }
            return false;
        }

        /// <summary>[operator] The left trigger on a container (BlockEditMode.OnContainerUse): the panel opens on its view.</summary>
        public void OpenContainer(Vector3Int pos)
        {
            Model.OpenContainer(pos);
            _placed = false;
            Debug.Log($"[Tabletop] CONTAINER open {pos.x},{pos.y},{pos.z}");
        }

        /// <summary>[operator] The left trigger on a sign (BlockEditMode.OnSignUse): the panel opens on the sign editor.</summary>
        public void OpenSign(Vector3Int pos, string state, BlockEntityData data)
        {
            Model.OpenSign(pos, state, data);
            _placed = false;
            Debug.Log($"[Tabletop] SIGN edit {pos.x},{pos.y},{pos.z} {(data != null ? "with text" : "empty")}");
        }

        /// <summary>An error that answers one of the panel's messages; true when it was ours (no toast needed).</summary>
        public bool OnError(Dictionary<string, object> m)
        {
            long refSeq = (long)m.D("ref_seq", -1);
            // [operator] bookmark, container and sign refusals carry their op (and pos): the note line says them
            string op = m.S("op");
            if (op == "bookmark" || op == "container_query" || op == "container_edit" || op == "sign_edit")
            {
                var p = m.A("pos");
                bool wasOpen = Model.Mode == PanelMode.Container;
                if (Model.OnOperatorError(op, p != null && p.Count == 3 ? OperatorProtocol.V3(p) : (Vector3Int?)null, m.S("message")))
                {
                    if (!Model.Open || (wasOpen && Model.Mode != PanelMode.Container)) Toast?.Invoke("Can't: " + m.S("message"), ToastKind.Warn);
                    return true;
                }
            }
            if (Model.Console.OnError(refSeq, m.S("code"), m.S("message"))) { if (Model.Tab != OpTab.Console) Model.Note = m.S("message"); return true; }
            // Our chat_send, completion or query: the note line says it (no toast; "rate_limited" there means edits).
            if (_queries.TryGetValue(refSeq, out var what)) { _queries.Remove(refSeq); Model.Note = (what == "chat" ? "chat: " : "") + m.S("message"); return true; }
            return false;
        }

        void Update()
        {
            float now = Time.unscaledTime;
            double dnow = Time.unscaledTimeAsDouble;
            if (_hapticOff >= 0 && now >= _hapticOff) { _hapticOff = -1; OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.RTouch); }
            Model.Connected = Online?.Invoke() ?? false;
            TrackPointer(now);
            // [operator] what the Marks tab, Follow and the container view need from the rest of the app
            if (Nav != null) { Model.Marks.Loaded = Nav.Loaded; Model.Marks.World = Nav.World; Model.Marks.View = Nav.View; }
            if (Following != null) { Model.Marks.FollowName = Following.Active && !Following.Once ? Following.Name : null; Model.Marks.FollowLine = Following.Active ? Following.Describe() : ""; }
            Model.HotbarSelection = Selection?.Invoke();
            Model.Registry = Registry?.Invoke();
            bool blocked = Blocked?.Invoke() ?? false;
            if (blocked) { if (Model.Open) Model.SetOpen(false); }
            else
            {
                // X on the left controller is Button.One with Controller.LTouch (Three/Four only exist for Controller.Touch).
                if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch)) { Model.XDown(dnow); _placed = false; }
                if (OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.LTouch)) Model.XHeld(dnow);
                if (OVRInput.GetUp(OVRInput.Button.One, OVRInput.Controller.LTouch)) Model.XUp(dnow);
                // The menu button switches to the block menu (which opens itself once this panel is closed).
                if (Model.Open && OVRInput.GetDown(OVRInput.Button.Start, OVRInput.Controller.LTouch)) Model.SetOpen(false);
            }
            Bubbles.Prune(dnow);

            if (Model.Open)
            {
                _layout = Model.Layout();
                PlacePanel();
                Pointer();
                if (Model.CapturesRight)
                {
                    if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch)) Act(Model.Click(_layout, dnow));
                    Model.Stick(OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch).y, dnow);
                    if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.RTouch) && Model.StickClick(dnow)) Haptic(0.2f);
                }
                Background(dnow);
            }
            else Model.Point(PanelHit.Nothing, false);
            _panel.gameObject.SetActive(Model.Open);
            _laser.gameObject.SetActive(Model.CapturesRight);
            if (Model.Open) Draw();
            SaveHistory();
            FlushOutbox();   // [operator]
        }

        /// <summary>[operator] Sends what the Marks tab, the container view and the sign editor queued (dropped while offline).</summary>
        void FlushOutbox()
        {
            if (Model.Outbox.Count == 0) return;
            foreach (var m in Model.Outbox)
                if (Send(m) < 0 && (string)m["type"] != "container_close") { Model.Note = "not connected"; break; }
            Model.Outbox.Clear();
        }

        /// <summary>The last aimed block is the pointer for a while, so aiming and then pointing at the panel works.</summary>
        void TrackPointer(float now)
        {
            var b = AimedBlock?.Invoke();
            if (b.HasValue) { _pointer = b; _pointerWorld = WorldName?.Invoke(); _pointerAt = now; }
            else if (_pointer.HasValue && now - _pointerAt > PointerSeconds) _pointer = null;
            Model.PointerLabel = _pointer.HasValue ? $"@ {_pointer.Value.x} {_pointer.Value.y} {_pointer.Value.z}" : null;
        }

        /// <summary>Completions after typing, and the World/Players tab queries.</summary>
        void Background(double now)
        {
            if (Model.CommandsOn && Model.Tab == OpTab.Console)
            {
                string due = Model.Console.CompletionDue(now);
                if (due != null) { long seq = Send(ControlProtocol.CommandComplete(due)); if (seq >= 0) { Model.Console.CompletionSent(seq, due); _queries[seq] = "complete"; } }
            }
            string q = Model.QueryDue(now);
            if (q == "world_query") { long s = Send(ControlProtocol.WorldQuery()); if (s >= 0) _queries[s] = q; }
            else if (q == "players_query") { long s = Send(ControlProtocol.PlayersQuery()); if (s >= 0) _queries[s] = q; }
            if (_queries.Count > 64) _queries.Clear();
        }

        long Send(Dictionary<string, object> body) => SendTracked?.Invoke(body) ?? -1;

        /// <summary>The keyboard's Enter (and Send): what the focused field produced.</summary>
        public void Enter() => Act(Model.Enter(Time.unscaledTimeAsDouble));

        void Act(PanelAction a)
        {
            switch (a.Kind)
            {
                case PanelActionKind.Command:
                {
                    Vector3Int? at = a.AtPointer ? _pointer : null;
                    string label = a.AtPointer ? Model.PointerLabel : null;
                    long seq = Send(ControlProtocol.Command(a.Line, a.AtPointer ? _pointerWorld : null, at));
                    if (seq < 0) Model.Console.Refused(a.Line, "not connected");
                    else Model.Console.Sent(seq, a.Line, label);
                    Haptic(0.35f);
                    break;
                }
                case PanelActionKind.Chat:
                {
                    long seq = Send(ControlProtocol.ChatSend(a.Line));
                    if (seq < 0) Model.Note = "not connected"; else _queries[seq] = "chat";
                    Haptic(0.35f);
                    break;
                }
                case PanelActionKind.Refused: Haptic(0.08f); break;
                case PanelActionKind.None: break;
                // [operator] jumps and follow requests are the app's (NavController), bookmark/container/sign messages are in the Outbox
                case PanelActionKind.Jump: JumpTo?.Invoke(a.Line, a.Pos); Haptic(0.35f); break;
                case PanelActionKind.Follow: Follow?.Invoke(a.Line, a.Name, a.Uuid); Haptic(0.3f); break;
                case PanelActionKind.Sent: Haptic(0.3f); break;
                default: Haptic(0.15f); break;
            }
        }

        void SaveHistory()
        {
            int n = Model.Console.History.Count;
            if (n == _historySaved || _historyPath == null) return;
            _historySaved = n;
            try { File.WriteAllText(_historyPath, Model.Console.SaveHistory()); }
            catch (Exception e) { Debug.LogWarning("[Tabletop] console history: " + e.Message); }
        }

        void Haptic(float amplitude)
        {
            OVRInput.SetControllerVibration(0.6f, amplitude, OVRInput.Controller.RTouch);
            _hapticOff = Time.unscaledTime + 0.035f;
        }

        /// <summary>Beside the left hand, turned to face the head, eased (as the block menu).</summary>
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

        void Pointer()
        {
            if (!RightHand || _layout == null) { Model.Point(PanelHit.Nothing, false); return; }
            Vector3 o = RightHand.position, d = RightHand.forward, n = _panel.forward;
            float den = Vector3.Dot(d, n);
            var hit = PanelHit.Nothing;
            if (Mathf.Abs(den) > 1e-4f)
            {
                float t = Vector3.Dot(_panel.position - o, n) / den;
                if (t > 0 && t < MaxRayM)
                {
                    _hitLocal = _panel.InverseTransformPoint(o + d * t);
                    float x = _hitLocal.x + _layout.Width / 2, y = _layout.Height / 2 - _hitLocal.y;
                    hit = OperatorPanelModel.HitTest(_layout, x, y);
                    if (hit.Part == PanelPart.None && x > -0.01f && y > -0.01f && x < _layout.Width + 0.01f && y < _layout.Height + 0.01f) hit = new PanelHit(PanelPart.Panel, 0);
                    if (hit.Part != PanelPart.None) { _laser.SetPosition(0, o); _laser.SetPosition(1, o + d * t); }
                }
            }
            Model.Point(hit, hit.Part != PanelPart.None);
        }

        // ---------- drawing ----------
        Vector3 L(float x, float y, float z) => new Vector3(x - _layout.Width / 2, _layout.Height / 2 - y, z);

        void Box(PanelMesh m, MenuRect r, Color32 c, float z = 0f, float inset = 0f) =>
            m.Quad(L(r.X + inset, r.Y + inset, z), L(r.X + r.W - inset, r.Y + inset, z), L(r.X + r.W - inset, r.Y + r.H - inset, z), L(r.X + inset, r.Y + r.H - inset, z), c);

        void Draw()
        {
            _b.Clear(); _o.Clear(); _ic.Clear(); _used.Clear();
            if (Icons != null && _iconMat.mainTexture != Icons.Texture) _iconMat.mainTexture = Icons.Texture;
            var Lo = _layout;
            Box(_b, new MenuRect(0, 0, Lo.Width, Lo.Height), PanelBg, 0.0004f);
            foreach (var it in Lo.Items)
            {
                bool hover = Model.Hover.Part == it.Part && Model.Hover.Index == it.Index && it.Part != PanelPart.None;
                string key = it.Part + "" + it.Index;
                switch (it.Part)
                {
                    case PanelPart.None:   // a row label
                        Label("lbl" + it.Label, it.Label, it.R.X + 0.002f, it.R.CY, 0.0015f, TextAnchor.MiddleLeft, TextDim);
                        break;
                    case PanelPart.ScrollUp:
                    case PanelPart.ScrollDown:
                    {
                        Box(_b, it.R, hover ? Hover : Slot, 0, 0.001f);
                        float cx = it.R.CX, cy = it.R.CY, s = it.R.W * 0.25f, dir = it.Part == PanelPart.ScrollUp ? -1 : 1;
                        _b.Tri(L(cx - s, cy - dir * s * 0.6f, -0.0003f), L(cx + s, cy - dir * s * 0.6f, -0.0003f), L(cx, cy + dir * s * 0.8f, -0.0003f), Arrow);
                        break;
                    }
                    case PanelPart.Key:
                    {
                        // The shared keyboard (World/KeyboardModel.cs): shift lit while on, enter as the accent, as in the block menu.
                        Box(_b, it.R, hover ? Hover : it.Active || it.Index == KeyboardModel.KeyEnter ? Active : Slot, 0, 0.0012f);
                        Label("key" + it.Index, it.Label, it.R.CX, it.R.CY, it.Label.Length > 1 ? 0.0016f : 0.0021f, TextAnchor.MiddleCenter, TextMain);
                        break;
                    }
                    // [operator] the Marks field, container slots (icon + count), the sign's dye swatches
                    case PanelPart.Field:
                        Box(_b, it.R, hover && !it.Active ? Hover : Field, 0, 0.0006f);
                        if (it.Active) Box(_b, new MenuRect(it.R.X, it.R.Y + it.R.H - 0.0012f, it.R.W, 0.0012f), Active, -0.0002f);
                        Label(key, Short(it.Label, 64), it.R.X + 0.003f, it.R.CY, 0.0014f, TextAnchor.MiddleLeft, it.Active ? TextMain : TextDim);
                        break;
                    case PanelPart.Slot:
                        Box(_b, it.R, it.Active ? Selected : hover ? Hover : Slot, 0, 0.0012f);
                        SlotIcon(it.Index, it.R);
                        if (it.Label.Length > 0) Label(key, it.Label, it.R.X + it.R.W - 0.0025f, it.R.Y + it.R.H - 0.002f, 0.0012f, TextAnchor.LowerRight, TextMain);
                        break;
                    case PanelPart.Swatch:
                        Box(_b, it.R, it.Active ? Cursor : hover ? Hover : Slot, 0, 0.0004f);
                        Box(_b, it.R, EntityCatalog.DyeColor(it.Label, BlockRules.FlatColor("minecraft:" + it.Label + "_wool")), -0.0002f, it.Active ? 0.0026f : 0.0016f);
                        break;
                    case PanelPart.PlayerRow:
                    case PanelPart.Rule:
                    case PanelPart.MarkRow:    // [operator]
                    case PanelPart.SignLine:   // [operator]
                    {
                        var c = it.Active ? (it.Part == PanelPart.Rule ? Active : Selected) : hover && it.Enabled ? Hover : it.Label.Length > 0 || it.Part == PanelPart.SignLine ? Slot : Disabled;
                        if (it.Part == PanelPart.Rule && it.Label.Length > 0 && !it.Enabled) c = it.Active ? new Color32(36, 70, 90, 220) : Disabled;
                        Box(_b, it.R, c, 0, 0.0008f);
                        if (it.Label.Length > 0)
                            Label(key, Short(it.Label, it.Part == PanelPart.Rule ? 30 : 64), it.R.X + 0.003f, it.R.CY, it.Part == PanelPart.Rule ? 0.00115f : 0.00135f,
                                  TextAnchor.MiddleLeft, it.Enabled || it.Part == PanelPart.PlayerRow ? TextMain : TextDim);
                        break;
                    }
                    default:
                    {
                        var c = !it.Enabled ? Disabled : it.Active ? Active : hover ? Hover : Slot;
                        Box(_b, it.R, c, 0, 0.0009f);
                        float size = it.Part == PanelPart.Tab ? 0.0017f : it.Label.Length > 10 ? 0.00115f : 0.0015f;
                        Label(key, Short(it.Label, it.Part == PanelPart.Chip || it.Part == PanelPart.Quick ? 22 : 16), it.R.CX, it.R.CY, size, TextAnchor.MiddleCenter, it.Enabled ? TextMain : TextDim);
                        break;
                    }
                }
            }
            DrawTab(Lo);
            if (Lo.Field.W > 0)
            {
                Box(_b, Lo.Field, Field, 0, 0.0006f);
                string prefix = Model.Focus == OperatorPanelModel.Field.Kick ? "kick " + Model.KickTarget + ": " : Model.Focus == OperatorPanelModel.Field.Console ? "/" : "";
                string text = Model.FocusText;
                int chars = OperatorPanelModel.CharsFor(Lo.Field.W - 0.006f) - prefix.Length - 1;
                string shown = text.Length > chars ? "…" + text.Substring(text.Length - chars + 1) : text;
                string ph = Model.Focus == OperatorPanelModel.Field.Chat ? (Model.ChatOn ? "say something..." : "chat is read-only") :
                            Model.Focus == OperatorPanelModel.Field.Console ? (Model.CommandsOn ? "type a command..." : "commands need auth.token") : "reason (optional)";
                bool empty = text.Length == 0;
                Label("field", empty ? prefix + ph : prefix + shown + "_", Lo.Field.X + 0.003f, Lo.Field.CY, 0.0015f, TextAnchor.MiddleLeft, empty ? TextDim : TextMain);
            }
            string note = Model.Note.Length > 0 ? Model.Note : HintLine();
            Label("note", Short(note, 110), Lo.Note.X + 0.002f, Lo.Note.CY, 0.00125f, TextAnchor.MiddleLeft, Model.Note.Length > 0 ? TextNote : TextDim);
            if (Model.CapturesRight)
            {
                float cx = _hitLocal.x + _layout.Width / 2, cy = _layout.Height / 2 - _hitLocal.y, s = 0.0025f;
                Box(_o, new MenuRect(cx - s, cy - s, 2 * s, 2 * s), Cursor, -0.002f);
            }
            _b.Apply(_bg); _o.Apply(_over); _ic.Apply(_icon);
            foreach (var kv in _texts) if (!_used.Contains(kv.Key) && kv.Value.gameObject.activeSelf) kv.Value.gameObject.SetActive(false);
        }

        string HintLine()
        {
            string ptr = Model.PointerLabel ?? "no block aimed";
            // [operator] the container view, the sign editor, the Marks tab
            if (Model.Mode == PanelMode.Container)
                return !Model.Container.CanEdit ? (Model.Container.View == null ? "waiting for the server..." : "view only (interact.containers: view)")
                     : "select a slot; Put adds " + Model.Container.Count + " " + ContainerModel.Label(ContainerModel.ItemOf(Model.HotbarSelection, Model.Registry)) + " (hotbar)";
            if (Model.Mode == PanelMode.Sign) return "pick a line and type (enter = next line), then Save; Front / Back switch sides";
            switch (Model.Tab)
            {
                case OpTab.Marks: return !Model.Marks.Welcome.Marks ? "jumps work; bookmarks need marks_v1 on the server" : "a world, Spawn, a bookmark or typed x z: the view jumps there (same width)";
                case OpTab.Chat: return Model.ChatOn ? $"as {Model.Welcome.ChatName} (Java players see [Giant] {Model.Welcome.ChatName})" : "chat: " + (Model.Connected ? "read-only" : "not connected");
                case OpTab.Console: return Model.CommandsOn ? $"stick click = tab   pointer {ptr}" : "commands " + Model.CommandsOffWhy;
                case OpTab.Players: return Model.CommandsOn ? $"pointer {ptr}" : "buttons need auth.token on the server";
                case OpTab.World: return Model.CommandsOn ? $"pointer {ptr}" : "buttons need auth.token on the server";
                default: return Model.Welcome.Status ? "updates every 2 s" : "the server sends no status (status_v1)";
            }
        }

        /// <summary>[operator] A container slot's item icon (from the block menu's atlas), or a flat placeholder while it renders.</summary>
        void SlotIcon(int slot, MenuRect r)
        {
            var v = Model.Container.View;
            if (v == null) return;
            var (item, _) = v.Slot(slot);
            float inset = r.W * 0.14f;
            if (item == null)
            {
                string what = ContainerModel.SlotLabel(v.Block, slot);
                if (what != null) Label("slotlbl" + slot, what, r.CX, r.CY, 0.0009f, TextAnchor.MiddleCenter, TextDim);
                return;
            }
            var (state, model) = ContainerModel.IconOf(item, Model.Registry);
            if (Icons != null && Icons.Get(state, model, 0, out var uv))
                _ic.QuadUV(L(r.X + inset, r.Y + inset, -0.0004f), L(r.X + r.W - inset, r.Y + inset, -0.0004f), L(r.X + r.W - inset, r.Y + r.H - inset, -0.0004f), L(r.X + inset, r.Y + r.H - inset, -0.0004f), uv);
            else Box(_b, new MenuRect(r.X + inset * 1.6f, r.Y + inset * 1.6f, r.W - inset * 3.2f, r.H - inset * 3.2f), BlockRules.FlatColor(state), -0.0003f);
        }

        void DrawTab(PanelLayout Lo)
        {
            if (Model.Mode != PanelMode.Tabs || Model.Tab == OpTab.Marks)   // [operator] the header line; waiting / empty notes
            {
                Label("modehdr", Short(Model.ModeHeader(), 110), Lo.Header.X + 0.002f, Lo.Header.CY, 0.00125f, TextAnchor.MiddleLeft, TextMain);
                if (Model.Mode == PanelMode.Container && Model.Container.View == null)
                    Label("empty", Model.Container.Waiting ? "opening..." : "closed", Lo.Text.CX, Lo.Text.CY, 0.0015f, TextAnchor.MiddleCenter, TextDim);
                if (Model.Mode == PanelMode.Tabs && Model.Marks.Bookmarks.Marks.Count == 0)
                    Label("empty", Model.Marks.Welcome.Marks ? "no bookmarks yet: Add mark saves where the view is" : "the server has no shared bookmarks (marks_v1)",
                          Lo.Text.CX, Lo.Text.CY, 0.0013f, TextAnchor.MiddleCenter, TextDim);
                return;
            }
            switch (Model.Tab)
            {
                case OpTab.Chat:
                {
                    Model.Log.Page(Lo.TextRows, Lo.TextChars, Model.Scroll, _chatLines);
                    for (int i = 0; i < Lo.TextRows; i++)
                    {
                        string t = i < _chatLines.Count ? _chatLines[i].Text : "";
                        Color c = i < _chatLines.Count ? (Color)ChatLog.ColorOf(_chatLines[i].Tone) : TextMain;
                        TextRow("chat" + i, t, Lo, i, c);
                    }
                    if (Model.Log.Count == 0) Label("empty", "no chat yet", Lo.Text.CX, Lo.Text.CY, 0.0015f, TextAnchor.MiddleCenter, TextDim);
                    break;
                }
                case OpTab.Console:
                {
                    Model.Console.Page(Lo.TextRows, Lo.TextChars, Model.Scroll, _consoleLines);
                    for (int i = 0; i < Lo.TextRows; i++)
                    {
                        if (i >= _consoleLines.Count) { TextRow("con" + i, "", Lo, i, TextMain); continue; }
                        var l = _consoleLines[i];
                        Color c = l.Kind == ConsoleLineKind.Command ? TextCmd : l.Kind == ConsoleLineKind.Error ? TextErr : l.Kind == ConsoleLineKind.Pending ? TextDim :
                                  l.Kind == ConsoleLineKind.Note ? TextNote : TextOk;
                        TextRow("con" + i, l.Text, Lo, i, c);
                    }
                    if (Model.Console.Entries.Count == 0) Label("empty", Model.CommandsOn ? "run a command: type it, or a quick button" : "commands need auth.token on the server", Lo.Text.CX, Lo.Text.CY, 0.0014f, TextAnchor.MiddleCenter, TextDim);
                    break;
                }
                case OpTab.Players:
                    if (Model.Players == null || Model.Players.Players.Count == 0)
                        Label("empty", Model.Players == null ? "waiting for players_info..." : "nobody online", Lo.Text.CX, Lo.Text.CY, 0.0015f, TextAnchor.MiddleCenter, TextDim);
                    break;
                case OpTab.World:
                    Label("worldhdr", OperatorPanelModel.WorldText(Model.World), Lo.Header.X + 0.002f, Lo.Header.Y + 0.001f, 0.00125f, TextAnchor.UpperLeft, TextMain);
                    if (Model.World == null) Label("empty", "waiting for world_info...", Lo.Text.CX, Lo.Text.CY, 0.0015f, TextAnchor.MiddleCenter, TextDim);
                    break;
                case OpTab.Server:
                    Label("server", OperatorPanelModel.StatusText(Model.Status), Lo.Text.X + 0.002f, Lo.Text.Y + 0.002f, 0.00135f, TextAnchor.UpperLeft, TextMain);
                    break;
            }
        }

        void TextRow(string key, string text, PanelLayout Lo, int row, Color c) =>
            Label(key, text, Lo.Text.X + 0.002f, Lo.Text.Y + (row + 0.5f) * OperatorPanelModel.LineH, 0.00125f, TextAnchor.MiddleLeft, c);

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
                t.fontSize = 48; t.richText = false;   // chat text is shown as typed: no markup from players
                _texts[key] = t;
            }
            _used.Add(key);
            if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            if (t.text != text) t.text = text;
            t.characterSize = size; t.anchor = anchor;
            t.color = QualitySettings.activeColorSpace == ColorSpace.Linear ? color.linear : color;
            t.transform.localPosition = L(x, y, -0.001f);
        }

        void OnDestroy()
        {
            Font.textureRebuilt -= OnFontRebuilt;
            foreach (var m in new[] { _bg, _over }) if (m) Destroy(m);
            if (_panel) Destroy(_panel.gameObject);
        }
    }
}
