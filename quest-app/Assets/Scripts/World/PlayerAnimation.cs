// Minecraft-style player animation from 10 Hz player_positions samples, as the game client animates other players:
// limb swing from horizontal speed (WalkAnimationState + HumanoidModel.setupAnim), idle arm bob, body yaw that
// turns toward the walking direction and stays within 50 degrees of the head (LivingEntity.tickHeadTurn), and the
// sneak pose. The game steps per tick (20/s); this integrates the same rules over a frame's dt. Engine-free.
using System;

namespace TabletopCraft.World
{
    /// <summary>Rotations in degrees, in the figure's part frames; offsets in model pixels (figure space).</summary>
    public struct PlayerPose
    {
        public float BodyYaw;                       // Minecraft yaw of the body (the figure root's yaw)
        public float HeadYaw, HeadPitch;            // head relative to the body: yaw, pitch (down = +)
        public float BodyX;                         // lean forward when sneaking
        public float RightArmX, RightArmZ, LeftArmX, LeftArmZ, RightLegX, LeftLegX;
        public bool Crouching;
        // Figure-space pivot offsets while crouching (model px).
        public float HeadDy, BodyDy, ArmDy, LegDz, RootDy;
    }

    public sealed class PlayerAnimation
    {
        const float Rad = 180f / (float)Math.PI;
        public const float MaxHeadTurn = 50f;

        public float WalkPos, WalkSpeed, BodyYaw, AgeTicks;
        bool _started;

        /// <summary>
        /// Advance by dt seconds. dx/dz: horizontal displacement of the displayed figure in Minecraft world blocks
        /// during dt; headYaw/headPitch: Minecraft degrees from player_positions.
        /// </summary>
        public PlayerPose Step(float dt, float dx, float dz, float headYaw, float headPitch, bool sneaking)
        {
            float ticks = Math.Max(0f, dt * 20f);
            if (!_started) { _started = true; BodyYaw = headYaw; }
            AgeTicks += ticks;

            // WalkAnimationState.update(min(distancePerTick * 4, 1), 0.4) once per tick.
            float dist = (float)Math.Sqrt(dx * dx + dz * dz);
            if (ticks > 0)
            {
                float target = Math.Min(dist / ticks * 4f, 1f);
                WalkSpeed += (target - WalkSpeed) * (1f - (float)Math.Pow(0.6, ticks));
                WalkPos += WalkSpeed * ticks;
            }

            // Body yaw: toward the walking direction (or its opposite when walking backwards), 0.3 per tick.
            float bodyTarget = BodyYaw;
            if (ticks > 0 && dist / ticks > 0.05f)
            {
                float moveYaw = (float)Math.Atan2(dz, dx) * Rad - 90f;
                float off = Math.Abs(Wrap(headYaw) - moveYaw);
                bodyTarget = off > 95f && off < 265f ? moveYaw - 180f : moveYaw;
            }
            BodyYaw = Wrap(BodyYaw + Wrap(bodyTarget - BodyYaw) * (1f - (float)Math.Pow(0.7, ticks)));
            float rel = Wrap(headYaw - BodyYaw);
            if (Math.Abs(rel) > MaxHeadTurn) { BodyYaw = Wrap(BodyYaw + rel - Math.Sign(rel) * MaxHeadTurn); rel = Wrap(headYaw - BodyYaw); }

            float speed = Math.Min(WalkSpeed, 1f), ph = WalkPos * 0.6662f;
            var p = new PlayerPose
            {
                BodyYaw = BodyYaw, HeadYaw = rel, HeadPitch = Math.Max(-90f, Math.Min(90f, headPitch)),
                RightArmX = (float)Math.Cos(ph + Math.PI) * 2f * speed * 0.5f * Rad,
                LeftArmX = (float)Math.Cos(ph) * 2f * speed * 0.5f * Rad,
                RightLegX = (float)Math.Cos(ph) * 1.4f * speed * Rad,
                LeftLegX = (float)Math.Cos(ph + Math.PI) * 1.4f * speed * Rad,
                Crouching = sneaking,
            };
            if (sneaking)
            {
                p.BodyX = 0.5f * Rad;
                p.RightArmX += 0.4f * Rad; p.LeftArmX += 0.4f * Rad;
                p.HeadDy = -4.2f; p.BodyDy = -3.2f; p.ArmDy = -3.2f; p.LegDz = -4f; p.RootDy = -PlayerModel.CrouchDrop;
            }
            // AnimationUtils.bobModelPart: the arms breathe a little (right +1, left -1).
            float bobZ = ((float)Math.Cos(AgeTicks * 0.09f) * 0.05f + 0.05f) * Rad, bobX = (float)Math.Sin(AgeTicks * 0.067f) * 0.05f * Rad;
            p.RightArmZ += bobZ; p.RightArmX += bobX;
            p.LeftArmZ -= bobZ; p.LeftArmX -= bobX;
            return p;
        }

        /// <summary>Wrap degrees into [-180, 180).</summary>
        public static float Wrap(float a)
        {
            a %= 360f;
            if (a >= 180f) a -= 360f;
            if (a < -180f) a += 360f;
            return a;
        }
    }
}
