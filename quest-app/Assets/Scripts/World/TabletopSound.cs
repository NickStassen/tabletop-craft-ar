// [sound] Plays the table's sounds (PROTOCOL.md "Sound", docs/QUEST_SETUP.md Part E): the user's own sounds.zip,
// SoundDirector's requests (what the game would play, inferred from the link's messages) and SoundMixer's voices, as
// AudioSources under RegionRoot so they stay on the miniature when it is dragged, turned or glided.
//
//   - Fall-off is linear like the game's, over attenuation_distance x max(1, volume) blocks, the blocks scaled to
//     metres by the block size times tabletop.json "sound_range" (SoundMixer). Unity does the panning from the head.
//   - A sound is copied out of sounds.zip the first time it plays (a worker) and decoded by Unity
//     (UnityWebRequestMultimedia, OGG Vorbis, file://); a request waits up to 0.4 s for its clip, then is dropped.
//     Streamed sounds (records) are streamed. About 300 clips stay loaded (least recently used go first).
//   - tabletop.json: "sound" (true), "volume" (0.8), "sound_range" (8), "max_voices" (24), "chat_ping" (true); the
//     block menu's Info tab changes the volume and mutes (prefs.json remembers it).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TabletopCraft.Net;
using UnityEngine;
using UnityEngine.Networking;

namespace TabletopCraft.World
{
    public sealed class TabletopSound : MonoBehaviour
    {
        public Transform Head;
        public WorldModel Model;
        public NavState Nav;
        public Func<float> BlockSize = () => 0.01f;
        public Func<float> Rain = () => 0f;
        public readonly SoundDirector Director = new SoundDirector();
        public bool On = true;
        public float Volume = 0.8f, Hearing = 8f;
        public int MaxVoices = 24;
        public const int MaxClips = 300, MaxLoads = 6;
        public const float WaitForClip = 0.4f;

        SoundPack _pack;
        Task<SoundPack> _opening;
        SoundMixer _mixer;
        AudioSource[] _voices;
        AudioSource _head;
        readonly System.Random _rng = new System.Random();
        sealed class Clip { public AudioClip Value; public bool Loading, Failed; public float Used; }
        readonly Dictionary<string, Clip> _clips = new Dictionary<string, Clip>();
        struct Waiting { public int Voice; public string File; public float Until, Volume, Pitch, Range; public bool Positional; public Vector3 Local; }
        readonly List<Waiting> _waiting = new List<Waiting>();
        int _loads;

        public string Status =>
            !On ? "off" : _pack == null ? (_opening != null ? "loading sounds.zip..." : "-") : !_pack.Loaded ? _pack.Status
            : $"{Mathf.RoundToInt(Volume * 100)}%  {_pack.Status}  voices {(_mixer != null ? _mixer.Busy : 0)}/{MaxVoices}  clips {_clips.Count}";
        public bool HasPack => _pack != null && _pack.Loaded;

        void Awake()
        {
            _mixer = new SoundMixer(MaxVoices);
            _voices = new AudioSource[_mixer.Voices.Length];
            for (int i = 0; i < _voices.Length; i++)
            {
                var go = new GameObject("Voice" + i);
                go.transform.SetParent(transform, false);
                _voices[i] = Setup(go.AddComponent<AudioSource>(), true);
            }
            _head = Setup(gameObject.AddComponent<AudioSource>(), false);
        }

        static AudioSource Setup(AudioSource s, bool spatial)
        {
            s.playOnAwake = false; s.loop = false; s.dopplerLevel = 0; s.spread = 0;
            s.spatialBlend = spatial ? 1f : 0f;
            s.rolloffMode = AudioRolloffMode.Linear;
            return s;
        }

        void Start()
        {
            // The scene has no AudioListener: the head hears the table.
            if (FindFirstObjectByType<AudioListener>() == null && Head != null) Head.gameObject.AddComponent<AudioListener>();
        }

        /// <summary>Opens (or re-opens, after a download) sounds.zip on a worker.</summary>
        public void Load(string path)
        {
            string cache = Application.temporaryCachePath;
            _opening = Task.Run(() => SoundPack.Open(path, cache));
        }

