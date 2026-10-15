// Break and place blocks in the miniature from the headset, Minecraft style (docs/QUEST_SETUP.md "Editing blocks"), and
// [hands] use blocks, items and mobs like a creative player's right click (QUEST_SETUP.md "Using blocks, items and mobs").
// A ray from the right controller (BlockRaycast, DDA in block space) targets a block; a thin outline shows it, like
// vanilla's selection box, and a short laser runs from the controller to the hit point (a short faded stub when the
// ray hits nothing). A faint ghost of the block the left trigger would place, oriented as it will be (BlockPlacement:
// wall torches, top slabs, door halves...), sits in the cell it fills, inside a second outline; both turn red when the
// placement would be refused. [hands] The same ray hits mobs and players too (EntityRaycast, the eased boxes as drawn):
// the nearer of a mob and a block wins. Bindings, active while area setup is off (it owns the trigger and B then):
//   Right trigger   break the targeted block (hold to repeat); on a mob: hit it (4 a second while held)
//   Left trigger    vanilla's right-click order (World/UseRules.cs): a mob (use the selected item on it, or open its
//                   info card), a block a hand can use (doors, levers, buttons...), the selected item on the face
//                   (bone meal, buckets, flint and steel, eggs...), else place the selected block (hold to repeat placing
//                   and item uses). Left grip + left trigger = sneak: always place or use the item. tabletop.json
//                   "left_trigger": "place" swaps the two.
//   Right grip      on a mob or player: pick it up; a ghost box shows where it goes down, release to drop it there. If
//                   the left grip joins (both grips = drag the table), the grab is let go.
//   B (right)       pick the targeted block as the selected block (middle click); on a mob: its spawn egg
// The outline colour says what the left trigger will do: dark (place, with the white ghost outline), cyan (use the
// block), orange (use the item), yellow (a mob). The selected block floats above the right controller as a small
// textured cube with its name (an item as its icon); under it, the target's name and world position. Edits go out as
// block_edit (PROTOCOL.md 4.20) and are shown at once when the result is already in the palette (BlockEdits.cs reconciles
// them with the server's block_update / error); uses go out as interact / entity_op and are never guessed locally. A
// short haptic tick confirms each.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

namespace TabletopCraft.XR
{
    public sealed class BlockEditMode : MonoBehaviour
    {
        public Transform Blocks;      // TabletopRenderer's transform: block space, scaled to block_size_m, Z mirrored
        public Transform RightHand, Head;
        public Func<WorldModel> World;
        public ResourcePackAtlas Atlas;
        public Material OpaqueMaterial, TransparentMaterial;
        public Action<Dictionary<string, object>> Send;
        public Func<bool> Online;     // link up (edits are dropped otherwise)
        public Func<bool> Blocked;    // another mode owns the controllers (area setup)
        public NavState Nav;          // visible window inside the region (navigation); null = the whole region is shown
        public Action ModelChanged;   // an optimistic edit or its revert changed the grid (clip caps re-slice)
        public Action<string> Picked; // [menu] B picked this state (the block menu puts it into its hotbar)
        public float MaxRayM = 2f;
        public float RepeatSeconds = 0.25f, FirstRepeatSeconds = 0.35f;
        /// <summary>Length of the faded laser stub shown when the ray hits nothing (metres).</summary>
        public float MissLaserM = 0.12f;
        /// <summary>Opacity of the placement ghost.</summary>
        public float GhostAlpha = 0.4f;
        // [hands] using blocks, items and mobs (interact_v1)
        public Func<InteractWelcome> Interact;            // welcome.interact; null or not Allowed: the trigger only places
        public Action<List<EntityTarget>> CollectTargets;  // mobs and players as drawn (EntityRenderer, PlayerMarkers)
        public TriggerMode Mode = TriggerMode.Use;         // tabletop.json "left_trigger"
        public Func<string> NameTag;                       // the name tag's text (block menu, Use tab)
        public Func<string, string> LabelOf;               // display name of a selection (blocks and items)
        /// <summary>Hooks for the container view and the sign editor of a later version: called with the block's world
        /// position when the left trigger uses a chest, barrel, shulker box... or a sign. Unset: a toast says it's coming.</summary>
        public Action<Vector3Int> OnContainerUse, OnSignUse;
        public BlockIconAtlas Icons;                        // the selected item's icon above the controller
        /// <summary>Seconds the right grip must be held on a mob before it is lifted (a quick both-grips drag never lifts it).</summary>
        public float GrabDelaySeconds = 0.25f;

        public readonly BlockEditor Editor = new BlockEditor();
        public BlockHit Target { get; private set; }
        /// <summary>What the left trigger would place now (cell, oriented state, or why not).</summary>
        public PlacePlan Placement { get; private set; }
        /// <summary>[hands] The mob or player the ray hits first (Hit false: a block or nothing is nearer).</summary>
        public EntityHit Aimed { get; private set; }
        /// <summary>[hands] What the left trigger does now.</summary>
        public LeftAction Action { get; private set; }

