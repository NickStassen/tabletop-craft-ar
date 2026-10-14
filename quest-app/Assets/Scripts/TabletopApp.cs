// Bootstrap: reads config, owns the link, routes protocol messages, and builds the TabletopAnchor hierarchy:
//   TabletopAnchor (table centre, +Y = up) > RegionRoot (region corner, unscaled) > Blocks (scale = block_size_m)
//                                                                               > Players
//                                                                               > Entities ([entities])
// Config file (optional): <persistentDataPath>/tabletop.json, e.g.
//   {"server":"ws://192.168.1.50:8765/tabletop","token":"","block_size_m":0.01,"skin_player":""}
// [area] optional: "area_width": 192 (start at that width preset, block size fitted to the table),
//   "batch_columns": 4 (sections per mesh batch side, TabletopRenderer)
// [talk] optional: "operator_name": "Nick" (the name in chat, else skin_player), "quick_commands": ["time set day"]
using System;
using System.Collections.Generic;
using System.IO;
using TabletopCraft.Net;
using TabletopCraft.World;
using TabletopCraft.XR;
using UnityEngine;

namespace TabletopCraft
{
    public sealed class TabletopApp : MonoBehaviour
    {
        [Header("Defaults (overridden by tabletop.json)")]
        public string ServerUrl = "ws://192.168.1.50:8765/tabletop";
        public string Token = "";
        public float BlockSizeM = 0.01f;
        public string SkinPlayer = "";
        public bool SendPoses = true;

        [Header("Scene refs (auto-found on OVRCameraRig if empty)")]
        public Transform Head, LeftHand, RightHand;
        public Material OpaqueMaterial, TransparentMaterial;
        [Tooltip("Unlit/Color material for player capsules and area handles. Primitives default to Standard, which isn't in the build.")]
        public Material MarkerMaterial;

        LinkClient _link;
        readonly WorldModel _world = new WorldModel();
        ResourcePackAtlas _atlas;
        Transform _anchor, _regionRoot, _blocks;
        TabletopRenderer _renderer;
        PlayerMarkers _players;
        PoseSender _poses;
        TablePlacement _placement;
        AreaSetupMode _area;
        BlockEditMode _edit;   // block editing from the headset (XR/BlockEditMode.cs)
        // [menu] block menu + hotbar (XR/BlockMenu.cs), its icons, the server's block list, and the HUD that replaced
        // the head-locked status text (XR/Hud.cs: toasts, area-setup panel, optional small status).
        BlockMenu _menu;
        BlockIconAtlas _icons;
        BlockRegistry _registry;
        Hud _hud;
        bool _showHud;
        readonly LinkWatch _linkWatch = new LinkWatch();
        string _lastAreaStatus = "", _lastEditResult = "";
        string _session;
        float _nextPing; int _pingId; long _rttMs = -1;
        float _avatarConfigAt = -1;   // send avatar_config once scaling settles, not every frame
        string _lastError = "";
        AndroidJavaObject _wifiLock;
        // [nav] moving around, streaming, clip caps
        NavController _nav;
        ClipCaps _caps;
        readonly SnapshotAssembler _snaps = new SnapshotAssembler();
        readonly Queue<Dictionary<string, object>> _held = new Queue<Dictionary<string, object>>();
        int _modelVersion;
        // [entities] boats, mobs, items (EntityRenderer), block actions (ActionEffects), their textures
        EntityRenderer _entities;
        ActionEffects _effects;
        EntityTextures _entityTex;
        readonly List<GameAction> _actions = new List<GameAction>();
        // [area] visible-width presets (left stick click; Presets/Current/Request for a menu), tabletop.json "area_width"
        public AreaSizeControl AreaSize { get; private set; }
        int _areaWidth;
        // [talk] operator panel on X (chat, console, players, world, server) and chat on the wrist / over players
        OperatorPanel _ops;
        ChatView _chatView;
        string _operatorName;
        readonly List<string> _quickCommands = new List<string>();

        void Start()
        {
            LoadConfig();
            var rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig != null)
            {
                if (!Head) Head = rig.centerEyeAnchor;
                if (!LeftHand) LeftHand = rig.leftHandAnchor;
                if (!RightHand) RightHand = rig.rightHandAnchor;
            }
            if (!Head && Camera.main) Head = Camera.main.transform;

