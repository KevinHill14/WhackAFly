using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace FlyAI
{
    [Serializable]
    public class BrainMessage
    {
        public string type;
        public float strength;
        public string side;
        public int[] spikes;
        public string mode;
        public string source;
        public int neurons;
    }

    public struct BrainEscape
    {
        public float strength;
        public string side; // The direction the fly jumps, in the fly's own frame.
        public int[] spikes;
    }

    // Talks to brain/server.py over a WebSocket: sends looming at 20 Hz, receives escape decisions.
    // Reconnects by itself. Network work stays off the main thread; results come out through TryGetEscape.
    public class FlyBrainClient : IDisposable
    {
        public const string DefaultUrl = "ws://127.0.0.1:8765";
        const int SendIntervalMs = 50;

        public readonly string url;
        public volatile bool Connected;
        public volatile string BrainInfo = ""; // e.g. "live, 1602 neurons", from the server's status reply.

        readonly ConcurrentQueue<BrainEscape> escapes = new ConcurrentQueue<BrainEscape>();
        readonly object gate = new object();
        readonly CancellationTokenSource cancel = new CancellationTokenSource();
        float looming;
        string eye = "center";

        public FlyBrainClient(string url)
        {
            this.url = url;
            Task.Run(() => Run(cancel.Token));
        }

        // Which URL to use: FLYAI_BRAIN overrides it; "off" (or empty) means no server, so the fly uses its stand-in.
        public static string ResolveUrl()
        {
            var value = Environment.GetEnvironmentVariable("FLYAI_BRAIN");
            if (value == null) return DefaultUrl;
            value = value.Trim();
            return value.Length == 0 || value.Equals("off", StringComparison.OrdinalIgnoreCase) ? null : value;
        }

        public void SetLooming(float value, string side)
        {
            lock (gate)
            {
                looming = Mathf.Clamp01(value);
                eye = side;
            }
        }

        public bool TryGetEscape(out BrainEscape escape)
        {
            var found = false;
            escape = default;
            // Only the newest decision matters.
            while (escapes.TryDequeue(out var next))
            {
                escape = next;
                found = true;
            }
            return found;
        }

        public void Dispose() => cancel.Cancel();

        async Task Run(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using (var socket = new ClientWebSocket())
                    {
                        await socket.ConnectAsync(new Uri(url), token);
                        Connected = true;
                        var receiving = Receive(socket, token);
                        await Send(socket, receiving, token);
                        if (socket.State == WebSocketState.Open)
                            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    }
                }
                catch (Exception) { /* Server not there yet or connection dropped: retry below. */ }
                Connected = false;
                try { await Task.Delay(2000, token); } catch (OperationCanceledException) { }
            }
        }

        async Task Send(ClientWebSocket socket, Task receiving, CancellationToken token)
        {
            var lastSent = 0f;
            var nextStatus = 0f;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (socket.State == WebSocketState.Open && !receiving.IsCompleted && !token.IsCancellationRequested)
            {
                float value;
                string side;
                lock (gate)
                {
                    value = looming;
                    side = eye;
                }
                // Keep sending while the swatter is a threat, plus one more message to let the brain settle at zero.
                if (value > 0.001f || lastSent > 0.001f)
                {
                    var json = string.Format(CultureInfo.InvariantCulture, "{{\"type\":\"looming\",\"value\":{0:0.###},\"side\":\"{1}\"}}", value, side);
                    await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, token);
                    lastSent = value;
                }
                if (clock.Elapsed.TotalSeconds >= nextStatus)
                {
                    nextStatus = (float)clock.Elapsed.TotalSeconds + 2f;
                    await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("{\"type\":\"status\"}")), WebSocketMessageType.Text, true, token);
                }
                await Task.Delay(SendIntervalMs, token);
            }
        }

        async Task Receive(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[4096];
            var text = new StringBuilder();
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close) return;
                text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;
                var json = text.ToString();
                text.Clear();
                try
                {
                    var message = JsonUtility.FromJson<BrainMessage>(json);
                    if (message != null && message.type == "status")
                        BrainInfo = $"{message.source}, {message.neurons} neurons";
                    else if (message != null && message.type == "escape")
                        escapes.Enqueue(new BrainEscape { strength = message.strength, side = message.side, spikes = message.spikes });
                }
                catch (Exception) { /* Ignore a malformed message. */ }
            }
        }
    }
}