        /// <summary>Status line for the HUD text.</summary>
        public string Status =>
            !Editor.ServerAllows ? "EDIT off" + (Editor.Result.Length > 0 ? ": " + Editor.Result : " (server)")
            : $"EDIT {Label(Editor.Selected)}  R-trig break/hit  L-trig {ActionWord(Action)}  R-grip carry  B pick" + (Editor.Result.Length > 0 ? "\n" + Editor.Result : "");

        LineRenderer _outline, _laser, _placeOutline, _entityOutline, _ghostOutline;
        Transform _preview, _ghost, _icon, _carryGhost;
        Mesh _previewMesh, _ghostMesh, _iconMesh;
        Material _ghostMat, _iconMat, _carryMat;
        TextMesh _label, _target;
        string _previewState, _ghostKey, _targetText, _iconState;
        float _breakAt = -1, _placeAt = -1, _hapticOff = -1, _planAt = -1;
        int _seenChanges;
        Vector3 _lo, _hi;   // the box targeting is limited to (region-local blocks)
        readonly Vector3[] _box = new Vector3[16], _placeBox = new Vector3[16], _entityBox = new Vector3[16], _ghostBox = new Vector3[16];
        readonly List<Vector3> _verts = new List<Vector3>();
        readonly List<Color32> _colors = new List<Color32>();
        readonly List<EntityTarget> _targets = new List<EntityTarget>();
        static readonly Color OutlineColor = new Color(0.02f, 0.02f, 0.02f, 0.85f);
        static readonly Color PlaceOkColor = new Color(1f, 1f, 1f, 0.35f), PlaceBadColor = new Color(1f, 0.15f, 0.1f, 0.8f);
        static readonly Color HitLaserStart = new Color(1f, 1f, 1f, 0.15f), HitLaserEnd = new Color(1f, 1f, 1f, 0.7f);
        static readonly Color MissLaserStart = new Color(1f, 1f, 1f, 0.3f), MissLaserEnd = new Color(1f, 1f, 1f, 0f);
        // [hands] what the left trigger does: cyan = use the block, orange = use the item, yellow = a mob
        static readonly Color InteractColor = new Color(0.2f, 0.9f, 1f, 0.95f), ItemColor = new Color(1f, 0.6f, 0.1f, 0.95f),
                              EntityColor = new Color(1f, 0.9f, 0.15f, 0.95f), CarryColor = new Color(1f, 0.9f, 0.15f, 0.28f);

        /// <summary>[hands] A mob or player picked up with the right grip.</summary>
        struct Grab
        {
            public bool Active, Holding;
            public EntityTarget Target;
            public float Since, NextHold;
            public Vector3 LastSent;
        }
        Grab _grab;

        void Start()
        {
            var lineMat = new Material(Shader.Find("Sprites/Default"));
            _outline = NewLine("BlockOutline", lineMat, 16);
            _outline.startColor = _outline.endColor = OutlineColor;
            _placeOutline = NewLine("PlaceOutline", lineMat, 16);
            _laser = NewLine("EditLaser", lineMat, 2);
            _laser.startWidth = 0.0025f; _laser.endWidth = 0.0008f;
            _entityOutline = NewLine("EntityOutline", lineMat, 16);
            _entityOutline.startColor = _entityOutline.endColor = EntityColor;
            _ghostOutline = NewLine("CarryGhostOutline", lineMat, 16);
            _ghostOutline.startColor = _ghostOutline.endColor = EntityColor;

            _preview = new GameObject("SelectedBlock", typeof(MeshFilter), typeof(MeshRenderer)).transform;
            if (RightHand) _preview.SetParent(RightHand, false);
            _preview.localPosition = new Vector3(0f, 0.045f, 0.03f);
            _preview.localScale = Vector3.one * 0.025f;
            _previewMesh = new Mesh();
            _preview.GetComponent<MeshFilter>().sharedMesh = _previewMesh;
            _preview.GetComponent<MeshRenderer>().sharedMaterials = new[] { OpaqueMaterial, TransparentMaterial };
            _label = NewText("SelectedLabel", new Vector3(0f, 0.075f, 0.03f), 0.0022f, TextAnchor.LowerCenter, Color.white);
            _target = NewText("TargetLabel", new Vector3(0f, 0.028f, 0.03f), 0.0016f, TextAnchor.UpperCenter, new Color(1f, 1f, 1f, 0.75f));

            // [hands] the selected item's icon (a flat quad) in the cube's place
            _icon = new GameObject("SelectedItem", typeof(MeshFilter), typeof(MeshRenderer)).transform;
            if (RightHand) _icon.SetParent(RightHand, false);
            _icon.localPosition = new Vector3(0f, 0.047f, 0.03f);
            _icon.localScale = Vector3.one * 0.03f;
            _iconMesh = new Mesh { name = "ItemIcon" };
            _iconMesh.SetVertices(new List<Vector3> { new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(-0.5f, -0.5f, 0) });
            _iconMesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            _iconMesh.SetUVs(0, new List<Vector2> { Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero });
            _icon.GetComponent<MeshFilter>().sharedMesh = _iconMesh;
            _iconMat = new Material(Shader.Find("Sprites/Default")) { renderQueue = 3001 };
            var ir = _icon.GetComponent<MeshRenderer>();
            ir.sharedMaterial = _iconMat; ir.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; ir.receiveShadows = false;
            _icon.gameObject.SetActive(false);

            // The placement ghost: block-space mesh under Blocks (Z mirrored like the terrain), drawn see-through with
            // its own copy of the transparent block material.
            _ghost = new GameObject("PlaceGhost", typeof(MeshFilter), typeof(MeshRenderer)).transform;
            if (Blocks) _ghost.SetParent(Blocks, false);
            _ghostMesh = new Mesh();
            _ghost.GetComponent<MeshFilter>().sharedMesh = _ghostMesh;
            _ghostMat = TransparentMaterial ? new Material(TransparentMaterial) : null;
            var gr = _ghost.GetComponent<MeshRenderer>();
            gr.sharedMaterial = _ghostMat;
            gr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; gr.receiveShadows = false;
            _ghost.gameObject.SetActive(false);

            // [hands] where a carried mob goes down: a translucent box of its size under Blocks
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "CarryGhost";
            Destroy(cube.GetComponent<Collider>());
            _carryGhost = cube.transform;
            if (Blocks) _carryGhost.SetParent(Blocks, false);
            _carryMat = new Material(Shader.Find("Sprites/Default")) { color = CarryColor, renderQueue = 3002 };
            var cr = cube.GetComponent<MeshRenderer>();
            cr.sharedMaterial = _carryMat; cr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; cr.receiveShadows = false;
            cube.SetActive(false);
        }