            _anchor = new GameObject("TabletopAnchor").transform;
            _regionRoot = new GameObject("RegionRoot").transform; _regionRoot.SetParent(_anchor, false);
            _blocks = new GameObject("Blocks").transform; _blocks.SetParent(_regionRoot, false);
            var playersT = new GameObject("Players").transform; playersT.SetParent(_regionRoot, false);

            _atlas = new ResourcePackAtlas();
            _atlas.TryLoad(ResourcePackAtlas.DefaultPath);
            if (!OpaqueMaterial) OpaqueMaterial = MakeMat(false);
            if (!TransparentMaterial) TransparentMaterial = MakeMat(true);
            if (!MarkerMaterial) MarkerMaterial = new Material(Shader.Find("Sprites/Default"));

            _renderer = _blocks.gameObject.AddComponent<TabletopRenderer>();
            _renderer.Model = _world; _renderer.Atlas = _atlas; _renderer.OpaqueMaterial = OpaqueMaterial; _renderer.TransparentMaterial = TransparentMaterial;
            _players = playersT.gameObject.AddComponent<PlayerMarkers>();
            _players.Model = _world; _players.MarkerMaterial = MarkerMaterial; _players.Atlas = _atlas;
            // [entities] non-player entities next to the players, block actions in block space
            _entityTex = new EntityTextures { Atlas = _atlas };
            // Entity models: the user's entity_models.json next to pack.zip (generated from their own client jar). Parsed
            // on a worker; until then (or without it) entities are boxes, and EntityRenderer rebuilds them when it lands.
            string modelsPath = Path.Combine(Application.persistentDataPath, EntityModels.FileName);
            System.Threading.Tasks.Task.Run(() => { EntityModels.LoadFile(modelsPath); Debug.Log("[Tabletop] " + EntityModels.Status); });
            var entitiesT = new GameObject("Entities").transform; entitiesT.SetParent(_regionRoot, false);
            _entities = entitiesT.gameObject.AddComponent<EntityRenderer>();
            _entities.Model = _world; _entities.Textures = _entityTex;
            _players.Textures = _entityTex; _players.Entities = _entities;
            _effects = new GameObject("Effects").AddComponent<ActionEffects>();
            _effects.transform.SetParent(_blocks, false);
            _effects.Model = _world; _effects.Atlas = _atlas; _effects.Textures = _entityTex; _effects.BlocksMaterial = OpaqueMaterial;

            // Eye in water/lava/powder snow/inside a block: fog, passthrough tint, the water's inside faces (EyeEffects.cs).
            var eye = gameObject.AddComponent<EyeEffects>();
            eye.Head = Head; eye.Blocks = _blocks; eye.Model = _world; eye.BlockRenderer = _renderer;

            _placement = gameObject.AddComponent<TablePlacement>();
            _placement.Anchor = _anchor; _placement.Head = Head; _placement.RightHand = RightHand;
            _placement.OnScaleChanged = k => SetBlockSize(Mathf.Clamp(BlockSizeM * k, 0.002f, 0.1f), false);

            _area = gameObject.AddComponent<AreaSetupMode>();
            _area.RegionRoot = _regionRoot; _area.RightHand = RightHand; _area.Placement = _placement; _area.MarkerMaterial = MarkerMaterial;
            _area.BlockSize = () => BlockSizeM; _area.World = () => _world;
            _area.Send = b => _link.Send(b);
            _area.ApplyBlockSize = bs => SetBlockSize(bs, false);

            // [nav] solid sides for the clipped box, and moving around (XR/NavController)
            _caps = new GameObject("Caps").AddComponent<ClipCaps>();
            _caps.transform.SetParent(_blocks, false);
            _caps.Model = _world; _caps.Faces = _atlas; _caps.OpaqueMaterial = OpaqueMaterial; _caps.TransparentMaterial = TransparentMaterial;
            _nav = gameObject.AddComponent<NavController>();
            _nav.Anchor = _anchor; _nav.RegionRoot = _regionRoot; _nav.Head = Head; _nav.Renderer = _renderer; _nav.Caps = _caps;
            _nav.BlockSize = () => BlockSizeM; _nav.Suspended = () => _area.Active; _nav.Send = b => _link.Send(b);
            _area.Nav = _nav.State;
            eye.Caps = _caps;   // water cuts of the visible window get inside faces too (EyeEffects.cs)
            // [area] width presets: left stick click, or a menu through Presets/Current/Request (XR/AreaSizeControl.cs)
            AreaSize = gameObject.AddComponent<AreaSizeControl>();
            AreaSize.Nav = _nav.State; AreaSize.Renderer = _renderer; AreaSize.Blocked = () => _area.Active; AreaSize.Send = b => _link.Send(b);
            AreaSize.BlockSize = () => BlockSizeM; AreaSize.SetBlockSize = bs => SetBlockSize(bs, false);
            AreaSize.TableSize = () => _placement.TableSize; AreaSize.StartWidth = _areaWidth;

