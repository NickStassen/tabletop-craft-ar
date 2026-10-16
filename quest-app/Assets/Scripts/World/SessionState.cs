// The session around the link, engine-free (Tests~): when to show the first-run setup card and the offline badge,
// when to hide the stale players and entities, the "Loading area" progress, and the A-button hold that re-places the
// table. TabletopApp, XR/SetupCard.cs, XR/TableMarkers.cs and XR/TablePlacement.cs draw and wire them.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;

namespace TabletopCraft.World
{
    public sealed class LinkHealth
    {
        /// <summary>Seconds without a first connection before the setup card shows (a fatal close shows it at once).</summary>
        public double CardAfter = 5;
        /// <summary>Seconds offline before players and entities are hidden (their positions are stale).</summary>
        public double HideAfter = 1.5;

        double _start, _downSince = -1;
        bool _up, _everUp;

        public bool EverConnected => _everUp;
        public bool Up => _up;

        /// <summary>A new link to a different server: the setup card may show again if it can't be reached.</summary>
        public void NewServer(double now) { _start = now; _everUp = false; _up = false; _downSince = now; }

        public void Update(bool connected, double now)
        {
            if (connected == _up) return;
            _up = connected;
            if (connected) { _everUp = true; _downSince = -1; }
            else _downSince = now;
        }

        /// <summary>The head-locked setup card: the link has never come up within CardAfter, or the server refused us for good.</summary>
        public bool ShowCard(bool fatal, double now) => fatal || (!_everUp && !_up && now - _start >= CardAfter);
        /// <summary>The "offline, reconnecting" badge at the table edge: the link was up and is down now.</summary>
        public bool Offline => _everUp && !_up;
        /// <summary>Players and entities are hidden after HideAfter seconds offline.</summary>
        public bool HideFigures(double now) => !_up && _downSince >= 0 && now - _downSince >= HideAfter;
    }

    public static class SetupCard
    {
        public const string Title = "Can't reach the Minecraft server";
        /// <summary>[setup] Minecraft's usage guidelines: shown prominently (first-run card, Info tab, store listing).</summary>
        public const string Disclaimer = "NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.";

        /// <summary>The card's text: what was tried, what happened, where the settings file is and what to do.</summary>
        public static string Text(string url, string status, int fatalCode, string fatalReason, string configPath, bool paused = false)
        {
            string title = fatalCode == 4003 ? "The server refused this headset"
                         : fatalCode == 4001 ? "App and server don't match"
                         : fatalCode == 1 ? "The server address isn't valid"
                         : Title;
            string why = fatalCode != 0 ? fatalReason : status;
            return $"{title}\n\n" +
                   $"Server: {Short(url, 56)}\n" +
                   $"Status: {Short(why, 64)}\n\n" +
                   $"Settings: {Short(configPath, 64)}\n\n" +
                   "Fix it here: menu button (left) > Info > Server\n" +
                   "  (type the PC's address or pick a found server)\n" +
                   "or edit tabletop.json: see docs/QUEST_SETUP.md Part D.\n" +
                   (fatalCode != 0 ? "Not retrying until the address or token changes." : "Retrying...");
        }

        static string Short(string s, int n) => string.IsNullOrEmpty(s) ? "-" : s.Length <= n ? s : s.Substring(0, n - 1) + "…";

        /// <summary>[setup] The pairing card: the code in large type and where to type it (QUEST_SETUP.md "Store install").</summary>
        public static string PairText(PairCode code, double now, string host)
        {
            int s = code.SecondsLeft(now);
            return "Pair this headset with your server\n\n" +
                   $"<size=110>{code.Spaced}</size>\n\n" +
                   "In the server window on your PC, type:\n" +
                   $"   tabletop pair {code.Code}\n" +
                   "(in game, as an operator: /tabletop pair " + code.Code + ")\n\n" +
                   $"Server: {Short(host, 48)}   code valid {s / 60}:{s % 60:00}\n" +
                   (code.Required ? "The world loads once the server knows this headset."
                                  : "The world works meanwhile; pairing adds the game's textures and sounds.");
        }