        void Opened()
        {
            if (_opening == null || !_opening.IsCompleted) return;
            var p = _opening.IsFaulted ? null : _opening.Result;
            _opening = null;
            foreach (var v in _voices) { v.Stop(); v.clip = null; }
            _head.Stop();
            _mixer.Clear(); _waiting.Clear();
            foreach (var c in _clips.Values) if (c.Value != null) Destroy(c.Value);
            _clips.Clear();
            _pack?.Dispose();
            _pack = p;
            Director.Defs = p != null && p.Loaded ? p.Defs : new SoundDefs();
            Debug.Log("[Tabletop] sounds: " + (p?.Status ?? "failed"));
        }

        Vector3 Local(Vector3 world)
        {
            float bs = BlockSize();
            var o = Model.Origin;
            return new Vector3((world.x - o.x) * bs, (world.y - o.y) * bs, -(world.z - o.z) * bs);
        }

        void Update()
        {
            Opened();
            if (!On || !HasPack || Model == null || !Model.Loaded)
            {
                Director.Out.Clear();
                Director.Tick(Time.unscaledTimeAsDouble, null, default, default, 0);
                return;
            }
            // The visible window, region-local (sounds come from the whole region; ambience only from what you see).
            Vector3Int lo = default, hi = Model.Dims;
            if (Nav != null && Nav.Loaded)
            {
                var a = Nav.WindowMin; var w = Nav.Window;
                lo = new Vector3Int(Mathf.Clamp(Mathf.FloorToInt(a.x) - Model.Origin.x, 0, Model.Dims.x), Mathf.Clamp(Mathf.FloorToInt(a.y) - Model.Origin.y, 0, Model.Dims.y),
                                    Mathf.Clamp(Mathf.FloorToInt(a.z) - Model.Origin.z, 0, Model.Dims.z));
                hi = new Vector3Int(Mathf.Min(Model.Dims.x, lo.x + w.x), Mathf.Min(Model.Dims.y, lo.y + w.y), Mathf.Min(Model.Dims.z, lo.z + w.z));
            }
            Director.Tick(Time.unscaledTimeAsDouble, Model, lo, hi, Rain());
            for (int i = 0; i < _voices.Length; i++)
                if (_mixer.Voices[i].Busy && !_voices[i].isPlaying && !IsWaiting(i)) _mixer.Release(i);
            foreach (var r in Director.Out) Play(r);
            Director.Out.Clear();
            float now = Time.unscaledTime;
            for (int k = _waiting.Count - 1; k >= 0; k--)
            {
                var w = _waiting[k];
                if (now > w.Until) { _mixer.Release(w.Voice); _waiting.RemoveAt(k); continue; }
                if (_clips.TryGetValue(w.File, out var c) && !c.Loading)
                {
                    _waiting.RemoveAt(k);
                    if (c.Value != null) Begin(w.Voice, c, w.Volume, w.Pitch, w.Range, w.Positional, w.Local);
                    else _mixer.Release(w.Voice);
                }
            }
        }

        bool IsWaiting(int voice) { foreach (var w in _waiting) if (w.Voice == voice) return true; return false; }

