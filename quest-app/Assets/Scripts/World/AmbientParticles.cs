// [motion] Ambient particles, client only (PROTOCOL.md needs nothing): what vanilla's Block.animateTick and a few block
// entities spawn around the player, keyed by the block states of the region. Torch, soul/copper torch and wall torch
// flames and smoke, redstone torch dust, lit candles and candle cakes (their flame is only a particle), campfire smoke
// and sparks, fire smoke, lava pops, the nether portal swirl, 26.x falling leaves, firefly bush fireflies, bubble
// columns and water/lava dripping through a ceiling. Engine-free: the rules, the scan of the grid (worker thread) and
// the slot plan; AmbientParticleRenderer turns the plan into one static mesh that the TabletopCraft/Particles shader
// animates (every particle is a looping slot: no CPU work per frame).
// Rates: vanilla calls animateTick for random blocks near the player (ClientLevel.animateTick: 667 tries a tick in a
// 32-block box, 667 in a 64-block one), about 2-3 times a second for a block a few blocks away; the table has no
// player, so every block gets CallsPerSecond calls. Each template's PerCall is the vanilla particle count per call.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TabletopCraft.World
{
    /// <summary>How the shader moves a particle (TabletopCraft/Particles: one branch per motion).</summary>
    public enum ParticleMotion : byte { Flame = 0, Smoke = 1, Campfire = 2, LavaPop = 3, Portal = 4, Leaf = 5, Firefly = 6, BubbleUp = 7, BubbleDown = 8, Drip = 9,
        Explosion = 10, Flash = 11 }   // one-shot (the explode action): they start at a time instead of looping

    /// <summary>Layers of the particle texture array, loaded from the pack's textures/particle/*.png in this order.</summary>
    public static class ParticleSprites
    {
        public const int Flame = 0, SoulFlame = 1, CopperFlame = 2, Generic = 3, BigSmoke = 11, Lava = 23, Bubble = 24, Firefly = 25,
            Leaf = 26, Cherry = 38, PaleOak = 50, RedPoplar = 62, OrangePoplar = 66, YellowPoplar = 70, DripHang = 74, DripFall = 75,
            Explosion = 76, Flash = 92, Count = 93;

        /// <summary>Texture names (textures/particle/NAME.png) per layer. Generic runs generic_7..generic_0, the order of
        /// vanilla's smoke.json, so a sprite picked by age (index = age * (n - 1) / lifetime) starts at generic_7.</summary>
        public static readonly string[] Names = Build();

        static string[] Build()
        {
            var n = new string[Count];
            n[Flame] = "flame"; n[SoulFlame] = "soul_fire_flame"; n[CopperFlame] = "copper_fire_flame";
            for (int i = 0; i < 8; i++) n[Generic + i] = "generic_" + (7 - i);
            for (int i = 0; i < 12; i++) { n[BigSmoke + i] = "big_smoke_" + i; n[Leaf + i] = "leaf_" + i; n[Cherry + i] = "cherry_" + i; n[PaleOak + i] = "pale_oak_" + i; }
            for (int i = 0; i < 4; i++) { n[RedPoplar + i] = "red_poplar_" + (i + 1); n[OrangePoplar + i] = "orange_poplar_" + (i + 1); n[YellowPoplar + i] = "yellow_poplar_" + (i + 1); }
            n[Lava] = "lava"; n[Bubble] = "bubble"; n[Firefly] = "firefly"; n[DripHang] = "drip_hang"; n[DripFall] = "drip_fall";
            for (int i = 0; i < 16; i++) n[Explosion + i] = "explosion_" + i;
            n[Flash] = "flash";
            return n;
        }
    }

    /// <summary>A vanilla particle type as the shader draws it.</summary>
    public struct ParticleKind
    {
        public ParticleMotion Motion;
        public byte Sprite, Sprites;   // first layer and how many (picked at random per particle, or by age for smoke)
        public float Life;             // longest lifetime, seconds (the shader spreads lifetimes below it like vanilla)
        public float Size;             // full width in blocks (vanilla quadSize is half of it)
        public Color32 Color;          // base colour; alpha = opacity
        public bool Bright;            // full bright (flames, lava, fireflies, portal) instead of the light at the emitter

        // Lifetimes from the particle classes (ticks / 20): FlameParticle 8/(r*0.8+0.2)+4, SmokeParticle (BaseAshSmoke)
        // 8/(r*0.8+0.2) (x2.5 large), CampfireSmokeParticle 280+r*50 (shortened: the column would be 20 blocks tall),
        // LavaParticle 16/(r*0.8+0.2), PortalParticle r*10+40, DripParticle hang 40 + fall, BubbleColumnUp 40/(r*0.8+0.2).
        public static readonly ParticleKind
            Flame = new ParticleKind { Motion = ParticleMotion.Flame, Sprite = ParticleSprites.Flame, Sprites = 1, Life = 2.2f, Size = 0.3f, Color = W, Bright = true },
            SoulFlame = new ParticleKind { Motion = ParticleMotion.Flame, Sprite = ParticleSprites.SoulFlame, Sprites = 1, Life = 2.2f, Size = 0.3f, Color = W, Bright = true },
            CopperFlame = new ParticleKind { Motion = ParticleMotion.Flame, Sprite = ParticleSprites.CopperFlame, Sprites = 1, Life = 2.2f, Size = 0.3f, Color = W, Bright = true },
            SmallFlame = new ParticleKind { Motion = ParticleMotion.Flame, Sprite = ParticleSprites.Flame, Sprites = 1, Life = 2.2f, Size = 0.15f, Color = W, Bright = true },
            Smoke = new ParticleKind { Motion = ParticleMotion.Smoke, Sprite = ParticleSprites.Generic, Sprites = 8, Life = 2f, Size = 0.225f, Color = new Color32(77, 77, 77, 255) },
            LargeSmoke = new ParticleKind { Motion = ParticleMotion.Smoke, Sprite = ParticleSprites.Generic, Sprites = 8, Life = 5f, Size = 0.56f, Color = new Color32(77, 77, 77, 255) },
            RedDust = new ParticleKind { Motion = ParticleMotion.Smoke, Sprite = ParticleSprites.Generic, Sprites = 8, Life = 1f, Size = 0.15f, Color = new Color32(255, 0, 0, 255), Bright = true },
            CosySmoke = new ParticleKind { Motion = ParticleMotion.Campfire, Sprite = ParticleSprites.BigSmoke, Sprites = 12, Life = 7f, Size = 0.9f, Color = new Color32(255, 255, 255, 230) },
            LavaPop = new ParticleKind { Motion = ParticleMotion.LavaPop, Sprite = ParticleSprites.Lava, Sprites = 1, Life = 4f, Size = 0.25f, Color = W, Bright = true },
            Portal = new ParticleKind { Motion = ParticleMotion.Portal, Sprite = ParticleSprites.Generic, Sprites = 8, Life = 2.5f, Size = 0.14f, Color = new Color32(230, 77, 255, 255), Bright = true },
            Leaf = new ParticleKind { Motion = ParticleMotion.Leaf, Sprite = ParticleSprites.Leaf, Sprites = 12, Life = 15f, Size = 0.25f, Color = W },
            CherryLeaf = new ParticleKind { Motion = ParticleMotion.Leaf, Sprite = ParticleSprites.Cherry, Sprites = 12, Life = 15f, Size = 0.25f, Color = W },
            PaleOakLeaf = new ParticleKind { Motion = ParticleMotion.Leaf, Sprite = ParticleSprites.PaleOak, Sprites = 12, Life = 15f, Size = 0.25f, Color = W },
            Firefly = new ParticleKind { Motion = ParticleMotion.Firefly, Sprite = ParticleSprites.Firefly, Sprites = 1, Life = 10f, Size = 0.15f, Color = W, Bright = true },
            BubbleUp = new ParticleKind { Motion = ParticleMotion.BubbleUp, Sprite = ParticleSprites.Bubble, Sprites = 1, Life = 6f, Size = 0.16f, Color = W },
            BubbleDown = new ParticleKind { Motion = ParticleMotion.BubbleDown, Sprite = ParticleSprites.Bubble, Sprites = 1, Life = 4.5f, Size = 0.12f, Color = W },
            DripWater = new ParticleKind { Motion = ParticleMotion.Drip, Sprite = ParticleSprites.DripHang, Sprites = 2, Life = 4.6f, Size = 0.12f, Color = new Color32(51, 77, 255, 255) },
            DripLava = new ParticleKind { Motion = ParticleMotion.Drip, Sprite = ParticleSprites.DripHang, Sprites = 2, Life = 4.6f, Size = 0.12f, Color = new Color32(255, 73, 21, 255), Bright = true };

        static Color32 W => new Color32(255, 255, 255, 255);

        /// <summary>[motion] The explode action: HugeExplosionParticle (6-9 ticks, 16 frames by age, 2-4 blocks wide, grey
        /// 0.4-1, full bright) and a white flash.</summary>
        public static readonly ParticleKind
            Explosion = new ParticleKind { Motion = ParticleMotion.Explosion, Sprite = ParticleSprites.Explosion, Sprites = 16, Life = 0.45f, Size = 4f, Color = W, Bright = true },
            Flash = new ParticleKind { Motion = ParticleMotion.Flash, Sprite = ParticleSprites.Flash, Sprites = 1, Life = 0.15f, Size = 6f, Color = W, Bright = true };

        /// <summary>[motion] Particles of one explosion of this power: vanilla's emitter (power 2 and up) spawns 6 a tick
        /// for 8 ticks within 4 blocks (here scaled with the power), a weaker one a single cloud; plus one flash.</summary>
        public static int ExplosionClouds(float power) => power >= 2f ? 48 : 1;
        public static float ExplosionSpread(float power) => power >= 2f ? 4f * Mathf.Clamp(power / 4f, 0.5f, 1.75f) : 0f;

        public static ParticleKind WithSprites(ParticleKind k, int first, int count) { k.Sprite = (byte)first; k.Sprites = (byte)count; return k; }
    }

    /// <summary>What a block state spawns, before its neighbours are looked at.</summary>
    public struct EmitterTemplate
    {
        public ParticleKind Kind;
        public Vector3 Offset, Spread;   // spawn box centre (in the block) and half extents
        public float PerCall;            // particles per animateTick call (a chance below 1)
        public Need Need;
        public byte Tint;                // 0 none, 1 the block's biome tint (tinted leaves), 2 azalea's fixed colour
    }

    /// <summary>Neighbour condition of a template (vanilla's checks in animateTick).</summary>
    public enum Need : byte { None, AirAbove, OpenBelow, ColumnUp, ColumnDown }

    /// <summary>One spawn point found in the grid.</summary>
    public struct Emitter
    {
        public Vector3 Pos;       // region-local blocks, spawn box centre
        public Vector3 Spread;    // half extents
        public float Rate;        // particles per second, before the density scale
        public float Reach;       // blocks the particle may travel (fall, rise); portals: the axis it swirls across (0 x, 2 z)
        public ParticleKind Kind;
        public Color32 Color;     // kind colour x biome tint x light at the emitter (noon lightmap)
        public byte Light;        // LightCode of that light (the shader applies the time of day like the blocks)
    }

    /// <summary>One looping particle of the mesh: an emitter's slot with its period and spawn chance per period.</summary>
    public struct ParticleSlot
    {
        public int Emitter;
        public float Period, Chance, Seed;
    }

    /// <summary>Per palette id, what the scan needs (built on the main thread from the palette).</summary>
    public struct AmbientInfo
    {
        public EmitterTemplate[] Templates;   // null = none
        public BlockShape Shape;
        public FluidKind Fluid;
        public bool Air, Impermeable, Column, ColumnDown;
    }

    public static class AmbientRules
    {
        /// <summary>animateTick calls per block per second (vanilla near the player; see the file header).</summary>
        public const float CallsPerSecond = 2f;
        /// <summary>Most slots one emitter may use; a busier emitter gets a higher spawn chance per slot instead.</summary>
        public const int MaxSlotsPerEmitter = 24;
        public const int FallCap = 24;   // blocks a leaf or drip may fall

        static readonly Dictionary<string, EmitterTemplate[]> Cache = new Dictionary<string, EmitterTemplate[]>();
        static readonly EmitterTemplate[] None = new EmitterTemplate[0];

        // CandleBlock.PARTICLE_OFFSETS (1-4 candles); a candle cake's flame is at (0.5, 1, 0.5).
        static readonly Vector3[][] CandleOffsets =
        {
            new[] { new Vector3(0.5f, 0.5f, 0.5f) },
            new[] { new Vector3(0.375f, 0.44f, 0.5f), new Vector3(0.625f, 0.5f, 0.44f) },
            new[] { new Vector3(0.5f, 0.313f, 0.625f), new Vector3(0.375f, 0.44f, 0.5f), new Vector3(0.56f, 0.5f, 0.44f) },
            new[] { new Vector3(0.44f, 0.313f, 0.56f), new Vector3(0.625f, 0.44f, 0.56f), new Vector3(0.375f, 0.44f, 0.375f), new Vector3(0.56f, 0.5f, 0.375f) },
        };

        /// <summary>Templates of a block state (cached; call from one thread, the renderer's main thread).</summary>
        public static EmitterTemplate[] Templates(string state)
        {
            if (Cache.TryGetValue(state, out var t)) return t;
            var l = new List<EmitterTemplate>();
            Classify(state, l);
            t = l.Count == 0 ? None : l.ToArray();
            Cache[state] = t;
            return t;
        }

        static EmitterTemplate T(ParticleKind k, float x, float y, float z, float perCall, Need need = Need.None, float sx = 0, float sy = 0, float sz = 0, byte tint = 0) =>
            new EmitterTemplate { Kind = k, Offset = new Vector3(x, y, z), Spread = new Vector3(sx, sy, sz), PerCall = perCall, Need = need, Tint = tint };

        static void Classify(string state, List<EmitterTemplate> l)
        {
            string p = BlockRules.Path(state);
            bool lit = state.Contains("lit=true");
            switch (p)
            {
                // TorchBlock.animateTick: smoke and the flame at (0.5, 0.7, 0.5) every call.
                case "torch": l.Add(T(ParticleKind.Smoke, 0.5f, 0.7f, 0.5f, 1)); l.Add(T(ParticleKind.Flame, 0.5f, 0.7f, 0.5f, 1)); return;
                case "soul_torch": l.Add(T(ParticleKind.Smoke, 0.5f, 0.7f, 0.5f, 1)); l.Add(T(ParticleKind.SoulFlame, 0.5f, 0.7f, 0.5f, 1)); return;
                case "copper_torch": l.Add(T(ParticleKind.Smoke, 0.5f, 0.7f, 0.5f, 1)); l.Add(T(ParticleKind.CopperFlame, 0.5f, 0.7f, 0.5f, 1)); return;
                case "wall_torch": WallTorch(state, ParticleKind.Flame, l); return;
                case "soul_wall_torch": WallTorch(state, ParticleKind.SoulFlame, l); return;
                case "copper_wall_torch": WallTorch(state, ParticleKind.CopperFlame, l); return;
                // RedstoneTorchBlock: red dust near the tip while lit.
                case "redstone_torch": if (lit) l.Add(T(ParticleKind.RedDust, 0.5f, 0.7f, 0.5f, 1, Need.None, 0.1f, 0.1f, 0.1f)); return;
                case "redstone_wall_torch":
                    if (lit) { var o = Facing(state); l.Add(T(ParticleKind.RedDust, 0.5f - 0.27f * o.x, 0.92f, 0.5f - 0.27f * o.z, 1, Need.None, 0.1f, 0.1f, 0.1f)); }
                    return;
                // CampfireBlockEntity.particleTick: 0.11 a tick of 2-3 cosy smokes (lowered: one per 0.5 s); CampfireBlock:
                // 1 in 5 calls a lava spark (not the soul campfire).
                case "campfire":
                case "soul_campfire":
                    if (!lit) return;
                    l.Add(T(ParticleKind.CosySmoke, 0.5f, 0.6f, 0.5f, 1f / CallsPerSecond, Need.None, 0.17f, 0.2f, 0.17f));
                    if (p == "campfire") l.Add(T(ParticleKind.LavaPop, 0.5f, 0.5f, 0.5f, 0.2f));
                    return;
                // BaseFireBlock: 3 large smokes a call in the upper half of the block.
                case "fire": case "soul_fire": l.Add(T(ParticleKind.LargeSmoke, 0.5f, 0.75f, 0.5f, 3, Need.None, 0.5f, 0.25f, 0.5f)); return;
                // NetherPortalBlock: 4 a call; the swirl crosses the portal's plane (the axis across it).
                case "nether_portal": l.Add(T(ParticleKind.Portal, 0.5f, 0.5f, 0.5f, 4, Need.None, 0.5f, 0.5f, 0.5f)); return;
                // FireflyBushBlock: 0.7 a call where it is dark (the shader fades them in at dusk), within 5 blocks.
                case "firefly_bush": l.Add(T(ParticleKind.Firefly, 0.5f, 2.5f, 0.5f, 0.7f, Need.None, 5f, 2.5f, 5f)); return;
                // BubbleColumnBlock: up (soul sand) two a call, one centred, one anywhere; down (magma) a whirl at 0.8.
                case "bubble_column":
                    if (state.Contains("drag=true")) l.Add(T(ParticleKind.BubbleDown, 0.5f, 0.8f, 0.5f, 1, Need.ColumnDown));
                    else { l.Add(T(ParticleKind.BubbleUp, 0.5f, 0f, 0.5f, 1, Need.ColumnUp)); l.Add(T(ParticleKind.BubbleUp, 0.5f, 0.5f, 0.5f, 1, Need.ColumnUp, 0.5f, 0.5f, 0.5f)); }
                    return;
                // LavaFluid.animateTick: 1 in 100 calls a pop from the top when there's air above.
                case "lava": l.Add(T(ParticleKind.LavaPop, 0.5f, 1f, 0.5f, 0.01f, Need.AirAbove, 0.5f, 0f, 0.5f)); return;
            }
            if (p.EndsWith("candle_cake")) { if (lit) Candle(new[] { new Vector3(0.5f, 1f, 0.5f) }, l); return; }
            if (p == "candle" || p.EndsWith("_candle"))
            {
                if (!lit) return;
                int n = Mathf.Clamp(FluidRules.IntProp(state, "candles"), 1, 4);
                Candle(CandleOffsets[n - 1], l);
                return;
            }
            if (p.EndsWith("_leaves"))
            {
                // 26.x leaves (Blocks): ParticleLeavesBlock chances; the particle falls from just under the block.
                ParticleKind k = ParticleKind.Leaf; float chance = 0.01f; byte tint = 1;
                if (p == "cherry_leaves") { k = ParticleKind.CherryLeaf; chance = 0.1f; tint = 0; }
                else if (p == "pale_oak_leaves") { k = ParticleKind.PaleOakLeaf; chance = 0.02f; tint = 0; }
                else if (p == "azalea_leaves" || p == "flowering_azalea_leaves") tint = 2;
                else if (p == "red_poplar_leaves") { k = ParticleKind.WithSprites(k, ParticleSprites.RedPoplar, 4); tint = 0; }
                else if (p == "orange_poplar_leaves") { k = ParticleKind.WithSprites(k, ParticleSprites.OrangePoplar, 4); tint = 0; }
                else if (p == "yellow_poplar_leaves") { k = ParticleKind.WithSprites(k, ParticleSprites.YellowPoplar, 4); tint = 0; }
                l.Add(T(k, 0.5f, -0.05f, 0.5f, chance, Need.OpenBelow, 0.5f, 0f, 0.5f, tint));
            }
        }

        // AbstractCandleBlock.addParticlesAndSound: per flame, smoke 30% of calls, the small flame every call.
        static void Candle(Vector3[] offsets, List<EmitterTemplate> l)
        {
            foreach (var o in offsets) { l.Add(T(ParticleKind.Smoke, o.x, o.y, o.z, 0.3f)); l.Add(T(ParticleKind.SmallFlame, o.x, o.y, o.z, 1)); }
        }

        // WallTorchBlock: 0.27 toward the wall's opposite side, 0.22 higher than a standing torch.
        static void WallTorch(string state, ParticleKind flame, List<EmitterTemplate> l)
        {
            var f = Facing(state);
            float x = 0.5f - 0.27f * f.x, z = 0.5f - 0.27f * f.z;
            l.Add(T(ParticleKind.Smoke, x, 0.92f, z, 1)); l.Add(T(flame, x, 0.92f, z, 1));
        }

        static Vector3Int Facing(string s) =>
            s.Contains("facing=north") ? new Vector3Int(0, 0, -1) : s.Contains("facing=south") ? new Vector3Int(0, 0, 1) :
            s.Contains("facing=west") ? new Vector3Int(-1, 0, 0) : new Vector3Int(1, 0, 0);

        /// <summary>The palette's scan table (main thread: string work and the template cache live here).</summary>
        public static AmbientInfo[] Table(IList<string> palette)
        {
            var t = new AmbientInfo[palette.Count];
            for (int i = 0; i < t.Length; i++)
            {
                string s = palette[i], p = BlockRules.Path(s);
                var tl = Templates(s);
                t[i] = new AmbientInfo
                {
                    Templates = tl.Length == 0 ? null : tl,
                    Shape = BlockRules.Shape(s),
                    Fluid = FluidRules.Kind(s),
                    Air = p == "air" || p == "cave_air" || p == "void_air",
                    Impermeable = p.Contains("glass"),   // BlockTags.IMPERMEABLE: glass, stained and tinted glass
                    Column = p == "bubble_column",
                    ColumnDown = p == "bubble_column" && s.Contains("drag=true"),
                };
            }
            return t;
        }

        /// <summary>Open space a leaf or drip falls through (no collision and no fluid).</summary>
        static bool Open(in AmbientInfo a) => a.Fluid == FluidKind.None && (a.Shape == BlockShape.Air || a.Shape == BlockShape.Cross);
        static bool Full(in AmbientInfo a) => a.Shape == BlockShape.Cube || a.Shape == BlockShape.TransparentCube;

        /// <summary>Emitters of the region-local box [min, max) (clamped to the grid). Runs on a worker thread: reads only
        /// the grid, the table and the light/biomes; faces gives the leaves' tint keys (cached by the renderer's prewarm).</summary>
        public static void Scan(WorldModel w, AmbientInfo[] table, IList<string> palette, IFaceSource faces, Vector3Int min, Vector3Int max, List<Emitter> output)
        {
            var g = w.Grid; var d = w.Dims;
            int x0 = Math.Max(0, min.x), y0 = Math.Max(0, min.y), z0 = Math.Max(0, min.z);
            int x1 = Math.Min(d.x, max.x), y1 = Math.Min(d.y, max.y), z1 = Math.Min(d.z, max.z);
            if (g == null || g.Length < d.x * d.y * d.z) return;
            int n = table.Length;
            for (int y = y0; y < y1; y++)
                for (int z = z0; z < z1; z++)
                {
                    int row = (y * d.z + z) * d.x;
                    for (int x = x0; x < x1; x++)
                    {
                        int id = g[row + x];
                        if (id >= n) continue;
                        ref readonly var a = ref table[id];
                        if (a.Templates != null) Templated(w, table, palette, faces, a, id, x, y, z, output);
                        if (a.Fluid != FluidKind.None) Drip(w, table, a.Fluid, x, y, z, output);
                    }
                }
        }

        static ref readonly AmbientInfo At(WorldModel w, AmbientInfo[] table, int x, int y, int z, out bool ok)
        {
            ushort id = w.Get(x, y, z);
            ok = id < table.Length;
            return ref table[ok ? id : 0];
        }

        static void Templated(WorldModel w, AmbientInfo[] table, IList<string> palette, IFaceSource faces, in AmbientInfo a, int id, int x, int y, int z, List<Emitter> output)
        {
            foreach (var t in a.Templates)
            {
                float reach = 0;
                switch (t.Need)
                {
                    case Need.AirAbove:
                    {
                        ref readonly var up = ref At(w, table, x, y + 1, z, out bool ok);
                        if (!ok || !up.Air) continue;
                        break;
                    }
                    case Need.OpenBelow:
                    {
                        ref readonly var below = ref At(w, table, x, y - 1, z, out bool ok);
                        if (!ok || !Open(below)) continue;
                        reach = FallDepth(w, table, x, y - 1, z) + 0.95f;
                        break;
                    }
                    case Need.ColumnUp:
                    {
                        int top = y; bool ok = true;
                        while (top + 1 < w.Dims.y && top - y < FallCap) { ref readonly var c = ref At(w, table, x, top + 1, z, out ok); if (!ok || !c.Column) break; top++; }
                        reach = top + 1 - (y + t.Offset.y);   // to the top of the column (the water surface over it)
                        break;
                    }
                    case Need.ColumnDown:
                    {
                        int bot = y;
                        while (bot - 1 >= 0 && y - bot < FallCap) { ref readonly var c = ref At(w, table, x, bot - 1, z, out bool ok); if (!ok || !c.Column) break; bot--; }
                        reach = y + t.Offset.y - bot;
                        break;
                    }
                }
                var k = t.Kind;
                if (k.Motion == ParticleMotion.Portal) reach = palette[id].Contains("axis=z") ? 0 : 2;   // swirl across the plane
                var pos = new Vector3(x + t.Offset.x, y + t.Offset.y, z + t.Offset.z);
                int ly = t.Need == Need.OpenBelow ? y - 1 : t.Need == Need.AirAbove ? y + 1 : y;
                output.Add(new Emitter { Pos = pos, Spread = t.Spread, Rate = t.PerCall * CallsPerSecond, Reach = reach, Kind = k, Color = Lit(w, faces, palette, id, t, k, x, ly, z, out byte code), Light = code });
            }
        }

        /// <summary>Open cells below (x, y, z) (itself included) down to the first that isn't, at most FallCap.</summary>
        static int FallDepth(WorldModel w, AmbientInfo[] table, int x, int y, int z)
        {
            int n = 0;
            for (int yy = y - 1; yy >= 0 && n < FallCap; yy--, n++) { ref readonly var c = ref At(w, table, x, yy, z, out bool ok); if (!ok || !Open(c)) break; }
            return n;
        }

        /// <summary>ClientLevel.trySpawnDripParticles, 1 in 10 calls of a fluid cell: through a full permeable block with
        /// open space under it, a drip hangs under that block and falls to the floor.</summary>
        static void Drip(WorldModel w, AmbientInfo[] table, FluidKind fluid, int x, int y, int z, List<Emitter> output)
        {
            ref readonly var b = ref At(w, table, x, y - 1, z, out bool ok);
            if (!ok || b.Fluid != FluidKind.None || !Full(b) || b.Impermeable) return;
            ref readonly var b2 = ref At(w, table, x, y - 2, z, out ok);
            if (!ok || !Open(b2)) return;
            var k = fluid == FluidKind.Lava ? ParticleKind.DripLava : ParticleKind.DripWater;
            float reach = FallDepth(w, table, x, y - 2, z) + 0.95f;
            var c = k.Color; byte code = LightCode.FullBright;
            if (!k.Bright) c = BlockShading.Flat(w, new FaceKey { Color = k.Color }, new Vector3Int(x, y - 2, z), out code);
            c.a = k.Color.a;
            output.Add(new Emitter { Pos = new Vector3(x + 0.5f, y - 1.05f, z + 0.5f), Spread = new Vector3(0.45f, 0, 0.45f), Rate = 0.1f * CallsPerSecond, Reach = reach, Kind = k, Color = c, Light = code });
        }

        static readonly Color32 AzaleaLeaf = new Color32(112, 147, 45, 255);   // UntintedParticleLeavesBlock colour -9399763

        static Color32 Lit(WorldModel w, IFaceSource faces, IList<string> palette, int id, in EmitterTemplate t, in ParticleKind k, int x, int y, int z, out byte code)
        {
            code = LightCode.FullBright;
            if (k.Bright) return k.Color;
            var key = new FaceKey { Color = k.Color };
            if (t.Tint == 2) key.Color = Mul(k.Color, AzaleaLeaf);
            else if (t.Tint == 1 && faces != null)
            {
                var f = faces.Face(palette[id], 0);   // the leaves' own tint (biome foliage, or birch/spruce's fixed colours)
                key.Color = f.Tint == TintKind.None ? Mul(k.Color, f.Color) : f.Color;
                key.Tint = f.Tint;
            }
            var c = BlockShading.Flat(w, key, new Vector3Int(x, y, z), out code);
            c.a = k.Color.a;
            return c;
        }

        static Color32 Mul(Color32 a, Color32 b) => new Color32((byte)(a.r * b.r / 255), (byte)(a.g * b.g / 255), (byte)(a.b * b.b / 255), a.a);

        /// <summary>Density for a visible window this many blocks wide: 1 up to 64, then falling with the area so the table
        /// shows about as many particles whatever the width (never below 0.1), times the user's tabletop.json scale.</summary>
        public static float Density(float windowWidth, float userScale)
        {
            float w = Mathf.Max(windowWidth, 1f);
            return Mathf.Max(0f, userScale) * Mathf.Clamp((64f / w) * (64f / w), 0.1f, 1f);
        }

        /// <summary>Slots of one emitter: enough that a particle finishes before its slot loops (period = longest life +
        /// 15%), each spawning with the chance that gives the rate. Returns 0 slots for a rate of 0.</summary>
        public static int Slots(float rate, float life, out float period, out float chance)
        {
            period = life * 1.15f + 0.05f;
            float expect = rate * period;   // spawns per period
            if (expect <= 0f) { chance = 0f; return 0; }
            int k = Mathf.Clamp(Mathf.CeilToInt(expect - 1e-4f), 1, MaxSlotsPerEmitter);
            chance = Mathf.Min(1f, expect / k);
            return k;
        }

        /// <summary>The slots of all emitters at this density, at most cap: past it the density drops to fit, then
        /// emitters are dropped evenly (by a hash of their position) if one-slot emitters alone overflow.</summary>
        public static List<ParticleSlot> Plan(List<Emitter> emitters, float density, int cap)
        {
            var slots = new List<ParticleSlot>();
            if (cap <= 0 || density <= 0f) return slots;
            int total = Count(emitters, density, out int ones);
            float keep = 1f;
            if (total > cap)
            {
                // Lower the density so the multi-slot emitters fit beside the one-slot ones; if those alone overflow, thin them.
                float multi = total - ones;
                density *= multi > 0 ? Mathf.Clamp01((cap - ones) / multi) : 1f;
                total = Count(emitters, density, out ones);
                if (total > cap) keep = (float)cap / total;
            }
            for (int i = 0; i < emitters.Count; i++)
            {
                var e = emitters[i];
                if (keep < 1f && Hash01(e.Pos) >= keep) continue;
                int k = Slots(e.Rate * density, e.Kind.Life, out float period, out float chance);
                for (int s = 0; s < k && slots.Count < cap; s++)
                    slots.Add(new ParticleSlot { Emitter = i, Period = period, Chance = chance, Seed = Hash01(e.Pos + new Vector3(s * 0.371f, s * 0.137f, i * 0.0013f)) });
            }
            return slots;
        }

        static int Count(List<Emitter> emitters, float density, out int ones)
        {
            int total = 0; ones = 0;
            foreach (var e in emitters) { int k = Slots(e.Rate * density, e.Kind.Life, out _, out _); total += k; if (k == 1) ones++; }
            return total;
        }

        /// <summary>Stable 0..1 hash of a position (emitter thinning, slot seeds).</summary>
        public static float Hash01(Vector3 p)
        {
            unchecked
            {
                uint h = (uint)Mathf.RoundToInt(p.x * 1024f) * 0x8da6b343u ^ (uint)Mathf.RoundToInt(p.y * 1024f) * 0xd8163841u ^ (uint)Mathf.RoundToInt(p.z * 1024f) * 0xcb1ab31fu;
                h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16;
                return (h >> 8) / 16777216f;
            }
        }
    }
}
