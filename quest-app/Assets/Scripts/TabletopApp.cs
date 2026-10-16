// Bootstrap: reads config, owns the link, routes protocol messages, and builds the TabletopAnchor hierarchy:
//   TabletopAnchor (table centre, +Y = up) > RegionRoot (region corner, unscaled) > Blocks (scale = block_size_m)
//                                                                               > Players
//                                                                               > Entities ([entities])
// Config file (optional): <persistentDataPath>/tabletop.json, e.g.
//   {"server":"ws://192.168.1.50:8765/tabletop","token":"","skin_player":""}
// [area] optional: "area_width": 192 (start at that width preset, block size fitted to the table),
//   "batch_columns": 4 (sections per mesh batch side, TabletopRenderer)
// [nav] optional: "mesh_sections_per_job": 4 (fewest sections a meshing job takes in a row; [mesh] more when sections are
//   cheap), "mesh_workers": 3 (meshing threads; the Quest 3 gives an app about three cores)
// [mesh] optional: "surface_skirt": true (a low-detail surface where the window isn't meshed yet), "skirt_speed": 2 (with
//   the skirt, glide up to this much faster than meshing keeps up; 1 = only as fast as detail arrives)
// [smooth] optional: "hitch_ms": 30 (log a HITCH line for frames longer than this; 0 = off), "upload_budget_ms": 3
//   (main-thread time per frame for mesh uploads), "reuse_snapshot_buffers": true (glide shifts reuse old grid arrays),
//   "mesh_data_on_workers": true (batch geometry written into mesh data on the assembling worker)
// [talk] optional: "operator_name": "Nick" (the name in chat, else skin_player), "quick_commands": ["time set day"]
// [power] B held = undo; the block menu's Tools tab: box wand, clone, brushes, undo/redo (World/PowerTools.cs)
// [hands] optional: "left_trigger": "use" (default: vanilla right-click order, left grip + trigger places) or "place"
//   (the trigger always places or uses the selected item; left grip + trigger does the right-click order)
// [comfort] prefs.json next to it (World/AppPrefs.cs) remembers block size, table placement, hotbar, selected block,
//   menu tab and Show HUD; without a saved or configured block size the area is fitted to the table. The link stops
//   while the headset is off (giant despawned, bye) and restarts when it's back on. A setup card, an offline badge
//   and the Info tab's Server field (keyboard, LAN discovery) cover first runs and dead links (docs/QUEST_SETUP.md).
// [setup] Zero files needed: without a "server" the app searches the LAN and connects when one server answers; a server
//   that pairs shows a 6-digit code (Net/Pairing.cs, pairings.json keeps the device token per server); the server sends
//   the game files from its PC. Older plugins keep working, with a note on what an update adds (PluginSupport).
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
        LinkWatch _linkWatch = new LinkWatch();
        string _lastAreaStatus = "", _lastEditResult = "";
        string _session;
        float _avatarConfigAt = -1;   // send avatar_config once scaling settles, not every frame
        // [comfort] session and comfort state (World/AppPrefs.cs, World/SessionState.cs)
        Dictionary<string, object> _config;              // tabletop.json as read
        AppPrefs _prefs = new AppPrefs();
        readonly PrefsSaver _saver = new PrefsSaver();
        float _prefsAt;
        bool _blockSizeKnown;                             // from prefs.json or tabletop.json; else fitted at the first snapshot
        readonly LinkHealth _health = new LinkHealth();
        readonly LoadProgress _loading = new LoadProgress();
        float _loadingAt;
        bool _paused, _unmounted, _assetsChecked, _announce;
        int _discoveryPort = ServerAddress.DefaultPort;
        System.Threading.Tasks.Task<List<FoundServer>> _finding;
        readonly Dictionary<string, FoundServer> _found = new Dictionary<string, FoundServer>();
        Transform _playersRoot, _entitiesRoot;
        bool Away => _paused || _unmounted;
        // [setup] pairing (Net/Pairing.cs, PROTOCOL.md "Pairing"), the first start without a server, older plugins
        PairingStore _pairings = new PairingStore();
        PairCode _pairCode;
        PairingWelcome _pairWelcome = new PairingWelcome();
        string _serverIdHint, _pluginNote, _pluginNoteShown;
        bool _pairRequested;
        readonly FirstRun _firstRun = new FirstRun();
        static string PairingsPath => Path.Combine(Application.persistentDataPath, PairingStore.FileName);
        static string DeviceName => PairingStore.DeviceName(SystemInfo.deviceModel);
        static string ConfigPath => Path.Combine(Application.persistentDataPath, ConfigFile.FileName);
        static string PrefsPath => Path.Combine(Application.persistentDataPath, AppPrefs.FileName);
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
        BlockEntityRenderer _blockEntities;   // [blocks] chests, signs, banners, heads... (BlockEntityRenderer.cs)
        // [area] visible-width presets (left stick click; Presets/Current/Request for a menu), tabletop.json "area_width"
        public AreaSizeControl AreaSize { get; private set; }
        int _areaWidth;
        // [talk] operator panel on X (chat, console, players, world, server) and chat on the wrist / over players
        OperatorPanel _ops;
        ChatView _chatView;
        string _operatorName;
        readonly List<string> _quickCommands = new List<string>();
        // [hands] using blocks, items and mobs (interact_v1): what the server allows, its item list, the mob info card
        InteractWelcome _interact = new InteractWelcome();
        ItemRegistry _items;
        EntityCard _card;
        TriggerMode _leftTrigger = TriggerMode.Use;
        // [power] undo/redo, the box wand and brushes (World/PowerTools.cs, XR/PowerToolMode.cs)
        PowerTools _tools;
        PowerToolMode _power;
        string _lastToolResult = "";
        // [living] time of day and weather (WorldLight), rain, snow and lightning (WeatherRenderer), leads (LeashRenderer)
        WorldLight _light;
        WeatherRenderer _weather;
        AmbientParticleRenderer _particles;   // [motion] torch flames, smoke, lava pops, leaves, fireflies...
        // [sound] spatial sound from the user's sounds.zip (World/TabletopSound.cs) and the opt-in download of the
        // operator's own files from their server (World/AssetSync.cs, PROTOCOL.md "Asset delivery")
        TabletopSound _sound;
        AssetSync _assets;
        float _volume = 0.8f, _soundRange = 8f, _assetToastAt, _reloadAt = -1;
        bool _soundOn = true, _chatPing = true, _downloadAssets = true, _reloadAfterPack = true, _packDownloaded, _linkUp;
        int _maxVoices = 24;

        /// <summary>[sound] The sound player under RegionRoot, its volume, and the asset downloader.</summary>
        void SetupSound()
        {
            var soundT = new GameObject("Sound").transform; soundT.SetParent(_regionRoot, false);
            soundT.gameObject.SetActive(false);   // Awake after MaxVoices is set
            _sound = soundT.gameObject.AddComponent<TabletopSound>();
            _sound.MaxVoices = Mathf.Clamp(_maxVoices, 4, 64);
            soundT.gameObject.SetActive(true);
            _sound.Head = Head; _sound.Model = _world; _sound.Nav = _nav.State; _sound.BlockSize = () => BlockSizeM;
            _sound.Rain = () => _light != null && _light.Clock.Known ? _light.Clock.Rain : 0f;
            _sound.Hearing = _soundRange; _sound.Director.ChatPing = _chatPing;
            _sound.SetVolume(_volume, _soundOn);
            _sound.Load(Path.Combine(Application.persistentDataPath, SoundPack.FileName));
            _edit.Editor.Edited = (op, pos, st) => _sound.Director.OnOwnEdit(op, pos, st, Time.unscaledTimeAsDouble);
            _menu.Model.Volume = Mathf.RoundToInt(_volume * 10) * 10; _menu.Model.SoundOn = _soundOn;
            _menu.VolumeChanged = (pct, on) => { _volume = pct / 100f; _soundOn = on; _sound.SetVolume(_volume, on); };
            if (_downloadAssets)
            {
                _assets = new AssetSync(Application.persistentDataPath);
                _assets.Completed = AssetDownloaded;
            }
        }

        /// <summary>[sound] A file from the server is in place: sounds and entity models reload; a new pack restarts the scene.</summary>
        void AssetDownloaded(string name)
        {
            string path = Path.Combine(Application.persistentDataPath, name);
            Debug.Log("[Tabletop] downloaded " + name);
            if (name == SoundPack.FileName) { _sound.Load(path); _hud.Toast("Sounds downloaded and loaded", ToastKind.Info); }
            else if (name == EntityModels.FileName) { System.Threading.Tasks.Task.Run(() => EntityModels.LoadFile(path)); _hud.Toast("Entity models downloaded and loaded", ToastKind.Info); }
            else if (name == "pack.zip") _packDownloaded = true;
        }

        /// <summary>[sound] Asset downloads: requests, the progress toast, a lost link, the restart for a new pack.zip.</summary>
        void PumpAssets(float now)
        {
            if (_assets == null) return;
            bool up = _link.Connected;
            if (_linkUp && !up) _assets.LinkLost();
            _linkUp = up;
            if (up) foreach (var g in _assets.Pump()) _link.Send(g);
            else _assets.Pump();
            if (now >= _assetToastAt)
            {
                _assetToastAt = now + 0.5f;
                string t = _assets.Toast();
                if (t != null) _hud.Toast(t, ToastKind.Info, 2, "assets");
            }
            if (_packDownloaded && !_assets.Busy && _reloadAt < 0)
            {
                _packDownloaded = false;
                if (_reloadAfterPack) { _reloadAt = now + 2f; _hud.Toast("pack.zip downloaded: reloading with the new textures...", ToastKind.Info, 3); }
                else _hud.Toast("pack.zip downloaded: restart the app to use it", ToastKind.Info, 8);
            }
            if (_reloadAt >= 0 && now >= _reloadAt)
            {
                _reloadAt = -1;
                FlushPrefs();
                UnityEngine.SceneManagement.SceneManager.LoadScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
            }
        }

        /// <summary>[living] The living-world parts: the light follows world_state, weather and leads are drawn on the table.</summary>
        void SetupLiving()
        {
            _light = gameObject.AddComponent<WorldLight>();
            _entities.Light = _light;
            _entityTex.BlockOpaque = OpaqueMaterial; _entityTex.BlockTransparent = TransparentMaterial;   // blocks on entities, block displays
            var weatherT = new GameObject("Weather").transform; weatherT.SetParent(_regionRoot, false);
            _weather = weatherT.gameObject.AddComponent<WeatherRenderer>();
            _weather.Model = _world; _weather.Light = _light; _weather.Textures = _entityTex; _weather.Nav = _nav.State; _weather.Head = Head;
            _entities.Weather = _weather;
            var particlesT = new GameObject("Particles").transform; particlesT.SetParent(_regionRoot, false);   // [motion]
            _particles = particlesT.gameObject.AddComponent<AmbientParticleRenderer>();
            _particles.Model = _world; _particles.Atlas = _atlas; _particles.Light = _light; _particles.Nav = _nav.State; _particles.BlockSizeM = BlockSizeM;
            var leash = _entitiesRoot.gameObject.AddComponent<LeashRenderer>();
            leash.Entities = _entities; leash.Players = _players; leash.Textures = _entityTex;
        }
        // [operator] what the server allows for bookmarks, the container view and the sign editor (welcome)
        OperatorWelcome _operator = new OperatorWelcome();

        void Start()
        {
            SnapshotBuffers.Enabled = true;   // [smooth] glide shifts reuse retired grid/light arrays (tabletop.json can turn it off)
            Protocol.ClientVersion = Application.version;   // [setup] hello.client_version
            LoadConfig();
            LoadPairings();   // [setup]
            // [smooth] Log lines without a stack trace: collecting one costs about a millisecond under IL2CPP, and the app
            // logs on every glide shift. Warnings and errors keep theirs.
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            gameObject.AddComponent<HitchProbe>();   // [smooth] HITCH lines for frames over hitch_ms
            MeshScope.SkirtFactor = SurfaceSkirt.Enabled ? SurfaceSkirt.SpeedFactor : 1f;   // [mesh] the skirt covers what meshing lags behind
            LoadPrefs();   // [comfort] prefs.json, and which of tabletop.json's shared keys win
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
            Debug.Log($"[Tabletop] MESH workers={_renderer.MaxJobsInFlight} cores={Environment.ProcessorCount} sections_per_job>={TabletopRenderer.SectionsPerJob} " +
                      $"skirt={(SurfaceSkirt.Enabled ? "on" : "off")} skirt_speed={MeshScope.SkirtFactor:F1}");   // [mesh]
            _renderer.Model = _world; _renderer.Atlas = _atlas; _renderer.OpaqueMaterial = OpaqueMaterial; _renderer.TransparentMaterial = TransparentMaterial;
            _players = playersT.gameObject.AddComponent<PlayerMarkers>();
            _players.Model = _world; _players.MarkerMaterial = MarkerMaterial; _players.Atlas = _atlas;
            _playersRoot = playersT;
            // [entities] non-player entities next to the players, block actions in block space
            _entityTex = new EntityTextures { Atlas = _atlas };
            // Entity models: the user's entity_models.json next to pack.zip (generated from their own client jar). Parsed
            // on a worker; until then (or without it) entities are boxes, and EntityRenderer rebuilds them when it lands.
            string modelsPath = Path.Combine(Application.persistentDataPath, EntityModels.FileName);
            System.Threading.Tasks.Task.Run(() => { EntityModels.LoadFile(modelsPath); Debug.Log("[Tabletop] " + EntityModels.Status); });
            var entitiesT = new GameObject("Entities").transform; entitiesT.SetParent(_regionRoot, false);
            _entities = entitiesT.gameObject.AddComponent<EntityRenderer>();
            _entities.Model = _world; _entities.Textures = _entityTex;
            _entitiesRoot = entitiesT;
            _players.Textures = _entityTex; _players.Entities = _entities;
            _effects = new GameObject("Effects").AddComponent<ActionEffects>();
            _effects.transform.SetParent(_blocks, false);
            _effects.Model = _world; _effects.Atlas = _atlas; _effects.Textures = _entityTex; _effects.BlocksMaterial = OpaqueMaterial;
            // [blocks] Block entities drawn like vanilla's block entity renderers; the terrain leaves out their placeholder boxes.
            _atlas.BlockEntityBoxes = false;
            _blockEntities = new GameObject("BlockEntities").AddComponent<BlockEntityRenderer>();
            _blockEntities.transform.SetParent(_blocks, false);
            _blockEntities.Model = _world; _blockEntities.Textures = _entityTex; _blockEntities.Atlas = _atlas; _blockEntities.Terrain = _renderer;

            // Eye in water/lava/powder snow/inside a block: fog, passthrough tint, the water's inside faces (EyeEffects.cs).
            var eye = gameObject.AddComponent<EyeEffects>();
            eye.Head = Head; eye.Blocks = _blocks; eye.Model = _world; eye.BlockRenderer = _renderer;

            _placement = gameObject.AddComponent<TablePlacement>();
            _placement.Anchor = _anchor; _placement.Head = Head; _placement.RightHand = RightHand;
            _placement.OnScaleChanged = k => SetBlockSize(Mathf.Clamp(BlockSizeM * k, 0.002f, 0.1f), false);
            _placement.SavedTableId = _prefs.TableAnchor; _placement.SavedPose = _prefs.Table;   // [comfort] where it was last time

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
            SetupLiving();   // [living] needs the entities and the nav window
            // [area] width presets: left stick click, or a menu through Presets/Current/Request (XR/AreaSizeControl.cs)
            AreaSize = gameObject.AddComponent<AreaSizeControl>();
            AreaSize.Nav = _nav.State; AreaSize.Renderer = _renderer; AreaSize.Blocked = () => _area.Active; AreaSize.Send = b => _link.Send(b);
            AreaSize.BlockSize = () => BlockSizeM; AreaSize.SetBlockSize = bs => SetBlockSize(bs, false);
            AreaSize.TableSize = () => _placement.TableSize; AreaSize.StartWidth = _areaWidth;
            // [comfort] first run: fit the area to the table; later runs: the saved block size, kept to its footprint
            AreaSize.TableReady = () => _placement.Settled;
            AreaSize.StartFit = !_blockSizeKnown;
            AreaSize.StartBlockWidth = _blockSizeKnown && _prefs.BlockSizeM.HasValue && Mathf.Approximately(_prefs.BlockSizeM.Value, BlockSizeM) ? _prefs.AreaWidth : 0;

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
            _menu.HudChanged = on => { _hud.ShowHud = on; _showHud = on; };
            _edit.Picked = s => _menu.OnPicked(s);
            // [comfort] hotbar, selection and tab from prefs.json; Reset view and the Server field (Info tab); readout
            _menu.Model.Restore(_prefs.Hotbar, _prefs.HotbarSelected, _prefs.Selected);
            _edit.Editor.Selected = _menu.Model.Selected;
            _menu.Model.RestoreTab(_prefs.MenuTab);
            _menu.ResetView = ResetView;
            _menu.ServerFind = FindServers;
            _menu.ServerConnect = ConnectTo;
            _menu.Readout = Readout;
            _placement.ABlocked = () => _menu.CapturesRight;
            _players.InWindow = p => _nav.State.InWindow(p);
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
            _ops.Model.Safety.Load(_prefs.ChatSafety);   // [safety] who this headset mutes and blocks, per server (prefs.json)
            // [hands] The left trigger's right-click order, mobs and players on the ray, the right grip's carry, B on a mob
            // (XR/BlockEditMode.cs, World/UseRules.cs), and the mob info card above the mob (XR/EntityCard.cs).
            _edit.Interact = () => _interact;
            _edit.CollectTargets = list => { _entities.Targets(list); _players.Targets(list); };
            _edit.Mode = _leftTrigger;
            _edit.NameTag = () => _menu.Model.NameTag;
            _edit.LabelOf = s => _menu.Model.LabelOf(s);
            _edit.Icons = _icons;
            _card = gameObject.AddComponent<EntityCard>();
            _card.Head = Head; _card.RightHand = RightHand;
            _card.Blocked = () => _area.Active;
            _card.RayTaken = () => _menu.CapturesRight || _ops.CapturesRight;
            _card.Send = b => _link.Send(b);
            _card.Interact = () => _interact;
            _card.Top = _edit.TryEntityTop;
            _card.Result = r => _edit.Editor.Result = r;
            _edit.Blocked = () => _area.Active || _menu.CapturesRight || _ops.CapturesRight || _card.CapturesRight;
            // [power] B held = undo, every trigger press one edit group, and the Tools tab: the box wand (left trigger =
            // corners), clone/paste, brushes, undo and redo (XR/PowerToolMode.cs, World/PowerTools.cs).
            _tools = new PowerTools(_edit.Groups);
            _power = gameObject.AddComponent<PowerToolMode>();
            _power.Tools = _tools; _power.Edit = _edit; _power.Blocks = _blocks; _power.World = () => _world; _power.Nav = _nav.State;
            _power.SendTracked = b => _link.SendTracked(b); _power.Online = () => _link.Connected; _power.Material = () => _edit.Editor.Selected;
            _power.Blocked = () => _area.Active || _menu.CapturesRight || _ops.CapturesRight || _card.CapturesRight;
            _edit.LeftTaken = () => _tools.Mode != ToolMode.None;
            _edit.PickFilter = st => _tools.PickFilter(st);
            _edit.Undo = () => _power.Press(ToolButton.Undo);
            _menu.Model.SetTools(_tools);
            _menu.ToolPressed = id => _power.Press((ToolButton)id);
            // [operator] Marks tab jumps, Players' Follow / Go to (NavController + World/FollowState.cs); the left trigger on a
            // container or a sign opens the container view or the sign editor in the operator panel (BlockEditMode's hooks).
            _ops.JumpTo = (w, p) => { if (!_nav.JumpTo(w, p)) _hud.Toast("No area yet", ToastKind.Warn); };
            _ops.Follow = _nav.FollowCommand;
            _ops.Icons = _icons; _ops.Nav = _nav.State; _ops.Following = _nav.Follow;
            _ops.Selection = () => _edit.Editor.Selected;
            _ops.Registry = () => _registry;
            _nav.WorldInfo = n => _ops.Model.Marks.WorldOf(n);
            _nav.PlayerShown = _players.TryShown;
            _nav.SendTracked = b => _link.SendTracked(b);
            _nav.FollowBlocked = () => _area.Active;
            _nav.FollowNote = t => _hud.Toast(t, ToastKind.Info);
            _edit.OnContainerUse = pos =>
            {
                if (!_operator.ContainersOn) { _edit.Editor.Result = "can't open containers: they're off on this server (interact.containers)"; return; }
                _ops.OpenContainer(pos);
            };
            _edit.OnSignUse = pos =>
            {
                if (!_operator.Signs) { _edit.Editor.Result = "can't edit signs: it's off on this server (interact.signs)"; return; }
                var local = pos - _world.Origin;
                string state = _world.Loaded && _world.InBounds(local.x, local.y, local.z) ? _world.State(_world.Get(local.x, local.y, local.z)) : null;
                _ops.OpenSign(pos, state, _blockEntities.Store.Get(pos));
            };
            _chatView = gameObject.AddComponent<ChatView>();
            _chatView.Head = Head; _chatView.LeftHand = LeftHand; _chatView.Log = _ops.Model.Log; _chatView.Bubbles = _ops.Bubbles;
            _chatView.Hidden = () => _ops.Open || _menu.Open || _area.Active;
            _chatView.FigureLabel = uuid => _players.LabelOf(uuid);
            _hud = gameObject.AddComponent<Hud>();
            _hud.Head = Head; _hud.LeftHand = LeftHand; _hud.StatusText = StatusText; _hud.ShowHud = _showHud;
            _hud.AreaActive = () => _area.Active; _hud.AreaText = () => _area.Status;
            // [comfort] setup card (no link yet / refused), north marker and offline badge on the table
            var card = gameObject.AddComponent<SetupCardView>();
            card.Head = Head;
            card.Visible = () => !Away && !_menu.Open && !PairCardShown && (_firstRun.Active && !_link.Connected || _health.ShowCard(_link.FatalCode != 0, Time.unscaledTime));
            card.Text = () => _firstRun.Active && !_link.Connected ? SetupCard.FirstRunText(_firstRun)
                            : SetupCard.Text(_link.Url, _link.Status, _link.FatalCode, _link.FatalReason, ConfigPath) + "\n\n" + SetupCard.Disclaimer;
            // [setup] the pairing code: in front while the world waits for it, lower down while the world already shows
            var pairCard = gameObject.AddComponent<SetupCardView>();
            pairCard.Head = Head;
            pairCard.Visible = () => PairCardShown;
            pairCard.Text = () => _pairCode == null ? "" : SetupCard.PairText(_pairCode, Time.unscaledTimeAsDouble, ServerAddress.HostOf(_link.Url));
            pairCard.Offset = () => _pairCode != null && _pairCode.Required ? new Vector3(0f, 0.02f, 0.85f) : new Vector3(0f, -0.24f, 0.8f);
            var markers = gameObject.AddComponent<TableMarkers>();
            markers.Anchor = _anchor; markers.Head = Head;
            markers.HalfSize = () => _nav.State.Loaded ? new Vector2(_nav.State.Window.x, _nav.State.Window.z) * (BlockSizeM / 2f) : Vector2.zero;
            markers.Badge = () => !_health.Offline || Away ? null : _link.FatalCode != 0 ? "Offline: " + _link.FatalReason : "Offline: reconnecting...";
            OVRManager.HMDUnmounted += OnUnmounted;
            OVRManager.HMDMounted += OnMounted;

            SetupSound();   // [sound] after the menu and the editor (its volume row, own edits)
            Protocol.OperatorName = !string.IsNullOrEmpty(_operatorName) ? _operatorName : SkinPlayer;   // [talk] hello.operator_name
            StartLink();
            _health.NewServer(Time.unscaledTime);
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
                if (!File.Exists(p)) { Debug.Log("[Tabletop] no " + p + ", using defaults"); _firstRun.Active = true; return; }
                var c = Json.Parse(File.ReadAllText(p)) as Dictionary<string, object>;
                _config = c;
                _firstRun.Active = c.S("server") == null;   // [setup] no server chosen yet: search the LAN
                ServerUrl = c.S("server") ?? ServerUrl;
                Token = c.S("token") ?? Token;
                SkinPlayer = c.S("skin_player") ?? SkinPlayer;
                _discoveryPort = c.I("discovery_port", _discoveryPort);   // [comfort] the plugin's discovery.port
                BlockShading.SmoothLighting = c.B("smooth_lighting", true);       // lighting look (BlockShading.cs)
                BlockShading.Brightness = (float)c.D("brightness", BlockShading.Brightness);   // vanilla slider, default 1 = Bright
                BlockShading.MinLight = (float)c.D("min_light", BlockShading.MinLight);         // brightness of light level 0
                GreedyMesher.LeavesInner = c.B("leaves_inner", true);                           // faces between leaves, like vanilla
                ApplyPerf(c.S("perf") ?? "high", c.I("foveation", 3));   // [perf] clocks and foveated rendering (wide areas are GPU-bound)
                ResourcePackAtlas.Mipmaps = c.B("mipmaps", true);                               // mip-mapped block textures
                WorldLight.WeatherFx = c.B("weather_fx", true);                                 // [living] rain, snow, lightning
                AmbientParticleRenderer.Enabled = c.B("particles", true);                       // [motion] ambient particles
                AmbientParticleRenderer.DensityScale = (float)c.D("particle_density", 1.0);     // [motion] 1 = vanilla near the player
                // [menu] show_hud, [area] area_width and block_size_m are shared with prefs.json: see LoadPrefs
                _operatorName = c.S("operator_name");                                           // [talk] the name in chat
                foreach (var q in c.A("quick_commands") ?? new List<object>()) if (q is string qs) _quickCommands.Add(qs);   // [talk] Console quick buttons
                _leftTrigger = c.S("left_trigger") == "place" ? TriggerMode.Place : TriggerMode.Use;   // [hands] what the left trigger does first
                // [sound] sound (on), volume and sound (shared with prefs.json: see LoadPrefs), hearing range, voices, chat ping,
                //   downloads from the server (assets_v1) and the restart after a new pack.zip
                _soundRange = Mathf.Clamp((float)c.D("sound_range", _soundRange), 1f, 64f);
                _maxVoices = c.I("max_voices", _maxVoices);
                _chatPing = c.B("chat_ping", true);
                _downloadAssets = c.B("download_assets", true);
                _reloadAfterPack = c.B("reload_after_pack", true);
                Protocol.WantAssets = _downloadAssets;
                BatchRules.Columns = Math.Max(1, c.I("batch_columns", BatchRules.Columns));     // [area] sections per batch side
                TabletopRenderer.SectionsPerJob = Math.Max(1, c.I("mesh_sections_per_job", TabletopRenderer.SectionsPerJob));   // [nav] meshing throughput
                TabletopRenderer.Workers = Mathf.Clamp(c.I("mesh_workers", TabletopRenderer.Workers), 1, 8);                    // [mesh] meshing threads
                SurfaceSkirt.Enabled = c.B("surface_skirt", SurfaceSkirt.Enabled);                                            // [mesh] low-detail surface ahead
                SurfaceSkirt.SpeedFactor = Mathf.Clamp((float)c.D("skirt_speed", SurfaceSkirt.SpeedFactor), 1f, 4f);          // [mesh] glide past meshing
                FrameHitch.ThresholdMs = Mathf.Max(0f, (float)c.D("hitch_ms", FrameHitch.ThresholdMs));                       // [smooth] HITCH log (0 = off)
                TabletopRenderer.UploadBudgetMs = Mathf.Clamp((float)c.D("upload_budget_ms", TabletopRenderer.UploadBudgetMs), 0.5f, 12f);   // [smooth]
                SnapshotBuffers.Enabled = c.B("reuse_snapshot_buffers", SnapshotBuffers.Enabled);                             // [smooth] A/B switch
                TabletopRenderer.WriteOnWorkers = c.B("mesh_data_on_workers", TabletopRenderer.WriteOnWorkers);               // [smooth] A/B switch
                SolidLayers.Enabled = c.B("solid_pass", SolidLayers.Enabled);                 // [gpu] opaque faces without alpha test
                BatchRules.PackedVertices = c.B("packed_vertices", BatchRules.PackedVertices);   // [gpu] 20-byte batch vertices
                BlockKeywords.UseClipPlanes = c.B("clip_planes", BlockKeywords.UseClipPlanes);   // [gpu] hardware clip planes for the window
            }
            catch (Exception e) { Debug.LogWarning("[Tabletop] bad tabletop.json: " + e.Message); }
        }

        /// <summary>
        /// [comfort] prefs.json (World/AppPrefs.cs). block_size_m, show_hud and area_width can be in both files: a
        /// tabletop.json value wins when it's new or was edited since the last start; otherwise the headset's choice.
        /// </summary>
        /// <summary>[perf] Wide areas are GPU-bound (VrApi GPU% about 0.98 at clock level 2): ask the Quest for sustained-high
        /// clocks (perf: high, boost or default) and dynamic foveated rendering (0 off .. 4 HighTop); the miniature sits
        /// mid-view and the periphery is mostly passthrough.</summary>
        static void ApplyPerf(string perf, int foveation)
        {
            try
            {
                if (perf != "default")
                {
                    var lvl = perf == "boost" ? OVRManager.ProcessorPerformanceLevel.Boost : OVRManager.ProcessorPerformanceLevel.SustainedHigh;
                    OVRManager.suggestedCpuPerfLevel = lvl;
                    OVRManager.suggestedGpuPerfLevel = lvl;
                }
                int f = Math.Max(0, Math.Min(4, foveation));
                OVRManager.foveatedRenderingLevel = (OVRManager.FoveatedRenderingLevel)f;
                OVRManager.useDynamicFoveatedRendering = f > 0;
                Debug.Log($"[Tabletop] PERF_LEVELS perf={perf} foveation={OVRManager.foveatedRenderingLevel} dynamic={OVRManager.useDynamicFoveatedRendering}");
            }
            catch (Exception e) { Debug.LogWarning("[Tabletop] perf levels: " + e.Message); }
        }

        void LoadPrefs()
        {
            try { if (File.Exists(PrefsPath)) _prefs = AppPrefs.Parse(File.ReadAllText(PrefsPath)); }
            catch (Exception e) { Debug.LogWarning("[Tabletop] bad prefs.json, starting fresh: " + e.Message); _prefs = new AppPrefs(); }
            object Cfg(string k) => _config != null && _config.TryGetValue(k, out var v) && (v is double || v is bool) ? v : null;
            object Seen(string k) => _prefs.Seen.TryGetValue(k, out var v) ? v : null;
            var bs = AppPrefs.Pick(Cfg("block_size_m"), _prefs.BlockSizeM.HasValue ? (object)(double)_prefs.BlockSizeM.Value : null, Seen("block_size_m"));
            if (bs is double b && b > 0) { BlockSizeM = Mathf.Clamp((float)b, 0.002f, 0.1f); _blockSizeKnown = true; }
            _showHud = AppPrefs.Pick(Cfg("show_hud"), _prefs.ShowHud, Seen("show_hud")) is bool h && h;
            if (AppPrefs.Pick(Cfg("volume"), _prefs.Volume.HasValue ? (object)(double)_prefs.Volume.Value : null, Seen("volume")) is double vol) _volume = Mathf.Clamp01((float)vol);   // [sound]
            if (AppPrefs.Pick(Cfg("sound"), _prefs.Sound, Seen("sound")) is bool snd) _soundOn = snd;
            // area_width is a request ("start at this preset"): only when tabletop.json's value is new or edited.
            var aw = Cfg("area_width");
            _areaWidth = aw is double w && !Equals(aw, Seen("area_width")) ? (int)w : 0;
            _prefs.SeeConfig(_config);
            Debug.Log($"[Tabletop] prefs: block {(_blockSizeKnown ? $"{BlockSizeM * 1000:F2} mm" : "fit to the table")}, width {_prefs.AreaWidth}, " +
                      $"table {(_prefs.Table.HasValue ? "saved" : "-")}, hotbar {(_prefs.Hotbar != null ? "saved" : "-")}, start width {_areaWidth}");
        }

        /// <summary>[comfort] What prefs.json should hold now, or null before the first area has settled.</summary>
        string CapturePrefs()
        {
            if (!AreaSize.Started) return null;
            var m = _menu.Model;
            var p = new AppPrefs
            {
                BlockSizeM = BlockSizeM, AreaWidth = AreaSizing.Current(_nav.State), ShowHud = _showHud, MenuTab = m.TabId(m.Tab),
                Hotbar = m.Hotbar.Slots(), HotbarSelected = m.Hotbar.Selected, Selected = m.Selected,
                Volume = _volume, Sound = _soundOn,   // [sound]
                ChatSafety = _ops.Model.Safety.ToJson(),   // [safety]
            };
            if (_placement.TryRelative(out var id, out var rel)) { p.TableAnchor = id; p.Table = rel; }
            else { p.TableAnchor = _prefs.TableAnchor; p.Table = _prefs.Table; }   // no table this session: keep the old one
            foreach (var kv in _prefs.Seen) p.Seen[kv.Key] = kv.Value;
            return p.ToJson();
        }

        void SavePrefs(string json)
        {
            if (json == null) return;
            try
            {
                string tmp = PrefsPath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(PrefsPath)) File.Delete(PrefsPath);
                File.Move(tmp, PrefsPath);
                _saver.Saved(json);
            }
            catch (Exception e) { Debug.LogWarning("[Tabletop] can't write prefs.json: " + e.Message); _saver.Saved(json); }
        }

        void FlushPrefs() => SavePrefs(_saver.Flush(CapturePrefs()));

        // ---------- [comfort] the link's life: headset off/on, server changes ----------

        void StartLink()
        {
            _link = new LinkClient(ServerUrl, Token);
            _link.DeviceToken = _pairings.TokenFor(ServerUrl, _serverIdHint);   // [setup] PROTOCOL.md "Pairing"
            _link.DeviceName = DeviceName;
            _pairCode = null; _pairRequested = false; _pairWelcome = new PairingWelcome();
            if (_assets != null) _link.Binary = _assets.OnBinary;   // [sound] asset chunks arrive as binary frames
            if (_poses) _poses.Link = _link;
            // [setup] first run: no server yet, so the link waits (LAN search in Comfort; ConnectTo starts a new one)
            if (_firstRun.Active) { _menu.Model.ServerUrl = ""; Debug.Log("[Tabletop] first run: no server yet, searching the LAN"); return; }
            _link.Start();
            _menu.Model.ServerUrl = ServerUrl;
            Debug.Log($"[Tabletop] link to {ServerUrl}");
        }

        /// <summary>Despawns the giant (avatar_config enabled:false), says bye and stops; the table stays as it is.</summary>
        void StopLink(string reason)
        {
            if (_link == null) return;
            var farewell = _link.Connected ? Protocol.AvatarConfig(false, BlockSizeM, SkinPlayer, true, "Tabletop Giant") : null;
            _link.Shutdown(reason, 400, farewell);
        }

        void OnApplicationPause(bool paused) { _paused = paused; AwayChanged(paused ? "app paused" : "app resumed"); }
        void OnUnmounted() { _unmounted = true; AwayChanged("headset off"); }
        void OnMounted() { _unmounted = false; AwayChanged("headset on"); }

        bool _wasAway;
        void AwayChanged(string why)
        {
            if (_link == null || Away == _wasAway) return;
            _wasAway = Away;
            Debug.Log($"[Tabletop] {why}: link {(Away ? "stopped" : "restarting")}");
            if (Away) { FlushPrefs(); StopLink(why); }
            else { _linkWatch = new LinkWatch(); StartLink(); }   // the badge covers the reconnect; no lost/restored toasts
        }

        /// <summary>The Info tab's Server field (or a found server): save it in tabletop.json and connect there.</summary>
        void ConnectTo(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                string old = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : null, text;
                try { text = ConfigFile.WithServer(old, url); }
                catch (FormatException)
                {
                    File.WriteAllText(ConfigPath + ".bak", old);
                    text = ConfigFile.WithServer(null, url);
                    _hud.Toast("tabletop.json was broken: the old one is tabletop.json.bak", ToastKind.Warn, 6);
                }
                File.WriteAllText(ConfigPath, text);
                _config = Json.Parse(text) as Dictionary<string, object>;
                Token = _config?.S("token") ?? Token;   // a token fixed in the file since the start counts too
            }
            catch (Exception e) { _hud.Toast("Can't write tabletop.json: " + e.Message, ToastKind.Error); }
            StopLink("server changed");
            ServerUrl = url;
            _firstRun.Done();   // [setup] a server is chosen (tabletop.json now has it)
            _serverIdHint = _found.TryGetValue(url, out var fs) && fs.Id.Length > 0 ? fs.Id : null;
            _health.NewServer(Time.unscaledTime);
            _linkWatch = new LinkWatch();
            _announce = true;
            if (!Away) StartLink();
            string host = ServerAddress.HostOf(url);
            _hud.Toast("Connecting to " + host + "...", ToastKind.Info);
            if (_found.TryGetValue(url, out var f) && f.TokenNeeded && !f.Pair && string.IsNullOrEmpty(Token))
                _hud.Toast("This server needs a token: add \"token\" to tabletop.json, or update its plugin to pair", ToastKind.Warn, 6);
        }

        /// <summary>"Find servers": the LAN probe (and the /24 TCP fallback) on a worker; Update shows the result.</summary>
        void FindServers()
        {
            if (_finding != null) return;
            _menu.Model.SetFound(null, "Searching the network...");
            int port = _discoveryPort;
            _finding = System.Threading.Tasks.Task.Run(() => LanDiscovery.Find(port));
        }

        void FinishFinding()
        {
            if (_finding == null || !_finding.IsCompleted) return;
            List<FoundServer> list = null;
            string note;
            if (_finding.IsFaulted) note = "Search failed: " + _finding.Exception?.GetBaseException().Message;
            else
            {
                list = _finding.Result;
                note = list.Count == 0 ? $"No server answered on port {_discoveryPort}: type the PC's address" : "";
            }
            _finding = null;
            _found.Clear();
            var buttons = new List<(string, string)>();
            if (list != null) foreach (var f in list) { _found[f.Url] = f; buttons.Add((f.Label, f.Url)); }
            _menu.Model.SetFound(buttons, note);
            Debug.Log($"[Tabletop] discovery: {list?.Count ?? 0} found {note}");
            if (_firstRun.Active)   // [setup] exactly one server answered: connect by itself
            {
                string pick = _firstRun.Searched(list, Time.unscaledTimeAsDouble);
                if (pick != null) { Debug.Log("[Tabletop] first run: connecting to the only server found, " + pick); ConnectTo(pick); }
            }
        }

        /// <summary>Reset view (Info tab): north away from you, the area fitted to the table, the view in its middle.</summary>
        void ResetView()
        {
            _placement.NorthUp();
            _nav.StopFollow("Reset view");   // [operator]
            _nav.State.Recenter();
            _nav.Place();
            if (_nav.State.Loaded) AreaSize.FitToTable(AreaSizing.Current(_nav.State));
            _hud.Toast("View reset: north is the far side", ToastKind.Info);
        }

        /// <summary>The line under the small hotbar: the block under the view centre and the direction you look.</summary>
        string Readout()
        {
            if (!_nav.State.Loaded || !Head) return "";
            Vector3 f = _anchor.InverseTransformDirection(Head.forward);   // anchor-local Unity; world Z is mirrored
            return _nav.State.Readout(f.x, -f.z);
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
            if (_weather != null) _weather.BlockSizeM = bs;   // [living]
            if (_particles != null) _particles.BlockSizeM = bs;   // [motion]
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
            // (pings, the round trip and dead-link detection live in LinkClient)
            if (_avatarConfigAt >= 0 && Time.unscaledTime >= _avatarConfigAt)
            {
                _avatarConfigAt = -1;   // while disconnected, welcome sends the current block size anyway
                if (_link.Connected) _link.Send(Protocol.AvatarConfig(true, BlockSizeM, SkinPlayer, true, "Tabletop Giant"));
            }
            Notify();   // [menu] toasts instead of the head-locked status text
            Comfort(Time.unscaledTime);
            PumpAssets(Time.unscaledTime);   // [sound]
        }

        /// <summary>[comfort] Link health (stale figures hidden), the loading toast, the LAN search, debounced prefs.json.</summary>
        void Comfort(float now)
        {
            _health.Update(_link.Connected, now);
            bool hide = _health.HideFigures(now);
            if (_playersRoot.gameObject.activeSelf == hide) _playersRoot.gameObject.SetActive(!hide);
            if (_entitiesRoot.gameObject.activeSelf == hide) _entitiesRoot.gameObject.SetActive(!hide);
            if (_loading.Active && now >= _loadingAt)
            {
                _loadingAt = now + 0.25f;
                string t = _loading.Text(_renderer.Pending);
                if (t != null) _hud.Toast(t, ToastKind.Info, t == "Area loaded" ? 1.5 : 3, "loading");
            }
            FinishFinding();
            if (_firstRun.ShouldSearch(now, _finding != null) && !Away) FindServers();   // [setup]
            PairingUpkeep(now);   // [setup]
            if (now >= _prefsAt)
            {
                _prefsAt = now + 1f;
                SavePrefs(_saver.Offer(CapturePrefs(), now));
            }
        }

        // ---------- [setup] pairing (Net/Pairing.cs, PROTOCOL.md "Pairing") ----------

        bool PairCardShown => _pairCode != null && _pairCode.Live(Time.unscaledTimeAsDouble) && !Away && !_menu.Open;

        void LoadPairings()
        {
            try { if (File.Exists(PairingsPath)) _pairings = PairingStore.Parse(File.ReadAllText(PairingsPath)); }
            catch (Exception e) { Debug.LogWarning("[Tabletop] bad pairings.json (pair again): " + e.Message); _pairings = new PairingStore(); }
        }

        void SavePairings()
        {
            try { File.WriteAllText(PairingsPath, _pairings.ToJson()); }
            catch (Exception e) { Debug.LogWarning("[Tabletop] can't write pairings.json: " + e.Message); }
        }

        /// <summary>welcome: where this server is now, whether our token still counts, and a code when we have none.</summary>
        void OnWelcomePairing(Dictionary<string, object> m)
        {
            _pairWelcome = PairingWelcome.From(m);
            if (_pairWelcome.ServerId.Length > 0)
            {
                _serverIdHint = _pairWelcome.ServerId;
                if (_pairings.Seen(_link.Url, _serverIdHint)) SavePairings();
            }
            if (_pairWelcome.Supported && !_pairWelcome.Paired)
            {
                if ((_pairWelcome.TokenRejected || !string.IsNullOrEmpty(_link.DeviceToken)) && _pairings.Forget(_link.Url, _serverIdHint)) SavePairings();
                _link.DeviceToken = null;
                if (!_pairRequested) { _pairRequested = true; _link.Send(PairingProtocol.Request(DeviceName)); }
            }
            if (_pairWelcome.Paired) _pairCode = null;
            _pluginNote = PluginSupport.Note(_pairWelcome, _atlas.HasPack);
            if (_pluginNote != null && _pluginNoteShown != _link.Url)
            {
                _pluginNoteShown = _link.Url;
                _hud.Toast(_pluginNote, ToastKind.Warn, 10);
                Debug.Log("[Tabletop] PLUGIN " + _pluginNote);
            }
        }

        /// <summary>Each frame: an unpaired headset's token is forgotten; an expired code is renewed while the world waits for it.</summary>
        void PairingUpkeep(float now)
        {
            if (_link.DeviceTokenRejected)
            {
                _link.DeviceTokenRejected = false;
                if (_pairings.Forget(_link.Url, _serverIdHint)) SavePairings();
                _hud.Toast("This headset was unpaired on the server: pair it again with the new code", ToastKind.Warn, 8);
            }
            if (_pairCode != null && !_pairCode.Live(now))
            {
                bool renew = _pairCode.Required && !Away;
                _pairCode = null;
                if (renew) _link.SendPairing(PairingProtocol.Request(DeviceName));
            }
        }

        string PairStatus()
        {
            if (!_link.Connected && !_link.Pairing) return "-";
            if (_pairCode != null && _pairCode.Live(Time.unscaledTimeAsDouble)) return $"code {_pairCode.Code}: type /tabletop pair {_pairCode.Code} in the server window";
            if (!_pairWelcome.Supported) return _pluginNote ?? "not supported by this server";
            if (_pairWelcome.Paired) return $"paired as {_pairWelcome.Name} ({_pairWelcome.Device})";
            return "not paired: pick the server again (Info > Server) for a new code";
        }

        /// <summary>[menu] The full status (the block menu's Info tab, and the small HUD when "Show HUD" is on).</summary>
        string StatusText() =>
            $"LINK {(_link.Connected ? "OK" : Away ? "paused (headset off)" : _link.Status)}  RTT {(_link.RttMs < 0 ? "-" : _link.RttMs + "ms")}\n" +
            $"WORLD {(_world.Loaded ? $"{_world.Dims.x}x{_world.Dims.y}x{_world.Dims.z} @ {_world.Origin}" : "-")}  PACK {(_atlas.HasPack ? "yes" : "flat")}  LIGHT {LightStatus()}\n" +
            $"TABLE {_placement.Status}\nPOSES {(_poses ? _poses.Sent : 0)}  MESH {_renderer.Quads / 1000}k faces  ENTITIES {_entities.Count}{(EntityModels.Loaded ? "" : " (boxes)")}  BLOCK ENTITIES {_blockEntities.Count}" +
            (_renderer.Pending > 0 ? $" ({_renderer.Pending} sections pending)" : "") + $"  {_lastError}\n" +
            _nav.Status + "\n" +   // [nav]
            AreaSize.Status + "\n" +   // [area]
            (_area.Active ? _area.Status : (_area.Status.Length > 0 ? _area.Status : "Y (left) = set up area   left stick = move") + "\n" + _edit.Status) +
            $"\nBLOCKS {(_registry != null ? _registry.Count + " placeable" : "-")}  {_icons.Status}" +
            $"\nTALK {TalkStatus()}" +   // [talk]
            $"\nHANDS {HandsStatus()}" +   // [hands]
            $"\nPOWER {_tools.Status}" +   // [power]
            $"\nTIME {LivingStatus()}" +   // [living]
            $"\nSOUND {_sound.Status}" +   // [sound]
            $"\nASSETS {(_assets == null ? "off (download_assets false)" : _assets.Status)}" +
            $"\nPAIR {PairStatus()}" +   // [setup]
            "\n" + SetupCard.Disclaimer;

        /// <summary>[hands] Using blocks, items and mobs as the server granted it, and what the left trigger does first.</summary>
        string HandsStatus() =>
            !_interact.Allowed ? "off (the server has no interact_v1, or interact.enabled is off)"
            : $"on, {_items?.Count ?? 0} items, left trigger {(_leftTrigger == TriggerMode.Use ? "uses first (grip = place)" : "places (grip = use)")}" +
              $"{(_interact.Entities ? "" : ", no mobs")}{(_interact.HarmPlayers ? ", players can be hurt" : "")}";

        /// <summary>[living] The world's time and weather as the table shows them (WorldLight, WeatherRenderer).</summary>
        string LivingStatus()
        {
            var c = _light != null ? _light.Clock : null;
            if (c == null || !c.Known) return "- (no world_state: noon)";
            return $"{(int)c.DayTime} {c.Dimension}  rain {c.Rain:F2} thunder {c.Thunder:F2}  sky {_light.Current.SkyFactor:F2}" +
                   $"  drops {(_weather != null ? _weather.Drops : 0)}{(WorldLight.WeatherFx ? "" : " (weather_fx off)")}" +
                   $"  particles {(_particles != null ? _particles.Particles : 0)}/{(_particles != null ? _particles.Emitters : 0)}{(AmbientParticleRenderer.Enabled ? "" : " (off)")}";
        }

        /// <summary>[talk] Chat, commands and status as the server granted them (X opens the operator panel).</summary>
        string TalkStatus()
        {
            var w = _ops.Model.Welcome;
            return $"chat {(w.Chat ? (w.ChatSend ? "as " + w.ChatName : "read-only") : "off")}  commands {(w.Commands ? "on" : "off (needs pairing or the token)")}  status {(w.Status ? "on" : "off")}  X = panel";
        }

        /// <summary>[menu] Events that used to be read off the status text: link lost/restored, area results, local edit refusals.</summary>
        void Notify()
        {
            string link = _linkWatch.Update(_link.Connected || Away);   // [comfort] no "link lost" for a link we stopped ourselves
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
            // [power] finished jobs ("Filled 500 blocks", "Undone 12 blocks; 2 changed since...") and local refusals
            foreach (var (text, warn) in _tools.TakeToasts()) _hud.Toast(text, warn ? ToastKind.Warn : ToastKind.Info);
            string tr = _tools.Result;
            if (tr != _lastToolResult)
            {
                _lastToolResult = tr;
                if (tr.StartsWith("can't") || tr.StartsWith("nothing to") || tr.StartsWith("wait") || tr.StartsWith("undo needs") || tr.Contains(" are off on this server")
                    || tr.StartsWith("not connected"))
                    _hud.Toast(char.ToUpperInvariant(tr[0]) + tr.Substring(1), ToastKind.Warn);
                else if (tr.StartsWith("corner") || tr.StartsWith("replace:") || tr.StartsWith("paste") || tr.StartsWith("wand") || tr.StartsWith("brush"))
                    _hud.Toast(char.ToUpperInvariant(tr[0]) + tr.Substring(1), ToastKind.Info, 2, "tools");
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
                    _ops.Model.Safety.UseServer(m.S("server_id"), _link.Url);   // [safety] this server's muted and blocked players
                    _interact = InteractWelcome.From(m);   // [hands] item_registry follows for interact_v1 sessions
                    _tools.OnWelcome(m); _menu.Model.SetTools(_tools);   // [power] power_v1 and its limits: the Tools tab
                    if (!_interact.Allowed && _items != null) { _items = null; _menu.SetItems(null); }
                    _card.Model.Close();
                    _operator = OperatorWelcome.From(m);   // [operator] worlds, bookmarks, containers, signs
                    _ops.Model.Marks.OnWelcome(_operator);
                    _ops.Model.LeaveMode();               // a container view of the old session is gone
                    _ops.Model.Outbox.Clear();
                    _nav.Follow.Focus.Clear();
                    Debug.Log($"[Tabletop] OPERATOR worlds {_operator.Worlds.Count}, bookmarks {(_operator.Marks ? (_operator.MarksEdit ? "on" : "read-only") : "off")}, containers {_operator.Containers}, signs {(_operator.Signs ? "on" : "off")}");
                    // Light from the server makes faces merge less: scale the area-setup face estimate (soft cap).
                    AreaMath.LightFactor = AreaMath.LightFactorFor(m.A("caps")?.Contains(Protocol.CapLight) == true, BlockShading.SmoothLighting);
                    _link.Send(Protocol.AvatarConfig(true, BlockSizeM, SkinPlayer, true, "Tabletop Giant"));
                    // [comfort] a new session lists its players and entities afresh (stale ones were hidden while offline)
                    _players.Apply(new List<PlayerInfo>());
                    _entities.Clear();
                    _light.Reset();   // [living] noon until this server's world_state
                    _sound.Director.Clear();   // [sound] a new session lists its entities and players afresh
                    if (_announce) { _announce = false; _hud.Toast("Connected to " + ServerAddress.HostOf(_link.Url), ToastKind.Info); }
                    OnWelcomePairing(m);   // [setup]
                    break;
                case "pair_code":   // [setup] show the code until /tabletop pair (or it expires)
                    _pairCode = PairCode.From(m, Time.unscaledTimeAsDouble);
                    if (_pairCode != null && _pairCode.TokenRejected && _pairings.Forget(_link.Url, _serverIdHint)) SavePairings();
                    Debug.Log($"[Tabletop] PAIR code shown ({(_pairCode != null && _pairCode.Required ? "the server waits for it" : "the world loads meanwhile")})");
                    break;
                case "paired":   // [setup] keep the token; an open server grants the trusted features at the next hello
                {
                    string token = m.S("token"), sid = m.S("server_id") ?? _serverIdHint;
                    if (string.IsNullOrEmpty(token)) break;
                    _pairings.Save(sid, _link.Url, token, m.S("device"), m.S("name"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    SavePairings();
                    _serverIdHint = sid;
                    _link.DeviceToken = token;
                    // A server that waited for the code goes on with welcome on this connection; an open one needs a new hello.
                    bool sameConnection = _pairCode != null && _pairCode.Required;
                    _pairCode = null;
                    _hud.Toast("Paired with " + ServerAddress.HostOf(_link.Url) + " as " + (m.S("name") ?? DeviceName), ToastKind.Info, 5);
                    Debug.Log("[Tabletop] PAIR paired as " + m.S("device"));
                    if (!sameConnection) _link.Reconnect();
                    break;
                }
                case "region_info":
                {
                    // [operator] the region world's spawn and the worlds; following's own region_query answers stop here
                    var spawn = m.A("spawn");
                    _ops.Model.Marks.OnRegionInfo(WorldEntry.ListFrom(m.A("worlds")), m.O("region")?.S("world"),
                                                  spawn != null && spawn.Count == 3 ? new Vector3Int((int)(double)spawn[0], (int)(double)spawn[1], (int)(double)spawn[2]) : (Vector3Int?)null);
                    if (_nav.OnFollowReply(m)) { _nav.OnLimits(m.O("limits")); break; }
                    _area.OnRegionInfo(RegionInfo.From(m));
                    _nav.OnLimits(m.O("limits"));   // [nav]
                    AreaSize.OnLimits(m.O("limits"));   // [area]
                    break;
                }
                case "region_changed":
                    // A snapshot of the new region follows; just note who changed it (shared region, last writer wins).
                    _area.OnRegionChanged(m, m.S("by_session") == _session);
                    // [comfort] the new region's limits: another world's build height before the snapshot lands (PROTOCOL.md 4.13)
                    _nav.OnLimits(m.O("limits"));
                    AreaSize.OnLimits(m.O("limits"));
                    if (m.O("limits") != null) _area.State.Limits = RegionLimits.From(m.O("limits"), _area.State.Limits);
                    break;
                case "chunk_snapshot":
                    // [nav] decoded (and compared with the current region) on a worker; ApplySnapshot runs when it's done.
                    try { _snaps.Offer(m, _world, _renderer.Unsettled()); }
                    catch (Exception e) { BadSnapshot(m.I("snapshot_id"), e.Message); }
                    break;
                case "block_update":
                    _sound.Director.OnBlockUpdate(_world, m, Time.unscaledTimeAsDouble);   // [sound] doors, levers, tools (before the cells change)
                    _edit.Editor.OnBlockUpdate(_world, m);   // the server's values replace block-edit guesses
                    if (_world.ApplyBlockUpdate(m)) _caps.ModelVersion = ++_modelVersion;   // [nav] caps re-slice
                    break;
                case "player_positions":
                    var list = new List<PlayerInfo>();
                    foreach (var o in m.A("players")) list.Add(PlayerInfo.From((Dictionary<string, object>)o));
                    _players.Apply(list);
                    _sound.Director.OnPlayers(list, _world, Time.unscaledTimeAsDouble);   // [sound] footsteps, hurt, death
                    break;
                case "entities": _entities.Apply(m); _sound.Director.OnEntities(m, _world, Time.unscaledTimeAsDouble); break;   // [entities] PROTOCOL.md 4.21, [sound]
                case BlockSounds.Type: _sound.Director.Groups = BlockSounds.From(m); Debug.Log($"[Tabletop] sound groups: {_sound.Director.Groups.Count} blocks"); break;   // [sound]
                case AssetProtocol.Type: _assets?.OnOffer(m); break;   // [sound] the operator's own files (PROTOCOL.md "Asset delivery")
                case "block_entities": _blockEntities.Apply(m); break;   // [blocks] PROTOCOL.md 4.29
                case "world_state": _light.Apply(m); break;   // [living] time of day and weather
                case "actions":                                  // [entities] PROTOCOL.md 4.22
                    _actions.Clear();
                    GameAction.Parse(m, _actions);
                    _sound.Director.OnActions(_actions, _world, Time.unscaledTimeAsDouble);   // [sound] breaks, places, digging, hurt, the server's sounds
                    foreach (var a in _actions)
                    {
                        if (a.Kind == ActionKind.Swing || a.Kind == ActionKind.Hurt) { if (!_entities.Act(a)) _players.Act(a); }
                        else if (a.Kind == ActionKind.Lid || a.Kind == ActionKind.Bell) _blockEntities.Act(a);   // [motion] lids, bells
                        else if (a.Kind == ActionKind.Explode) _particles?.Explode(a.At, a.Power);              // [motion] cloud and flash
                        else _effects.Apply(a);
                    }
                    break;
                case "avatar_state":
                    Debug.Log($"[Tabletop] avatar enabled={m.B("enabled")} scale={m.D("scale")} note={m.S("note")}");
                    break;
                case "chat": case "command_result": case "command_output": case "command_suggestions":   // [talk] PROTOCOL.md 4.26-4.28
                case "server_status": case "world_info": case "players_info":
                case "bookmarks": case "container":   // [operator] PROTOCOL.md "Marks...", "Containers"
                case "report_ack":                    // [safety] PROTOCOL.md "Chat safety"
                    _ops.OnMessage(m);
                    // [sound] a soft ping for what others say ([safety] not for muted or blocked players)
                    if (m.S("type") == "chat" && !_ops.Model.Safety.Hides(ChatMessage.From(m))) _sound.Director.OnChat(m, Time.unscaledTimeAsDouble);
                    break;
                case ItemRegistry.Type:   // [hands] usable items and spawn eggs (PROTOCOL.md "Using blocks and items")
                    try
                    {
                        _items = ItemRegistry.From(m);
                        _menu.SetItems(_items);
                        Debug.Log($"[Tabletop] item registry: {_items.Use.Count} usable items, {_items.Eggs.Count} spawn eggs");
                    }
                    catch (Exception e) { Debug.LogWarning("[Tabletop] bad item_registry: " + e.Message); }
                    break;
                case "entity_info":   // [hands] the info card above the mob
                    _card.Show(EntityInfoMessage.From(m));
                    break;
                case "history": _tools.OnHistory(m); break;        // [power] undo/redo counts
                case "box_progress": _tools.OnProgress(m); break;  // [power] a box edit, undo or redo running or done
                case BlockRegistry.Type:   // [menu] every placeable block (PROTOCOL.md 4.24)
                    try
                    {
                        _registry = BlockRegistry.From(m);
                        _menu.SetRegistry(_registry);
                        _edit.Editor.Registry = _registry;   // placeable blocks, pick defaults, wall forms
                        Debug.Log($"[Tabletop] block registry: {_registry.Count} blocks in {_registry.CategoryIds.Count} categories, {_registry.Variants.Count} variants");
                    }
                    catch (Exception e) { Debug.LogWarning("[Tabletop] bad block_registry: " + e.Message); _hud.Toast("Bad block list from the server", ToastKind.Error); }
                    break;
                case "error":
                    if (_assets != null && _assets.OnError(m)) { _hud.Toast("Download: " + m.S("message"), ToastKind.Warn, 6); Debug.LogWarning($"[Tabletop] asset error {m.S("code")}: {m.S("message")}"); break; }   // [sound]
                    if (_nav.OnFollowReply(m)) break;   // [operator] following's region_query: the player left
                    if (_ops.OnError(m)) { Debug.Log($"[Tabletop] panel error {m.S("code")}: {m.S("message")}"); break; }   // [talk] the panel shows it
                    _tools.OnError(m);   // [power] a refused box edit, undo, redo or stroke: its job is no longer pending
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
            long h0 = FrameHitch.Begin();   // [smooth] HITCH apply_ms (includes rekey_ms)
            FrameHitch.Snapshot();
            try { Apply(r); }
            finally { FrameHitch.End(FrameHitch.Part.Apply, h0); }
        }

        void Apply(SnapshotAssembler.Result r)
        {
            if (r.Snapshot == null) { BadSnapshot(r.SnapshotId, r.Error); return; }
            var s = r.Snapshot;
            bool had = _world.Loaded;
            var oldOrigin = had ? _world.Origin : s.Origin;
            var oldGrid = _world.Grid; var oldLight = _world.Light;   // [smooth] reused by a later snapshot (SnapshotBuffers)
            _world.ApplySnapshot(s);
            _edit.Editor.OnSnapshot(_world, r.Delta ? r.ListedKeys : null);   // block-edit guesses: dropped, or moved along by a delta
            _renderer.OnSnapshot(r.Plan, oldOrigin, s.Origin);
            _blockEntities.OnSnapshot(r.Plan, oldOrigin, s.Origin);   // [blocks] keep, drop and scan block entities
            if (oldOrigin != s.Origin) _effects.OnRegionChanged();   // [entities] cracks sit at region-local positions
            _caps.ModelVersion = ++_modelVersion;
            int oldWidth = AreaSizing.Current(_nav.State);   // [area]
            string oldWorld = _nav.State.World;
            _nav.OnSnapshot(s.World, s.Origin, s.Dims, r.ViewMargin);
            AreaSize.OnRegionApplied(oldWidth);   // [area] a new width: scale the block size to keep the footprint
            // [comfort] "Loading area N %" for big changes (first load, a resize, a jump or another world); not for glides
            bool big = !had || r.Plan == null || !r.Plan.Rekey || oldWidth != AreaSizing.Current(_nav.State) || oldWorld != s.World;
            if (big) { _loading.Begin(_renderer.Pending); _loadingAt = 0; }
            if (!_assetsChecked) { _assetsChecked = true; AssetToasts(); }
            if (!ReferenceEquals(oldGrid, _world.Grid)) SnapshotBuffers.Retire(oldGrid, ReferenceEquals(oldLight, _world.Light) ? null : oldLight);
            Debug.Log($"[Tabletop] snapshot {s.SnapshotId}: {s.Dims} palette {s.Palette.Length} parts {r.Parts}" +
                      (r.Delta ? $" delta ({r.Listed} sections sent)" : "") +
                      (r.Plan.Rekey ? $" re-keyed by {r.Plan.ShiftBlocks}: {r.Plan.Mesh.Count} to mesh, {r.Plan.Kept} kept" : " full re-mesh") +
                      $" decode+plan {r.Ms:F0} ms");
        }

        /// <summary>[comfort] Once, when the first area shows: say which of the user's data files are missing.</summary>
        void AssetToasts()
        {
            if (!_atlas.HasPack) _hud.Toast("No pack.zip: plain colours (QUEST_SETUP Part E)", ToastKind.Warn, 8);
            if (_soundOn && !File.Exists(Path.Combine(Application.persistentDataPath, SoundPack.FileName)))
                _hud.Toast("No sounds.zip: no sound (Part E)", ToastKind.Warn, 8);   // [sound]
            if (!File.Exists(Path.Combine(Application.persistentDataPath, EntityModels.FileName)))
                _hud.Toast("No entity_models.json: mobs are boxes (Part E)", ToastKind.Warn, 8);
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

        void OnApplicationQuit() => FlushPrefs();

        void OnDestroy()
        {
            OVRManager.HMDUnmounted -= OnUnmounted;
            OVRManager.HMDMounted -= OnMounted;
            if (_link != null && !Away) StopLink("app closed");   // [comfort] despawn the giant now, not 10 s later
            _link?.Dispose();
            _atlas?.Dispose();
            _entityTex?.Dispose();   // [entities]
            _icons?.Dispose();       // [menu] stops the icon worker
            try { _wifiLock?.Call("release"); } catch { }
        }
    }
}