            // Block editing: right trigger break, left trigger place, B pick (XR/BlockEditMode.cs, PROTOCOL.md 4.20).
            _edit = gameObject.AddComponent<BlockEditMode>();
            _edit.Blocks = _blocks; _edit.RightHand = RightHand; _edit.Head = Head; _edit.World = () => _world; _edit.Atlas = _atlas;
            _edit.OpaqueMaterial = OpaqueMaterial; _edit.TransparentMaterial = TransparentMaterial; _edit.Nav = _nav.State;
            _edit.Send = b => _link.Send(b); _edit.Online = () => _link.Connected;
            _edit.Blocked = () => _area.Active || _menu.CapturesRight;   // [menu] the right ray on the menu clicks the menu
            _edit.ModelChanged = () => _caps.ModelVersion = ++_modelVersion;   // optimistic edits re-slice the clip caps too

            // [menu] Block menu (left ≡): categories from the server's block_registry, search, hotbar, Info tab; icons
            // rendered from pack.zip on a worker and cached under persistentDataPath/icons. While the right ray is on the
            // panel, editing, gliding, turning and scaling pause (the trigger and right stick belong to the menu).
            _icons = new BlockIconAtlas(ResourcePackAtlas.DefaultPath, Path.Combine(Application.persistentDataPath, "icons"));
            _menu = gameObject.AddComponent<BlockMenu>();
            _menu.Head = Head; _menu.LeftHand = LeftHand; _menu.RightHand = RightHand; _menu.Icons = _icons;
            _menu.Blocked = () => _area.Active || (_ops != null && _ops.Open); _menu.CanEdit = () => _edit.Editor.ServerAllows; _menu.InfoText = StatusText;   // [talk] X and the menu button switch panels
            _menu.SelectedChanged = s => _edit.Editor.Selected = s;
            _menu.Model.ShowHud = _showHud;
            _menu.HudChanged = on => _hud.ShowHud = on;
            _edit.Picked = s => _menu.OnPicked(s);
            // [menu+area] the menu's area-size row drives AreaSizeControl; the left-stick size click is off while the menu is open
            _menu.AreaPresets = AreaSize.Presets; _menu.AreaCurrent = AreaSize.Current; _menu.AreaRequest = AreaSize.Request;
            AreaSize.Blocked = () => _area.Active || _menu.Open || _ops.Open;   // [talk]
            _nav.Suspended = () => _area.Active || _menu.CapturesRight || _ops.CapturesRight;
            _placement.StickBlocked = () => _menu.CapturesRight || _ops.CapturesRight;
            // [talk] Operator panel (X): chat, console, players, world, server (XR/OperatorPanel.cs). The right ray on it
            // pauses editing, gliding, turning and scaling like the block menu; "@ pointer" is the last aimed block.
            _ops = gameObject.AddComponent<OperatorPanel>();
            _ops.Head = Head; _ops.LeftHand = LeftHand; _ops.RightHand = RightHand;
            _ops.Blocked = () => _area.Active;
            _ops.Online = () => _link.Connected;
            _ops.SendTracked = b => _link.SendTracked(b);
            _ops.WorldName = () => _world.World;
            _ops.AimedBlock = () => _edit.Target.Hit && _world.Loaded ? _world.Origin + _edit.Target.Block : (Vector3Int?)null;
            _ops.Toast = (t, k) => _hud.Toast(t, k);
            _edit.Blocked = () => _area.Active || _menu.CapturesRight || _ops.CapturesRight;
            _chatView = gameObject.AddComponent<ChatView>();
            _chatView.Head = Head; _chatView.LeftHand = LeftHand; _chatView.Log = _ops.Model.Log; _chatView.Bubbles = _ops.Bubbles;
            _chatView.Hidden = () => _ops.Open || _menu.Open || _area.Active;
            _chatView.FigureLabel = uuid => _players.LabelOf(uuid);
            _hud = gameObject.AddComponent<Hud>();
            _hud.Head = Head; _hud.LeftHand = LeftHand; _hud.StatusText = StatusText; _hud.ShowHud = _showHud;
            _hud.AreaActive = () => _area.Active; _hud.AreaText = () => _area.Status;

