// [motion] Vanilla 26.3's animation curves for what moves on the table (engine-free, tested in Tests~ MotionTests):
// container lids (ChestLidController + ChestRenderer + ChestModel, ShulkerBoxBlockEntity + ShulkerBoxRenderer), the bell
// swing (BellBlockEntity + BellModel), the death tilt and the sleeping pose (LivingEntityRenderer.setupRotations), the TNT
// fuse (TntRenderer) and the creeper's swelling (CreeperRenderer.scale / getWhiteOverlayProgress). Times in ticks.
using System;
using UnityEngine;

namespace TabletopCraft.World
{
    public static class MotionCurves
    {
        public const float TicksPerSecond = 20f;

        /// <summary>ChestLidController / ShulkerBoxBlockEntity: 0.1 a tick toward open (1) or closed (0).</summary>
        public static float StepLid(float openness, bool open, float ticks) =>
            Mathf.Clamp01(openness + (open ? 0.1f : -0.1f) * ticks);

        /// <summary>ChestRenderer eases the openness (1 - (1 - o)^3) and ChestModel turns the lid and lock by
        /// -eased * pi/2 about x (radians).</summary>
        public static float ChestLidRad(float openness)
        {
            float g = 1f - Mathf.Clamp01(openness);
            g = 1f - g * g * g;
            return -g * Mathf.PI / 2f;
        }

        /// <summary>ShulkerBoxModel.setupAnim: the lid's pivot y (pixels; 24 closed) and its turn about y (radians).</summary>
        public static void ShulkerLid(float progress, out float pivotY, out float yRot)
        {
            progress = Mathf.Clamp01(progress);
            pivotY = 24f - progress * 0.5f * 16f;
            yRot = 270f * progress * Mathf.Deg2Rad;
        }

        /// <summary>BellBlockEntity rings for 50 ticks; BellModel swings by sin(t / pi) / (4 + t / 3).</summary>
        public const float BellTicks = 50f;

        public static float BellSwing(float ticks) => ticks < 0f || ticks >= BellTicks ? 0f : Mathf.Sin(ticks / Mathf.PI) / (4f + ticks / 3f);

        /// <summary>The bell body's x and z turn for a swing rung from dir (north -x, south +x, east -z, west +z).</summary>
        public static void BellAxes(string dir, float swing, out float xRot, out float zRot)
        {
            xRot = zRot = 0f;
            switch (dir)
            {
                case "north": xRot = -swing; break;
                case "south": xRot = swing; break;
                case "east": zRot = -swing; break;
                case "west": zRot = swing; break;
            }
        }

        /// <summary>LivingEntityRenderer: a dying mob tips over about its forward axis, sqrt((t - 1) / 20 * 1.6) of its flip
        /// angle (90 degrees; spiders 180) at most, in degrees. The server removes it 20 ticks after death.</summary>
        public static float DeathTiltDeg(float deathTicks, float flipDeg = 90f)
        {
            if (deathTicks <= 0f) return 0f;
            float f = Mathf.Sqrt(Mathf.Max(0f, (deathTicks - 1f) / 20f * 1.6f));
            return Mathf.Min(f, 1f) * flipDeg;
        }

        /// <summary>TntRenderer: the block swells by up to 30% over the fuse's last 10 ticks ((1 - fuse/10)^4).</summary>
        public static float TntScale(float fuseTicks)
        {
            float g = Mathf.Clamp01(1f - fuseTicks / 10f);
            g *= g; g *= g;
            return 1f + g * 0.3f;
        }

        /// <summary>TntRenderer: the white flash shows while (fuse / 5) is even (every other quarter second).</summary>
        public static bool TntWhite(float fuseTicks) => ((int)Mathf.Max(0f, fuseTicks) / 5) % 2 == 0;

        /// <summary>CreeperRenderer.scale: (1 + g 0.4) h across, (1 + g 0.1) / h up, g = swelling^4, h a fast wobble.</summary>
        public static void CreeperScale(float swell, out float across, out float up)
        {
            float h = 1f + Mathf.Sin(swell * 100f) * swell * 0.01f;
            float g = Mathf.Clamp01(swell);
            g *= g; g *= g;
            across = (1f + g * 0.4f) * h;
            up = (1f + g * 0.1f) / h;
        }

