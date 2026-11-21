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
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Current != null) return;
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
                sb.Append("{\"id\":\"").Append(spot.id).Append("\",\"position\":").Append(V(RoomLoader.ToUnity(spot.position)))
                  .Append(",\"normal\":").Append(V(normal)).Append('}');
            }
            sb.Append("]}");
            Current.pendingRoom = sb.ToString();   // resent after every reconnect
            Current.outgoing.Enqueue(Current.pendingRoom);
        }

        public static void SendPose(Vector3 head, Vector3 aim, float reach)
        {
            if (Current == null || Time.unscaledTime - Current.lastPose < PoseInterval) return;
            Current.lastPose = Time.unscaledTime;
            Current.outgoing.Enqueue($"{{\"type\":\"pose\",\"head\":{V(head)},\"aim\":{V(aim)},\"reach\":{F(reach)}}}");
        }

        public static void SendSwing(Vector3 origin, Vector3 direction, float strength, float reach)
        {
            if (Current == null) return;
            Current.outgoing.Enqueue($"{{\"type\":\"swing\",\"origin\":{V(origin)},\"dir\":{V(direction)},\"strength\":{F(strength)},\"reach\":{F(reach)}}}");
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
                        connected = true;
                        if (pendingRoom != null) outgoing.Enqueue(pendingRoom);
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
                        if (view == null) view = FlyView.Create();
                        view.Apply(m);
                        if (RoomTension.Current != null) RoomTension.Current.SetFlyDistance(m.distance);
                        break;
                    case "event":
                        if (view != null) view.OnEvent(m);
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
            GUI.Label(new Rect(16, 36, 400, 24), connected ? $"Backend connected   Flies swatted: {Score}" : "Backend offline (retrying)");
        }
    }
}
