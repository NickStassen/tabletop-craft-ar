// WebSocket link to the server plugin. Network I/O runs on background tasks; inbound messages are queued and
// drained on the Unity main thread by TabletopApp. Reconnects with backoff.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace TabletopCraft.Net
{
    public sealed class LinkClient : IDisposable
    {
        public readonly ConcurrentQueue<Dictionary<string, object>> Inbound = new ConcurrentQueue<Dictionary<string, object>>();
        public volatile string Status = "idle";
        public bool Connected => _ws != null && _ws.State == WebSocketState.Open && _welcomed;

        readonly Uri _uri;
        readonly string _token;
        readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        CancellationTokenSource _cts;
        ClientWebSocket _ws;
        uint _seq;
        volatile bool _welcomed;

        public LinkClient(string url, string token) { _uri = new Uri(url); _token = token; }

        public void Start() { _cts = new CancellationTokenSource(); _ = Task.Run(() => RunLoop(_cts.Token)); }

        async Task RunLoop(CancellationToken ct)
        {
            int backoffMs = 500;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _ws = new ClientWebSocket();
                    _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
                    Status = "connecting " + _uri;
                    await _ws.ConnectAsync(_uri, ct);
                    _seq = 0; _welcomed = false;
                    await SendAsync(Protocol.Hello(_token), force: true);
                    Status = "connected";
                    backoffMs = 500;
                    await ReceiveLoop(ct);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    Status = "error: " + e.Message;
                    Debug.LogWarning("[Tabletop] link: " + e.Message);
                }
                _welcomed = false;
                try { _ws?.Dispose(); } catch { }
                if (ct.IsCancellationRequested) break;
                await Task.Delay(backoffMs, ct).ContinueWith(_ => { });
                backoffMs = Math.Min(backoffMs * 2, 8000);
            }
        }

        async Task ReceiveLoop(CancellationToken ct)
        {
            var buf = new byte[64 * 1024];
            var sb = new StringBuilder();
            while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var seg = new ArraySegment<byte>(buf);
                WebSocketReceiveResult r = await _ws.ReceiveAsync(seg, ct);
                if (r.MessageType == WebSocketMessageType.Close) { Status = $"closed {(int?)r.CloseStatus} {r.CloseStatusDescription}"; return; }
                if (r.MessageType == WebSocketMessageType.Binary) continue; // v1: text only
                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                if (!r.EndOfMessage) continue;
                string text = sb.ToString(); sb.Clear();
                Dictionary<string, object> msg;
                try { msg = Json.Parse(text) as Dictionary<string, object>; }
                catch (Exception e) { Debug.LogWarning("[Tabletop] bad JSON: " + e.Message); continue; }
                if (msg == null) continue;
                if (msg.S("type") == "welcome") _welcomed = true;
                Inbound.Enqueue(msg); // JSON parse happens here, off the main thread
            }
        }

        /// <summary>Fire-and-forget send; adds the envelope (v, seq, t). Dropped while not connected.</summary>
        public void Send(Dictionary<string, object> body) { if (Connected) _ = SendAsync(body, false); }

        async Task SendAsync(Dictionary<string, object> body, bool force)
        {
            var ws = _ws;
            if (ws == null || ws.State != WebSocketState.Open) return;
            if (!force && !_welcomed) return;
            await _sendLock.WaitAsync();
            try
            {
                body["v"] = Protocol.Version;
                body["seq"] = _seq++;
                body["t"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var bytes = Encoding.UTF8.GetBytes(Json.Write(body));
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception e) { Debug.LogWarning("[Tabletop] send failed: " + e.Message); }
            finally { _sendLock.Release(); }
        }

        public void Dispose()
        {
            try { if (Connected) SendAsync(Protocol.Bye("app closed"), true).Wait(200); } catch { }
            _cts?.Cancel();
            try { _ws?.Abort(); } catch { }
        }
    }
}
