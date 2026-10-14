// Generic Minecraft-style animation for the entity models, keyed on the game's part names: head yaw/pitch, quadruped
// and humanoid limb swing from movement (QuadrupedModel / HumanoidModel.setupAnim, WalkAnimationState), zombie arms,
// the attack swing (HumanoidModel.setupAttackAnimation), wing flaps, tail and fin wiggles, squid tentacles, spider
// legs, the riding pose. Outputs rotation deltas (radians, added to each part's default) per part. Engine-free.
using System;
using System.Collections.Generic;

namespace TabletopCraft.World
{
    public enum PartRole { None, Head, RightHindLeg, LeftHindLeg, RightFrontLeg, LeftFrontLeg, RightArm, LeftArm, RightLeg, LeftLeg, RightWing, LeftWing, Tail, Fin, Tentacle, SpiderLeg }

    /// <summary>EndCrystalModel.setupAnim: the glass spins at 3 degrees per tick and bobs (EndCrystalRenderer.getY).
    /// outer_glass: rotY(spin) * rot(60 degrees about (1, 0, 1)), and its pivot y + BobPx; inner_glass and cube:
    /// rot(60 degrees about (1, 0, 1)) * rotY(spin). Figure space keeps the game's rotation numbers.</summary>
    public static class CrystalAnimation
    {
        public const float TiltDeg = 60f;   // 1.0471976 rad about (sin 45, 0, sin 45)

        public static float SpinDeg(float ageTicks) => ageTicks * 3f;

        /// <summary>EndCrystalRenderer.getY(age) * 16 / 2: added to outer_glass.y (game model pixels).</summary>
        public static float BobPx(float ageTicks)
        {
            float s = (float)Math.Sin(ageTicks * 0.2f) / 2f + 0.5f;
            float y = (s * s + s) * 0.4f - 1.4f;
            return y * 16f / 2f;
        }
    }

    /// <summary>Per-model part roles, worked out once from the part names.</summary>
    public sealed class EntityRig
    {
        public readonly EModel Model;
        public readonly PartRole[] Roles;
        public readonly float[] Phase;      // tentacle/spider leg index phases
        public readonly bool Humanoid;      // has right_arm (held items go there)
        static readonly Dictionary<string, EntityRig> Cache = new Dictionary<string, EntityRig>();

        public static EntityRig For(EModel m)
        {
            if (m == null) return null;
            lock (Cache)
            {
                if (!Cache.TryGetValue(m.Name, out var r)) Cache[m.Name] = r = new EntityRig(m);
                return r;
            }
        }

        EntityRig(EModel m)
        {
            Model = m;
            Roles = new PartRole[m.Parts.Count];
            Phase = new float[m.Parts.Count];
            bool spider = m.Find("body0") != null && m.Find("left_middle_front_leg") != null;
            int tentacle = 0;
            foreach (var p in m.Parts)
            {
                string n = p.Name ?? "";
                PartRole role = PartRole.None;
                switch (n)
                {
                    case "head": case "head_parts": role = m.Find("head_parts") != null && n == "head" ? PartRole.None : PartRole.Head; break;
                    case "right_hind_leg": role = spider ? PartRole.SpiderLeg : PartRole.RightHindLeg; break;
                    case "left_hind_leg": role = spider ? PartRole.SpiderLeg : PartRole.LeftHindLeg; break;
                    case "right_front_leg": role = spider ? PartRole.SpiderLeg : PartRole.RightFrontLeg; break;
                    case "left_front_leg": role = spider ? PartRole.SpiderLeg : PartRole.LeftFrontLeg; break;
                    case "right_arm": role = PartRole.RightArm; break;
                    case "left_arm": role = PartRole.LeftArm; break;
                    case "right_leg": role = PartRole.RightLeg; break;
                    case "left_leg": role = PartRole.LeftLeg; break;
                    case "right_wing": role = PartRole.RightWing; break;
                    case "left_wing": role = PartRole.LeftWing; break;
                    case "tail": case "tail1": case "real_tail": role = m.Find("real_tail") != null && n == "tail" ? PartRole.None : PartRole.Tail; break;
                    case "tail_fin": case "body_back": role = PartRole.Fin; break;
                    default:
                        if (n.StartsWith("tentacle")) { role = PartRole.Tentacle; Phase[p.Index] = tentacle++; }
                        else if (spider && n.Contains("middle")) role = PartRole.SpiderLeg;
                        break;
                }
                Roles[p.Index] = role;
                if (role == PartRole.SpiderLeg) Phase[p.Index] = n.StartsWith("right") ? 1 : -1;
            }
            Humanoid = m.Find("right_arm") != null;
        }
    }

    /// <summary>What drives one frame of animation.</summary>
    public struct EntityAnimInput
    {
        public float WalkPos, WalkSpeed;    // WalkAnimationState position and speed (0..1)
        public float HeadYaw, HeadPitch;    // degrees, head relative to the body
        public float AgeTicks;
        public float AttackTime;            // 0 = no swing, else 0..1 through the arm swing
        public bool OffHandSwing, OnGround, ArmsForward, Riding;
    }

    public static class EntityAnimation
    {
        const float Deg = (float)Math.PI / 180f;
        const float Pi = (float)Math.PI;

