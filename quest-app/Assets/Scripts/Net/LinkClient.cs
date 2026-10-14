// WebSocket link to the server plugin. Network I/O runs on background tasks; inbound messages are queued (InboundQueue:
// bounded, superseded snapshots dropped) and drained on the Unity main thread by TabletopApp. Engine-free (Tests~).
//
//   - Reconnects with backoff (0.5 s doubling to 8 s); connecting gives up after ConnectTimeoutSeconds.
//   - Pings every PingSeconds and measures the round trip itself (RttMs; -1 while the link is down).
//   - DeadSeconds without any inbound bytes (Wi-Fi gone without a FIN, a frozen server) aborts and reconnects.
//   - Close 4001 (version mismatch) or 4003 (wrong token) stops retrying: FatalCode / FatalReason say why.
//   - Shutdown sends the given farewell messages and a bye, waits briefly for the server's close, then stops.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Debug = UnityEngine.Debug;

namespace TabletopCraft.Net
{
    /// <summary>
    /// The inbound message queue. A full snapshot (a chunk_snapshot without base, part 0) makes every queued
    /// chunk_snapshot and block_update obsolete, so they are dropped. When it holds Max messages the receive loop stops
    /// reading until the main thread catches up (TCP slows the server down); if that lasts DeadSeconds the link resets.
    /// </summary>
    public sealed class InboundQueue
    {
        public int Max = 4096;
        readonly LinkedList<Dictionary<string, object>> _q = new LinkedList<Dictionary<string, object>>();
        public int Dropped { get; private set; }
        public int Count { get { lock (_q) return _q.Count; } }
        public bool Full { get { lock (_q) return _q.Count >= Max; } }

        public static bool IsFullSnapshotStart(Dictionary<string, object> m) =>
            m.S("type") == "chunk_snapshot" && m.O("base") == null && m.I("part") == 0;

        public void Enqueue(Dictionary<string, object> m)
        {
            lock (_q)
            {
                if (IsFullSnapshotStart(m))
                    for (var n = _q.First; n != null;)
                    {
                        var next = n.Next;
                        string t = n.Value.S("type");
                        if (t == "chunk_snapshot" || t == "block_update") { _q.Remove(n); Dropped++; }
                        n = next;
                    }
                _q.AddLast(m);
            }
        }

        public bool TryDequeue(out Dictionary<string, object> m)
        {
            lock (_q)
            {
                if (_q.Count == 0) { m = null; return false; }
                m = _q.First.Value;
                _q.RemoveFirst();
                return true;
            }
        }

        public void Clear() { lock (_q) _q.Clear(); }
    }

    public sealed class LinkClient : IDisposable
    {
        public readonly InboundQueue Inbound = new InboundQueue();
        public volatile string Status = "idle";
        public bool Connected => _ws != null && _ws.State == WebSocketState.Open && _welcomed;
        public string Url { get; }
        /// <summary>Round trip of the last pong in ms; -1 while unknown or the link is down.</summary>
        public long RttMs => Interlocked.Read(ref _rtt);
        /// <summary>Non-zero when the client stopped retrying: 4001 (version), 4003 (token) or 1 (not a ws:// URL).</summary>
        public int FatalCode => _fatalCode;
        public string FatalReason => _fatalReason;
        volatile int _fatalCode;
        volatile string _fatalReason = "";
        /// <summary>Sessions that got a welcome, and links aborted for silence.</summary>
        public int Welcomes => _welcomes;
        public int DeadAborts => _deadAborts;
        public float PingSeconds = 2f, DeadSeconds = 6f, ConnectTimeoutSeconds = 5f;

        readonly Uri _uri;
        readonly string _token;
        readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        CancellationTokenSource _cts;
        Task _loop;
        volatile ClientWebSocket _ws;
        uint _seq;
        readonly object _seqLock = new object();   // [talk] SendTracked reserves seqs on the caller's thread
        const string ReservedSeq = "\u0001seq";      // [talk] a seq SendTracked took; becomes "seq" when the message goes out
        volatile bool _welcomed, _stopping;
        long _rtt = -1, _lastInbound;
        int _welcomes, _deadAborts, _pingId;

