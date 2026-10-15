// [operator] The Marks tab's data, engine-free (Tests~): the worlds the server has (welcome.worlds) with their spawn and
// build height, the shared bookmarks (marks_v1), where the view is now, and the field the keyboard types into (typed
// coordinates, or the name of a new bookmark). The tab's layout and clicks are in OperatorPanelModel; the jumps
// themselves are ordinary region_requests (World/FollowState.cs: Jumps, JumpFocus) sent by the app.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class MarksModel
    {
        public enum FieldMode { Coords, Name }

        public OperatorWelcome Welcome = new OperatorWelcome();
        public readonly List<WorldEntry> Worlds = new List<WorldEntry>();
        public BookmarkList Bookmarks = new BookmarkList();
        /// <summary>Where the view is (NavState: the region's world, the window centre), set by the app every frame.</summary>
        public string World = "";
        public Vector3 View;
        public bool Loaded;
        /// <summary>"following Alex" / "stopped following Alex: ..." (FollowState.Describe), set by the app.</summary>
        public string FollowLine = "";
        /// <summary>The player being followed, or null.</summary>
        public string FollowName;
        public bool Following => FollowName != null;

        public FieldMode Mode { get; private set; } = FieldMode.Coords;
        /// <summary>The keyboard shows (the field was clicked, or Add mark asked for a name).</summary>
        public bool Typing { get; private set; }
        public readonly KeyboardModel CoordKeys = new KeyboardModel { MaxLength = 40, EnterLabel = "go" };
        public readonly KeyboardModel NameKeys = new KeyboardModel { MaxLength = 32, CollapseSpaces = true, EnterLabel = "save" };
        public KeyboardModel ActiveKeys => Typing ? (Mode == FieldMode.Name ? NameKeys : CoordKeys) : null;
        public string FieldText => Mode == FieldMode.Name ? NameKeys.Text : CoordKeys.Text;
        /// <summary>Selected bookmark (name), or null.</summary>
        public string Selected;
        public int Version { get; private set; }

        public void OnWelcome(OperatorWelcome w)
        {
            Welcome = w ?? new OperatorWelcome();
            Worlds.Clear(); Worlds.AddRange(Welcome.Worlds);
            Bookmarks = new BookmarkList { Max = Welcome.MarksMax };
            Selected = null;
            Version++;
        }

        public void OnBookmarks(BookmarkList b)
        {
            Bookmarks = b ?? new BookmarkList();
            if (Selected != null && Find(Selected) == null) Selected = null;
            Version++;
        }

        /// <summary>region_info: a fresher world list and the region world's spawn.</summary>
        public void OnRegionInfo(List<WorldEntry> worlds, string world, Vector3Int? spawn)
        {
            if (worlds != null && worlds.Count > 0) { Worlds.Clear(); Worlds.AddRange(worlds); }
            if (spawn.HasValue)
                for (int i = 0; i < Worlds.Count; i++)
                    if (Worlds[i].Name == world) { var w = Worlds[i]; w.Spawn = spawn.Value; Worlds[i] = w; }
            Version++;
        }

        public WorldEntry? WorldOf(string name)
        {
            foreach (var w in Worlds) if (w.Name == name) return w;
            return null;
        }

        public Bookmark? Find(string name)
        {
            foreach (var b in Bookmarks.Marks) if (string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)) return b;
            return null;
        }

        public void SetTyping(bool on, FieldMode mode)
        {
            if (on == Typing && mode == Mode) return;
            Typing = on; Mode = mode;
            ActiveKeys?.ResetMode();
            Version++;
        }

        /// <summary>A new bookmark's default name: "Mark 3", the first number not taken.</summary>
        public string NextName()
        {
            for (int i = Bookmarks.Marks.Count + 1; ; i++) if (Find("Mark " + i) == null) return "Mark " + i;
        }

        /// <summary>The block under the view centre (bookmarks store it; a jump there centres the window on it again).</summary>
        public Vector3Int Here => new Vector3Int((int)Math.Floor(View.x), (int)Math.Floor(View.y), (int)Math.Floor(View.z));

        /// <summary>The header line: where the view is and who is followed.</summary>
        public string Header()
        {
            if (!Loaded) return "no area yet";
            var w = WorldOf(World);
            string name = w.HasValue ? w.Value.Label : World;
            var h = Here;
            return $"{name}  {h.x} {h.y} {h.z}" + (FollowLine.Length > 0 ? "   " + FollowLine : "");
        }

        /// <summary>A bookmark row: name, world (when it's another one) and position.</summary>
        public string Row(Bookmark b)
        {
            var w = WorldOf(b.World);
            string world = b.World == World ? "" : "  [" + (w.HasValue ? w.Value.Label : b.World) + "]";
            return $"{b.Name}{world}  {b.Pos.x} {b.Pos.y} {b.Pos.z}";
        }
    }
}