            Protocol.OperatorName = !string.IsNullOrEmpty(_operatorName) ? _operatorName : SkinPlayer;   // [talk] hello.operator_name
            _link = new LinkClient(ServerUrl, Token);
            _link.Start();
            if (SendPoses)
            {
                _poses = gameObject.AddComponent<PoseSender>();
                _poses.Link = _link; _poses.RegionRoot = _regionRoot; _poses.Head = Head; _poses.LeftHand = LeftHand; _poses.RightHand = RightHand;
                _poses.SnapshotId = () => _world.SnapshotId;   // [nav] the frame the pose is in (PROTOCOL.md 4.7)
            }
            SetBlockSize(BlockSizeM, false);
            AcquireWifiLock();
            Debug.Log($"[Tabletop] server {ServerUrl}, pack: {_atlas.Status}");
        }

        void LoadConfig()
        {
            string p = Path.Combine(Application.persistentDataPath, "tabletop.json");
            try
            {
                if (!File.Exists(p)) { Debug.Log("[Tabletop] no " + p + ", using inspector defaults"); return; }
                var c = Json.Parse(File.ReadAllText(p)) as Dictionary<string, object>;
                ServerUrl = c.S("server") ?? ServerUrl;
                Token = c.S("token") ?? Token;
                BlockSizeM = (float)c.D("block_size_m", BlockSizeM);
                SkinPlayer = c.S("skin_player") ?? SkinPlayer;
                BlockShading.SmoothLighting = c.B("smooth_lighting", true);       // lighting look (BlockShading.cs)
                BlockShading.Brightness = (float)c.D("brightness", BlockShading.Brightness);   // vanilla slider, default 1 = Bright
                BlockShading.MinLight = (float)c.D("min_light", BlockShading.MinLight);         // brightness of light level 0
                _showHud = c.B("show_hud", false);   // [menu] small head-locked status (also "Show HUD" in the menu's Info tab)
                _areaWidth = c.I("area_width", 0);                                              // [area] starting width preset
                _operatorName = c.S("operator_name");                                           // [talk] the name in chat
                foreach (var q in c.A("quick_commands") ?? new List<object>()) if (q is string qs) _quickCommands.Add(qs);   // [talk] Console quick buttons
                BatchRules.Columns = Math.Max(1, c.I("batch_columns", BatchRules.Columns));     // [area] sections per batch side
            }
            catch (Exception e) { Debug.LogWarning("[Tabletop] bad tabletop.json: " + e.Message); }
        }

        static Material MakeMat(bool transparent)
        {
            var sh = Shader.Find(transparent ? "TabletopCraft/BlocksTransparent" : "TabletopCraft/Blocks");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            return new Material(sh);
        }

        void SetBlockSize(float bs, bool fromServer)
        {
            BlockSizeM = bs;
            _blocks.localScale = Vector3.one * bs;
            _players.BlockSizeM = bs;
            _entities.BlockSizeM = bs;   // [entities]
            RecenterRegion();
            _avatarConfigAt = Time.unscaledTime + 0.3f;
        }

        /// <summary>Put the visible window's bottom centre on the anchor (NavController; RegionRoot = region corner, Z mirrored).</summary>
        void RecenterRegion()
        {
            if (!_world.Loaded) return;
            _nav.Place();
        }

