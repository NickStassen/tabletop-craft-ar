// Player figure additions from the entities work (PROTOCOL.md 4.6 extras, 4.22 actions), kept out of PlayerMarkers /
// PlayerFigure except for three marked hooks: held items and armour (EntityGear on the figure's parts), the riding
// pose seated in the vehicle the server names (the figure follows the vehicle's shown position, legs forward like
// HumanoidModel's riding pose), the held-item arm pose, the arm swing, lying down for sleeping / swimming / gliding,
// and the red hurt flash.
using System.Collections.Generic;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class PlayerExtras
    {
        public int Id = -1, Vehicle = -1;
        public string Pose;
        public bool HoldsMain, HoldsOff;
        public SwingState Swing;
        public float HurtUntil;

        EntityGear _gear;
        bool _flashing;
        readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();

        public bool Riding => Vehicle >= 0;

        /// <summary>From player_positions: ids, pose and equipment (gear rebuilt only when it changes).</summary>
        public void Update(in PlayerInfo p, PlayerFigure fig, EntityTextures tex)
        {
            Id = p.Id; Vehicle = p.Vehicle; Pose = p.Pose;
            HoldsMain = p.Equipment != null && p.Equipment.ContainsKey("main_hand");
            HoldsOff = p.Equipment != null && p.Equipment.ContainsKey("off_hand");
            if (tex == null) return;
            if (_gear == null) _gear = new EntityGear(tex, name => Part(fig, name));
            _gear.Set(p.Equipment);
        }

        static Transform Part(PlayerFigure f, string name)
        {
            switch (name)
            {
                case "head": case "hat": return f.PartTransform(PlayerPart.Head);
                case "body": return f.PartTransform(PlayerPart.Body);
                case "right_arm": return f.PartTransform(PlayerPart.RightArm);
                case "left_arm": return f.PartTransform(PlayerPart.LeftArm);
                case "right_leg": return f.PartTransform(PlayerPart.RightLeg);
                case "left_leg": return f.PartTransform(PlayerPart.LeftLeg);
                default: return null;
            }
        }

        public void Act(in GameAction a)
        {
            if (a.Kind == ActionKind.Swing) Swing.Start(a.OffHand);
            else if (a.Kind == ActionKind.Hurt) HurtUntil = Time.unscaledTime + EntityRenderer.HurtSeconds;
        }

        /// <summary>Arm angles (degrees, PlayerPose convention): riding, holding an item, the attack swing.</summary>
        public void AdjustPose(ref PlayerPose p, float dt, float headPitch)
        {
            if (Riding) { p.RightArmX -= 36f; p.LeftArmX -= 36f; }
            // HumanoidModel ArmPose.ITEM: the arm swings half as much and is raised 18 degrees.
            if (HoldsMain) p.RightArmX = p.RightArmX * 0.5f - 18f;
            if (HoldsOff) p.LeftArmX = p.LeftArmX * 0.5f - 18f;
            float t = Swing.Step(dt);
            if (t <= 0) return;
            // HumanoidModel.setupAttackAnimation (X and Z; the figure's arms have no Y rotation).
            float f = 1 - t; f *= f; f *= f; f = 1 - f;
            float x = -(Mathf.Sin(f * Mathf.PI) * 1.2f + Mathf.Sin(t * Mathf.PI) * -(headPitch * Mathf.Deg2Rad - 0.7f) * 0.75f) * Mathf.Rad2Deg;
            float z = Mathf.Sin(t * Mathf.PI) * -0.4f * Mathf.Rad2Deg;
            if (Swing.OffHand) { p.LeftArmX += x; p.LeftArmZ += z; } else { p.RightArmX += x; p.RightArmZ += z; }
        }

        /// <summary>After PlayerFigure.Apply: riding legs, lying poses, gear colours and the hurt flash.</summary>
        public void AfterApply(PlayerFigure fig, Material skinMaterial, EntityTextures tex, Color light)
        {
            if (Riding)
            {
                // HumanoidModel riding pose: legs forward (-1.4137 rad), splayed 18 degrees, rolled 4.5 degrees.
                fig.PartTransform(PlayerPart.RightLeg).localRotation = Rot(-1.4137167f, 0.31415927f, 0.07853982f);
                fig.PartTransform(PlayerPart.LeftLeg).localRotation = Rot(-1.4137167f, -0.31415927f, -0.07853982f);
            }
            var root = fig.Root.transform;
            if (Pose == "swimming" || Pose == "gliding" || Pose == "sleeping")
            {
                // Swim/glide: face down along the look direction; sleep: on the back. Turned about the figure's middle.
                bool sleep = Pose == "sleeping";
                var c = new Vector3(0, sleep ? 2f : 12f, 0);
                var r0 = root.localRotation;
                var r1 = r0 * Quaternion.AngleAxis(sleep ? -90f : 90f, Vector3.right);
                float s = root.localScale.x;
                root.localRotation = r1;
                root.localPosition += (r0 * c - r1 * c) * s + r0 * new Vector3(0, sleep ? 2f : -7.2f, 0) * s;
            }
            bool hurt = Time.unscaledTime < HurtUntil;
            var overlay = hurt ? EntityRenderer.HurtOverlay : new Color(1, 0, 0, 0);
            _gear?.SetColors(light, overlay);
            if (hurt == _flashing || tex == null) return;
            // The skin shader has no overlay: switch to the entity shader (same _MainTex from the figure's block) to flash.
            _flashing = hurt;
            foreach (var r in fig.Renderers)
            {
                r.sharedMaterial = hurt ? tex.Base : skinMaterial;
                r.GetPropertyBlock(_mpb);
                _mpb.SetColor(EntityTextures.OverlayId, overlay);
                _mpb.SetColor(EntityTextures.ColorId, Color.white);
                r.SetPropertyBlock(_mpb);
            }
        }

        static Quaternion Rot(float x, float y, float z) =>
            Quaternion.AngleAxis(z * Mathf.Rad2Deg, Vector3.forward) * Quaternion.AngleAxis(y * Mathf.Rad2Deg, Vector3.up) * Quaternion.AngleAxis(x * Mathf.Rad2Deg, Vector3.right);

        public void Destroy() => _gear?.Clear();
    }
}
