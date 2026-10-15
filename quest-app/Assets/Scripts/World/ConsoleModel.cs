// [talk] The operator panel's Console tab (docs/PROTOCOL.md 4.27, docs/QUEST_SETUP.md Part L), engine-free (Tests~):
// the input line, command history (last 100, up/down, saved as JSON in the app's files folder), suggestion chips from
// command_suggestions (asked 150 ms after the last edit; a reply for an older line is dropped; a chip replaces the
// line from the server's start), and the output grouped per command (pending, ok, failed or refused), newest last.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;

namespace TabletopCraft.World
{
    public enum ConsoleState { Pending, Ok, Failed, Refused }

    public sealed class ConsoleEntry
    {
        public long Seq;
        public string Line, At;   // At: "@ x y z" when it ran at the pointer
        public ConsoleState State;
        public bool AsChat;
        public double Ms;
        public readonly List<string> Lines = new List<string>();
    }

    public enum ConsoleLineKind { Command, Output, Error, Pending, Note }

    public struct ConsoleLine { public string Text; public ConsoleLineKind Kind; }

    public sealed class ConsoleModel
    {
        public const int HistoryMax = 100, EntriesMax = 40, ChipCount = 6;
        public const double Debounce = 0.15;
        public const string HistoryFile = "history.json";

        public string Input { get; private set; } = "";
        public int MaxLen = ControlProtocol.MaxLen;
        public readonly List<string> History = new List<string>();
        public readonly List<ConsoleEntry> Entries = new List<ConsoleEntry>();
        /// <summary>Quick buttons: welcome.commands.quick, then tabletop.json quick_commands (no duplicates).</summary>
        public readonly List<string> Quick = new List<string>();
        /// <summary>"@ pointer": commands run at the block the right controller last aimed at.</summary>
        public bool AtPointer;
        public int Version { get; private set; }

        int _historyAt = -1;          // index into History while browsing with up/down, -1 = editing a new line
        string _draft = "";           // the line being typed before browsing started
        double _editedAt = double.NegativeInfinity;
        bool _wantCompletion;
        long _askedSeq = -1;
        string _askedLine;
        CommandSuggestions _suggestions;

        /// <summary>Text from the keyboard (or a chip, history, a quick button). Starts a new completion round.</summary>
        public void SetInput(string text, double now)
        {
            text = text ?? "";
            if (text.Length > MaxLen) text = text.Substring(0, MaxLen);
            if (text == Input) return;
            Input = text;
            _editedAt = now;
            _wantCompletion = true;
            _historyAt = -1;
            Version++;
        }

        /// <summary>
        /// Enter: the line to send (without a leading '/'), or null when it's empty. It goes into the history (an
        /// immediate repeat isn't added twice) and the input clears. The caller sends it and calls Sent.
        /// </summary>
        public string Submit(double now)
        {
            string line = Input.Trim();
            while (line.StartsWith("/")) line = line.Substring(1).TrimStart();
            if (line.Length == 0) return null;
            Remember(line);
            Input = ""; _historyAt = -1; _wantCompletion = false; _suggestions = null; _askedLine = null; _editedAt = now;
            Version++;
            return line;
        }

        void Remember(string line)
        {
            if (History.Count == 0 || History[History.Count - 1] != line) History.Add(line);
            if (History.Count > HistoryMax) History.RemoveRange(0, History.Count - HistoryMax);
        }

        /// <summary>A command went out with this envelope seq (its result comes back with ref_seq = seq).</summary>
        public ConsoleEntry Sent(long seq, string line, string at = null)
        {
            var e = new ConsoleEntry { Seq = seq, Line = line, At = at, State = ConsoleState.Pending };
            Entries.Add(e);
            if (Entries.Count > EntriesMax) Entries.RemoveRange(0, Entries.Count - EntriesMax);
            Version++;
            return e;
        }

        /// <summary>A line that couldn't be sent (offline, no command_v1): shown as refused at once.</summary>
        public void Refused(string line, string why)
        {
            var e = Sent(-1, line);
            e.State = ConsoleState.Refused;
            e.Lines.Add(why);
        }

        ConsoleEntry Find(long seq)
        {
            if (seq < 0) return null;
            for (int i = Entries.Count - 1; i >= 0; i--) if (Entries[i].Seq == seq) return Entries[i];
            return null;
        }

        public bool OnResult(CommandResult r)
        {
            var e = Find(r.RefSeq);
            if (e == null) return false;
            e.State = r.Ok ? ConsoleState.Ok : ConsoleState.Failed;
            e.AsChat = r.AsChat; e.Ms = r.Ms;
            e.Lines.AddRange(r.Lines);
            Version++;
            return true;
        }

        public bool OnOutput(CommandOutput o)
        {
            var e = Find(o.RefSeq);
            if (e == null) return false;
            e.Lines.AddRange(o.Lines);
            Version++;
            return true;
        }

        /// <summary>An error reply (command_denied, rate_limited, bad_message, world_not_found) for one of our commands.</summary>
        public bool OnError(long refSeq, string code, string message)
        {
            var e = Find(refSeq);
            if (e == null || e.State != ConsoleState.Pending) return false;
            e.State = ConsoleState.Refused;
            e.Lines.Add(code == "rate_limited" ? "too many commands, slow down" : code == "command_denied" ? "refused: " + message : code + ": " + message);
            Version++;
            return true;
        }