        void Update()
        {
            // [nav] Snapshots decode on a worker; block updates and newer snapshots wait, in order, until it is applied.
            if (_snaps.TryTake(out var decoded)) ApplySnapshot(decoded);
            int budget = 50;
            while (budget-- > 0)
            {
                Dictionary<string, object> m;
                bool fromHeld = !_snaps.Busy && _held.Count > 0;
                if (fromHeld) m = _held.Dequeue();
                else if (!_link.Inbound.TryDequeue(out m)) break;
                string type = m.S("type");
                if (!fromHeld && (_snaps.Busy || _held.Count > 0) && (type == "block_update" || type == "chunk_snapshot")) { _held.Enqueue(m); continue; }
                Handle(m);
            }
            if (_link.Connected && Time.unscaledTime > _nextPing) { _nextPing = Time.unscaledTime + 2f; _link.Send(Protocol.Ping(_pingId++)); }
            if (_avatarConfigAt >= 0 && Time.unscaledTime >= _avatarConfigAt)
            {
                _avatarConfigAt = -1;   // while disconnected, welcome sends the current block size anyway
                if (_link.Connected) _link.Send(Protocol.AvatarConfig(true, BlockSizeM, SkinPlayer, true, "Tabletop Giant"));
            }
            Notify();   // [menu] toasts instead of the head-locked status text
        }

        /// <summary>[menu] The full status (the block menu's Info tab, and the small HUD when "Show HUD" is on).</summary>
        string StatusText() =>
            $"LINK {(_link.Connected ? "OK" : _link.Status)}  RTT {(_rttMs < 0 ? "-" : _rttMs + "ms")}\n" +
            $"WORLD {(_world.Loaded ? $"{_world.Dims.x}x{_world.Dims.y}x{_world.Dims.z} @ {_world.Origin}" : "-")}  PACK {(_atlas.HasPack ? "yes" : "flat")}  LIGHT {LightStatus()}\n" +
            $"TABLE {_placement.Status}\nPOSES {(_poses ? _poses.Sent : 0)}  MESH {_renderer.Quads / 1000}k faces  ENTITIES {_entities.Count}{(EntityModels.Loaded ? "" : " (boxes)")}" +
            (_renderer.Pending > 0 ? $" ({_renderer.Pending} sections pending)" : "") + $"  {_lastError}\n" +
            _nav.Status + "\n" +   // [nav]
            AreaSize.Status + "\n" +   // [area]
            (_area.Active ? _area.Status : (_area.Status.Length > 0 ? _area.Status : "Y (left) = set up area   left stick = move") + "\n" + _edit.Status) +
            $"\nBLOCKS {(_registry != null ? _registry.Count + " placeable" : "-")}  {_icons.Status}" +
            $"\nTALK {TalkStatus()}";   // [talk]

        /// <summary>[talk] Chat, commands and status as the server granted them (X opens the operator panel).</summary>
        string TalkStatus()
        {
            var w = _ops.Model.Welcome;
            return $"chat {(w.Chat ? (w.ChatSend ? "as " + w.ChatName : "read-only") : "off")}  commands {(w.Commands ? "on" : "off (needs auth.token)")}  status {(w.Status ? "on" : "off")}  X = panel";
        }

        /// <summary>[menu] Events that used to be read off the status text: link lost/restored, area results, local edit refusals.</summary>
        void Notify()
        {
            string link = _linkWatch.Update(_link.Connected);
            if (link != null) _hud.Toast(link, link.StartsWith("Link lost") ? ToastKind.Warn : ToastKind.Info);
            if (!_area.Active && _area.Status != _lastAreaStatus)
            {
                _lastAreaStatus = _area.Status;
                bool bad = _area.Status.StartsWith("server refused") || _area.Status.StartsWith("cannot");
                if (_area.Status.Length > 0) _hud.Toast(char.ToUpperInvariant(_area.Status[0]) + _area.Status.Substring(1), bad ? ToastKind.Error : ToastKind.Info);
            }
            else if (_area.Active) _lastAreaStatus = _area.Status;
            string r = _edit.Editor.Result;
            if (r != _lastEditResult)
            {
                _lastEditResult = r;
                if (r.StartsWith("can't") || r.StartsWith("not connected") || r.StartsWith("editing is off") || r.StartsWith("the server has no"))
                    _hud.Toast(char.ToUpperInvariant(r[0]) + r.Substring(1), ToastKind.Warn);
            }
        }

        /// <summary>Status text: server light (smooth/flat/off), biome count and whether the pack has colour maps.</summary>
        string LightStatus() =>
            (_world.Light == null ? "off" : BlockShading.SmoothLighting ? "smooth" : "flat") +
            (_world.Biomes != null ? $" BIOMES {_world.Biomes.Palette.Length}{(BiomeColors.GrassMap != null ? "" : " (no colormap)")}" : "");