        void Play(SoundRequest r)
        {
            if (r.Stop)
            {
                _mixer.StopBlock(r.Block, i => { _voices[i].Stop(); _waiting.RemoveAll(w => w.Voice == i); });
                return;
            }
            if (!Director.Defs.Pick(r.Event, _rng, out var c)) return;
            float volume = r.Volume * c.Volume, pitch = Mathf.Clamp(r.Pitch * c.Pitch, 0.5f, 2f);
            float range = SoundMixer.Range(c.Attenuation, volume, SoundMixer.MetresPerBlock(BlockSize(), Hearing));
            Vector3 local = r.Positional ? Local(r.Pos) : default;
            float dist = r.Positional && Head != null ? Vector3.Distance(transform.TransformPoint(local), Head.position) : 0f;
            float gain = r.Positional ? SoundMixer.Gain(volume, dist, range, Volume) : Mathf.Clamp01(volume) * Volume;
            if (!r.Positional)   // the chat ping: its own source in the head
            {
                var hc = ClipFor(c.File, c.Stream);
                if (hc != null && hc.Value != null && gain >= SoundMixer.MinGain) { _head.pitch = pitch; _head.PlayOneShot(hc.Value, gain); }
                return;
            }
            int v = _mixer.Allocate(r.Event, r.Priority, gain, Time.unscaledTimeAsDouble, r.HasBlock, r.Block);
            if (v < 0) return;
            _voices[v].Stop();
            _waiting.RemoveAll(w => w.Voice == v);
            var clip = ClipFor(c.File, c.Stream);
            if (clip == null) { _mixer.Release(v); return; }
            if (clip.Value != null) Begin(v, clip, volume, pitch, range, true, local);
            else _waiting.Add(new Waiting { Voice = v, File = c.File, Until = Time.unscaledTime + (c.Stream ? 3f : WaitForClip), Volume = volume, Pitch = pitch, Range = range, Positional = true, Local = local });
        }

        void Begin(int v, Clip clip, float volume, float pitch, float range, bool positional, Vector3 local)
        {
            var s = _voices[v];
            clip.Used = Time.unscaledTime;
            s.transform.localPosition = local;
            s.clip = clip.Value;
            s.volume = Mathf.Clamp01(volume) * Volume;
            s.pitch = pitch;
            s.minDistance = Mathf.Max(0.001f, range * 0.02f);
            s.maxDistance = Mathf.Max(s.minDistance + 0.001f, range);
            s.spatialBlend = positional ? 1f : 0f;
            s.Play();
        }

        /// <summary>The clip of a file: loaded, loading (Value null, Loading), or null when it can't be had.</summary>
        Clip ClipFor(string file, bool stream)
        {
            if (_clips.TryGetValue(file, out var c)) { c.Used = Time.unscaledTime; return c.Failed ? null : c; }
            if (_loads >= MaxLoads) return null;
            if (_clips.Count >= MaxClips) Evict();
            c = new Clip { Loading = true, Used = Time.unscaledTime };
            _clips[file] = c;
            StartCoroutine(LoadClip(file, stream, c));
            return c;
        }

        IEnumerator LoadClip(string file, bool stream, Clip c)
        {
            _loads++;
            var pack = _pack;
            var extract = Task.Run(() => pack?.Extract(file));
            while (!extract.IsCompleted) yield return null;
            string path = extract.IsFaulted ? null : extract.Result;
            if (path == null || pack != _pack) { c.Failed = true; c.Loading = false; _loads--; yield break; }
            using (var req = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.OGGVORBIS))
            {
                var dh = (DownloadHandlerAudioClip)req.downloadHandler;
                dh.streamAudio = stream;
                dh.compressed = false;
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success && pack == _pack) c.Value = dh.audioClip;
                else { c.Failed = true; Debug.LogWarning("[Tabletop] sound " + file + ": " + req.error); }
            }
            c.Loading = false;
            _loads--;
        }

        void Evict()
        {
            string oldest = null; float t = float.MaxValue;
            foreach (var kv in _clips)
            {
                if (kv.Value.Loading || kv.Value.Used >= t || Playing(kv.Value.Value)) continue;
                oldest = kv.Key; t = kv.Value.Used;
            }
            if (oldest == null) return;
            if (_clips[oldest].Value != null) Destroy(_clips[oldest].Value);
            _clips.Remove(oldest);
        }

        bool Playing(AudioClip clip)
        {
            if (clip == null) return false;
            foreach (var v in _voices) if (v.clip == clip && v.isPlaying) return true;
            return false;
        }

        /// <summary>Mute or unmute and set the master volume (0..1); playing voices follow.</summary>
        public void SetVolume(float volume, bool on)
        {
            Volume = Mathf.Clamp01(volume); On = on;
            if (!on) { foreach (var v in _voices) v.Stop(); _mixer.Clear(); _waiting.Clear(); }
        }

        void OnDestroy()
        {
            foreach (var c in _clips.Values) if (c.Value != null) Destroy(c.Value);
            _pack?.Dispose();
        }
    }
}