        public LinkClient(string url, string token)
        {
            Url = url ?? "";
            _token = token;
            if (!Uri.TryCreate(Url, UriKind.Absolute, out _uri) || (_uri.Scheme != "ws" && _uri.Scheme != "wss"))
            {
                _uri = null;
                _fatalReason = "not a ws:// address: " + Url; _fatalCode = 1;
                Status = _fatalReason;
            }
        }

        public void Start()
        {
            if (_uri == null || _loop != null) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _loop = Task.Run(() => RunLoop(ct));
        }

        static double Now => Clock.Elapsed.TotalSeconds;
        void Touch() => Interlocked.Exchange(ref _lastInbound, Clock.ElapsedTicks);
        double SinceInbound => (Clock.ElapsedTicks - Interlocked.Read(ref _lastInbound)) / (double)Stopwatch.Frequency;

        async Task RunLoop(CancellationToken ct)
        {
            int backoffMs = 500;
            while (!ct.IsCancellationRequested && !_stopping && FatalCode == 0)
            {
                var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
                _ws = ws;
                bool dead = false;
                try
                {
                    Status = "connecting " + _uri;
                    using (var cto = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        cto.CancelAfter(TimeSpan.FromSeconds(ConnectTimeoutSeconds));
                        try { await ws.ConnectAsync(_uri, cto.Token); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"no answer within {ConnectTimeoutSeconds:F0} s"); }
                        catch (WebSocketException) when (cto.IsCancellationRequested && !ct.IsCancellationRequested) { throw new TimeoutException($"no answer within {ConnectTimeoutSeconds:F0} s"); }
                    }
                    lock (_seqLock) _seq = 0;
                    _welcomed = false; Touch();
                    await SendAsync(ws, Protocol.Hello(_token), force: true);
                    Status = "connected";
                    backoffMs = 500;
                    using var life = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var keep = KeepAlive(ws, life.Token, () => dead = true);
                    try { await ReceiveLoop(ws, ct); }
                    finally { life.Cancel(); try { await keep; } catch (OperationCanceledException) { } }
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    Status = dead ? $"no answer for {DeadSeconds:F0} s: reconnecting" : "error: " + e.Message;
                    Debug.LogWarning("[Tabletop] link: " + Status);
                }
                _welcomed = false;
                Interlocked.Exchange(ref _rtt, -1);
                try { ws.Dispose(); } catch { }
                if (ct.IsCancellationRequested || _stopping || FatalCode != 0) break;
                try { await Task.Delay(backoffMs, ct); } catch (OperationCanceledException) { break; }
                backoffMs = Math.Min(backoffMs * 2, 8000);
            }
        }