        /// <summary>CreeperRenderer.getWhiteOverlayProgress: blinks white every tenth of the swelling, at least half white.</summary>
        public static float CreeperWhite(float swell) => ((int)(swell * 10f)) % 2 == 0 ? 0f : Mathf.Clamp(swell, 0.5f, 1f);

        /// <summary>The sleeping pose's direction from a bed state ("facing" of its head toward the headboard), as a
        /// game-space unit step (x, z); false when the state isn't a bed.</summary>
        public static bool BedFacing(string state, out int dx, out int dz)
        {
            dx = dz = 0;
            if (state == null || !BlockRules.Path(state).EndsWith("_bed")) return false;
            if (state.Contains("facing=north")) dz = -1;
            else if (state.Contains("facing=south")) dz = 1;
            else if (state.Contains("facing=west")) dx = -1;
            else dx = 1;
            return true;
        }

        /// <summary>A part's sitting pose: pivot moved by (Dx, Dy, Dz) pixels (game axes, y down as the models) and turned
        /// to XRot (and YRot) radians when set, on top of the standing pose.</summary>
        public struct SitPart
        {
            public string Part;
            public float Dx, Dy, Dz, XRot, YRot;
            public bool TurnX, TurnY;
        }

        static SitPart Sit(string part, float dx, float dy, float dz) => new SitPart { Part = part, Dx = dx, Dy = dy, Dz = dz };
        static SitPart Sit(string part, float dx, float dy, float dz, float xRot) => new SitPart { Part = part, Dx = dx, Dy = dy, Dz = dz, XRot = xRot, TurnX = true };

        // WolfModel.setSittingPose + AdultWolfModel (upper body) and AdultFelineModel.setupAnim's sitting branch (26.3).
        static readonly SitPart[] WolfSit =
        {
            Sit("body", 0, 4, -2, 0.7853982f), Sit("tail", 0, 9, -2),
            Sit("right_hind_leg", 0, 6.7f, -5, 4.712389f), Sit("left_hind_leg", 0, 6.7f, -5, 4.712389f),
            Sit("right_front_leg", 0.01f, 1, 0, 5.811947f), Sit("left_front_leg", -0.01f, 1, 0, 5.811947f),
            new SitPart { Part = "upper_body", Dy = 2, XRot = 1.2566371f, TurnX = true, YRot = 0, TurnY = true },
        };
        static readonly SitPart[] CatSit =
        {
            Sit("body", 0, -4, 5, 0.7853982f), Sit("head", 0, -3.3f, 1),
            Sit("tail1", 0, 8, -2, 1.7278761f), Sit("tail2", 0, 2, -0.8f, 2.670354f),
            Sit("left_front_leg", 0, 2, -2, -0.15707964f), Sit("right_front_leg", 0, 2, -2, -0.15707964f),
            Sit("left_hind_leg", 0, 3, -4, -1.5707964f), Sit("right_hind_leg", 0, 3, -4, -1.5707964f),
        };

        /// <summary>The sitting pose of a tamed pet's model (adult wolves and cats), or null.</summary>
        public static SitPart[] SitPose(string path) => path == "wolf" ? WolfSit : path == "cat" ? CatSit : null;

        /// <summary>Pose keys for the quantised lid and bell meshes (BlockEntityCatalog.Pose): steps of 1/16 open and
        /// 1/100 radian of swing, so a handful of baked meshes covers every frame.</summary>
        public static string ChestPoseKey(float openness) { int q = Mathf.RoundToInt(Mathf.Clamp01(openness) * 16f); return q == 0 ? null : "lid:" + q; }
        public static string ShulkerPoseKey(float progress) { int q = Mathf.RoundToInt(Mathf.Clamp01(progress) * 16f); return q == 0 ? null : "shulker:" + q; }
        public static string BellPoseKey(string dir, float ticks)
        {
            int q = Mathf.RoundToInt(BellSwing(ticks) * 100f);
            return q == 0 || dir == null ? null : "bell:" + dir + ":" + q;
        }
    }
}
