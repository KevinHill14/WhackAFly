using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace FlyAI
{
    // Trace log for hunting delays between the laptop and the Pi. On unless FLYAI_TRACE=0.
    // Writes WhackAFlyGame/Logs/flyai_trace_<time>.log: every message each way with its size and how long it waited in
    // a queue, the clock offset to the Pi (so one-way trip times can be worked out), and once a second the frame rate,
    // the queues and the room's lights. Safe to call from any thread.
    public static class NetTrace
    {
        public static bool On { get; private set; }
        public static string Path { get; private set; }

        static readonly Stopwatch clock = Stopwatch.StartNew();
        static readonly object gate = new object();
        static readonly Dictionary<string, int[]> counts = new Dictionary<string, int[]>();
        static readonly Dictionary<string, double> peaks = new Dictionary<string, double>();
        static StreamWriter writer;
        static float nextSummary;
        static int frames;
        static volatile int frame;

        public static double NowMs => clock.Elapsed.TotalMilliseconds;
        static string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

        // Main thread, once.
        public static void Start()
        {
            if (writer != null || Environment.GetEnvironmentVariable("FLYAI_TRACE") == "0") return;
            try
            {
                var dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..", "Logs"));
                Directory.CreateDirectory(dir);
                Path = System.IO.Path.Combine(dir, $"flyai_trace_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                writer = new StreamWriter(Path, false, new UTF8Encoding(false), 1 << 16);
                On = true;
                Event($"START unity={Application.unityVersion} pipeline={(GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.name : "built-in")} " +
                      $"screen={Screen.width}x{Screen.height} vsync={QualitySettings.vSyncCount} targetFps={Application.targetFrameRate} " +
                      $"quality={QualitySettings.names[QualitySettings.GetQualityLevel()]} pixelLights={QualitySettings.pixelLightCount} shadows={QualitySettings.shadows}");
                UnityEngine.Debug.Log($"[FlyAI] Trace log: {Path}");
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[FlyAI] Trace log off: {e.Message}");
                On = false;
            }
        }

        public static void Event(string text)
        {
            if (!On) return;
            lock (gate) writer?.WriteLine($"{F(NowMs)} f{frame} {text}");   // null once stopped
        }

        public static void Count(string key, int bytes)
        {
            if (!On) return;
            lock (gate)
            {
                if (!counts.TryGetValue(key, out var c)) counts[key] = c = new int[2];
                c[0]++;
                c[1] += bytes;
            }
        }

        public static void Peak(string key, double value)
        {
            if (!On) return;
            lock (gate)
                if (!peaks.TryGetValue(key, out var p) || value > p) peaks[key] = value;
        }

        public static string Short(string text, int limit = 180) =>
            text.Length <= limit ? text : text.Substring(0, limit) + $"...(+{text.Length - limit})";

        // Main thread, every frame (from BackendClient.Update).
        public static void Frame(int incoming, int outgoing, bool connected)
        {
            if (!On) return;
            frame = Time.frameCount;
            frames++;
            var ms = Time.unscaledDeltaTime * 1000f;
            Peak("frame_ms", ms);
            if (ms > 50f) Event($"SLOW-FRAME {F(ms)}ms");
            if (Time.unscaledTime < nextSummary) return;
            nextSummary = Time.unscaledTime + 1f;

            // What lighting costs to draw: every light that is on, and the ones casting shadows.
            int lights = 0, shadowLights = 0, realtime = 0;
            foreach (var light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (!light.enabled || !light.gameObject.activeInHierarchy || light.intensity <= 0f) continue;
                lights++;
                if (light.shadows != LightShadows.None) shadowLights++;
                if (light.lightmapBakeType != LightmapBakeType.Baked) realtime++;
            }
            var events = RoomEvents.Current;
            var room = events != null
                ? $"lightning={F(events.LightningLevel)} surge={F(events.Surge)} headlights={F(events.HeadlightLevel)} power={F(events.Power)}"
                : "no room events";
            var sb = new StringBuilder();
            lock (gate)
            {
                sb.Append($"SUM fps={frames} connected={connected} in_queue={incoming} out_queue={outgoing} lights={lights} " +
                          $"shadow_lights={shadowLights} realtime_lights={realtime} {room} |");
                foreach (var kv in counts) sb.Append($" {kv.Key}={kv.Value[0]}/{kv.Value[1]}B");
                sb.Append(" | peak");
                foreach (var kv in peaks) sb.Append($" {kv.Key}={F(kv.Value)}");
                counts.Clear();
                peaks.Clear();
            }
            frames = 0;
            Event(sb.ToString());
            lock (gate) writer?.Flush();
        }

        public static void Stop()
        {
            if (!On) return;
            Event("STOP");
            lock (gate)
            {
                writer.Flush();
                writer.Dispose();
                writer = null;
                On = false;
            }
        }
    }
}