        // ---------- history ----------
        public bool HistoryUp()
        {
            if (History.Count == 0) return false;
            if (_historyAt < 0) { _draft = Input; _historyAt = History.Count - 1; }
            else if (_historyAt > 0) _historyAt--;
            else return false;
            Input = History[_historyAt]; _wantCompletion = false; _suggestions = null;
            Version++;
            return true;
        }

        public bool HistoryDown()
        {
            if (_historyAt < 0) return false;
            if (_historyAt < History.Count - 1) { _historyAt++; Input = History[_historyAt]; }
            else { _historyAt = -1; Input = _draft; }
            _wantCompletion = false; _suggestions = null;
            Version++;
            return true;
        }

        public string SaveHistory()
        {
            var l = new List<object>();
            foreach (var h in History) l.Add(h);
            return Json.Write(l);
        }

        /// <summary>Restores the history from SaveHistory's JSON; anything malformed is ignored.</summary>
        public void LoadHistory(string json)
        {
            try
            {
                if (!(Json.Parse(json) is List<object> l)) return;
                History.Clear();
                foreach (var x in l) if (x is string s && s.Trim().Length > 0 && s.Length <= MaxLen) History.Add(s);
                if (History.Count > HistoryMax) History.RemoveRange(0, History.Count - HistoryMax);
                Version++;
            }
            catch (FormatException) { }
        }

        // ---------- completion ----------
        /// <summary>The line to ask completions for, once the input has rested 150 ms (null if nothing is due).</summary>
        public string CompletionDue(double now)
        {
            if (!_wantCompletion || now - _editedAt < Debounce) return null;
            _wantCompletion = false;
            if (Input.Trim().Length == 0) { _suggestions = null; Version++; return null; }
            return Input;
        }

        public void CompletionSent(long seq, string line) { _askedSeq = seq; _askedLine = line; }

        /// <summary>Takes suggestions only for the last line asked about, while the input still is that line.</summary>
        public bool OnSuggestions(CommandSuggestions s)
        {
            if (s.RefSeq != _askedSeq || _askedLine != Input) return false;
            _suggestions = s;
            Version++;
            return true;
        }

        /// <summary>Up to 6 chips for the current input.</summary>
        public List<string> Chips()
        {
            var l = new List<string>();
            if (_suggestions == null || _askedLine != Input) return l;
            foreach (var it in _suggestions.Items) { if (l.Count == ChipCount) break; l.Add(it); }
            return l;
        }

        /// <summary>A chip replaces the line from the suggestion's start (Tab). Asks for the next completions at once.</summary>
        public bool AcceptChip(int i, double now)
        {
            var chips = Chips();
            if (i < 0 || i >= chips.Count) return false;
            int start = Math.Max(0, Math.Min(Input.Length, _suggestions.Start));
            SetInput(Input.Substring(0, start) + chips[i], now);
            _editedAt = now - Debounce;   // no wait: the next argument's suggestions come straight away
            return true;
        }

        // ---------- quick buttons ----------
        public void SetQuick(IEnumerable<string> server, IEnumerable<string> local)
        {
            Quick.Clear();
            foreach (var src in new[] { server, local })
                if (src != null)
                    foreach (var q in src)
                    {
                        string t = (q ?? "").Trim().TrimStart('/');
                        if (t.Length > 0 && t.Length <= MaxLen && !Quick.Contains(t)) Quick.Add(t);
                    }
            Version++;
        }

        // ---------- output ----------
        /// <summary>The output as lines, oldest first: each command ("&gt; /line", with "@ x y z"), then its feedback.</summary>
        public List<ConsoleLine> Output()
        {
            var l = new List<ConsoleLine>();
            foreach (var e in Entries)
            {
                l.Add(new ConsoleLine { Text = "> /" + e.Line + (e.At != null ? "  " + e.At : ""), Kind = ConsoleLineKind.Command });
                if (e.State == ConsoleState.Pending) { l.Add(new ConsoleLine { Text = "  ...", Kind = ConsoleLineKind.Pending }); continue; }
                var kind = e.State == ConsoleState.Ok ? ConsoleLineKind.Output : ConsoleLineKind.Error;
                if (e.Lines.Count == 0) l.Add(new ConsoleLine { Text = e.State == ConsoleState.Ok ? "  (done)" : "  (failed)", Kind = kind });
                foreach (var x in e.Lines) l.Add(new ConsoleLine { Text = "  " + x, Kind = e.AsChat ? ConsoleLineKind.Note : kind });
            }
            return l;
        }

        /// <summary>Wrapped output lines, rows high, scrolled `scroll` lines up from the newest; returns the total.</summary>
        public int Page(int rows, int width, int scroll, List<ConsoleLine> into)
        {
            into.Clear();
            var all = new List<ConsoleLine>();
            foreach (var x in Output()) foreach (var w in ChatLog.Wrap(x.Text, width)) all.Add(new ConsoleLine { Text = w, Kind = x.Kind });
            int end = Math.Max(0, all.Count - Math.Max(0, scroll)), start = Math.Max(0, end - rows);
            for (int i = start; i < end; i++) into.Add(all[i]);
            return all.Count;
        }
    }
}
