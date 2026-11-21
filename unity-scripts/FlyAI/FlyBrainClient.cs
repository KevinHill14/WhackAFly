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
        public string[] senses; // From status. Brains with an ears pathway list "sound".
    }

    public struct BrainEscape
    {
        public float strength;
        public string side; // The direction the fly jumps, in the fly's own frame.
        public int[] spikes;
    }

    // Talks to brain/server.py over a WebSocket: sends looming (and sound, if the brain has ears) at 20 Hz,
    // receives escape decisions. Reconnects by itself. Network work stays off the main thread; results come
    // out through TryGetEscape.
    public class FlyBrainClient : IDisposable
    {
        public const string DefaultUrl = "ws://127.0.0.1:8765";
        const int SendIntervalMs = 50;

        public readonly string url;
        public volatile bool Connected;
        public volatile string BrainInfo = ""; // e.g. "live, 1602 neurons", from the server's status reply.
        // True once the connected brain's status says it hears ("sound" in senses). Older brains only have
        // eyes; then FlyAgent keeps turning sounds into looming pulses.
        public volatile bool SupportsSound;

        readonly ConcurrentQueue<BrainEscape> escapes = new ConcurrentQueue<BrainEscape>();
        readonly object gate = new object();
        readonly CancellationTokenSource cancel = new CancellationTokenSource();
        float looming;
        string eye = "center";
        float sound;
        string ear = "center";
        long soundUntilMs;
        bool resetPending;
        readonly System.Diagnostics.Stopwatch since = System.Diagnostics.Stopwatch.StartNew();

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

        // A sound reaching the fly's ears for the next `seconds`. A louder sound replaces a quieter one that is
        // still playing. It is sent in the same 50 ms frame as the looming, so the brain combines both.
        public void SetSound(float value, string side, float seconds)
        {
            lock (gate)
            {
                var now = since.ElapsedMilliseconds;
                value = Mathf.Clamp01(value);
                if (now < soundUntilMs && value <= sound) return;
                sound = value;
                ear = side;
                soundUntilMs = now + (long)(seconds * 1000f);
            }
        }

        // A new round: the brain forgets everything, including what it had got used to (habituation).
        public void RequestReset()
        {
            lock (gate) resetPending = true;
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
                        SupportsSound = false; // Until this brain's status says otherwise.
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
                float value, heard;
                string side, heardSide;
                bool reset;
                lock (gate)
                {
                    value = looming;
                    side = eye;
                    heard = since.ElapsedMilliseconds < soundUntilMs ? sound : 0f;
                    heardSide = ear;
                    reset = resetPending;
                    resetPending = false;
                }
                if (reset)
                {
                    await SendText(socket, "{\"type\":\"reset\"}", token);
                    lastSent = 0f;
                }
                // Keep sending while the swatter is a threat, plus one more message to let the brain settle at zero.
                if (value > 0.001f || lastSent > 0.001f)
                {
                    await SendText(socket, string.Format(CultureInfo.InvariantCulture, "{{\"type\":\"looming\",\"value\":{0:0.###},\"side\":\"{1}\"}}", value, side), token);
                    lastSent = value;
                }
                // Right after the looming, in the same frame: the brain merges the two into one decision and one reply.
                if (heard > 0.001f && SupportsSound)
                    await SendText(socket, string.Format(CultureInfo.InvariantCulture, "{{\"type\":\"sound\",\"value\":{0:0.###},\"side\":\"{1}\"}}", heard, heardSide), token);
                if (clock.Elapsed.TotalSeconds >= nextStatus)
                {
                    nextStatus = (float)clock.Elapsed.TotalSeconds + 2f;
                    await SendText(socket, "{\"type\":\"status\"}", token);
                }
                await Task.Delay(SendIntervalMs, token);
            }
        }

        static Task SendText(ClientWebSocket socket, string json, CancellationToken token) =>
            socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, token);

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
                    {
                        BrainInfo = $"{message.source}, {message.neurons} neurons";
                        SupportsSound = message.senses != null && Array.IndexOf(message.senses, "sound") >= 0;
                    }
                    else if (message != null && message.type == "escape")
                        escapes.Enqueue(new BrainEscape { strength = message.strength, side = message.side, spikes = message.spikes });
                }
                catch (Exception) { /* Ignore a malformed message. */ }
            }
        }
    }
}
