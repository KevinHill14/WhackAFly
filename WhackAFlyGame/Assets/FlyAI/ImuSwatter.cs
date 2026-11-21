using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FlyAI
{
    [Serializable]
    class ImuPacket
    {
        public float[] dg; // Rotation since the last packet, radians, device axes.
        public float[] g;  // Latest gyro, rad/s.
        public float[] l;  // Latest linear acceleration (gravity removed), m/s^2.
        public float lp;   // Peak |linear acceleration| since the last packet.
        public int n;
    }

    // Receives the sensor packets that firmware/imu_bridge.py sends over UDP (port 9100).
    public class ImuReceiver : IDisposable
    {
        public const int Port = 9100;
        readonly UdpClient udp;
        readonly object gate = new object();
        volatile bool running = true;
        Vector3 pendingRotation; // Degrees since the last Take.
        float pendingPeak;
        double lastPacketAt = -100.0;
        public Vector3 Gyro;     // rad/s
        public Vector3 Linear;   // m/s^2
        public float Peak;       // Latest packet's peak acceleration.
        public int Packets;
        public string Error = "";

        // Packets arrive on another thread, so the time comes from a stopwatch, not Time.
        static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        public bool Live => Clock.Elapsed.TotalSeconds - lastPacketAt < 1.0;

        public ImuReceiver()
        {
            try
            {
                udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, Port));
                new Thread(Listen) { IsBackground = true }.Start();
            }
            catch (Exception e) { Error = e.Message; running = false; }
        }

        void Listen()
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            while (running)
            {
                try
                {
                    var bytes = udp.Receive(ref from);
                    var packet = JsonUtility.FromJson<ImuPacket>(System.Text.Encoding.UTF8.GetString(bytes));
                    if (packet == null || packet.dg == null || packet.dg.Length < 3 || packet.g == null || packet.g.Length < 3 || packet.l == null || packet.l.Length < 3) continue;
                    lock (gate)
                    {
                        pendingRotation += new Vector3(packet.dg[0], packet.dg[1], packet.dg[2]) * Mathf.Rad2Deg;
                        pendingPeak = Mathf.Max(pendingPeak, packet.lp);
                        Gyro = new Vector3(packet.g[0], packet.g[1], packet.g[2]);
                        Linear = new Vector3(packet.l[0], packet.l[1], packet.l[2]);
                        Peak = Mathf.Max(packet.lp, Peak * 0.92f); // Held and faded so the panel is readable.
                        Packets++;
                        lastPacketAt = Clock.Elapsed.TotalSeconds;
                    }
                }
                catch (Exception) { if (!running) return; }
            }
        }

        // Rotation (degrees, device axes) and the biggest acceleration since the last call.
        public void Take(out Vector3 rotation, out float peak)
        {
            lock (gate)
            {
                rotation = pendingRotation;
                peak = pendingPeak;
                pendingRotation = Vector3.zero;
                pendingPeak = 0f;
            }
        }

        public void Dispose()
        {
            running = false;
            try { udp?.Close(); } catch (Exception) { }
        }
    }

    // Aim from the gyro, swing from acceleration. Walking and the other keys still come from the keyboard.
    public class ImuSwatter : ISwatterInput
    {
        public readonly ISwatterInput Keyboard;
        readonly ImuReceiver imu;
        public static float SwingThreshold = 10f;      // m/s^2
        public static int YawAxis = 2, PitchAxis = 1;  // Which device gyro axis turns left/right and up/down.
        public static float YawSign = 1f, PitchSign = 1f;
        public static float Sensitivity = 1f;
        // Directional swings: the swatter sweeps the way the real swing went (F5 in the sensor panel turns it off).
        public static bool Directional = true;
        public static float StrokeWindow = 0.1f;      // seconds of rotation that say which way the swing is going
        public static float MinStrokeDegrees = 1.5f;  // less rotation than this: no clear direction, classic chop
        public static float ArcMin = 35f, ArcMax = 55f; // how far the sweep goes, by swing strength (degrees)
        public int Swings;
        public Vector2 LastStroke;
        float lastSwingAt = -10f;
        readonly System.Collections.Generic.Queue<(float time, Vector2 turn)> recent = new System.Collections.Generic.Queue<(float time, Vector2 turn)>();

        public static float Arc(float strength) => Mathf.Lerp(ArcMin, ArcMax, Mathf.Clamp01(strength));

        // A swing from the backend names the gyro axis it turned about and which way. The yaw axis means a swing
        // to the right or left, the pitch axis up or down (same axes and signs as aiming). Anything else: classic chop.
        public static Vector2 StrokeFor(string axis, string dir, float strength)
        {
            if (!Directional || string.IsNullOrEmpty(axis) || string.IsNullOrEmpty(dir)) return Vector2.zero;
            var index = "XYZ".IndexOf(char.ToUpperInvariant(axis[0]));
            var sign = dir == "positive" ? 1f : dir == "negative" ? -1f : 0f;
            if (index < 0 || sign == 0f) return Vector2.zero;
            if (index == YawAxis) return new Vector2(sign * YawSign, 0f) * Arc(strength);
            if (index == PitchAxis) return new Vector2(0f, sign * PitchSign) * Arc(strength);
            return Vector2.zero;
        }

        public ImuSwatter(ISwatterInput keyboard, ImuReceiver imu)
        {
            Keyboard = keyboard;
            this.imu = imu;
        }

        public SwatterFrame Read()
        {
            var frame = Keyboard.Read();
            imu.Take(out var rotation, out var peak);
            // The mouse no longer aims or swings; the sensor does.
            frame.aimDelta = new Vector2(rotation[YawAxis] * YawSign, rotation[PitchAxis] * PitchSign) * Sensitivity;
            frame.swing = false;
            var now = Time.unscaledTime;
            recent.Enqueue((now, frame.aimDelta));
            while (recent.Count > 0 && now - recent.Peek().time > StrokeWindow) recent.Dequeue();
            if (peak > SwingThreshold && now - lastSwingAt > 0.5f) // One swing per shake, not one per packet.
            {
                lastSwingAt = now;
                frame.swing = true;
                frame.swingStrength = Mathf.Clamp01(0.4f + 0.6f * Mathf.InverseLerp(SwingThreshold, SwingThreshold * 3f, peak));
                frame.swingStroke = StrokeFromTurn(SumRecent(), frame.swingStrength);
                LastStroke = frame.swingStroke;
                Swings++;
            }
            return frame;
        }

        Vector2 SumRecent()
        {
            var sum = Vector2.zero;
            foreach (var entry in recent) sum += entry.turn;
            return sum;
        }

        // The way the swatter was turning as the swing began is the way it sweeps: direction only, a set length.
        public static Vector2 StrokeFromTurn(Vector2 turn, float strength)
        {
            if (!Directional || turn.magnitude < MinStrokeDegrees) return Vector2.zero;
            return turn.normalized * Arc(strength);
        }

        // "up-left", "right" and so on, for the panel.
        public static string Describe(Vector2 stroke)
        {
            if (stroke.sqrMagnitude < 0.0001f) return "straight (classic chop)";
            var d = stroke.normalized;
            var v = d.y > 0.38f ? "up" : d.y < -0.38f ? "down" : "";
            var h = d.x > 0.38f ? "right" : d.x < -0.38f ? "left" : "";
            return (v.Length > 0 && h.Length > 0 ? v + "-" + h : v + h) + " " + stroke.magnitude.ToString("0") + " deg";
        }
    }

    // The sensor test. The game starts firmware/imu_bridge.py itself; once the sensor is live the swatter aims with the sensor and
    // swings when acceleration passes the threshold. A panel on the left shows the live readings (it appears once
    // the bridge is sending).
    //   F10 sensor control on/off (it starts on)    F6/F7 which gyro axis is yaw/pitch    F8/F9 flip yaw/pitch    F11/F12 threshold
    //   F5 directional swings on/off (the swatter sweeps the way you swing; off = always the classic chop)
    public class ImuTest : MonoBehaviour
    {
        ImuReceiver imu;
        ImuSwatter swatter;
        ISwatterInput normal;
        PlayerController player;
        GUIStyle style;
        float lastSwingAt = -10f;
        int lastSwings;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (FindFirstObjectByType<ImuTest>() != null) return;
            DontDestroyOnLoad(new GameObject("ImuTest", typeof(ImuTest)));
        }

        bool autoOn;

        void SetSensorControl(bool on)
        {
            if (player == null) return;
            if (on && swatter == null)
            {
                normal = player.InputSource;
                swatter = new ImuSwatter(normal, imu);
                player.SetInput(swatter);
            }
            else if (!on && swatter != null)
            {
                player.SetInput(normal);
                swatter = null;
            }
        }

        void Awake() => imu = new ImuReceiver();
        void OnDestroy() => imu?.Dispose();

        void Update()
        {
            var keys = Keyboard.current;
            if (player == null)
            {
                player = FindFirstObjectByType<PlayerController>();
                if (player == null) { swatter = null; autoOn = false; return; } // The room was unloaded: control goes back to normal with it.
            }
            // The first time the sensor is live in a room, it takes over aiming and swinging. F10 hands it back.
            if (!autoOn && imu.Live)
            {
                autoOn = true;
                SetSensorControl(true);
            }
            if (keys == null) return;
            if (keys.f10Key.wasPressedThisFrame) SetSensorControl(swatter == null);
            if (keys.f5Key.wasPressedThisFrame) ImuSwatter.Directional = !ImuSwatter.Directional;
            if (keys.f6Key.wasPressedThisFrame) ImuSwatter.YawAxis = (ImuSwatter.YawAxis + 1) % 3;
            if (keys.f7Key.wasPressedThisFrame) ImuSwatter.PitchAxis = (ImuSwatter.PitchAxis + 1) % 3;
            if (keys.f8Key.wasPressedThisFrame) ImuSwatter.YawSign = -ImuSwatter.YawSign;
            if (keys.f9Key.wasPressedThisFrame) ImuSwatter.PitchSign = -ImuSwatter.PitchSign;
            if (keys.f11Key.wasPressedThisFrame) ImuSwatter.SwingThreshold = Mathf.Max(2f, ImuSwatter.SwingThreshold - 2f);
            if (keys.f12Key.wasPressedThisFrame) ImuSwatter.SwingThreshold += 2f;
            if (swatter != null && swatter.Swings != lastSwings) { lastSwings = swatter.Swings; lastSwingAt = Time.time; }
        }

        void OnGUI()
        {
            if (imu == null || imu.Packets == 0) return; // Nothing connected: stay out of the way.
            if (style == null) style = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true };
            var peak = imu.Peak;
            var axes = "xyz";
            var text = "<b>Motion sensor test</b>  " + (imu.Live ? "<color=#6f6>connected</color>" : "<color=#f66>no data</color>") + "  packets " + imu.Packets + "\n" +
                       $"gyro  x {imu.Gyro.x,6:0.00}  y {imu.Gyro.y,6:0.00}  z {imu.Gyro.z,6:0.00} rad/s\n" +
                       $"accel {imu.Linear.magnitude,5:0.0} m/s²   peak {peak,5:0.0}   swing above {ImuSwatter.SwingThreshold:0} (F11/F12)\n" +
                       "sensor control: " + (swatter != null ? "<color=#6f6>ON</color>" : "off") + " (F10)   yaw = gyro " + axes[ImuSwatter.YawAxis] + (ImuSwatter.YawSign < 0 ? " flipped" : "") + " (F6/F8)   pitch = gyro " + axes[ImuSwatter.PitchAxis] + (ImuSwatter.PitchSign < 0 ? " flipped" : "") + " (F7/F9)\n" +
                       "swings " + (swatter != null ? swatter.Swings : 0) + (Time.time - lastSwingAt < 0.6f ? "   <color=#ff6><b>SWING!</b></color>" : "") + "\n" +
                       "directional swings " + (ImuSwatter.Directional ? "<color=#6f6>ON</color>" : "off") + " (F5)   last swing " + (swatter != null && swatter.Swings > 0 ? ImuSwatter.Describe(swatter.LastStroke) : "-");
            var rect = new Rect(12f, Screen.height * 0.5f - 40f, 640f, 110f);
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(rect.x + 8f, rect.y + 4f, rect.width - 16f, rect.height - 8f), text, style);
            var bar = new Rect(rect.x, rect.yMax + 4f, 300f, 8f); // Acceleration against the threshold.
            GUI.color = new Color(1f, 1f, 1f, 0.25f);
            GUI.DrawTexture(bar, Texture2D.whiteTexture);
            GUI.color = peak > ImuSwatter.SwingThreshold ? Color.yellow : Color.cyan;
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(peak / (ImuSwatter.SwingThreshold * 3f)), bar.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }
    }
}
