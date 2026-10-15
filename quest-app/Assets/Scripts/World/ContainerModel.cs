// [operator] The container view, engine-free (Tests~): the left trigger on a chest, barrel, shulker box, hopper,
// dispenser, dropper, furnace, smoker, blast furnace, brewing stand or crafter opens it in the operator panel
// (OperatorPanelModel's container mode) with container_query; the server answers and re-sends `container` whenever the
// contents change while it is open (PROTOCOL.md "Containers"). A double chest is one view of 54 slots.
//
// Slots are laid out as the game does (rows of 9; 3x3 for dispensers, droppers and crafters; 5 in a row for hoppers;
// input, fuel and output for furnaces; three bottles, the ingredient and the fuel for brewing stands). The right trigger
// selects a slot; the buttons act on it: Take (the count picker's 1, 16 or 64; the items go nowhere, as a creative
// player's would), Put (the selected hotbar block or item, the picker's count, onto the same item or in place of another),
// Clear, and Sort (storage only). Each edit carries what the slot held ("was"), so the server refuses it if a Java player
// changed the slot meanwhile; nothing is guessed: the next `container` shows the result.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class ContainerModel
    {
        public static readonly int[] Counts = { 1, 16, 64 };

        public Vector3Int Pos { get; private set; }
        public bool Open { get; private set; }
        /// <summary>container_query went out, no answer yet.</summary>
        public bool Waiting { get; private set; }
        public ContainerView View { get; private set; }
        public int Selected = -1;
        public int Count = 16;
        public string Note = "";
        public int Version { get; private set; }

        public string Title => View != null ? View.Title : Waiting ? "opening..." : "";
        public bool CanEdit => View != null && View.Edit;

        /// <summary>The left trigger on a container: the view opens and asks for its contents.</summary>
        public Dictionary<string, object> Begin(Vector3Int pos)
        {
            Pos = pos; Open = true; Waiting = true; View = null; Selected = -1; Note = ""; Version++;
            return OperatorProtocol.ContainerQuery(pos);
        }

        /// <summary>Closing (the panel's Close, another tab, the panel closing): the message that ends the view, or null.</summary>
        public Dictionary<string, object> Close()
        {
            if (!Open) return null;
            Open = false; Waiting = false; View = null; Selected = -1; Version++;
            return OperatorProtocol.ContainerClose(Pos);
        }

        /// <summary>A container message: true when it is this view's (the clicked block or the other half of a double chest).</summary>
        public bool OnView(ContainerView v)
        {
            if (!Open || v == null || !(v.Pos == Pos || (v.Other.HasValue && v.Other.Value == Pos) || (View != null && View.Other.HasValue && View.Other.Value == v.Pos))) return false;
            Waiting = false;
            if (v.Closed) { Open = false; View = null; Selected = -1; Note = "closed: " + (v.Reason ?? "the container is gone"); Version++; return true; }
            View = v;
            if (Selected >= v.Size) Selected = -1;
            Version++;
            return true;
        }

        /// <summary>A refusal about this view (error with op container_query / container_edit and its pos).</summary>
        public bool OnError(string op, Vector3Int? pos, string message)
        {
            if (!Open || (op != "container_query" && op != "container_edit") || (pos.HasValue && pos.Value != Pos && !(View?.Other == pos))) return false;
            Note = message ?? "refused";
            if (op == "container_query") { Open = false; View = null; Waiting = false; }
            Version++;
            return true;
        }

        // ---------- layout ----------

        /// <summary>Columns and rows of the slot grid.</summary>
        public static (int Cols, int Rows) Grid(string block, int size)
        {
            string p = BlockRegistry.PathOf(block ?? "");
            if (p == "dispenser" || p == "dropper" || p == "crafter") return (3, 3);
            if (size <= 0) return (9, 3);
            if (size <= 5) return (size, 1);
            return (9, (size + 8) / 9);
        }

        /// <summary>What a slot is for, where the game shows it (furnaces, brewing stands), else null.</summary>
        public static string SlotLabel(string block, int slot)
        {
            string p = BlockRegistry.PathOf(block ?? "");
            if (p == "furnace" || p == "smoker" || p == "blast_furnace") return slot == 0 ? "input" : slot == 1 ? "fuel" : slot == 2 ? "output" : null;
            if (p == "brewing_stand") return slot <= 2 ? "bottle" : slot == 3 ? "ingredient" : slot == 4 ? "fuel" : null;
            return null;
        }

        public static bool Sortable(string block)
        {
            string p = BlockRegistry.PathOf(block ?? "");
            return p == "chest" || p == "trapped_chest" || p == "barrel" || p.EndsWith("shulker_box") || p.EndsWith("copper_chest") || p == "hopper" || p == "dispenser" || p == "dropper";
        }

        // ---------- actions ----------

        public void Select(int slot)
        {
            if (View == null || slot < 0 || slot >= View.Size) return;
            Selected = Selected == slot ? -1 : slot; Note = ""; Version++;
        }

        public void PickCount(int n) { if (Count != n) { Count = n; Version++; } }

        string Gate()
        {
            if (View == null) return Waiting ? "waiting for the server..." : "nothing open";
            if (!View.Edit) return "containers are view-only on this server (interact.containers)";
            return null;
        }

        /// <summary>Take the picker's count (or all there is) out of the selected slot: they go nowhere.</summary>
        public Dictionary<string, object> Take(out string why)
        {
            why = Gate() ?? (Selected < 0 ? "select a slot first" : null);
            if (why != null) return null;
            var (item, n) = View.Slot(Selected);
            if (item == null) { why = "that slot is empty"; return null; }
            int left = Math.Max(0, n - Count);
            Note = $"take {Math.Min(n, Count)} {Label(item)}";
            return OperatorProtocol.ContainerSet(Pos, Selected, left > 0 ? item : null, left, item, n);
        }

        /// <summary>Put the picker's count of an item into the selected slot (added to the same item, else in its place).</summary>
        public Dictionary<string, object> Put(string itemId, out string why)
        {
            why = Gate() ?? (Selected < 0 ? "select a slot first" : itemId == null ? "select a block or item in the hotbar first" : null);
            if (why != null) return null;
            if (itemId == InteractProtocol.Lightning) { why = "the lightning wand isn't an item"; return null; }
            var (item, n) = View.Slot(Selected);
            int count = item == itemId ? n + Count : Count;
            Note = item == null || item == itemId ? $"put {Count} {Label(itemId)}" : $"put {Count} {Label(itemId)} in place of {n} {Label(item)}";
            return OperatorProtocol.ContainerSet(Pos, Selected, itemId, Math.Min(127, count), item, n);
        }

        public Dictionary<string, object> Clear(out string why)
        {
            why = Gate() ?? (Selected < 0 ? "select a slot first" : null);
            if (why != null) return null;
            var (item, n) = View.Slot(Selected);
            if (item == null) { why = "that slot is empty"; return null; }
            Note = $"clear {n} {Label(item)}";
            return OperatorProtocol.ContainerSet(Pos, Selected, null, 0, item, n);
        }

        public Dictionary<string, object> Sort(out string why)
        {
            why = Gate() ?? (!Sortable(View.Block) ? "only chests, barrels, shulker boxes, hoppers, dispensers and droppers sort" : null);
            if (why != null) return null;
            Note = "sort";
            return OperatorProtocol.ContainerSort(Pos);
        }

        /// <summary>The item a hotbar selection puts into a container: an item ("item:minecraft:bone_meal"), or a block's item
        /// (the registry's item for it: redstone wire -> redstone, a wall torch -> torch).</summary>
        public static string ItemOf(string selection, BlockRegistry registry)
        {
            if (string.IsNullOrEmpty(selection)) return null;
            if (InteractProtocol.IsItem(selection)) return InteractProtocol.ItemId(selection);
            var e = registry?.Find(selection);
            return e != null ? e.Item : BlockRegistry.IdOf(selection);
        }

        /// <summary>The icon key and model item for an item id: a registry block's default state, else "item:&lt;id&gt;".</summary>
        public static (string State, string Item) IconOf(string itemId, BlockRegistry registry)
        {
            if (registry != null)
            {
                var e = registry.Find(itemId);
                if (e != null && e.Item == itemId) return (e.State, e.Item);
            }
            return (InteractProtocol.Selection(itemId), itemId);
        }

        public static string Label(string itemId) => itemId == null ? "nothing" : BlockRegistry.DefaultLabel(BlockRegistry.PathOf(itemId)).ToLowerInvariant();

        /// <summary>The selected slot's line: what it holds.</summary>
        public string SlotLine()
        {
            if (View == null) return Waiting ? "opening..." : "";
            if (Selected < 0) return $"{View.Items.Count} of {View.Size} slots used";
            var (item, n) = View.Slot(Selected);
            string what = SlotLabel(View.Block, Selected);
            return $"slot {Selected}{(what != null ? " (" + what + ")" : "")}: " + (item == null ? "empty" : $"{n} {Label(item)}");
        }
    }
}
