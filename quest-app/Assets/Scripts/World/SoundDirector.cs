// [sound] What the table sounds like, worked out from what the server already sends (PROTOCOL.md "Sound"), the way the
// game client would play it: block breaks and places (actions), digging hits, the sounds of block state changes
// (doors, levers, buttons, pistons, tools: block_update), footsteps of players and mobs (their movement), mob ambient,
// hurt and death sounds, item and experience pickups, thunder and TNT fuses (entities), rain and the ambience of water,
// lava and fire near the view (world_state, the region), a chat ping, the headset's own edits, and the sounds the server
// names because they can't be inferred (the sound action). Engine-free (Tests~): TabletopSound turns the requests into
// AudioSources; SoundMixer decides which of them get a voice.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    /// <summary>How much a sound matters when voices run out: a higher class steals from a lower one.</summary>
    public enum SoundPriority { Ambient = 0, Step = 1, Mob = 2, Block = 3, Event = 4, Own = 5 }

    public struct SoundRequest
    {
        public string Event;
        public Vector3 Pos;          // world block coordinates (block sounds at the block centre)
        public bool Positional;      // false: plays in the head (the chat ping)
        public float Volume, Pitch;  // vanilla's: volume over 1 is heard further, pitch 0.5..2
        public SoundPriority Priority;
        public bool Stream;
        public bool HasBlock, Stop;  // Block: the block it belongs to; Stop: stop what plays there (a jukebox)
        public Vector3Int Block;
        public override string ToString() => Stop ? $"stop @{Block}" : $"{Event} v{Volume:F2} p{Pitch:F2} {Priority} @{Pos}";
    }

    public sealed class SoundDirector
    {
        public SoundDefs Defs = new SoundDefs();          // empty until sounds.zip is loaded: then nothing is requested
        public BlockSounds Groups = new BlockSounds();    // the server's sound_groups (else guesses)
        public System.Random Rng = new System.Random();
        public readonly List<SoundRequest> Out = new List<SoundRequest>();
        public bool ChatPing = true;
        public double DedupeSeconds = 1.0;
        /// <summary>Cells sampled per tick for water, lava and fire ambience: about vanilla's density (2 x 667 random
        /// cells in 32 blocks around the player), at most this many.</summary>
        public int MaxAmbientSamples = 4096;
        public const int MaxOutPerCall = 64;
        public int Requested, Deduped;

        readonly EntityStore _store = new EntityStore();
        readonly List<int> _removed = new List<int>();
        // Entity#moveDist / nextStep (the first step after one "step block" of 1/0.6 blocks walked)
        sealed class Mob { public string Type; public Vector3 Pos; public bool Living, Item, Orb, Dying; public float MoveDist, NextStep = 1; public int AmbientTime; }
        readonly Dictionary<int, Mob> _mobs = new Dictionary<int, Mob>();
        sealed class Walker { public Vector3 Pos; public float MoveDist, NextStep = 1; public int Id = -1; public bool Dying; public bool Seen; }
        readonly Dictionary<string, Walker> _players = new Dictionary<string, Walker>();
        readonly Dictionary<int, string> _playerIds = new Dictionary<int, string>();
        sealed class Dig { public double Last, NextHit; }
        readonly Dictionary<Vector3Int, Dig> _digs = new Dictionary<Vector3Int, Dig>();
        readonly Dictionary<string, double> _recent = new Dictionary<string, double>();
        readonly List<string> _drop = new List<string>();
        readonly List<Vector3Int> _dropDigs = new List<Vector3Int>();
        int _rainTime;
        double _lastTick = -1;
        // per palette index: 0 nothing, 1 flowing water, 2 lava, 3 fire (rebuilt when the palette changes)
        byte[] _ambient = new byte[0];
        int _ambientFor = -1, _ambientSnapshot = -1;

        public bool Ready => Defs != null && Defs.Count > 0;
        public int Mobs => _mobs.Count;

        /// <summary>A new session or server: forget every entity, player and dig.</summary>
        public void Clear()
        {
            _store.All.Clear(); _mobs.Clear(); _players.Clear(); _playerIds.Clear(); _digs.Clear(); _recent.Clear(); Out.Clear();
            _lastTick = -1;
        }

        float R() => (float)Rng.NextDouble();

        static Vector3 Centre(Vector3Int b) => new Vector3(b.x + 0.5f, b.y + 0.5f, b.z + 0.5f);

        void Emit(SoundCue c, Vector3 pos, SoundPriority pr, Vector3Int? block = null, bool stream = false, double now = 0, bool positional = true)
        {
            if (!Ready || c.Event == null || Out.Count >= MaxOutPerCall || !Defs.Has(c.Event)) return;
            if (block.HasValue)
            {
                string k = block.Value.x + "," + block.Value.y + "," + block.Value.z + "|" + c.Event;
                if (_recent.TryGetValue(k, out var t) && now - t < DedupeSeconds) { Deduped++; return; }
                _recent[k] = now;
            }
            Requested++;
            Out.Add(new SoundRequest { Event = c.Event, Pos = pos, Positional = positional, Volume = c.Volume, Pitch = c.Pitch, Priority = pr, Stream = stream,
                                       HasBlock = block.HasValue, Block = block ?? default });
        }

        string StateAt(WorldModel w, Vector3Int world)
        {
            if (w == null || !w.Loaded) return null;
            var l = world - w.Origin;
            ushort id = w.Get(l.x, l.y, l.z);
            return id == 0xFFFF ? null : w.State(id);
        }

        // ---------- actions (PROTOCOL.md 4.22 and the sound action) ----------

        public void OnActions(List<GameAction> actions, WorldModel w, double now)
        {
            foreach (var a in actions)
                switch (a.Kind)
                {
                    case ActionKind.Break:
                        _digs.Remove(a.Pos);
                        if (a.Id < 0) break;   // an explosion's blocks: the explosion's own sound covers them
                        Emit(SoundRules.Break(Groups.Of(a.Block, Defs)), Centre(a.Pos), SoundPriority.Block, a.Pos, false, now);
                        break;
                    case ActionKind.Place:
                        Emit(SoundRules.Place(Groups.Of(a.Block, Defs)), Centre(a.Pos), SoundPriority.Block, a.Pos, false, now);
                        break;
                    case ActionKind.Dig:
                        if (a.Stage < 0) _digs.Remove(a.Pos);
                        else if (_digs.TryGetValue(a.Pos, out var d)) d.Last = now;
                        else _digs[a.Pos] = new Dig { Last = now, NextHit = now };
                        break;
                    case ActionKind.Hurt:
                        Hurt(a.Id, now);
                        break;
                    case ActionKind.Sound:
                        Emit(new SoundCue(SoundDefs.Key(a.Sound), a.Volume, a.Pitch), a.At, SoundPriority.Event, a.Pos, a.Stream, now);
                        break;
                    case ActionKind.SoundStop:
                        if (Ready) Out.Add(new SoundRequest { Stop = true, HasBlock = true, Block = a.Pos });
                        break;
                }
        }

        void Hurt(int id, double now)
        {
            if (_mobs.TryGetValue(id, out var m))
            {
                var e = _store.Get(id);
                Emit(new SoundCue(SoundRules.Mob(m.Type, "hurt", Defs), SoundRules.MobVolume(m.Type), SoundRules.VoicePitch(e != null && e.Baby, Rng)),
                     m.Pos + new Vector3(0, 0.5f, 0), SoundPriority.Block, null, false, now);
            }
            else if (_playerIds.TryGetValue(id, out var uuid) && _players.TryGetValue(uuid, out var p))
                Emit(new SoundCue("entity.player.hurt", 1f, SoundRules.VoicePitch(false, Rng)), p.Pos + new Vector3(0, 1, 0), SoundPriority.Block, null, false, now);
        }

        // ---------- block_update: state changes that make a sound (call before WorldModel.ApplyBlockUpdate) ----------

        public void OnBlockUpdate(WorldModel w, Dictionary<string, object> m, double now)
        {
            if (!Ready || w == null || !w.Loaded || m.I("snapshot_id", -2) != w.SnapshotId) return;
            var changes = m.A("changes");
            if (changes == null || changes.Count == 0 || changes.Count > 512) return;   // a /fill or an explosion: no per-block sounds
            var add = m.A("palette_add");
            int cues = 0;
            foreach (var c in changes)
            {
                if (!(c is List<object> a) || a.Count < 4) continue;
                int x = (int)(double)a[0], y = (int)(double)a[1], z = (int)(double)a[2], p = (int)(double)a[3];
                if (!w.InBounds(x, y, z)) continue;
                string old = w.State(w.Get(x, y, z));
                string nu = p < w.Palette.Count ? w.Palette[p] : add != null && p - w.Palette.Count < add.Count ? add[p - w.Palette.Count] as string : null;
                var cue = SoundRules.Transition(old, nu, Rng);
                if (!cue.HasValue) continue;
                var b = w.Origin + new Vector3Int(x, y, z);
                Emit(cue.Value, Centre(b), SoundPriority.Event, b, false, now);
                if (++cues >= 16) break;
            }
        }

        // ---------- entities (PROTOCOL.md 4.21): steps, ambient, death, pickups, thunder, TNT ----------

        public void OnEntities(Dictionary<string, object> m, WorldModel w, double now)
        {
            bool full = m.B("full");
            _removed.Clear();
            _store.Apply(m, _removed);
            foreach (int id in _removed)
            {
                if (!_mobs.TryGetValue(id, out var gone)) continue;
                _mobs.Remove(id);
                if (full || (!gone.Item && !gone.Orb) || !NearTaker(gone.Pos)) continue;
                if (gone.Item) Emit(new SoundCue("entity.item.pickup", 0.2f, (R() - R()) * 1.4f + 2f), gone.Pos, SoundPriority.Block, null, false, now);
                else Emit(new SoundCue("entity.experience_orb.pickup", 0.1f, (R() - R()) * 0.35f + 0.9f), gone.Pos, SoundPriority.Block, null, false, now);
            }
            var list = m.A("entities");
            if (list == null) return;
            foreach (var o in list)
            {
                if (!(o is Dictionary<string, object> d) || !d.ContainsKey("id")) continue;
                var e = _store.Get(d.I("id"));
                if (e == null) continue;
                if (!_mobs.TryGetValue(e.Id, out var mob))
                {
                    string path = e.Path ?? "";
                    mob = new Mob { Type = e.Type, Pos = e.Pos, Living = SoundRules.Living(e), Item = path == "item", Orb = path == "experience_orb",
                                    AmbientTime = -Rng.Next(SoundRules.AmbientInterval), Dying = e.Pose == "dying" };
                    _mobs[e.Id] = mob;
                    if (!full) Appeared(e, now);   // a full list (a new region) also lists what was already there
                    continue;
                }
                mob.Type = e.Type;
                if (mob.Living && !mob.Dying && e.Pose == "dying")
                {
                    mob.Dying = true;
                    Emit(new SoundCue(SoundRules.Mob(e.Type, "death", Defs), SoundRules.MobVolume(e.Type), SoundRules.VoicePitch(e.Baby, Rng)),
                         e.Pos + new Vector3(0, 0.5f, 0), SoundPriority.Block, null, false, now);
                }
                if (mob.Living && !mob.Dying && e.Vehicle < 0)
                {
                    float dx = e.Pos.x - mob.Pos.x, dz = e.Pos.z - mob.Pos.z, dist = (float)Math.Sqrt(dx * dx + dz * dz);
                    if (dist < 4f)
                    {
                        mob.MoveDist += dist * 0.6f;
                        if (mob.MoveDist > mob.NextStep && e.OnGround)
                        {
                            mob.NextStep = (int)mob.MoveDist + 1;
                            MobStep(e, w, now);
                        }
                    }
                }
                mob.Pos = e.Pos;
            }
        }

        /// <summary>Entities that make a sound when they show up: a lightning bolt's thunder and impact, a primed TNT's fuse.</summary>
        void Appeared(EntityInfo e, double now)
        {
            string p = e.Path ?? "";
            if (p == "lightning_bolt")
            {
                Emit(new SoundCue("entity.lightning_bolt.thunder", 10000f, 0.8f + R() * 0.2f), e.Pos, SoundPriority.Event, null, false, now);
                Emit(new SoundCue("entity.lightning_bolt.impact", 2f, 0.5f + R() * 0.2f), e.Pos, SoundPriority.Event, null, false, now);
            }
            // keyed by its block: the server's own entity.tnt.primed for TNT the headset lit plays once
            else if (p == "tnt") Emit(new SoundCue("entity.tnt.primed", 1f, 1f), e.Pos, SoundPriority.Event,
                                      new Vector3Int(Mathf.FloorToInt(e.Pos.x), Mathf.FloorToInt(e.Pos.y), Mathf.FloorToInt(e.Pos.z)), false, now);
        }

        void MobStep(EntityInfo e, WorldModel w, double now)
        {
            string own = "entity." + (e.Path ?? "") + ".step";
            if (Defs.Has(own)) { Emit(new SoundCue(own, 0.15f, 1f), e.Pos, SoundPriority.Step, null, false, now); return; }
            BlockStep(e.Pos, w, now);
        }

        /// <summary>The block under the feet (Entity#getOnPos: 0.2 below) plays its step sound.</summary>
        void BlockStep(Vector3 feet, WorldModel w, double now)
        {
            var under = new Vector3Int(Mathf.FloorToInt(feet.x), Mathf.FloorToInt(feet.y - 0.2f), Mathf.FloorToInt(feet.z));
            string s = StateAt(w, under);
            if (s == null || BlockRules.Shape(s) == BlockShape.Air) return;
            // A snow layer, carpet or moss carpet on top is what you walk on.
            string at = StateAt(w, new Vector3Int(under.x, under.y + 1, under.z));
            if (at != null) { string ap = BlockRules.Path(at); if (ap == "snow" || ap.EndsWith("carpet")) s = at; }
            var g = Groups.Of(s, Defs);
            if (!g.Silent) Emit(SoundRules.Step(g), feet, SoundPriority.Step, null, false, now);
        }

        /// <summary>A player or mob close enough to have picked up an item where it vanished (the pickup boxes, inflated 1 block).</summary>
        bool NearTaker(Vector3 p)
        {
            foreach (var w in _players.Values)
                if (Math.Abs(w.Pos.x - p.x) <= 1.5f && Math.Abs(w.Pos.z - p.z) <= 1.5f && p.y - w.Pos.y >= -1f && p.y - w.Pos.y <= 2.5f) return true;
            foreach (var m in _mobs.Values)
                if (m.Living && Math.Abs(m.Pos.x - p.x) <= 1.2f && Math.Abs(m.Pos.z - p.z) <= 1.2f && Math.Abs(m.Pos.y - p.y) <= 1.5f) return true;
            return false;
        }

        // ---------- players (PROTOCOL.md 4.6): footsteps, hurt, death ----------

        public void OnPlayers(List<PlayerInfo> list, WorldModel w, double now)
        {
            foreach (var v in _players.Values) v.Seen = false;
            foreach (var p in list)
            {
                if (p.Uuid == null) continue;
                if (p.Id >= 0) _playerIds[p.Id] = p.Uuid;
                if (!_players.TryGetValue(p.Uuid, out var wk)) { _players[p.Uuid] = new Walker { Pos = p.Pos, Id = p.Id, Seen = true, Dying = p.Pose == "dying" }; continue; }
                wk.Seen = true;
                if (p.Pose == "dying" && !wk.Dying) Emit(new SoundCue("entity.player.death", 1f, SoundRules.VoicePitch(false, Rng)), p.Pos + new Vector3(0, 1, 0), SoundPriority.Block, null, false, now);
                wk.Dying = p.Pose == "dying";
                float dx = p.Pos.x - wk.Pos.x, dz = p.Pos.z - wk.Pos.z, dist = (float)Math.Sqrt(dx * dx + dz * dz);
                bool walking = p.Vehicle < 0 && (p.Pose == null || p.Pose == "sneaking" || p.Pose == "standing");
                if (walking && dist < 8f)
                {
                    wk.MoveDist += dist * 0.6f;
                    if (wk.MoveDist > wk.NextStep)
                    {
                        wk.NextStep = (int)wk.MoveDist + 1;
                        BlockStep(p.Pos, w, now);
                    }
                }
                wk.Pos = p.Pos;
            }
            _drop.Clear();
            foreach (var kv in _players) if (!kv.Value.Seen) _drop.Add(kv.Key);
            foreach (var k in _drop) _players.Remove(k);
        }

        // ---------- chat (PROTOCOL.md 4.26) and the headset's own edits ----------

        /// <summary>A soft ping in the head for what other people say (not the headset's own lines or server notices).</summary>
        public void OnChat(Dictionary<string, object> m, double now)
        {
            if (!ChatPing) return;
            string k = m.S("kind");
            if (k == "chat" || k == "say" || k == "me") Emit(new SoundCue("block.note_block.pling", 0.3f, 1.5f), default, SoundPriority.Own, null, false, now, false);
            else if (k == "whisper") Emit(new SoundCue("block.note_block.pling", 0.4f, 2f), default, SoundPriority.Own, null, false, now, false);
        }

        /// <summary>The headset broke or placed a block (BlockEditor.Edited): the plugin applies it without a player, so no
        /// break/place action comes back; play it now.</summary>
        public void OnOwnEdit(string op, Vector3Int world, string state, double now)
        {
            var g = Groups.Of(state, Defs);
            if (g.Silent) return;
            Emit(op == "break" ? SoundRules.Break(g) : SoundRules.Place(g), Centre(world), SoundPriority.Own, world, false, now);
        }

        // ---------- the 20 Hz clock: mob ambient sounds, digging hits, rain, water/lava/fire ambience ----------

        /// <summary>
        /// Runs the game's 20 Hz sound clocks up to now. lo/hi: the visible window, region-local (hi exclusive); rain: the
        /// world's rain level 0..1 (WorldClock.Rain).
        /// </summary>
        public void Tick(double now, WorldModel w, Vector3Int lo, Vector3Int hi, float rain)
        {
            if (!Ready) { _lastTick = now; return; }
            if (_lastTick < 0 || now - _lastTick > 1) _lastTick = now - 0.05;
            int steps = 0;
            while (_lastTick + 0.05 <= now && steps++ < 4)
            {
                _lastTick += 0.05;
                Step(_lastTick, w, lo, hi, rain);
            }
        }

        void Step(double now, WorldModel w, Vector3Int lo, Vector3Int hi, float rain)
        {
            foreach (var kv in _mobs)
            {
                var m = kv.Value;
                if (!m.Living || m.Dying) continue;
                if (Rng.Next(1000) < m.AmbientTime++)
                {
                    m.AmbientTime = -SoundRules.AmbientInterval;
                    var e = _store.Get(kv.Key);
                    if (e == null) continue;
                    var key = SoundRules.Ambient(e, Defs);
                    if (key != null) Emit(new SoundCue(key, SoundRules.MobVolume(e.Type), SoundRules.VoicePitch(e.Baby, Rng)), e.Pos + new Vector3(0, e.Height * 0.8f, 0), SoundPriority.Mob, null, false, now);
                }
            }
            _dropDigs.Clear();
            foreach (var kv in _digs)
            {
                var d = kv.Value;
                if (now - d.Last > 1.0) { _dropDigs.Add(kv.Key); continue; }
                if (now < d.NextHit) continue;
                d.NextHit = now + 0.2;   // MultiPlayerGameMode: a hit sound every 4 ticks while digging
                var g = Groups.Of(StateAt(w, kv.Key), Defs);
                if (!g.Silent) Emit(SoundRules.Hit(g), Centre(kv.Key), SoundPriority.Block, null, false, now);
            }
            foreach (var k in _dropDigs) _digs.Remove(k);
            if (w == null || !w.Loaded) return;
            if (rain > 0.05f && Rng.Next(3) < _rainTime++) { _rainTime = 0; Rain(w, lo, hi, rain, now); }
            Ambience(w, lo, hi, now);
            if (_recent.Count > 512) PruneRecent(now);
        }

        void PruneRecent(double now)
        {
            _drop.Clear();
            foreach (var kv in _recent) if (now - kv.Value > DedupeSeconds) _drop.Add(kv.Key);
            foreach (var k in _drop) _recent.Remove(k);
        }

        /// <summary>WeatherEffectRenderer#tickRainParticles: now and then rain is heard where it lands on a random column.</summary>
        void Rain(WorldModel w, Vector3Int lo, Vector3Int hi, float rain, double now)
        {
            if (hi.x <= lo.x || hi.z <= lo.z) return;
            for (int tries = 0; tries < 4; tries++)
            {
                int x = Rng.Next(lo.x, hi.x), z = Rng.Next(lo.z, hi.z);
                int g = Precipitation.Ground(w, x, z);
                if (g >= w.Dims.y || Precipitation.KindAt(w, x, g, z) != Precip.Rain) continue;
                var at = new Vector3(w.Origin.x + x + 0.5f, w.Origin.y + g, w.Origin.z + z + 0.5f);
                Emit(new SoundCue("weather.rain", 0.2f * Mathf.Clamp01(rain), 1f), at, SoundPriority.Ambient, null, false, now);
                return;
            }
        }

        /// <summary>Block#animateTick for fluids and fire over random cells of the window: flowing water babbles, lava
        /// pops and roars where it's open above, fire crackles.</summary>
        void Ambience(WorldModel w, Vector3Int lo, Vector3Int hi, double now)
        {
            if (_ambientFor != w.Palette.Count || _ambientSnapshot != w.SnapshotId)
            {
                _ambientFor = w.Palette.Count; _ambientSnapshot = w.SnapshotId;
                _ambient = new byte[w.Palette.Count];
                for (int i = 0; i < _ambient.Length; i++)
                {
                    string s = w.Palette[i], p = BlockRules.Path(s);
                    string lv = p == "water" ? BlockEditRules.Prop(s, "level") : null;
                    _ambient[i] = (byte)(p == "water" && lv != null && lv != "0" && int.TryParse(lv, out var n) && n < 8 ? 1 : p == "lava" ? 2 : p == "fire" || p == "soul_fire" ? 3 : 0);
                }
            }
            int sx = hi.x - lo.x, sy = hi.y - lo.y, sz = hi.z - lo.z;
            if (sx <= 0 || sy <= 0 || sz <= 0) return;
            long volume = (long)sx * sy * sz;
            int n2 = (int)Math.Min(MaxAmbientSamples, volume * 3 / 80);
            for (int i = 0; i < n2; i++)
            {
                int x = lo.x + Rng.Next(sx), y = lo.y + Rng.Next(sy), z = lo.z + Rng.Next(sz);
                ushort id = w.Grid[w.Index(x, y, z)];
                if (id >= _ambient.Length || _ambient[id] == 0) continue;
                var at = new Vector3(w.Origin.x + x + 0.5f, w.Origin.y + y + 0.5f, w.Origin.z + z + 0.5f);
                switch (_ambient[id])
                {
                    case 1:
                        if (Rng.Next(64) == 0) Emit(new SoundCue("block.water.ambient", R() * 0.25f + 0.75f, R() + 0.5f), at, SoundPriority.Ambient, null, false, now);
                        break;
                    case 2:
                        if (y + 1 < w.Dims.y && w.Get(x, y + 1, z) != 0xFFFF && w.ShapeOf(w.Get(x, y + 1, z)) != BlockShape.Air) break;
                        if (Rng.Next(100) == 0) Emit(new SoundCue("block.lava.pop", 0.2f + R() * 0.2f, 0.9f + R() * 0.15f), at, SoundPriority.Ambient, null, false, now);
                        if (Rng.Next(200) == 0) Emit(new SoundCue("block.lava.ambient", 0.2f + R() * 0.2f, 0.9f + R() * 0.15f), at, SoundPriority.Ambient, null, false, now);
                        break;
                    case 3:
                        if (Rng.Next(24) == 0) Emit(new SoundCue("block.fire.ambient", 1f + R(), R() * 0.7f + 0.3f), at, SoundPriority.Ambient, null, false, now);
                        break;
                }
            }
        }
    }
}