        void Handle(Dictionary<string, object> m)
        {
            switch (m.S("type"))
            {
                case "welcome":
                    Debug.Log($"[Tabletop] welcome from {m.S("server")} {m.S("server_version")} mc {m.S("mc_version")}");
                    _session = m.S("session");
                    _edit.Editor.ServerAllows = m.A("caps")?.Contains(Protocol.CapEdit) == true;   // block editing granted
                    _area.State.Limits = RegionLimits.From(m.O("limits"), RegionLimits.Default);
                    _nav.OnWelcome(m);   // [nav] server caps, limits, build height
                    AreaSize.OnLimits(m.O("limits"));   // [area] max_view_width
                    _ops.OnWelcome(m, _quickCommands);   // [talk] chat, commands, status granted
                    // Light from the server makes faces merge less: scale the area-setup face estimate (soft cap).
                    AreaMath.LightFactor = AreaMath.LightFactorFor(m.A("caps")?.Contains(Protocol.CapLight) == true, BlockShading.SmoothLighting);
                    _link.Send(Protocol.AvatarConfig(true, BlockSizeM, SkinPlayer, true, "Tabletop Giant"));
                    break;
                case "region_info":
                    _area.OnRegionInfo(RegionInfo.From(m));
                    _nav.OnLimits(m.O("limits"));   // [nav]
                    AreaSize.OnLimits(m.O("limits"));   // [area]
                    break;
                case "region_changed":
                    // A snapshot of the new region follows; just note who changed it (shared region, last writer wins).
                    _area.OnRegionChanged(m, m.S("by_session") == _session);
                    break;
                case "chunk_snapshot":
                    // [nav] decoded (and compared with the current region) on a worker; ApplySnapshot runs when it's done.
                    try { _snaps.Offer(m, _world, _renderer.Unsettled()); }
                    catch (Exception e) { BadSnapshot(m.I("snapshot_id"), e.Message); }
                    break;
                case "block_update":
                    _edit.Editor.OnBlockUpdate(_world, m);   // the server's values replace block-edit guesses
                    if (_world.ApplyBlockUpdate(m)) _caps.ModelVersion = ++_modelVersion;   // [nav] caps re-slice
                    break;
                case "player_positions":
                    var list = new List<PlayerInfo>();
                    foreach (var o in m.A("players")) list.Add(PlayerInfo.From((Dictionary<string, object>)o));
                    _players.Apply(list);
                    break;
                case "entities": _entities.Apply(m); break;   // [entities] PROTOCOL.md 4.21
                case "actions":                                  // [entities] PROTOCOL.md 4.22
                    _actions.Clear();
                    GameAction.Parse(m, _actions);
                    foreach (var a in _actions)
                    {
                        if (a.Kind == ActionKind.Swing || a.Kind == ActionKind.Hurt) { if (!_entities.Act(a)) _players.Act(a); }
                        else _effects.Apply(a);
                    }
                    break;
                case "pong":
                    _rttMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)m.D("echo_t");
                    if (_pingId % 5 == 0) Debug.Log($"[Tabletop] rtt {_rttMs} ms");
                    break;
                case "ping":
                    _link.Send(new Dictionary<string, object> { ["type"] = "pong", ["id"] = m.I("id"), ["echo_t"] = (long)m.D("t") });
                    break;
                case "avatar_state":
                    Debug.Log($"[Tabletop] avatar enabled={m.B("enabled")} scale={m.D("scale")} note={m.S("note")}");
                    break;
                case "chat": case "command_result": case "command_output": case "command_suggestions":   // [talk] PROTOCOL.md 4.26-4.28
                case "server_status": case "world_info": case "players_info":
                    _ops.OnMessage(m);
                    break;
                case BlockRegistry.Type:   // [menu] every placeable block (PROTOCOL.md 4.24)
                    try
                    {
                        _registry = BlockRegistry.From(m);
                        _menu.SetRegistry(_registry);
                        _edit.Editor.Placeable = _registry.IsPlaceable;
                        Debug.Log($"[Tabletop] block registry: {_registry.Count} blocks in {_registry.CategoryIds.Count} categories, {_registry.Variants.Count} variants");
                    }
                    catch (Exception e) { Debug.LogWarning("[Tabletop] bad block_registry: " + e.Message); _hud.Toast("Bad block list from the server", ToastKind.Error); }
                    break;
                case "error":
                    if (_ops.OnError(m)) { Debug.Log($"[Tabletop] panel error {m.S("code")}: {m.S("message")}"); break; }   // [talk] the panel shows it
                    _lastError = $"{m.S("code")}: {m.S("message")}";
                    string areaBefore = _area.Status;   // [menu]
                    _area.OnError(m);
                    // [menu] a toast, unless area setup reports it itself (its status becomes a toast in Notify)
                    if (_area.Status == areaBefore && HudText.ForError(m.S("code"), m.S("message")) is string toast) _hud.Toast(toast, ToastKind.Error);
                    _edit.Editor.OnError(_world, m);   // undoes an optimistic edit the server refused
                    AreaSize.OnError(m);   // [area] a refused resize
                    if (m.S("code") == "region_too_large" && _nav.State.Pending) _nav.OnRegionError(m.S("message"));   // [nav]
                    Debug.LogWarning("[Tabletop] server error " + _lastError);
                    break;
            }
        }

        // [nav] Apply a decoded snapshot without emptying the table (TabletopRenderer.OnSnapshot) and keep the view
        // where it is in the world. (Player figures keep world positions and follow Model.Origin by themselves.)
        void ApplySnapshot(SnapshotAssembler.Result r)
        {
            if (r.Snapshot == null) { BadSnapshot(r.SnapshotId, r.Error); return; }
            var s = r.Snapshot;
            bool had = _world.Loaded;
            var oldOrigin = had ? _world.Origin : s.Origin;
            _world.ApplySnapshot(s);
            _edit.Editor.OnSnapshot();   // a snapshot replaces block-edit guesses
            _renderer.OnSnapshot(r.Plan, oldOrigin, s.Origin);
            if (oldOrigin != s.Origin) _effects.OnRegionChanged();   // [entities] cracks sit at region-local positions
            _caps.ModelVersion = ++_modelVersion;
            int oldWidth = AreaSizing.Current(_nav.State);   // [area]
            _nav.OnSnapshot(s.World, s.Origin, s.Dims, r.ViewMargin);
            AreaSize.OnRegionApplied(oldWidth);   // [area] a new width: scale the block size to keep the footprint
            Debug.Log($"[Tabletop] snapshot {s.SnapshotId}: {s.Dims} palette {s.Palette.Length} parts {r.Parts}" +
                      (r.Delta ? $" delta ({r.Listed} sections sent)" : "") +
                      (r.Plan.Rekey ? $" re-keyed by {r.Plan.ShiftBlocks}: {r.Plan.Mesh.Count} to mesh, {r.Plan.Kept} kept" : " full re-mesh") +
                      $" decode+plan {r.Ms:F0} ms");
        }

        void BadSnapshot(int snapshotId, string why)
        {
            _lastError = "bad snapshot: " + why;
            Debug.LogWarning("[Tabletop] " + _lastError);
            // The server answers with a full snapshot (PROTOCOL.md 4.18).
            _link.Send(new Dictionary<string, object> { ["type"] = "error", ["code"] = "bad_snapshot", ["message"] = why, ["snapshot_id"] = snapshotId, ["fatal"] = false });
        }

        /// <summary>A TextMesh added from code has no font and draws nothing: use the built-in one (always in the player).</summary>
        public static void SetBuiltinFont(TextMesh t)
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.font = font;
            t.GetComponent<MeshRenderer>().sharedMaterial = font.material;
        }

        void AcquireWifiLock()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var wifi = activity.Call<AndroidJavaObject>("getSystemService", "wifi");
                _wifiLock = wifi.Call<AndroidJavaObject>("createWifiLock", 4 /* WIFI_MODE_FULL_LOW_LATENCY */, "tabletop");
                _wifiLock.Call("acquire");
            }
            catch (Exception e) { Debug.LogWarning("[Tabletop] wifi lock: " + e.Message); }
#endif
        }

        void OnDestroy()
        {
            _link?.Dispose();
            _atlas?.Dispose();
            _entityTex?.Dispose();   // [entities]
            _icons?.Dispose();       // [menu] stops the icon worker
            try { _wifiLock?.Call("release"); } catch { }
        }
    }
}