        /// <summary>Rotation deltas (x, y, z radians per part, 3 floats each) for this frame.</summary>
        public static void Pose(EntityRig rig, in EntityAnimInput a, float[] rot)
        {
            Array.Clear(rot, 0, rot.Length);
            float p = a.WalkPos * 0.6662f, s = Math.Min(1f, a.WalkSpeed), age = a.AgeTicks;
            float swingBody = 0, swingX = 0, swingY = 0, swingZ = 0;
            if (a.AttackTime > 0)
            {
                float t = a.AttackTime;
                swingBody = (float)Math.Sin(Math.Sqrt(t) * 2 * Math.PI) * 0.2f;
                float f = 1 - t; f *= f; f *= f; f = 1 - f;
                swingX = -((float)Math.Sin(f * Math.PI) * 1.2f + (float)Math.Sin(t * Math.PI) * -(a.HeadPitch * Deg - 0.7f) * 0.75f);
                swingY = swingBody * 2;
                swingZ = (float)Math.Sin(t * Math.PI) * -0.4f;
            }
            for (int i = 0; i < rig.Roles.Length; i++)
            {
                float x = 0, y = 0, z = 0;
                switch (rig.Roles[i])
                {
                    case PartRole.Head: x = a.HeadPitch * Deg; y = a.HeadYaw * Deg; break;
                    case PartRole.RightHindLeg: case PartRole.LeftFrontLeg: x = (float)Math.Cos(p) * 1.4f * s; break;
                    case PartRole.LeftHindLeg: case PartRole.RightFrontLeg: x = (float)Math.Cos(p + Pi) * 1.4f * s; break;
                    case PartRole.RightLeg:
                        if (a.Riding) { x = -1.4137f; y = 0.31416f; z = 0.07854f; }
                        else x = (float)Math.Cos(p) * 1.4f * s;
                        break;
                    case PartRole.LeftLeg:
                        if (a.Riding) { x = -1.4137f; y = -0.31416f; z = -0.07854f; }
                        else x = (float)Math.Cos(p + Pi) * 1.4f * s;
                        break;
                    case PartRole.RightArm:
                    case PartRole.LeftArm:
                        bool right = rig.Roles[i] == PartRole.RightArm;
                        if (a.ArmsForward) x = -Pi / 2 + (float)Math.Cos(age * 0.09f) * 0.05f;
                        else x = (float)Math.Cos(p + (right ? Pi : 0)) * s;   // 2 * 0.5 * speed
                        if (a.Riding) x += -Pi / 5;
                        z = (right ? 1 : -1) * ((float)Math.Cos(age * 0.09f) * 0.05f + 0.05f);
                        if (a.AttackTime > 0 && right != a.OffHandSwing) { x += swingX; y += swingY; z += swingZ; }
                        else if (a.AttackTime > 0) y += swingBody;
                        break;
                    case PartRole.RightWing: z = a.OnGround ? 0 : (float)Math.Sin(age * 1.4f) * 0.6f + 0.6f; break;
                    case PartRole.LeftWing: z = a.OnGround ? 0 : -((float)Math.Sin(age * 1.4f) * 0.6f + 0.6f); break;
                    case PartRole.Tail: y = (float)Math.Cos(p) * 1.4f * s * 0.5f + (float)Math.Sin(age * 0.1f) * 0.05f; break;
                    case PartRole.Fin: y = (float)Math.Sin(age * 0.6f) * 0.3f; break;
                    case PartRole.Tentacle: x = (float)Math.Sin(age * 0.2f + rig.Phase[i] * 0.4f) * 0.15f; break;
                    case PartRole.SpiderLeg:
                        y = rig.Phase[i] * (float)Math.Cos(p * 2 + rig.Phase[i]) * 0.4f * s;
                        z = rig.Phase[i] * Math.Abs((float)Math.Sin(p + rig.Phase[i])) * 0.4f * s;
                        break;
                }
                rot[3 * i] = x; rot[3 * i + 1] = y; rot[3 * i + 2] = z;
            }
        }
    }

    /// <summary>WalkAnimationState over a frame (PlayerAnimation's rule): position and speed from horizontal movement.</summary>
    public struct WalkState
    {
        public float Pos, Speed;

        public void Step(float dt, float dx, float dz)
        {
            float ticks = Math.Max(0f, dt * 20f);
            if (ticks <= 0) return;
            float dist = (float)Math.Sqrt(dx * dx + dz * dz);
            float target = Math.Min(dist / ticks * 4f, 1f);
            Speed += (target - Speed) * (1f - (float)Math.Pow(0.6, ticks));
            Pos += Speed * ticks;
        }
    }

    /// <summary>Vanilla arm swing timing: 6 ticks per swing (LivingEntity.getCurrentSwingDuration without effects).</summary>
    public struct SwingState
    {
        public const float Ticks = 6f;
        public float Left;     // ticks left in the swing, 0 = idle (the default)
        public bool OffHand;

        public void Start(bool offHand)
        {
            // Swinging again past halfway restarts the swing, as LivingEntity.swing does; earlier it keeps going.
            if (Left <= Ticks / 2) Left = Ticks;
            OffHand = offHand;
        }

        /// <summary>Advance by dt seconds; returns attack time 0..1 (0 when idle).</summary>
        public float Step(float dt)
        {
            if (Left <= 0) return 0;
            Left -= dt * 20f;
            if (Left <= 0) { Left = 0; return 0; }
            return Math.Max(1e-4f, 1f - Left / Ticks);
        }
    }
}
