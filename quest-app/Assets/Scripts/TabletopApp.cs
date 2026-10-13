// Bootstrap: reads config, owns the link, routes protocol messages, and builds the TabletopAnchor hierarchy:
//   TabletopAnchor (table centre, +Y = up) > RegionRoot (region corner, unscaled) > Blocks (scale = block_size_m)
//                                                                               > Players
// Config file (optional): <persistentDataPath>/tabletop.json, e.g.
//   {"server":"ws://192.168.1.50:8765/tabletop","token":"","block_size_m":0.01,"skin_player":""}
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

        LinkClient _link;
        readonly WorldModel _world = new WorldModel();
        ResourcePackAtlas _atlas;
        Transform _anchor, _regionRoot, _blocks;
        TabletopRenderer _renderer;
        PlayerMarkers _players;
        PoseSender _poses;
        TablePlacement _placement;
        TextMesh _status;
        float _nextPing; int _pingId; long _rttMs = -1;
        string _lastError = "";
        AndroidJavaObject _wifiLock;

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

            _renderer = _blocks.gameObject.AddComponent<TabletopRenderer>();
            _renderer.Model = _world; _renderer.Atlas = _atlas; _renderer.OpaqueMaterial = OpaqueMaterial; _renderer.TransparentMaterial = TransparentMaterial;
            _players = playersT.gameObject.AddComponent<PlayerMarkers>();
            _players.Model = _world;

            _placement = gameObject.AddComponent<TablePlacement>();
            _placement.Anchor = _anchor; _placement.Head = Head; _placement.RightHand = RightHand;
            _placement.OnScaleChanged = k => SetBlockSize(Mathf.Clamp(BlockSizeM * k, 0.002f, 0.1f), false);

            _link = new LinkClient(ServerUrl, Token);
            _link.Start();
            if (SendPoses)
            {
                _poses = gameObject.AddComponent<PoseSender>();
                _poses.Link = _link; _poses.RegionRoot = _regionRoot; _poses.Head = Head; _poses.LeftHand = LeftHand; _poses.RightHand = RightHand;
            }
            SetBlockSize(BlockSizeM, false);
            CreateStatus();
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
            RecenterRegion();
            if (_link != null && _link.Connected) _link.Send(Protocol.AvatarConfig(true, bs, SkinPlayer, true, "Tabletop Giant"));
        }

        /// <summary>Put the region centre on the anchor: RegionRoot local = (-dx/2, 0, +dz/2) * bs (Z mirrored).</summary>
        void RecenterRegion()
        {
            if (!_world.Loaded) return;
            _regionRoot.localPosition = new Vector3(-_world.Dims.x / 2f, 0, _world.Dims.z / 2f) * BlockSizeM;
        }

        void Update()
        {
            int budget = 50;
            while (budget-- > 0 && _link.Inbound.TryDequeue(out var m)) Handle(m);
            if (_link.Connected && Time.unscaledTime > _nextPing) { _nextPing = Time.unscaledTime + 2f; _link.Send(Protocol.Ping(_pingId++)); }
            if (_status)
                _status.text = $"LINK {(_link.Connected ? "OK" : _link.Status)}  RTT {(_rttMs < 0 ? "-" : _rttMs + "ms")}\n" +
                               $"WORLD {(_world.Loaded ? $"{_world.Dims.x}x{_world.Dims.y}x{_world.Dims.z} @ {_world.Origin}" : "-")}  PACK {(_atlas.HasPack ? "yes" : "flat")}\n" +
                               $"TABLE {_placement.Status}\nPOSES {(_poses ? _poses.Sent : 0)}  {_lastError}";
        }

        void Handle(Dictionary<string, object> m)
        {
            switch (m.S("type"))
            {
                case "welcome":
                    Debug.Log($"[Tabletop] welcome from {m.S("server")} {m.S("server_version")} mc {m.S("mc_version")}");
                    _link.Send(Protocol.AvatarConfig(true, BlockSizeM, SkinPlayer, true, "Tabletop Giant"));
                    break;
                case "chunk_snapshot":
                    try
                    {
                        var s = ChunkSnapshot.From(m);
                        _renderer.Clear();
                        _world.ApplySnapshot(s);
                        RecenterRegion();
                        Debug.Log($"[Tabletop] snapshot {s.SnapshotId}: {s.Dims} palette {s.Palette.Length}");
                    }
                    catch (Exception e)
                    {
                        _lastError = "bad snapshot: " + e.Message;
                        _link.Send(new Dictionary<string, object> { ["type"] = "error", ["code"] = "bad_snapshot", ["message"] = e.Message, ["fatal"] = false });
                    }
                    break;
                case "block_update": _world.ApplyBlockUpdate(m); break;
                case "player_positions":
                    var list = new List<PlayerInfo>();
                    foreach (var o in m.A("players"))
                    {
                        var p = (Dictionary<string, object>)o;
                        var pos = Json.Doubles(p.A("pos"));
                        list.Add(new PlayerInfo { Uuid = p.S("uuid"), Name = p.S("name"), Pos = new Vector3((float)pos[0], (float)pos[1], (float)pos[2]),
                            Yaw = (float)p.D("yaw"), Pitch = (float)p.D("pitch"), Sneaking = p.B("sneaking"), Skin = p.S("skin") });
                    }
                    _players.Apply(list);
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
                case "error":
                    _lastError = $"{m.S("code")}: {m.S("message")}";
                    Debug.LogWarning("[Tabletop] server error " + _lastError);
                    break;
            }
        }

        void CreateStatus()
        {
            if (!Head) return;
            var go = new GameObject("Status");
            go.transform.SetParent(Head, false);
            go.transform.localPosition = new Vector3(0, -0.25f, 0.8f);
            _status = go.AddComponent<TextMesh>();
            _status.characterSize = 0.008f; _status.fontSize = 48; _status.anchor = TextAnchor.UpperCenter; _status.color = new Color(0.6f, 1f, 1f);
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
            try { _wifiLock?.Call("release"); } catch { }
        }
    }
}
