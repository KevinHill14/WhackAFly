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
    }

    // Connects to the game backend on the Pi, sends the room, pose and swings,
    // and hands fly state and swatter events back on the main thread.
    // Set FLYAI_BACKEND (for example ws://192.168.4.1:8766) to override the address.
    public class BackendClient : MonoBehaviour
    {
        const string DefaultUrl = "ws://127.0.0.1:8766";
        const float PoseInterval = 0.05f;

        public static BackendClient Current { get; private set; }
        public static bool Connected => Current != null && Current.connected;
        // True while the Pi runs the fly for the current room (decided when the room loads).
        public static bool Driving { get; private set; }
        public static BackendMessage LastFly { get; private set; }
        public static int Score { get; private set; }

        readonly ConcurrentQueue<BackendMessage> incoming = new ConcurrentQueue<BackendMessage>();
        readonly ConcurrentQueue<string> outgoing = new ConcurrentQueue<string>();
        readonly ConcurrentQueue<BackendMessage> swatterEvents = new ConcurrentQueue<BackendMessage>();
        CancellationTokenSource cancel;
        volatile bool connected;
        float lastPose;
        string pendingRoom;
        FlyView view;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Current = null;
            LastFly = null;
            Score = 0;
            Driving = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Current != null) return;
            if ((Environment.GetEnvironmentVariable("FLYAI_BACKEND") ?? "").ToLowerInvariant() == "off") return;
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

        public static void SendRoom(RoomData room, Vector3 head)
        {
            if (Current == null || room.landingSpots == null) return;
            var sb = new StringBuilder("{\"type\":\"room\",\"head\":").Append(V(head)).Append(",\"landingSpots\":[");
            var first = true;
            foreach (var spot in room.landingSpots)
            {
                if (spot.position == null || spot.position.Length != 3) continue;
                if (!first) sb.Append(',');
                first = false;
                var normal = spot.normal != null && spot.normal.Length == 3 ? new Vector3(-spot.normal[0], spot.normal[1], spot.normal[2]) : Vector3.up;
                sb.Append("{\"position\":").Append(V(RoomLoader.ToUnity(spot.position)))
                  .Append(",\"normal\":").Append(V(normal)).Append('}');
            }
            sb.Append("]}");
            Current.pendingRoom = sb.ToString();   // sent now if connected, and again after every reconnect
            if (Current.connected) Current.outgoing.Enqueue(Current.pendingRoom);
        }

        public static void SendPose(Vector3 head, Vector3 aim, float reach)
        {
            if (Current == null || !Current.connected || Time.unscaledTime - Current.lastPose < PoseInterval) return;
            Current.lastPose = Time.unscaledTime;
            Current.outgoing.Enqueue($"{{\"type\":\"pose\",\"head\":{V(head)},\"aim\":{V(aim)},\"reach\":{F(reach)}}}");
        }

        // swept: for a directional swing, the directions the swatter passed through before `direction`, where it
        // landed. The backend hits the fly if it is near any of them. Older backends ignore the field.
        public static void SendSwing(Vector3 origin, Vector3 direction, float strength, float reach, List<Vector3> swept = null)
        {
            if (Current == null || !Current.connected) return;   // old swings must not replay later
            var dirs = "";
            if (swept != null && swept.Count > 0)
            {
                var parts = new string[Mathf.Min(swept.Count, 32)];
                for (var i = 0; i < parts.Length; i++) parts[i] = V(swept[i]);
                dirs = ",\"dirs\":[" + string.Join(",", parts) + "]";
            }
            Current.outgoing.Enqueue($"{{\"type\":\"swing\",\"origin\":{V(origin)},\"dir\":{V(direction)},\"strength\":{F(strength)},\"reach\":{F(reach)}{dirs}}}");
        }

        // The room has loaded. If the backend is reachable it runs the fly; Unity only draws it and runs the round UI.
        // Returns false when there is no backend, and the caller spawns the local fly instead.
        public static bool BeginRemoteRound(Transform parent)
        {
            Driving = false;
            if (!Connected) return false;
            if (Current.view != null) Destroy(Current.view.gameObject);
            LastFly = null;
            Score = 0;
            Current.view = FlyView.Create(parent);
            GameRound.Begin(parent, null);
            Driving = true;
            Current.outgoing.Enqueue($"{{\"type\":\"difficulty\",\"level\":{Difficulty.Level}}}");
            Debug.Log("[FlyAI] The backend is running the fly for this room.");
            return true;
        }

        // The 3-2-1 is over: the hunt starts on the backend too, with a fresh brain.
        public static void StartRemoteHunt()
        {
            if (!Driving || !Connected) return;
            Current.outgoing.Enqueue($"{{\"type\":\"difficulty\",\"level\":{Difficulty.Level}}}");
            Current.outgoing.Enqueue("{\"type\":\"reset\",\"start\":true}");
        }

        // A sound near the fly (glass, a swing landing). Unity works out how loud it is at the fly; the brain's ears get it.
        public static void SendSound(Vector3 point, float loudness, float range)
        {
            if (!Driving || !Connected || LastFly == null || LastFly.pos == null || LastFly.pos.Length != 3) return;
            var fly = new Vector3(LastFly.pos[0], LastFly.pos[1], LastFly.pos[2]);
            var value = Mathf.Clamp01(loudness * Mathf.InverseLerp(range, 0.4f, Vector3.Distance(point, fly)));
            if (value <= 0.05f) return;
            var heading = LastFly.heading != null && LastFly.heading.Length == 3 ? new Vector3(LastFly.heading[0], 0f, LastFly.heading[2]) : Vector3.forward;
            var to = point - fly;
            to.y = 0f;
            var across = to.sqrMagnitude < 1e-6f || heading.sqrMagnitude < 1e-6f ? 0f : Vector3.Dot(Vector3.Cross(Vector3.up, heading.normalized), to.normalized);
            var side = Mathf.Abs(across) < 0.2f ? "center" : across > 0f ? "right" : "left";
            Current.outgoing.Enqueue($"{{\"type\":\"stimulus\",\"kind\":\"sound\",\"value\":{F(value)},\"side\":\"{side}\"}}");
        }

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
                        await socket.ConnectAsync(new Uri(url), token);
                        while (outgoing.TryDequeue(out _)) { }   // drop anything queued before the connection
                        if (pendingRoom != null) outgoing.Enqueue(pendingRoom);
                        connected = true;
                        outgoing.Enqueue($"{{\"type\":\"difficulty\",\"level\":{Difficulty.Level}}}");
                        await Task.WhenAny(Receive(socket, token), Send(socket, token));
                    }
                    catch (Exception) when (!token.IsCancellationRequested) { }
                    catch (OperationCanceledException) { return; }
                    finally { connected = false; }
                }
                try { await Task.Delay(2000, token); } catch (OperationCanceledException) { return; }
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

        async Task Send(ClientWebSocket socket, CancellationToken token)
        {
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                if (outgoing.TryDequeue(out var json))
                    await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, token);
                else
                    await Task.Delay(5, token);
            }
        }

        // ---- main thread ---------------------------------------------------
        void Update()
        {
            var handled = 0;
            while (handled++ < 64 && incoming.TryDequeue(out var m))
            {
                if (m == null) continue;
                switch (m.type)
                {
                    case "fly":
                        LastFly = m;
                        Score = m.score;
                        if (!Driving) break;   // a local fly is playing this room
                        if (view == null) view = FlyView.Create(null);
                        view.Apply(m);
                        if (RoomTension.Current != null) RoomTension.Current.SetFlyDistance(m.distance);
                        break;
                    case "event":
                        if (view != null) view.OnEvent(m);
                        if (Driving && m.kind == "squish" && GameRound.Current != null)
                            GameRound.Current.Win(m.pos != null && m.pos.Length == 3 ? new Vector3(m.pos[0], m.pos[1], m.pos[2]) : Vector3.zero);
                        break;
                    case "swatter":
                        swatterEvents.Enqueue(m);
                        break;
                }
            }
            // Unused events must not pile up if nothing is reading them.
            while (swatterEvents.Count > 16) swatterEvents.TryDequeue(out _);
        }

        void OnGUI()
        {
            if (connected && Driving && !MainMenu.Active) GUI.Label(new Rect(16, 36, 400, 24), "Pi backend is running the fly");
        }
    }
}
