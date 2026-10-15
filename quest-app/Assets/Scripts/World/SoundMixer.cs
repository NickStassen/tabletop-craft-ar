// [sound] How loud a sound on the table is and which sounds get one of the few voices. Engine-free (Tests~).
//
// Distance: like the game, a sound fades linearly to silence over attenuation_distance (16 blocks) x max(1, volume), and
// its loudness is min(volume, 1). On the table that range is scaled with the block size, so zooming in brings sounds
// closer and a zoomed-out area is quieter at the edges, like standing further away: metres per block of hearing range =
// block_size_m x tabletop.json "sound_range" (8: at 1 cm blocks a stone breaking is heard 1.3 m away), but never less
// than MinMetresPerBlock (0.6 m for a 16-block sound) so a wide area isn't silent.
//
// Voices: at most MaxVoices play at once (24). A new sound takes a free voice, else the least important one (lower
// priority class, then quieter, then older) if that is less important than itself; at most MaxPerEvent copies of one
// event, MaxAmbient ambience voices (rain, water, lava, fire) and MaxSteps footsteps play together.
using System;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class SoundMixer
    {
        public struct Voice
        {
            public bool Busy;
            public string Event;
            public SoundPriority Priority;
            public float Gain;
            public double Started;
            public bool HasBlock;
            public Vector3Int Block;
        }

        public const float MinGain = 0.01f;
        public const float MinMetresPerBlock = 0.6f / 16f;
        public readonly Voice[] Voices;
        public int MaxPerEvent = 4, MaxAmbient = 6, MaxSteps = 6;
        public int Dropped, Stolen, Started;

        public SoundMixer(int voices = 24) { Voices = new Voice[Math.Max(1, voices)]; }

        public int Busy { get { int n = 0; foreach (var v in Voices) if (v.Busy) n++; return n; } }

        /// <summary>Metres of table per block of hearing range.</summary>
        public static float MetresPerBlock(float blockSizeM, float hearing) => Math.Max(blockSizeM * hearing, MinMetresPerBlock);

        /// <summary>Where the sound has faded out, in metres: attenuation (blocks) x max(1, volume) x metres per block.</summary>
        public static float Range(int attenuation, float volume, float metresPerBlock) => Math.Max(1, attenuation) * Math.Max(1f, volume) * metresPerBlock;

        /// <summary>The game's linear fall-off: min(volume, 1) x (1 - distance / range), times the master volume.</summary>
        public static float Gain(float volume, float distance, float range, float master = 1f)
        {
            if (range <= 0) return 0;
            return Mathf.Clamp01(volume) * Mathf.Clamp01(1f - distance / range) * Mathf.Clamp01(master);
        }

        static float Score(SoundPriority p, float gain, double age) => (int)p + Mathf.Clamp01(gain) * 0.9f - (float)Math.Min(age, 3.0) * 0.03f;

        /// <summary>A voice for a sound, or -1 (too quiet, or everything playing matters more). The caller stops whatever the
        /// voice played before (Stolen counts those).</summary>
        public int Allocate(string ev, SoundPriority pr, float gain, double now, bool hasBlock = false, Vector3Int block = default)
        {
            if (gain < MinGain) { Dropped++; return -1; }
            float score = Score(pr, gain, 0);
            int sameEvent = 0, sameClass = 0, weakestEvent = -1, weakestClass = -1, free = -1, weakest = -1;
            float wEvent = float.MaxValue, wClass = float.MaxValue, wAll = float.MaxValue;
            for (int i = 0; i < Voices.Length; i++)
            {
                ref var v = ref Voices[i];
                if (!v.Busy) { if (free < 0) free = i; continue; }
                float s = Score(v.Priority, v.Gain, now - v.Started);
                if (v.Event == ev) { sameEvent++; if (s < wEvent) { wEvent = s; weakestEvent = i; } }
                if (v.Priority == pr) { sameClass++; if (s < wClass) { wClass = s; weakestClass = i; } }
                if (s < wAll) { wAll = s; weakest = i; }
            }
            int slot;
            int classCap = pr == SoundPriority.Ambient ? MaxAmbient : pr == SoundPriority.Step ? MaxSteps : int.MaxValue;
            if (sameEvent >= MaxPerEvent) slot = wEvent < score ? weakestEvent : -1;
            else if (sameClass >= classCap) slot = wClass < score ? weakestClass : -1;
            else if (free >= 0) slot = free;
            else slot = wAll < score ? weakest : -1;
            if (slot < 0) { Dropped++; return -1; }
            if (Voices[slot].Busy) Stolen++;
            Voices[slot] = new Voice { Busy = true, Event = ev, Priority = pr, Gain = gain, Started = now, HasBlock = hasBlock, Block = block };
            Started++;
            return slot;
        }

        public void Release(int i) { if (i >= 0 && i < Voices.Length) Voices[i] = default; }

        /// <summary>Releases every voice of a block (sound_stop) and calls stop for each.</summary>
        public void StopBlock(Vector3Int block, Action<int> stop)
        {
            for (int i = 0; i < Voices.Length; i++)
                if (Voices[i].Busy && Voices[i].HasBlock && Voices[i].Block == block) { stop?.Invoke(i); Voices[i] = default; }
        }

        public void Clear() { for (int i = 0; i < Voices.Length; i++) Voices[i] = default; }
    }
}
