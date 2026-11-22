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
        public float[] s;  // Thumbstick x, y (-1 to 1).
        public int b;      // Thumbstick pressed.
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
        Vector2 stick;
        bool stickDown;
        bool pressWaiting;
        Vector3 gravity;
        bool hasGravity;

        // The thumbstick now, or zero if the board has stopped sending, so the player never keeps walking on their own.
        public static ImuReceiver Current { get; private set; }
        public static Vector2 LiveStick => Current != null ? Current.StickValue : Vector2.zero;
        // True once per press. The menu reads this; during a round it is drained, so a press there cannot click the menu later.
        public static bool IsLive => Current != null && Current.Live;
        public bool FromNetwork { get; private set; }

        // The same readings, arriving from the Pi over Wi-Fi instead of from the USB bridge.
        public void FeedNetwork(float[] turn, float[] gyro, float[] stickXY, int pressed, float[] down = null)
        {
            if (turn == null || turn.Length < 3) return;
            lock (gate)
            {
                FromNetwork = true;
                if (down != null && down.Length >= 3)
                {
                    gravity = new Vector3(down[0], down[1], down[2]);
                    hasGravity = gravity.sqrMagnitude > 0.25f;
                }
                pendingRotation += new Vector3(turn[0], turn[1], turn[2]) * Mathf.Rad2Deg;
                if (gyro != null && gyro.Length >= 3) Gyro = new Vector3(gyro[0], gyro[1], gyro[2]);
                if (stickXY != null && stickXY.Length >= 2) stick = new Vector2(stickXY[0], stickXY[1]);
                if (pressed == 1 && !stickDown) pressWaiting = true;
                stickDown = pressed == 1;
                Packets++;
                lastPacketAt = Clock.Elapsed.TotalSeconds;
            }
        }

        public static bool TakePress() => Current != null && Current.TakeOwnPress();
        public bool StickDown { get { lock (gate) return Live && stickDown; } }
        // Which way gravity points on the board (unit vector), from the Pi. False over the USB bridge, which does not send it.
        public bool TryGravity(out Vector3 g)
        {
            lock (gate)
            {
                g = gravity;
                return Live && hasGravity;
            }
        }

        bool TakeOwnPress()
        {
            lock (gate)
            {
                var waiting = pressWaiting;
                pressWaiting = false;
                return waiting;
            }
        }

        public Vector2 StickValue { get { lock (gate) return Live ? stick : Vector2.zero; } }
        public string Error = "";

        // Packets arrive on another thread, so the time comes from a stopwatch, not Time.
        static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        public bool Live => Clock.Elapsed.TotalSeconds - lastPacketAt < 1.0;

        public ImuReceiver()
        {
            Current = this;
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
                        if (packet.s != null && packet.s.Length >= 2) stick = new Vector2(packet.s[0], packet.s[1]);
                        if (packet.b == 1 && !stickDown) pressWaiting = true;
                        stickDown = packet.b == 1;
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
            if (Current == this) Current = null;
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
        // Yaw is flipped on this board: turning the swatter left must turn the view left. F6-F9 change these and are saved.
        public static float YawSign = -1f, PitchSign = 1f;
        public static float Sensitivity = 1f;
        public static float StickForwardSign = 1f, StickTurnSign = 1f;  // F3 / F4 flip them; remembered between runs.
        // The board's stick sits a quarter turn round: its x is forward-back and its y is turning. F1 swaps them back.
        public static bool StickSwapAxes = true;
        public static float StickForward(Vector2 raw) => (StickSwapAxes ? raw.x : raw.y) * StickForwardSign;
        // Gravity on the board while it is held upright, which is "look straight ahead". It starts along the yaw axis (held
        // upright, turning left-right spins the board about it); holding the stick press (or Space) re-sets it to now.
        public static Vector3 Upright;
        public static bool HasUpright;
        public static void SetUpright(Vector3 g)
        {
            Upright = g.normalized;
            HasUpright = true;
            PlayerPrefs.SetFloat("FlyAI.UprightX", Upright.x);
            PlayerPrefs.SetFloat("FlyAI.UprightY", Upright.y);
            PlayerPrefs.SetFloat("FlyAI.UprightZ", Upright.z);
            PlayerPrefs.Save();
        }

        // How far the board is tilted forward or back from upright, about the pitch axis, in the same sense as gyro aiming.
        // NaN when it lies on its side and the tilt cannot be read.
        public static float PitchFromGravity(Vector3 g)
        {
            int a = (PitchAxis + 1) % 3, b = (PitchAxis + 2) % 3;
            var now = new Vector2(g[a], g[b]);
            var upright = new Vector2(Upright[a], Upright[b]);
            if (now.magnitude < 0.35f || upright.magnitude < 0.35f) return float.NaN;
            var delta = Mathf.DeltaAngle(Mathf.Atan2(upright.y, upright.x) * Mathf.Rad2Deg, Mathf.Atan2(now.y, now.x) * Mathf.Rad2Deg);
            return -delta * PitchSign;   // turning the board by +θ turns gravity by -θ in the board's own axes
        }
        public static float StickTurn(Vector2 raw) => (StickSwapAxes ? raw.y : raw.x) * StickTurnSign;
        // Directional swings: the swatter sweeps the way the real swing went (F5 in the sensor panel turns it off).
        public static bool Directional = true;
        public static float StrokeWindow = 0.1f;      // seconds of rotation that say which way the swing is going
        public static float MinStrokeDegrees = 1.5f;  // less rotation than this: no clear direction, classic chop
        public static float ArcMin = 35f, ArcMax = 55f; // how far the sweep goes, by swing strength (degrees)
        public int Swings;
        public Vector2 LastStroke;
        float lastSwingAt = -10f;
        float pressStartedAt = -1f;
        bool pressRecentered;
        const float HoldToRecenter = 0.6f;
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
            if (!imu.Live) return frame; // The swatter is gone: the mouse and keyboard carry on.
            var modelSwing = frame.swing && imu.FromNetwork; // Over Wi-Fi, the trained model on the Pi decides what is a swing.
            var modelStrength = frame.swingStrength;
            var modelStroke = frame.swingStroke;
            imu.Take(out var rotation, out var peak);
            // The mouse no longer aims or swings; the sensor does.
            frame.aimDelta = new Vector2(rotation[YawAxis] * YawSign, rotation[PitchAxis] * PitchSign) * Sensitivity;
            frame.swing = false;
            var now = Time.unscaledTime;

            // Holding the stick press recenters, like holding Space.
            if (imu.StickDown)
            {
                if (pressStartedAt < 0f) pressStartedAt = now;
                else if (!pressRecentered && now - pressStartedAt >= HoldToRecenter)
                {
                    pressRecentered = true;
                    frame.recenter = true;
                }
            }
            else
            {
                pressStartedAt = -1f;
                pressRecentered = false;
            }
            if (imu.TryGravity(out var g))
            {
                if (frame.recenter) SetUpright(g);    // however it is held now becomes straight ahead
                else if (!HasUpright && Mathf.Abs(g[YawAxis]) > 0.8f)
                    SetUpright(new Vector3(YawAxis == 0 ? 1 : 0, YawAxis == 1 ? 1 : 0, YawAxis == 2 ? 1 : 0) * Mathf.Sign(g[YawAxis]));   // first time held upright
                var pitch = HasUpright ? PitchFromGravity(g) : float.NaN;
                if (!float.IsNaN(pitch))
                {
                    frame.hasPitch = true;
                    frame.pitch = pitch;
                }
            }
            recent.Enqueue((now, frame.aimDelta));
            while (recent.Count > 0 && now - recent.Peek().time > StrokeWindow) recent.Dequeue();
            if (imu.FromNetwork)
            {
                if (modelSwing)
                {
                    lastSwingAt = now;
                    frame.swing = true;
                    frame.swingStrength = modelStrength;
                    var fromTurn = StrokeFromTurn(SumRecent(), modelStrength);
                    frame.swingStroke = fromTurn != Vector2.zero ? fromTurn : modelStroke;
                    LastStroke = frame.swingStroke;
                    Swings++;
                }
            }
            else if (peak > SwingThreshold && now - lastSwingAt > 0.5f) // One swing per shake, not one per packet.
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
        bool showPanel;

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

        void Awake()
        {
            imu = new ImuReceiver();
            var forward = PlayerPrefs.GetFloat("FlyAI.StickForward", 1f) < 0f ? -1f : 1f;
            var turn = PlayerPrefs.GetFloat("FlyAI.StickTurn", 1f) < 0f ? -1f : 1f;
            if (!PlayerPrefs.HasKey("FlyAI.StickSwap"))
            {
                // Saved before the axes were swapped: each flip belonged to the stick axis that now drives the other control.
                (forward, turn) = (turn, forward);
                PlayerPrefs.SetInt("FlyAI.StickSwap", 1);
                PlayerPrefs.SetFloat("FlyAI.StickForward", forward);
                PlayerPrefs.SetFloat("FlyAI.StickTurn", turn);
                PlayerPrefs.Save();
            }
            ImuSwatter.StickForwardSign = forward;
            ImuSwatter.StickTurnSign = turn;
            ImuSwatter.StickSwapAxes = PlayerPrefs.GetInt("FlyAI.StickSwap", 1) == 1;
            ImuSwatter.YawAxis = Mathf.Clamp(PlayerPrefs.GetInt("FlyAI.YawAxis", ImuSwatter.YawAxis), 0, 2);
            ImuSwatter.PitchAxis = Mathf.Clamp(PlayerPrefs.GetInt("FlyAI.PitchAxis", ImuSwatter.PitchAxis), 0, 2);
            ImuSwatter.YawSign = PlayerPrefs.GetFloat("FlyAI.YawSign", ImuSwatter.YawSign) < 0f ? -1f : 1f;
            ImuSwatter.PitchSign = PlayerPrefs.GetFloat("FlyAI.PitchSign", ImuSwatter.PitchSign) < 0f ? -1f : 1f;
            if (PlayerPrefs.HasKey("FlyAI.UprightX"))
            {
                ImuSwatter.Upright = new Vector3(PlayerPrefs.GetFloat("FlyAI.UprightX"), PlayerPrefs.GetFloat("FlyAI.UprightY"), PlayerPrefs.GetFloat("FlyAI.UprightZ"));
                ImuSwatter.HasUpright = ImuSwatter.Upright.sqrMagnitude > 0.25f;
            }
        }
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
            if (keys.f1Key.wasPressedThisFrame)
                PlayerPrefs.SetInt("FlyAI.StickSwap", (ImuSwatter.StickSwapAxes = !ImuSwatter.StickSwapAxes) ? 1 : 0);
            if (keys.f2Key.wasPressedThisFrame) showPanel = !showPanel;
            if (keys.f3Key.wasPressedThisFrame) PlayerPrefs.SetFloat("FlyAI.StickForward", ImuSwatter.StickForwardSign = -ImuSwatter.StickForwardSign);
            if (keys.f4Key.wasPressedThisFrame) PlayerPrefs.SetFloat("FlyAI.StickTurn", ImuSwatter.StickTurnSign = -ImuSwatter.StickTurnSign);
            if (keys.f5Key.wasPressedThisFrame) ImuSwatter.Directional = !ImuSwatter.Directional;
            if (keys.f6Key.wasPressedThisFrame) PlayerPrefs.SetInt("FlyAI.YawAxis", ImuSwatter.YawAxis = (ImuSwatter.YawAxis + 1) % 3);
            if (keys.f7Key.wasPressedThisFrame) PlayerPrefs.SetInt("FlyAI.PitchAxis", ImuSwatter.PitchAxis = (ImuSwatter.PitchAxis + 1) % 3);
            if (keys.f8Key.wasPressedThisFrame) PlayerPrefs.SetFloat("FlyAI.YawSign", ImuSwatter.YawSign = -ImuSwatter.YawSign);
            if (keys.f9Key.wasPressedThisFrame) PlayerPrefs.SetFloat("FlyAI.PitchSign", ImuSwatter.PitchSign = -ImuSwatter.PitchSign);
            if (keys.f11Key.wasPressedThisFrame) ImuSwatter.SwingThreshold = Mathf.Max(2f, ImuSwatter.SwingThreshold - 2f);
            if (keys.f12Key.wasPressedThisFrame) ImuSwatter.SwingThreshold += 2f;
            if (swatter != null && swatter.Swings != lastSwings) { lastSwings = swatter.Swings; lastSwingAt = Time.time; }
        }

        void OnGUI()
        {
            if (!showPanel || imu == null || imu.Packets == 0) return; // Hidden unless F2 opens it.
            if (style == null) style = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true };
            var peak = imu.Peak;
            var axes = "xyz";
            var text = "<b>Motion sensor test</b>  " + (imu.Live ? "<color=#6f6>connected</color>" : "<color=#f66>no data</color>") + "  packets " + imu.Packets + "\n" +
                       $"gyro  x {imu.Gyro.x,6:0.00}  y {imu.Gyro.y,6:0.00}  z {imu.Gyro.z,6:0.00} rad/s\n" +
                       $"accel {imu.Linear.magnitude,5:0.0} m/s²   peak {peak,5:0.0}   swing above {ImuSwatter.SwingThreshold:0} (F11/F12)\n" +
                       "sensor control: " + (swatter != null ? "<color=#6f6>ON</color>" : "off") + " (F10)   yaw = gyro " + axes[ImuSwatter.YawAxis] + (ImuSwatter.YawSign < 0 ? " flipped" : "") + " (F6/F8)   pitch = gyro " + axes[ImuSwatter.PitchAxis] + (ImuSwatter.PitchSign < 0 ? " flipped" : "") + " (F7/F9)\n" +
                       $"stick x {imu.StickValue.x:0.00} y {imu.StickValue.y:0.00} -> forward {ImuSwatter.StickForward(imu.StickValue):0.00} turn {ImuSwatter.StickTurn(imu.StickValue):0.00} " +
                       $"(F1 swap{(ImuSwatter.StickSwapAxes ? "ped" : "")}, F3/F4 flip)   swings " + (swatter != null ? swatter.Swings : 0) + (Time.time - lastSwingAt < 0.6f ? "   <color=#ff6><b>SWING!</b></color>" : "") + "\n" +
                       (imu.TryGravity(out var down) ? $"gravity {down.x:0.00} {down.y:0.00} {down.z:0.00}  tilt {ImuSwatter.PitchFromGravity(down):0} deg (hold the stick press upright to reset)\n" : "") +
                       "directional swings " + (ImuSwatter.Directional ? "<color=#6f6>ON</color>" : "off") + " (F5)   last swing " + (swatter != null && swatter.Swings > 0 ? ImuSwatter.Describe(swatter.LastStroke) : "-");
            var rect = new Rect(12f, Screen.height * 0.5f - 40f, 700f, 130f);
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
