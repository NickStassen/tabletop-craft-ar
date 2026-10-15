// [hands] What the left trigger does, engine-free (Tests~). Vanilla's right-click order: (1) the ray is on a mob or
// player: act on it (use the held item on it, or ask for its info card); (2) the block is one a hand can use (a door,
// lever, button, note block, cake...): use it; (3) an item is selected: use it on the clicked face; (4) otherwise place
// the selected block. Holding the left grip ("sneak") skips (1) and (2), so you can place a block against a door or
// a chest, as shift-right-click does. tabletop.json "left_trigger": "place" swaps the two: the trigger always places
// (or uses an item) and the grip chord does the right-click order.
//
// The block families mirror the server's InteractRules.kind (server-plugin, unit-tested there); the server stays
// authoritative and refuses with interact_denied when it disagrees. The outline shows the answer: white = place,
// cyan = use the block, orange = use the item, yellow = a mob.
using TabletopCraft.Net;

namespace TabletopCraft.World
{
    public enum UseKind { None, Door, Trapdoor, Gate, Lever, Button, NoteBlock, Repeater, Comparator, Daylight, Bell, Cake, CandleCake, Composter,
                          Jukebox, Candle, Campfire, Container, Sign }

    public enum LeftAction { None, Entity, Interact, Container, Sign, Item, Place }

    public enum OutlineKind { Place, Interact, Item, Entity }

    public enum TriggerMode { Use, Place }

    public static class UseRules
    {
        static string Path(string state) => BlockRules.Path(state ?? "minecraft:air");

        static bool Container(string p) => p == "chest" || p == "trapped_chest" || p == "barrel" || p == "hopper" || p == "dispenser" || p == "dropper" ||
                                           p == "furnace" || p == "blast_furnace" || p == "smoker" || p == "brewing_stand" || p == "crafter" ||
                                           p.EndsWith("shulker_box") || p.EndsWith("copper_chest");

        /// <summary>What an empty hand does to a block (None: the trigger places there instead).</summary>
        public static UseKind Kind(string state, bool ironDoors = true)
        {
            string p = Path(state);
            if (p.EndsWith("_door")) return p == "iron_door" && !ironDoors ? UseKind.None : UseKind.Door;
            if (p.EndsWith("_trapdoor")) return p == "iron_trapdoor" && !ironDoors ? UseKind.None : UseKind.Trapdoor;
            if (p.EndsWith("_fence_gate")) return UseKind.Gate;
            if (p == "lever") return UseKind.Lever;
            if (p.EndsWith("_button")) return UseKind.Button;
            if (Container(p)) return UseKind.Container;
            if (p.EndsWith("_sign")) return UseKind.Sign;
            switch (p)
            {
                case "note_block": return UseKind.NoteBlock;
                case "repeater": return UseKind.Repeater;
                case "comparator": return UseKind.Comparator;
                case "daylight_detector": return UseKind.Daylight;
                case "bell": return UseKind.Bell;
                case "cake": return UseKind.Cake;
                case "composter": return BlockEditRules.Prop(state, "level") == "8" ? UseKind.Composter : UseKind.None;
                case "jukebox": return BlockEditRules.Prop(state, "has_record") == "true" ? UseKind.Jukebox : UseKind.None;
                case "campfire": case "soul_campfire": return BlockEditRules.Prop(state, "lit") == "true" ? UseKind.Campfire : UseKind.None;
            }
            if (p.EndsWith("candle_cake")) return UseKind.CandleCake;
            if (p.EndsWith("candle")) return BlockEditRules.Prop(state, "lit") == "true" ? UseKind.Candle : UseKind.None;
            return UseKind.None;
        }

        /// <summary>
        /// The left trigger's action. <paramref name="entity"/>: the ray's nearest target is a mob or player;
        /// <paramref name="block"/>: the state of the targeted block (null on a miss); <paramref name="grip"/>: the left grip
        /// is held. Without interact_v1 the trigger only places (and an item does nothing).
        /// </summary>
        public static LeftAction Decide(bool entity, string block, string selected, bool grip, TriggerMode mode, InteractWelcome w)
        {
            bool item = InteractProtocol.IsItem(selected);
            if (w == null || !w.Allowed) return item || block == null ? LeftAction.None : LeftAction.Place;
            bool rightClick = mode == TriggerMode.Use ? !grip : grip;
            if (rightClick && entity) return w.Entities ? LeftAction.Entity : LeftAction.None;
            if (block == null) return LeftAction.None;
            if (rightClick)
            {
                var k = Kind(block, w.IronDoors);
                if (k == UseKind.Container) return LeftAction.Container;
                if (k == UseKind.Sign) return LeftAction.Sign;
                if (k != UseKind.None) return LeftAction.Interact;
            }
            return item ? LeftAction.Item : LeftAction.Place;
        }

        public static OutlineKind Outline(LeftAction a) =>
            a == LeftAction.Entity ? OutlineKind.Entity : a == LeftAction.Interact || a == LeftAction.Container || a == LeftAction.Sign ? OutlineKind.Interact
            : a == LeftAction.Item ? OutlineKind.Item : OutlineKind.Place;

        /// <summary>Holding the trigger repeats placing and item uses (bone meal, buckets), like vanilla; using a block or a
        /// mob happens once per press (a held trigger would flap a door open and shut).</summary>
        public static bool Repeats(LeftAction a) => a == LeftAction.Item || a == LeftAction.Place;

        /// <summary>The empty bucket aims at fluid sources (vanilla's SOURCE_ONLY clip): the ray stops at still water or lava.</summary>
        public static bool FluidRay(string selected) => InteractProtocol.ItemId(selected) == InteractProtocol.Bucket;

        /// <summary>The mob action of the left trigger: the selected item used on it, the name tag's text, or the info card.</summary>
        public static string EntityOp(string selected, string nameTag, out string item, out string text)
        {
            item = InteractProtocol.ItemId(selected); text = null;
            if (item == null) return "info";
            if (item == InteractProtocol.NameTag) { item = null; text = nameTag ?? ""; return "name"; }
            return "use";
        }

        /// <summary>A refusal the headset can tell at once, before sending (null when the server should decide).</summary>
        public static string LocalRefusal(LeftAction a, string selected, bool player, InteractWelcome w, string nameTag)
        {
            if (a != LeftAction.Entity) return null;
            if (InteractProtocol.ItemId(selected) == InteractProtocol.NameTag && string.IsNullOrWhiteSpace(nameTag))
                return "type the name tag's text first (block menu > Use)";
            if (player && InteractProtocol.ItemId(selected) == InteractProtocol.NameTag) return "players keep their own names";
            return null;
        }
    }
}