        /// <summary>Pings while welcomed; aborts the socket after DeadSeconds without inbound bytes.</summary>
        async Task KeepAlive(ClientWebSocket ws, CancellationToken ct, Action markDead)
        {
            double nextPing = Now + PingSeconds;
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await Task.Delay(100, ct);
                if (SinceInbound > DeadSeconds)
                {
                    markDead();
                    Interlocked.Increment(ref _deadAborts);
                    try { ws.Abort(); } catch { }
                    return;
                }
                if (_welcomed && Now >= nextPing)
                {
                    nextPing = Now + PingSeconds;
                    _ = SendAsync(ws, Protocol.Ping(_pingId++), false);
                }
            }
        }

        async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
        {
            var buf = new byte[64 * 1024];
            var sb = new StringBuilder();
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                // Backpressure: the main thread hasn't drained the queue (if this lasts DeadSeconds, KeepAlive resets the link).
                while (Inbound.Full && ws.State == WebSocketState.Open && !ct.IsCancellationRequested) await Task.Delay(20, ct);
                var seg = new ArraySegment<byte>(buf);
                WebSocketReceiveResult r = await ws.ReceiveAsync(seg, ct);
                Touch();
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    int code = (int?)r.CloseStatus ?? 0;
                    Status = $"closed {code} {r.CloseStatusDescription}";
                    if (code == 4001) { _fatalReason = "the server speaks another protocol version: update the app or the plugin"; _fatalCode = code; }
                    else if (code == 4003) { _fatalReason = "wrong token: tabletop.json \"token\" must match the plugin's auth.token"; _fatalCode = code; }
                    return;
                }
                if (r.MessageType == WebSocketMessageType.Binary) continue; // v1: text only
                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                if (!r.EndOfMessage) continue;
                string text = sb.ToString(); sb.Clear();
                Dictionary<string, object> msg;
                try { msg = Json.Parse(text) as Dictionary<string, object>; }
                catch (Exception e) { Debug.LogWarning("[Tabletop] bad JSON: " + e.Message); continue; }
                if (msg == null) continue;
                switch (msg.S("type"))
                {
                    case "pong":
                        long rtt = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)msg.D("echo_t"));
                        Interlocked.Exchange(ref _rtt, rtt);
                        if (msg.I("id") % 15 == 0) Debug.Log($"[Tabletop] rtt {rtt} ms");
                        continue;
                    case "ping":
                        _ = SendAsync(ws, new Dictionary<string, object> { ["type"] = "pong", ["id"] = msg.I("id"), ["echo_t"] = (long)msg.D("t") }, false);
                        continue;
                    case "welcome":
                        _welcomed = true;
                        Interlocked.Increment(ref _welcomes);
                        break;
                }
                Inbound.Enqueue(msg); // JSON parse happens here, off the main thread
            }
        }

        /// <summary>Fire-and-forget send; adds the envelope (v, seq, t). Dropped while not connected.</summary>
        public void Send(Dictionary<string, object> body) { var ws = _ws; if (Connected && ws != null) _ = SendAsync(ws, body, false); }

        /// <summary>[talk] Like Send, but takes the envelope seq now and returns it (-1 when not connected), so a reply's
        /// ref_seq can be matched (commands, completions, queries). Under contention the wire order may differ from the
        /// seq order; the server doesn't depend on it.</summary>
        public long SendTracked(Dictionary<string, object> body)
        {
            var ws = _ws;
            if (!Connected || ws == null) return -1;
            uint s;
            lock (_seqLock) s = _seq++;
            body[ReservedSeq] = s;
            _ = SendAsync(ws, body, false);
            return s;
        }

        async Task SendAsync(ClientWebSocket ws, Dictionary<string, object> body, bool force)
        {
            if (ws == null || ws.State != WebSocketState.Open) return;
            if (!force && !_welcomed) return;
            await _sendLock.WaitAsync();
            try
            {
                body["v"] = Protocol.Version;
                if (body.TryGetValue(ReservedSeq, out var reserved)) { body.Remove(ReservedSeq); body["seq"] = reserved; }   // [talk] SendTracked
                else lock (_seqLock) body["seq"] = _seq++;
                body["t"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var bytes = Encoding.UTF8.GetBytes(Json.Write(body));
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception e) { Debug.LogWarning("[Tabletop] send failed: " + e.Message); }
            finally { _sendLock.Release(); }
        }

        /// <summary>
        /// Stops for good: sends the farewell messages (e.g. avatar_config enabled:false) and a bye while connected, waits
        /// up to waitMs for the server to close, then aborts. Blocks the caller for at most about waitMs.
        /// </summary>
        public void Shutdown(string reason, int waitMs = 400, params Dictionary<string, object>[] farewell)
        {
            _stopping = true;
            var ws = _ws;
            var sw = Stopwatch.StartNew();
            if (Connected && ws != null)
            {
                try
                {
                    Task.Run(async () =>
                    {
                        foreach (var m in farewell) if (m != null) await SendAsync(ws, m, false);
                        await SendAsync(ws, Protocol.Bye(reason), true);
                    }).Wait(waitMs);
                    // The server answers bye with a close; leaving before it would reset the socket and could lose the bye.
                    _loop?.Wait(Math.Max(0, waitMs - (int)sw.ElapsedMilliseconds));
                }
                catch (Exception) { }
            }
            _cts?.Cancel();
            try { ws?.Abort(); } catch { }
            _welcomed = false;
            Interlocked.Exchange(ref _rtt, -1);
            Status = "stopped (" + reason + ")";
        }

        public void Dispose() => Shutdown("app closed");
    }
}