        LineRenderer NewLine(string name, Material mat, int points)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var l = go.AddComponent<LineRenderer>();
            l.useWorldSpace = true; l.positionCount = points; l.loop = false; l.material = mat;
            l.numCapVertices = 0; l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; l.receiveShadows = false;
            go.SetActive(false);
            return l;
        }

        TextMesh NewText(string name, Vector3 local, float size, TextAnchor anchor, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(RightHand ? RightHand : transform, false);
            go.transform.localPosition = local;
            var t = go.AddComponent<TextMesh>();
            TabletopApp.SetBuiltinFont(t);
            t.characterSize = size; t.fontSize = 48; t.anchor = anchor; t.color = color;
            return t;
        }

        string Label(string sel) => LabelOf != null ? LabelOf(sel) : InteractProtocol.IsItem(sel) ? BlockRegistry.DefaultLabel(BlockRegistry.PathOf(InteractProtocol.ItemId(sel))) : BlockEditRules.Label(sel);

        static string ActionWord(LeftAction a) =>
            a == LeftAction.Entity ? "use on mob" : a == LeftAction.Interact ? "use block" : a == LeftAction.Container ? "open" : a == LeftAction.Sign ? "edit sign"
            : a == LeftAction.Item ? "use item" : "place";

        void Update()
        {
            float now = Time.unscaledTime;
            if (_hapticOff >= 0 && now >= _hapticOff) { _hapticOff = -1; OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.RTouch); OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.LTouch); }
            var w = World?.Invoke();
            Editor.Tick(w, Time.unscaledTimeAsDouble);
            // Nothing to aim with until the server grants edit_v1 (the status line says why), or while area setup runs.
            bool active = Editor.ServerAllows && w != null && w.Loaded && RightHand && Blocks && !(Blocked?.Invoke() ?? false);
            ShowPreview(active);
            if (!active)
            {
                Target = default; Placement = default; Aimed = default; Action = LeftAction.None;
                _outline.gameObject.SetActive(false); _laser.gameObject.SetActive(false); _placeOutline.gameObject.SetActive(false);
                _entityOutline.gameObject.SetActive(false);
                _ghost.gameObject.SetActive(false); SetTargetText("");
                _breakAt = _placeAt = -1;
                if (_grab.Active) LetGo(null);
                return;
            }

            // Only what's on the table can be targeted: the visible window when navigating (the margin is clipped away).
            _lo = Vector3.zero; _hi = w.Dims;
            if (Nav != null && Nav.Loaded && Nav.Origin == w.Origin) { _lo = Nav.WindowMin - (Vector3)w.Origin; _hi = _lo + (Vector3)Nav.Window; }
            // The controller ray in block space (InverseTransformVector includes the block scale: length in blocks).
            Vector3 o = BlockRaycast.FromBlocksLocal(Blocks.InverseTransformPoint(RightHand.position));
            Vector3 d = BlockRaycast.FromBlocksLocal(Blocks.InverseTransformVector(RightHand.forward * MaxRayM));
            var interact = Interact?.Invoke();
            bool uses = interact != null && interact.Allowed, mobs = uses && interact.Entities;
            string sel = Editor.Selected;
            var prev = Target;
            Target = BlockRaycast.Cast(w, o, d, d.magnitude, _lo, _hi, uses && UseRules.FluidRay(sel));   // [hands] the empty bucket stops at sources
            // [hands] mobs and players: the nearer of a mob and a block wins
            _targets.Clear();
            if (mobs) CollectTargets?.Invoke(_targets);
            Aimed = mobs ? EntityRaycast.Cast(_targets, w.Origin, o, d, d.magnitude, _lo, _hi, _grab.Active ? _grab.Target.Uuid : null) : default;
            bool onMob = EntityRaycast.EntityFirst(Aimed, Target);
            bool leftGrip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch);
            string block = Target.Hit ? w.State(w.Get(Target.Block.x, Target.Block.y, Target.Block.z)) : null;
            Action = UseRules.Decide(onMob, block, sel, leftGrip, Mode, interact);
            // What the left trigger would place: re-planned when the aim changes, and a few times a second for the rest
            // (look direction, neighbours, server updates).
            if (!Same(prev, Target) || now >= _planAt) { Placement = Editor.Plan(w, Target, d, Time.unscaledTimeAsDouble); _planAt = now + 0.1f; }
            Draw(Target, onMob ? Aimed : default);
            DrawPlacement(Action == LeftAction.Place ? Placement : default, Target.Hit);
            DrawEntity(onMob && !_grab.Active ? Aimed : default, w.Origin);
            SetTargetText(onMob ? Aimed.Target.Label : BlockEditor.Describe(w, Target));

            bool online = Online?.Invoke() ?? true;
            // Right trigger = break (on a mob: hit it); press acts at once, holding repeats.
            if (Held(OVRInput.Controller.RTouch, ref _breakAt, now, true) && (Target.Hit || onMob))
            {
                if (!online) Editor.Result = "not connected";
                else if (onMob) Hit(Aimed.Target, d, interact);
                else Do(Editor.Break(w, Target, Time.unscaledTimeAsDouble), OVRInput.Controller.RTouch);
                _planAt = -1;
            }
            // Left trigger = the right-click order (UseRules); placing and item uses repeat while held, the rest don't.
            if (Held(OVRInput.Controller.LTouch, ref _placeAt, now, UseRules.Repeats(Action)) && Action != LeftAction.None)
            {
                if (online) UseLeft(w, d, interact, leftGrip);
                else Editor.Result = "not connected";
                _planAt = -1;
            }
            if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch))
            {
                if (onMob) PickEgg(Aimed.Target);
                else if (Editor.Pick(w, Target)) { Haptic(OVRInput.Controller.RTouch, 0.15f); Picked?.Invoke(Editor.Selected); _planAt = -1; }   // [menu] hotbar
            }
            Carry(w, onMob, leftGrip, now, online);
        }

        /// <summary>[hands] The left trigger's action (UseRules.Decide picked it).</summary>
        void UseLeft(WorldModel w, Vector3 d, InteractWelcome interact, bool grip)
        {
            string sel = Editor.Selected;
            switch (Action)
            {
                case LeftAction.Entity:
                {
                    var t = Aimed.Target;
                    string why = UseRules.LocalRefusal(Action, sel, t.Player, interact, NameTag?.Invoke());
                    if (why != null) { Editor.Result = "can't: " + why; return; }
                    string op = UseRules.EntityOp(sel, NameTag?.Invoke(), out string item, out string text);
                    Send?.Invoke(InteractProtocol.EntityOp(t.Uuid, op, item: item, text: text));
                    Editor.Result = op == "info" ? "info on " + t.Label : op == "name" ? "named " + t.Label + " " + text : "used " + Label(sel) + " on " + t.Label;
                    Haptic(OVRInput.Controller.LTouch, 0.3f);
                    return;
                }
                case LeftAction.Interact:
                    Do(Editor.Interact(w, Target, null, grip, d), OVRInput.Controller.LTouch);
                    return;
                case LeftAction.Container:
                case LeftAction.Sign:
                {
                    var pos = w.Origin + Target.Block;
                    var hook = Action == LeftAction.Container ? OnContainerUse : OnSignUse;
                    if (hook != null) { hook(pos); Haptic(OVRInput.Controller.LTouch, 0.2f); }
                    else Editor.Result = Action == LeftAction.Container ? "can't open containers from the headset yet (hold the left grip to place against it)"
                                                                         : "can't edit signs from the headset yet (hold the left grip to place against it)";
                    return;
                }
                case LeftAction.Item:
                    Do(Editor.Interact(w, Target, InteractProtocol.ItemId(sel), grip, d), OVRInput.Controller.LTouch);
                    return;
                case LeftAction.Place:
                    Do(Editor.Place(w, Target, d, Time.unscaledTimeAsDouble), OVRInput.Controller.LTouch);
                    return;
            }
        }

        /// <summary>[hands] The right trigger on a mob: a hit with knockback along the ray (players only when the server allows it).</summary>
        void Hit(EntityTarget t, Vector3 d, InteractWelcome interact)
        {
            if (t.Player && (interact == null || !interact.HarmPlayers)) { Editor.Result = "can't hit players (interact.harm-players is off)"; return; }
            Send?.Invoke(InteractProtocol.EntityOp(t.Uuid, "attack", dir: d));
            Editor.Result = "hit " + t.Label;
            Haptic(OVRInput.Controller.RTouch, 0.45f);
        }

        /// <summary>[hands] B on a mob: its spawn egg goes into the selection (the block menu checks the server offers it).</summary>
        void PickEgg(EntityTarget t)
        {
            if (t.Player) { Editor.Result = "can't pick a player (no spawn egg)"; return; }
            string egg = InteractProtocol.EggOf(t.Type);
            if (egg == null) return;
            Haptic(OVRInput.Controller.RTouch, 0.15f);
            Picked?.Invoke(InteractProtocol.Selection(egg));
        }

        /// <summary>
        /// [hands] Right grip on a mob or player: after GrabDelaySeconds it is picked up (a mob is lifted: entity_op hold,
        /// about 10 a second, a block above the drop point; players stay put), a ghost box shows the drop point, and letting
        /// go drops it there (drop; move for players). The left grip joining lets go where it hangs (the both-grips drag
        /// takes over); before the delay nothing is sent at all.
        /// </summary>
        void Carry(WorldModel w, bool onMob, bool leftGrip, float now, bool online)
        {
            bool rightGrip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
            if (!_grab.Active)
            {
                _ghostOutline.gameObject.SetActive(false); _carryGhost.gameObject.SetActive(false);
                if (online && onMob && !leftGrip && OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch))
                    _grab = new Grab { Active = true, Target = Aimed.Target, Since = now };
                return;
            }
            var drop = EntityRaycast.DropPoint(w, Target);
            if (drop.HasValue && !BlockRaycast.Overlaps(Vector3Int.FloorToInt(drop.Value), _lo, _hi)) drop = null;
            if (leftGrip || !online) { LetGo(null); return; }
            if (!rightGrip) { LetGo(drop.HasValue ? (Vector3?)(drop.Value + (Vector3)w.Origin) : null); return; }
            if (!_grab.Holding && now - _grab.Since >= GrabDelaySeconds) { _grab.Holding = true; _grab.NextHold = 0; Haptic(OVRInput.Controller.RTouch, 0.4f); }
            DrawCarryGhost(_grab.Holding ? drop : null, w.Origin);
            if (_grab.Holding && !_grab.Target.Player && drop.HasValue && now >= _grab.NextHold)
            {
                var to = drop.Value + (Vector3)w.Origin + new Vector3(0, EntityRaycast.HoldLift, 0);
                // ~10 Hz while it moves, every 0.5 s while it doesn't (the server lets go after 3 s of silence)
                if ((to - _grab.LastSent).sqrMagnitude > 0.0025f || now >= _grab.NextHold + 0.4f)
                {
                    Send?.Invoke(InteractProtocol.EntityOp(_grab.Target.Uuid, "hold", to: to));
                    _grab.LastSent = to;
                    _grab.NextHold = now + 0.1f;
                }
            }
        }

        /// <summary>Ends a grab: drops it at <paramref name="to"/> (world), or where it hangs when null.</summary>
        void LetGo(Vector3? to)
        {
            var g = _grab;
            _grab = default;
            _ghostOutline.gameObject.SetActive(false); _carryGhost.gameObject.SetActive(false);
            // Released (or the left grip joined) before the grab lifted it: a quick both-grips drag, nothing happens.
            if (!g.Holding || Send == null || !(Online?.Invoke() ?? true)) return;
            if (g.Target.Player)
            {
                if (to.HasValue) { Send(InteractProtocol.EntityOp(g.Target.Uuid, "move", to: to)); Editor.Result = "moved " + g.Target.Label; Haptic(OVRInput.Controller.RTouch, 0.3f); }
                return;
            }
            Send(InteractProtocol.EntityOp(g.Target.Uuid, "drop", to: to));
            Editor.Result = (to.HasValue ? "put down " : "let go of ") + g.Target.Label;
            Haptic(OVRInput.Controller.RTouch, 0.3f);
        }

        /// <summary>[hands] For the info card: the top of a mob or player as drawn, in Unity world space.</summary>
        public bool TryEntityTop(string uuid, out Vector3 top)
        {
            top = default;
            var w = World?.Invoke();
            if (w == null || !w.Loaded || !Blocks) return false;
            _targets.Clear();
            CollectTargets?.Invoke(_targets);
            foreach (var t in _targets)
                if (t.Uuid == uuid)
                {
                    var p = t.Pos - (Vector3)w.Origin + new Vector3(0, Mathf.Max(EntityRaycast.MinSize, t.Height), 0);
                    top = Blocks.TransformPoint(BlockRaycast.ToBlocksLocal(p));
                    return true;
                }
            return false;
        }

        static bool Same(BlockHit a, BlockHit b) =>
            a.Hit == b.Hit && a.Block == b.Block && a.Face == b.Face &&
            (a.Point.y - a.Block.y > 0.5f) == (b.Point.y - b.Block.y > 0.5f) && (a.Point.x - a.Block.x > 0.5f) == (b.Point.x - b.Block.x > 0.5f) &&
            (a.Point.z - a.Block.z > 0.5f) == (b.Point.z - b.Block.z > 0.5f);

        // After every Update (TabletopApp handles server errors there): grid changes from guesses and reverts.
        void LateUpdate()
        {
            if (Editor.Pending.Changes == _seenChanges) return;
            _seenChanges = Editor.Pending.Changes;
            _planAt = -1;
            ModelChanged?.Invoke();
        }

        /// <summary>True on the trigger press, then (when <paramref name="repeat"/>) every RepeatSeconds while it stays held.</summary>
        bool Held(OVRInput.Controller c, ref float next, float now, bool repeat)
        {
            if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, c)) { next = now + FirstRepeatSeconds; return true; }
            if (next < 0 || !OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, c)) { next = -1; return false; }
            if (now < next || !repeat) return false;
            next = now + RepeatSeconds;
            return true;
        }

        void Do(Dictionary<string, object> msg, OVRInput.Controller c)
        {
            if (msg == null) return;   // refused locally (Editor.Result says why)
            Send?.Invoke(msg);
            Haptic(c, 0.3f);
        }

        void Haptic(OVRInput.Controller c, float amplitude)
        {
            OVRInput.SetControllerVibration(0.6f, amplitude, c);
            _hapticOff = Time.unscaledTime + 0.04f;
        }

        void Draw(BlockHit h, EntityHit e)
        {
            // [hands] the block outline says what the left trigger does to it: dark = place (see the ghost), cyan = use, orange = item
            bool showBlock = h.Hit && !e.Hit;
            _outline.gameObject.SetActive(showBlock);
            var c = Action == LeftAction.Interact || Action == LeftAction.Container || Action == LeftAction.Sign ? InteractColor
                  : Action == LeftAction.Item ? ItemColor : OutlineColor;
            _outline.startColor = _outline.endColor = c;
            _laser.gameObject.SetActive(true);
            _laser.SetPosition(0, RightHand.position);
            if (!h.Hit && !e.Hit)
            {
                // A miss: a short stub that fades out, so the aim is still visible.
                _laser.startColor = MissLaserStart; _laser.endColor = MissLaserEnd;
                _laser.SetPosition(1, RightHand.position + RightHand.forward * MissLaserM);
                return;
            }
            _laser.startColor = HitLaserStart; _laser.endColor = HitLaserEnd;
            _laser.SetPosition(1, Blocks.TransformPoint(BlockRaycast.ToBlocksLocal(e.Hit ? e.Point : h.Point)));
            if (showBlock) BoxLines(_outline, _box, h.Block, h.Block);
        }

        /// <summary>[hands] The yellow box around the aimed mob or player.</summary>
        void DrawEntity(EntityHit e, Vector3Int origin)
        {
            _entityOutline.gameObject.SetActive(e.Hit);
            if (!e.Hit) return;
            EntityRaycast.Box(e.Target, origin, out var min, out var max);
            BoxLinesF(_entityOutline, _entityBox, min, max, 0.02f);
        }

        /// <summary>[hands] The carried mob's drop point: a yellow outline and a translucent box of its size.</summary>
        void DrawCarryGhost(Vector3? drop, Vector3Int origin)
        {
            bool show = drop.HasValue;
            _ghostOutline.gameObject.SetActive(show); _carryGhost.gameObject.SetActive(show);
            if (!show) return;
            var t = _grab.Target;
            t.Pos = drop.Value + (Vector3)origin;
            EntityRaycast.Box(t, origin, out var min, out var max);
            BoxLinesF(_ghostOutline, _ghostBox, min, max, 0.01f);
            var size = max - min;
            _carryGhost.localPosition = BlockRaycast.ToBlocksLocal((min + max) * 0.5f);
            _carryGhost.localRotation = Quaternion.identity;
            _carryGhost.localScale = size;
        }

        /// <summary>
        /// Selection box over cells <paramref name="a"/>..<paramref name="b"/> (inclusive): grown by 1/64 block so it
        /// doesn't z-fight the faces (vanilla grows it by 0.002), cut to the visible window like the terrain.
        /// </summary>
        void BoxLines(LineRenderer line, Vector3[] box, Vector3Int a0, Vector3Int b0)
        {
            var lo = new Vector3Int(Math.Min(a0.x, b0.x), Math.Min(a0.y, b0.y), Math.Min(a0.z, b0.z));
            var hi = new Vector3Int(Math.Max(a0.x, b0.x), Math.Max(a0.y, b0.y), Math.Max(a0.z, b0.z));
            BoxLinesF(line, box, lo, hi + Vector3Int.one, 1f / 64);
        }

        /// <summary>A box [a, b] in block space (grown by <paramref name="e"/>, cut to the visible window) as 16 line points.</summary>
        void BoxLinesF(LineRenderer line, Vector3[] box, Vector3 a0, Vector3 b0, float e)
        {
            Vector3 a = new Vector3(Mathf.Max(a0.x, _lo.x) - e, Mathf.Max(a0.y, _lo.y) - e, Mathf.Max(a0.z, _lo.z) - e);
            Vector3 b = new Vector3(Mathf.Min(b0.x, _hi.x) + e, Mathf.Min(b0.y, _hi.y) + e, Mathf.Min(b0.z, _hi.z) + e);
            int i = 0;
            void P(float x, float y, float z) => box[i++] = Blocks.TransformPoint(BlockRaycast.ToBlocksLocal(new Vector3(x, y, z)));
            P(a.x, a.y, a.z); P(b.x, a.y, a.z); P(b.x, a.y, b.z); P(a.x, a.y, b.z); P(a.x, a.y, a.z);
            P(a.x, b.y, a.z); P(b.x, b.y, a.z); P(b.x, a.y, a.z); P(b.x, b.y, a.z); P(b.x, b.y, b.z); P(b.x, a.y, b.z);
            P(b.x, b.y, b.z); P(a.x, b.y, b.z); P(a.x, a.y, b.z); P(a.x, b.y, b.z); P(a.x, b.y, a.z);
            line.SetPositions(box);
            line.widthMultiplier = Mathf.Clamp(Blocks.lossyScale.x * 0.08f, 0.0006f, 0.003f);
        }

        /// <summary>The cell the left trigger fills: a faint outline and a ghost of the oriented block (red when refused).</summary>
        void DrawPlacement(PlacePlan p, bool hit)
        {
            var w = World?.Invoke();
            bool hasCell = hit && w != null && !p.Waiting && w.InBounds(p.Cell.x, p.Cell.y, p.Cell.z) && BlockRaycast.Overlaps(p.Cell, _lo, _hi) && (p.Ok || p.State != null || p.Why?.Length > 0);
            bool show = hasCell && (p.Ok || p.Why.StartsWith("can't place:"));   // not for "can't place fire" (a selection problem)
            _placeOutline.gameObject.SetActive(show);
            if (!show) { _ghost.gameObject.SetActive(false); return; }
            var color = p.Ok ? PlaceOkColor : PlaceBadColor;
            _placeOutline.startColor = _placeOutline.endColor = color;
            BoxLines(_placeOutline, _placeBox, p.Cell, p.HasPartner ? p.PartnerCell : p.Cell);
            if (!p.Ok || _ghostMat == null) { _ghost.gameObject.SetActive(false); return; }
            string key = p.State + "|" + p.PartnerState + "|" + (p.PartnerCell - p.Cell);
            if (key != _ghostKey) BuildGhost(p);
            _ghost.gameObject.SetActive(true);
        }

        /// <summary>The planned block(s) as a see-through mesh through the real mesher, placed at their cell.</summary>
        void BuildGhost(PlacePlan p)
        {
            _ghostKey = p.State + "|" + p.PartnerState + "|" + (p.PartnerCell - p.Cell);
            var one = BlockEditor.PreviewWorld(p, out var corner);
            _ghostMesh.Clear();
            if (one == null || Atlas == null) return;
            foreach (var s in one.Palette) for (int dir = 0; dir < 6; dir++) Atlas.Face(s, dir);   // main thread: new states join the atlas
            Atlas.Commit();
            if (Atlas.Array != null) { _ghostMat.SetTexture("_Blocks", Atlas.Array); _ghostMat.SetFloat("_HasTex", 1); }
            var data = new MeshData();
            IFaceSource faces = Atlas;
            GreedyMesher.Build(one, Vector3Int.zero, faces, data);
            GreedyMesher.BuildSpecials(one, Vector3Int.zero, faces, data);
            // [blocks] Chests, banners, heads... are drawn by BlockEntityRenderer, not the terrain mesher: ghost their box.
            if (Atlas.HasPack && BlockEntityPreview.AddBoxes(one, data, Atlas))
            {
                Atlas.Commit();
                if (Atlas.Array != null) foreach (var m in new[] { _ghostMat, OpaqueMaterial, TransparentMaterial }) if (m) m.SetTexture("_Blocks", Atlas.Array);   // a new layer rebuilds the array
            }
            // Block space -> Blocks-local (mirror Z, flip winding); every face see-through at GhostAlpha.
            _verts.Clear(); _colors.Clear();
            foreach (var v in data.Verts) _verts.Add(new Vector3(v.x, v.y, -v.z));
            byte alpha = (byte)Mathf.Clamp(GhostAlpha * 255f, 0f, 255f);
            foreach (var c in data.Colors) _colors.Add(new Color32(c.r, c.g, c.b, (byte)(c.a * alpha / 255)));
            var tris = new List<int>(data.Opaque.Count + data.Transparent.Count);
            tris.AddRange(Flip(data.Opaque));
            tris.AddRange(Flip(data.Transparent));
            _ghostMesh.SetVertices(_verts);
            _ghostMesh.SetUVs(0, data.Uvs);
            _ghostMesh.SetColors(_colors);
            _ghostMesh.SetTriangles(tris, 0, false);
            _ghostMesh.RecalculateNormals();
            _ghostMesh.RecalculateBounds();
            _ghost.localPosition = BlockRaycast.ToBlocksLocal(corner);
            _ghost.localRotation = Quaternion.identity; _ghost.localScale = Vector3.one;
        }

        void SetTargetText(string s)
        {
            if (s == _targetText) return;
            _targetText = s;
            _target.text = s;
            _target.gameObject.SetActive(s.Length > 0);
        }

        void ShowPreview(bool active)
        {
            string sel = Editor.Selected;
            bool item = InteractProtocol.IsItem(sel);   // [hands] an item shows its icon instead of a block
            _preview.gameObject.SetActive(active && !item);
            _label.gameObject.SetActive(active);
            if (!active) { _icon.gameObject.SetActive(false); return; }
            if (item) ShowIcon(sel);
            else
            {
                _icon.gameObject.SetActive(false);
                if (_previewState != sel) BuildPreview(sel);
            }
            if (_label.text != Label(sel)) _label.text = Label(sel);
            _preview.localRotation = Quaternion.Euler(20f, Time.unscaledTime * 30f % 360f, 0f);
            if (Head)
            {
                _label.transform.rotation = Quaternion.LookRotation(_label.transform.position - Head.position, Vector3.up);
                _target.transform.rotation = Quaternion.LookRotation(_target.transform.position - Head.position, Vector3.up);
                _icon.rotation = Quaternion.LookRotation(_icon.position - Head.position, Vector3.up);
            }
        }

        /// <summary>[hands] The selected item's menu icon (from the block menu's icon atlas) above the controller.</summary>
        void ShowIcon(string sel)
        {
            if (Icons == null || !Icons.Get(sel, InteractProtocol.IconItem(InteractProtocol.ItemId(sel)), 0, out var uv)) { _icon.gameObject.SetActive(false); return; }
            if (_iconMat.mainTexture != Icons.Texture) _iconMat.mainTexture = Icons.Texture;
            if (_iconState != sel + uv)
            {
                _iconState = sel + uv;
                _iconMesh.SetUVs(0, new List<Vector2> { new Vector2(uv.xMin, uv.yMax), new Vector2(uv.xMax, uv.yMax), new Vector2(uv.xMax, uv.yMin), new Vector2(uv.xMin, uv.yMin) });
            }
            _icon.gameObject.SetActive(true);
        }

        /// <summary>The selected block as a one-block world through the real mesher: textures and models like the miniature.</summary>
        void BuildPreview(string state)
        {
            _previewState = state;
            if (Atlas != null)
            {
                // Main thread: resolve the state's textures/model (it may be new to the atlas), as TabletopRenderer.Prewarm does.
                for (int dir = 0; dir < 6; dir++) Atlas.Face(state, dir);
                Atlas.Commit();
                if (Atlas.Array != null)
                    foreach (var m in new[] { OpaqueMaterial, TransparentMaterial }) { m.SetTexture("_Blocks", Atlas.Array); m.SetFloat("_HasTex", 1); }
            }
            var one = new WorldModel { SnapshotId = 0, World = "preview", Dims = new Vector3Int(1, 1, 1) };
            one.Palette.Add(BlockEditRules.Air); one.Palette.Add(state);
            one.Grid = new ushort[] { 1 };
            IFaceSource faces = Atlas;
            var data = new MeshData();
            if (faces != null) { GreedyMesher.Build(one, Vector3Int.zero, faces, data); GreedyMesher.BuildSpecials(one, Vector3Int.zero, faces, data); }
            // [blocks] Chests, banners, heads... are drawn by BlockEntityRenderer, not the terrain mesher: preview their box.
            if (Atlas != null && Atlas.HasPack && BlockEntityPreview.AddBoxes(one, data, Atlas))
            {
                Atlas.Commit();
                if (Atlas.Array != null) foreach (var m in new[] { OpaqueMaterial, TransparentMaterial }) m.SetTexture("_Blocks", Atlas.Array);
            }
            // Block space -> Unity (mirror Z, flip winding), centred on the cell.
            _verts.Clear();
            foreach (var v in data.Verts) _verts.Add(new Vector3(v.x - 0.5f, v.y - 0.5f, 0.5f - v.z));
            _previewMesh.Clear();
            _previewMesh.SetVertices(_verts);
            _previewMesh.SetUVs(0, data.Uvs);
            _previewMesh.SetColors(data.Colors);
            _previewMesh.subMeshCount = 2;
            _previewMesh.SetTriangles(Flip(data.Opaque), 0, false);
            _previewMesh.SetTriangles(Flip(data.Transparent), 1, false);
            _previewMesh.RecalculateNormals();
            _previewMesh.RecalculateBounds();
        }

        static List<int> Flip(List<int> t)
        {
            var r = new List<int>(t.Count);
            for (int i = 0; i + 2 < t.Count; i += 3) { r.Add(t[i]); r.Add(t[i + 2]); r.Add(t[i + 1]); }
            return r;
        }

        void OnDestroy()
        {
            if (_grab.Active) LetGo(null);
            if (_previewMesh) Destroy(_previewMesh);
            if (_ghostMesh) Destroy(_ghostMesh);
            if (_ghostMat) Destroy(_ghostMat);
            if (_iconMesh) Destroy(_iconMesh);
            if (_iconMat) Destroy(_iconMat);
            if (_carryMat) Destroy(_carryMat);
        }
    }
}