        /// <summary>[setup] First run (no server chosen yet): looking for servers on the LAN, or how to start one.</summary>
        public static string FirstRunText(FirstRun run)
        {
            string found = run.Searches == 0 ? "Looking for your Minecraft server on this Wi-Fi..."
                         : run.Candidates == 0 ? $"No server found on this Wi-Fi yet (searched {run.Searches}x, again every {run.Interval:F0} s)."
                         : $"Found {run.Candidates} servers: pick yours in the menu (left menu button) > Info > Server.";
            return "Welcome to Tabletop Craft AR\n\n" + found + "\n\n" +
                   "1. On your PC, start Minecraft: Java Edition once (same version as the server).\n" +
                   "2. Run the server starter there (start.bat / start.sh, see the setup guide).\n" +
                   "3. This headset finds it on the same Wi-Fi and asks to pair.\n\n" +
                   "Or type its address: menu button (left) > Info > Server.\n\n" + Disclaimer;
        }
    }

    /// <summary>
    /// [setup] The first start without a server (no tabletop.json "server"): search the LAN every Interval seconds and
    /// connect by itself when exactly one plugin answers; with several, the user picks one on the Info tab.
    /// </summary>
    public sealed class FirstRun
    {
        public bool Active;
        public double Interval = 8;
        public int Searches, Candidates;
        double _next;

        public bool ShouldSearch(double now, bool searching) => Active && !searching && now >= _next;

        /// <summary>A search finished: the URL to connect to by itself (exactly one plugin answered), else null.</summary>
        public string Searched(List<FoundServer> found, double now)
        {
            Searches++;
            _next = now + Interval;
            var real = found == null ? new List<FoundServer>() : found.FindAll(f => !f.PortOnly);
            Candidates = real.Count;
            return Active && real.Count == 1 ? real[0].Url : null;
        }

        public void Done() => Active = false;
    }

    /// <summary>"Loading area 40 %" while a big area change is meshed (a resize, a jump elsewhere or a full snapshot).</summary>
    public sealed class LoadProgress
    {
        /// <summary>Smaller loads (a glide's slice of sections) show nothing.</summary>
        public int MinSections = 24;
        int _total;
        public bool Active => _total > 0;

        public void Begin(int pending) => _total = pending >= MinSections ? pending : 0;

        /// <summary>The toast text for the sections still pending, "Area loaded" when done (then inactive), or null.</summary>
        public string Text(int pending)
        {
            if (_total <= 0) return null;
            if (pending <= 0) { _total = 0; return "Area loaded"; }
            if (pending > _total) _total = pending;
            int pct = (int)(100L * (_total - pending) / _total);
            return $"Loading area {pct} %";
        }

        public void Cancel() => _total = 0;
    }

    /// <summary>
    /// A button that acts only after being held (A re-places the table after 0.5 s). A press that starts or passes
    /// through a blocked moment (the menu owns the right hand) never fires until it is released.
    /// </summary>
    public sealed class HoldButton
    {
        public enum Event { None, Pressed, Fired, Released }
        public float HoldSeconds = 0.5f;
        float _downAt = -1;
        bool _fired, _ignore;

        public float Progress(float now) => _downAt < 0 || _ignore ? 0 : Math.Min(1f, (now - _downAt) / HoldSeconds);

        public Event Update(bool down, float now, bool blocked)
        {
            if (!down)
            {
                bool was = _downAt >= 0 && !_ignore;
                _downAt = -1; _fired = false; _ignore = false;
                return was ? Event.Released : Event.None;
            }
            if (blocked) _ignore = true;
            if (_ignore) return Event.None;
            if (_downAt < 0) { _downAt = now; return Event.Pressed; }
            if (!_fired && now - _downAt >= HoldSeconds) { _fired = true; return Event.Fired; }
            return Event.None;
        }
    }
}
