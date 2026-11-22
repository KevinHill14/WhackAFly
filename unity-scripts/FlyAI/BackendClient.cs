using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace FlyAI
{
    // One message from the backend. JsonUtility fills what is present; the rest stays default.
    [Serializable]
    public class BackendMessage
    {
        public string type;
        public string state;
        public float[] pos;
        public float[] heading;
        public float[] up;
        public float[] vel;
        public bool grooming;
        public bool hurrying;
        public string personality;
        public float looming;
        public string eye;
        public string source;
        public string action;
        public float distance;
        public int score;
        public int swings;
        public string kind;
        public string side;
        public float strength;
        public bool hit;
        public string evt;   // ESP32 swatter events: "swing" or "move"
        public string axis;
        public string dir;
        public float rate;
        public string phase;
        public float time_left;
        public string swatter;      // Swatter link: never, ok or lost.
        public string classifier;   // What detects swings on the Pi.
        public int[] spikes;        // Brain output, for the panel and neural view.
        public int[] active;
        public int[] neuron_groups;
        public string[] group_names;
        public string mode;
        public int neurons;
        public float[] efficacy;
        public float[] dg;          // Controller: rotation since the last message, radians.
        public float[] g;           // Controller: gyro, rad/s.
        public float[] s;           // Controller: thumbstick x, y.
        public int b;               // Controller: thumbstick pressed.
    }

    // The game's link to the Raspberry Pi over Wi-Fi. The Pi runs the fly, the swing model and the brain. This sends the
    // room, the player's pose, swings and what the room looks like to the fly, and hands back the fly's state and the
    // swatter's aim, thumbstick and swings. It reconnects by itself.
    // Set FLYAI_BACKEND to override the address (for example ws://127.0.0.1:8766), or to "off".
    public class BackendClient : MonoBehaviour
    {
        const string DefaultUrl = "ws://172.20.10.10:8766"; // The Pi, on the Samsung Smart Fridge hotspot.
        const float PoseInterval = 0.05f;

        public static BackendClient Current { get; private set; }
        public static bool Enabled => (Environment.GetEnvironmentVariable("FLYAI_BACKEND") ?? "").ToLowerInvariant() != "off";
        public static bool Connected => Current != null && Current.connected;
        // True while the Pi runs the fly for the current room (decided when the room loads).
        public static bool Driving { get; set; }
        public static BackendMessage LastFly { get; private set; }
        public static int Score { get; private set; }
        public static int[] NeuronGroups { get; private set; }
        public static string[] GroupNames { get; private set; }
        public static string BrainInfo { get; private set; } = "";
        public static float[] Efficacy { get; private set; } = { 1f, 1f, 1f, 1f };

        readonly ConcurrentQueue<BackendMessage> incoming = new ConcurrentQueue<BackendMessage>();
        readonly ConcurrentQueue<string> outgoing = new ConcurrentQueue<string>();
        readonly ConcurrentQueue<BackendMessage> swatterEvents = new ConcurrentQueue<BackendMessage>();
        CancellationTokenSource cancel;
        volatile bool connected;
        volatile bool roundPlaying;
        volatile string pendingRoom;
        float lastPose;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Current = null;
            LastFly = null;
            Score = 0;
            Driving = false;
            NeuronGroups = null;
            GroupNames = null;
            BrainInfo = "";
            Efficacy = new[] { 1f, 1f, 1f, 1f };
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Current != null) return;
            if (!Enabled) return;
            new GameObject("FlyAI Backend").AddComponent<BackendClient>();
        }

        void Awake()
        {
            Current = this;
            DontDestroyOnLoad(gameObject);
            var url = Environment.GetEnvironmentVariable("FLYAI_BACKEND");
            if (string.IsNullOrEmpty(url)) url = DefaultUrl;
            cancel = new CancellationTokenSource();
            _ = Run(url, cancel.Token);
            Debug.Log($"[FlyAI] Backend {url}");
        }

        void OnDestroy()
        {
            cancel?.Cancel();
            if (Current == this) Current = null;
        }

        // ---- send (safe to call when there is no connection) --------------
        static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        static string V(Vector3 v) => $"[{F(v.x)},{F(v.y)},{F(v.z)}]";

        static void Send(string json)
        {
            if (Connected) Current.outgoing.Enqueue(json);
        }

        // The fly's landing spots, plus the points in the air and which of them have a clear line between them, because
        // the Pi has no copy of the room's walls. Sent now if connected, and again after every reconnect.
        public static void SendRoom(IList<(Vector3 position, Vector3 normal)> spots, Vector3 head, float ceiling,
                                    IList<Vector3> air, IList<string> links, IList<int> spotAir)
        {
            if (Current == null) return;
            LastFly = null;
            var sb = new StringBuilder("{\"type\":\"room\",\"hold\":true,\"head\":").Append(V(head))
                .Append(",\"ceiling\":").Append(F(ceiling)).Append(",\"landingSpots\":[");
            for (var i = 0; i < spots.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"position\":").Append(V(spots[i].position)).Append(",\"normal\":").Append(V(spots[i].normal)).Append('}');
            }
            sb.Append("],\"air\":[");
            for (var i = 0; i < air.Count; i++) sb.Append(i > 0 ? "," : "").Append(V(air[i]));
            sb.Append("],\"links\":[");
            for (var i = 0; i < links.Count; i++) sb.Append(i > 0 ? ",\"" : "\"").Append(links[i]).Append('"');
            sb.Append("],\"spotAir\":[");
            for (var i = 0; i < spotAir.Count; i++) sb.Append(i > 0 ? "," : "").Append(spotAir[i]);
            sb.Append("]}");
            Current.pendingRoom = sb.ToString();
            Send(Current.pendingRoom);
        }

        public static void SendPose(Vector3 head, Vector3 aim, float reach)
        {
            if (!Driving || !Connected || Time.unscaledTime - Current.lastPose < PoseInterval) return;
            Current.lastPose = Time.unscaledTime;
            Send($"{{\"type\":\"pose\",\"head\":{V(head)},\"aim\":{V(aim)},\"reach\":{F(reach)}}}");
        }

        // The swing as a threat the fly can see. Unity decides what the swatter actually hits (see SendSwat).
        public static void SendSwing(Vector3 origin, Vector3 direction, float strength, float reach, List<Vector3> swept = null)
        {
            if (!Driving || !Connected) return;   // old swings must not replay later
            Send($"{{\"type\":\"swing\",\"threat\":true,\"origin\":{V(origin)},\"dir\":{V(direction)},\"strength\":{F(strength)},\"reach\":{F(reach)}}}");
        }

        public static void SendSwat() => Send("{\"type\":\"swat\"}");

        // The 3-2-1 is over: the hunt starts on the Pi, with this difficulty and a fresh brain.
        public static void StartHunt() => Send($"{{\"type\":\"hunt\",\"level\":{Difficulty.Level}}}");

        // A sound at the fly, already scaled by distance. The fly's own hearing is applied on the Pi.
        public static void SendSound(float value, string ear) =>
            Send($"{{\"type\":\"stimulus\",\"kind\":\"sound\",\"value\":{F(value)},\"side\":\"{ear}\"}}");

        // What only the game can know: the light at the fly, the flashlight, and room events.
        public static void SendSenses(float vision, float beam, float surge, float lightning, float headlights, float power,
                                      bool sneak, Vector3 swatter, Vector3 flashlight) =>
            Send($"{{\"type\":\"senses\",\"vision\":{F(vision)},\"beam\":{F(beam)},\"surge\":{F(surge)},\"lightning\":{F(lightning)}," +
                 $"\"headlights\":{F(headlights)},\"power\":{F(power)},\"sneak\":{(sneak ? "true" : "false")}," +
                 $"\"swatter\":{V(swatter)},\"flashlight\":{V(flashlight)}}}");

        public static bool TakeSwatterEvent(out BackendMessage message)
        {
            message = null;
            return Current != null && Current.swatterEvents.TryDequeue(out message);
        }

        // ---- network loop (background) ------------------------------------
        async Task Run(string url, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                using (var socket = new ClientWebSocket())
                {
                    try
                    {
                        using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            attempt.CancelAfter(3000);   // An unreachable Pi must not hold this up for long.
                            await socket.ConnectAsync(new Uri(url), attempt.Token);
                        }
                        while (outgoing.TryDequeue(out _)) { }   // drop anything queued before the connection
                        var room = pendingRoom;
                        if (room != null)
                            outgoing.Enqueue(room.Replace("{\"type\":\"room\",", $"{{\"type\":\"room\",\"resume\":true,\"playing\":{(roundPlaying ? "true" : "false")},"));
                        connected = true;
                        await Task.WhenAny(Receive(socket, token), SendLoop(socket, token));
                    }
                    catch (Exception) when (!token.IsCancellationRequested) { }
                    catch (OperationCanceledException) { return; }
                    finally { connected = false; }
                }
                try { await Task.Delay(1000, token); } catch (OperationCanceledException) { return; }
            }
        }

        async Task Receive(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[16384];
            var text = new StringBuilder();
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close) return;
                text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;
                try { incoming.Enqueue(JsonUtility.FromJson<BackendMessage>(text.ToString())); }
                catch (Exception) { }
                text.Clear();
            }
        }

        async Task SendLoop(ClientWebSocket socket, CancellationToken token)
        {
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                if (outgoing.TryDequeue(out var json))
                    await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, token);
                else
                    await Task.Delay(2, token);
            }
        }

        // ---- main thread ---------------------------------------------------
        void Update()
        {
            roundPlaying = GameRound.Current != null && GameRound.Current.FlyActive;
            var handled = 0;
            while (handled++ < 256 && incoming.TryDequeue(out var m))
            {
                if (m == null) continue;
                switch (m.type)
                {
                    case "fly":
                        LastFly = m;
                        Score = m.score;
                        break;
                    case "controller":
                        ImuReceiver.Current?.FeedNetwork(m.dg, m.g, m.s, m.b);
                        break;
                    case "swatter":
                        swatterEvents.Enqueue(m);
                        break;
                    case "brain":
                        FlyAgent.Current?.FeedBrain(m);
                        break;
                    case "brain_groups":
                        NeuronGroups = m.neuron_groups;
                        GroupNames = m.group_names;
                        break;
                    case "brain_status":
                        BrainInfo = $"{m.source}, {m.neurons} neurons";
                        if (m.efficacy != null && m.efficacy.Length == 4) Efficacy = m.efficacy;
                        break;
                }
            }
            // Unused events must not pile up if nothing is reading them.
            while (swatterEvents.Count > 16) swatterEvents.TryDequeue(out _);
        }

        // One line saying how each piece is connected.
        public static string StatusLine()
        {
            string Dot(bool ok) => ok ? "<color=#6FE08A>●</color>" : "<color=#FF7C6B>●</color>";
            if (!Connected) return $"{Dot(false)} Raspberry Pi offline: {(Driving ? "reconnecting" : "playing on this laptop, retrying")}";
            var fly = LastFly;
            var swatter = fly != null && fly.swatter == "ok";
            var model = fly != null && fly.classifier != null && fly.classifier.StartsWith("model");
            return $"{Dot(true)} Pi    {Dot(swatter)} Swatter    {Dot(model)} Model    {Dot(BrainInfo.Length > 0)} Brain";
        }

        void OnGUI()
        {
            if (MainMenu.Active) return;
            GUI.Label(new Rect(16, 36, 520, 24), StatusLine(), new GUIStyle(GUI.skin.label) { richText = true });
        }
    }
}
