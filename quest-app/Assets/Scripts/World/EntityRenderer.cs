// Draws the region's non-player entities (PROTOCOL.md 4.21) on the table: boats, minecarts, mobs, dropped items, item
// frames, paintings, falling blocks; a box of the entity's size for anything unknown, and for every model-based entity
// while there is no entity_models.json (the models come from the user's own client jar, see EntityModels). Like PlayerMarkers it works in
// Minecraft world coordinates, eases positions and angles toward the 10 Hz samples, seats passengers relative to their
// vehicle (so a rider never drifts out of its boat), and converts to RegionRoot-local Unity metres (Z mirrored) at the
// end. Also plays the entity side of actions (4.22): arm swings and the red hurt flash. Lit by the world's light at the
// entity's block when the server sends light.
using System.Collections.Generic;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class EntityRenderer : MonoBehaviour
    {
        public WorldModel Model;
        public EntityTextures Textures;
        public float BlockSizeM = 0.01f;
        public float LerpRate = 12f;
        public const float HurtSeconds = 0.5f;   // the game's 10 hurt ticks
        public static readonly Color HurtOverlay = new Color(1, 0, 0, 0.3f);   // OverlayTexture: 30 % red

        public readonly EntityStore Store = new EntityStore();
        public int Count => _shown.Count;
        /// <summary>[living] Lightning flashes the sky (WorldLight); the weather renderer is told about bolts too.</summary>
        public WorldLight Light;
        public WeatherRenderer Weather;

        sealed class Shown
        {
            public EntityInfo Info;
            public EntityFigure Fig;
            public int Looks = -1;
            public Vector3 Target, Pos;             // world coordinates
            public float Yaw, Body, Pitch;
            public WalkState Walk;
            public SwingState Swing;
            public float Age, HurtUntil, NextLight;
            public Color Light = Color.white;
            public float DyingSince = -1, FuseAt;   // [motion] when the dying pose arrived; when the fuse was last sent
            public int LastFuse;
        }

        readonly Dictionary<int, Shown> _shown = new Dictionary<int, Shown>();
        readonly List<int> _removed = new List<int>();
        readonly List<int> _order = new List<int>();
        int _generation = -1;

        public void Apply(Dictionary<string, object> m)
        {
            _removed.Clear();
            Store.Apply(m, _removed);
            foreach (var id in _removed) Remove(id);
            foreach (var e in Store.All.Values)
            {
                if (!_shown.TryGetValue(e.Id, out var s))
                {
                    s = new Shown { Info = e, Target = e.Pos, Pos = e.Pos, Yaw = e.Yaw, Body = e.BodyYaw, Pitch = e.Pitch, Age = (e.Id * 7) % 100 };
                    _shown[e.Id] = s;
                    if (e.Path == "lightning_bolt") Light?.Flash();   // [living] ClientLevel.setSkyFlashTime(2)
                }
                s.Info = e;
                s.Target = e.Pos;
                if (s.Looks != e.LooksVersion) Rebuild(s);
            }
        }

        void Rebuild(Shown s)
        {
            s.Fig?.Destroy();
            var look = EntityCatalog.Resolve(s.Info);
            s.Fig = new EntityFigure(transform, s.Info, look, Textures);
            s.Fig.SetEquipment(s.Info.Equipment, Textures);
            s.Looks = s.Info.LooksVersion;
            s.NextLight = 0;
        }

        void Remove(int id)
        {
            if (!_shown.TryGetValue(id, out var s)) return;
            s.Fig?.Destroy();
            _shown.Remove(id);
        }

        public void Clear()
        {
            foreach (var s in _shown.Values) s.Fig?.Destroy();
            _shown.Clear();
            Store.All.Clear();
        }

        /// <summary>An arm swing or hurt flash for an entity id; false if it isn't one of ours (a player, say).</summary>
        public bool Act(in GameAction a)
        {
            if (!_shown.TryGetValue(a.Id, out var s)) return false;
            if (a.Kind == ActionKind.Swing) s.Swing.Start(a.OffHand);
            else if (a.Kind == ActionKind.Hurt) s.HurtUntil = Time.unscaledTime + HurtSeconds;
            return true;
        }

        /// <summary>[hands] The entities as drawn (eased world positions, sizes), for aiming at them (World/EntityRaycast.cs).</summary>
        public void Targets(List<EntityTarget> into)
        {
            foreach (var s in _shown.Values)
            {
                var e = s.Info;
                if (s.Fig == null || string.IsNullOrEmpty(e.Uuid)) continue;
                into.Add(new EntityTarget { Uuid = e.Uuid, Id = e.Id, Type = e.Type, Name = e.Name, Pos = s.Pos, Width = e.Width, Height = e.Height });
            }
        }

        /// <summary>Where a rider sits: the vehicle's shown position plus the rider's offset in the latest samples.
        /// riderTarget is the rider's latest world position; returns false when the vehicle isn't shown.</summary>
        public bool Seat(int vehicle, Vector3 riderTarget, out Vector3 shown, out float vehicleYaw)
        {
            shown = riderTarget; vehicleYaw = 0;
            if (vehicle < 0 || !_shown.TryGetValue(vehicle, out var v)) return false;
            shown = v.Pos + (riderTarget - v.Target);
            vehicleYaw = v.Body;
            return true;
        }

        /// <summary>[living] Where an entity is shown this frame (world coordinates), its shown body yaw and its info;
        /// false if it isn't on the table (leads and lines look up their ends here).</summary>
        public bool Pose(int id, out Vector3 pos, out float bodyYaw, out EntityInfo info)
        {
            pos = default; bodyYaw = 0; info = null;
            if (id < 0 || !_shown.TryGetValue(id, out var s) || s.Fig == null) return false;
            pos = s.Pos; bodyYaw = s.Info.HasBodyYaw ? s.Body : s.Yaw; info = s.Info;
            return true;
        }

        /// <summary>[living] Every entity on the table (LeashRenderer walks them for leads and fishing lines).</summary>
        public IEnumerable<EntityInfo> ShownInfos { get { foreach (var s in _shown.Values) if (s.Fig != null) yield return s.Info; } }

        /// <summary>[living] Sky and block light at a world position (15, 0 without light data or outside the region).</summary>
        public void LightLevels(Vector3 world, out int sky, out int block)
        {
            sky = 15; block = 0;
            if (Model == null || Model.Light == null) return;
            int x = Mathf.FloorToInt(world.x) - Model.Origin.x, y = Mathf.FloorToInt(world.y) - Model.Origin.y, z = Mathf.FloorToInt(world.z) - Model.Origin.z;
            if (!Model.InBounds(x, y, z)) return;
            byte v = Model.Light[Model.Index(x, y, z)];
            sky = v >> 4; block = v & 15;
        }

        public Vector3 ToLocal(Vector3 world)
        {
            if (Model == null) return Vector3.zero;
            Vector3 l = world - (Vector3)Model.Origin;
            return new Vector3(l.x, l.y, -l.z) * BlockSizeM;
        }

        void Update()
        {
            // entity_models.json loads on a worker after start: rebuild the boxes drawn before it arrived as models.
            if (_generation != EntityModels.Generation)
            {
                _generation = EntityModels.Generation;
                foreach (var sh in _shown.Values) if (sh.Fig != null) Rebuild(sh);
            }
            var cam = Camera.main;
            float dt = Time.deltaTime, k = 1 - Mathf.Exp(-LerpRate * dt), now = Time.unscaledTime;
            float scale = BlockSizeM / 16f;
            // Vehicles first, so riders follow this frame's vehicle position.
            _order.Clear();
            foreach (var kv in _shown) if (kv.Value.Info.Vehicle < 0) _order.Add(kv.Key);
            foreach (var kv in _shown) if (kv.Value.Info.Vehicle >= 0) _order.Add(kv.Key);
            foreach (var id in _order)
            {
                var s = _shown[id];
                var e = s.Info;
                if (s.Fig == null) continue;
                Vector3 before = s.Pos;
                if (e.Vehicle >= 0 && Seat(e.Vehicle, s.Target, out var seat, out _)) s.Pos = seat;
                else
                {
                    s.Pos = Vector3.Lerp(s.Pos, s.Target, k);
                    if ((s.Target - s.Pos).sqrMagnitude > 16f * 16f) { s.Pos = s.Target; before = s.Pos; }   // teleport: no sprint
                }
                s.Yaw = Mathf.LerpAngle(s.Yaw, e.Yaw, k);
                s.Body = Mathf.LerpAngle(s.Body, e.BodyYaw, k);
                s.Pitch = Mathf.Lerp(s.Pitch, e.Pitch, k);
                s.Age += dt * 20f;
                Vector3 d = s.Pos - before;
                bool riding = e.Vehicle >= 0;
                s.Walk.Step(dt, riding ? 0 : d.x, riding ? 0 : d.z);
                float attack = s.Swing.Step(dt);
                var look = s.Fig.Look;
                var anim = new EntityAnimInput
                {
                    WalkPos = s.Walk.Pos, WalkSpeed = s.Walk.Speed,
                    HeadYaw = Mathf.Clamp(Mathf.DeltaAngle(s.Body, s.Yaw), -85f, 85f), HeadPitch = s.Pitch,
                    AgeTicks = s.Age, AttackTime = attack, OffHandSwing = s.Swing.OffHand,
                    OnGround = e.OnGround, ArmsForward = look.ArmsForward, Riding = riding, BodyYaw = e.HasBodyYaw ? s.Body : s.Yaw,
                };
                s.Fig.Animate(anim, s.Pitch);

                var root = s.Fig.Root.transform;
                root.localPosition = ToLocal(s.Pos);
                bool turns = look.Kind == EntityKind.Model || look.Kind == EntityKind.Box;
                // Minecraft yaw 0 faces +Z (south) = Unity -Z after the mirror (Euler Y 180), as for the player figures;
                // projectiles, knots and end crystals have their own rule (EntityCatalog.RootTurn).
                float yaw = e.HasBodyYaw ? s.Body : s.Yaw;
                root.localRotation = !turns ? Quaternion.identity
                    : Quaternion.Euler(0, look.Kind == EntityKind.Model ? EntityCatalog.RootTurn(look.Root, yaw) : 180f + yaw, 0);
                root.localScale = Vector3.one * (scale * look.Scale);
                var overlay = now < s.HurtUntil ? HurtOverlay : new Color(1, 0, 0, 0);
                var light = Motion(s, e, root, scale * look.Scale, now, ref overlay);   // [motion] dying, sleeping, fuses

                if (now >= s.NextLight) { s.Light = LivingLight(e, look, s.Pos); s.NextLight = now + 0.25f; }
                s.Fig.SetColors(s.Light * light, overlay);
                if (s.Fig.Label != null && cam != null) s.Fig.Label.transform.rotation = Quaternion.LookRotation(s.Fig.Label.transform.position - cam.transform.position);
                if (cam != null) s.Fig.FaceCamera(cam.transform);   // [living] sprites and display billboards
            }
        }

        /// <summary>[motion] LivingEntityRenderer and the fuse renderers on top of the standing pose (MotionCurves): a dying
        /// mob tips over, a mob asleep in a bed lies in it, primed TNT swells and flashes white, a creeper swells and
        /// blinks. Returns a light multiplier (the TNT flash); may set the overlay.</summary>
        float Motion(Shown s, EntityInfo e, Transform root, float baseScale, float now, ref Color overlay)
        {
            if (e.Pose == "dying")
            {
                if (s.DyingSince < 0) s.DyingSince = now;
                float tilt = MotionCurves.DeathTiltDeg((now - s.DyingSince) * MotionCurves.TicksPerSecond + 2f, e.Path != null && e.Path.EndsWith("spider") ? 180f : 90f);
                root.localRotation *= Quaternion.AngleAxis(tilt, Vector3.forward);
            }
            else s.DyingSince = -1;
            s.Fig.Sit(e.Pose == "sitting" && !e.Baby ? MotionCurves.SitPose(e.Path) : null);   // tamed wolves and cats
            if (e.Pose == "sleeping" && Model != null && Model.Loaded)
            {
                // In a bed: lying on the back, head toward the headboard, shifted toward the foot by the standing eye
                // height less 0.1 (vanilla's translate), the model's face (+Z) up.
                var cell = Vector3Int.FloorToInt(s.Pos) - Model.Origin;
                if (MotionCurves.BedFacing(Model.State(Model.Get(cell.x, cell.y, cell.z)), out int dx, out int dz))
                {
                    root.localRotation = Quaternion.LookRotation(Vector3.up, new Vector3(dx, 0, -dz));
                    root.localPosition = ToLocal(s.Pos - new Vector3(dx, 0, dz) * 1.4f);
                }
            }
            if (e.Fuse > 0)
            {
                if (e.Fuse != s.LastFuse) { s.LastFuse = e.Fuse; s.FuseAt = now; }
                float fuse = e.Fuse - (now - s.FuseAt) * MotionCurves.TicksPerSecond;
                root.localScale = Vector3.one * (baseScale * MotionCurves.TntScale(fuse));
                if (MotionCurves.TntWhite(fuse)) return 2.6f;   // the white block (vertex colours saturate)
            }
            else if (e.Swell > 0)
            {
                MotionCurves.CreeperScale(e.Swell, out float across, out float up);
                root.localScale = new Vector3(across, up, across) * baseScale;
                float w = MotionCurves.CreeperWhite(e.Swell);
                if (w > 0) overlay = new Color(1, 1, 1, w * 0.8f);
            }
            return 1f;
        }

        /// <summary>The lightmap colour at a world position (the block the entity's middle is in); [living] in the current
        /// time of day and weather (BlockShading.LightmapLive); white without light data.</summary>
        public Color LightAt(Vector3 world, float lift, int blockBoost = 0)
        {
            if (Model == null || Model.Light == null) { var o = BlockShading.LightmapLive(15, 0); return new Color(o.x, o.y, o.z, 1); }   // white at noon, as before
            int x = Mathf.FloorToInt(world.x) - Model.Origin.x, y = Mathf.FloorToInt(world.y + lift) - Model.Origin.y, z = Mathf.FloorToInt(world.z) - Model.Origin.z;
            int sky = 15, block = 0;
            if (Model.InBounds(x, y, z)) { byte v = Model.Light[Model.Index(x, y, z)]; sky = v >> 4; block = v & 15; }
            var l = BlockShading.LightmapLive(sky, Mathf.Min(15, block + blockBoost));
            return new Color(l.x, l.y, l.z, 1);
        }

        /// <summary>[living] An entity's light: displays may override it (brightness), experience orbs glow 7 levels
        /// brighter, lightning isn't lit.</summary>
        Color LivingLight(EntityInfo e, EntityLook look, Vector3 pos)
        {
            if (look.Kind == EntityKind.Display && e.Display != null && e.Display.BlockLight >= 0)
            {
                var o = BlockShading.LightmapLive(e.Display.SkyLight, e.Display.BlockLight);
                return new Color(o.x, o.y, o.z, 1);
            }
            if (look.Kind == EntityKind.Orb) return LightAt(pos, 0.25f, 7);
            return LightAt(pos, look.Kind == EntityKind.Model ? e.Height * 0.5f : 0.25f);
        }

        void OnDestroy() => Clear();
    }
}
