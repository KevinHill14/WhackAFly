using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FlyAI
{
    // The fly's actual neural network, drawn in the bottom right as a fly's head. Every dot is one of the 1,602
    // neurons the brain simulates. A neuron that is not firing is faint and clear; when it fires it flashes red and
    // fades. Eyes hold the looming detectors, the middle holds the interneurons, and the descending neurons that
    // trigger the escape (including the Giant Fiber) run down the neck. It needs the brain server to be running.
    // It does not depend on the debug panel. Button at the bottom right, or press X.
    public class NeuralView : MonoBehaviour
    {
        const int TextureSize = 512;
        const float FadePerSecond = 2.6f;

        public static NeuralView Current { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Current = null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Current != null) return;
            var go = new GameObject("NeuralView");
            DontDestroyOnLoad(go);
            Current = go.AddComponent<NeuralView>();
        }

        bool open = true;   // On by default; X or the button hides it.
        string[] groupNames;
        int[] groupIds;
        Vector2[] positions;    // In the head's own space, -1 to 1.
        float[] sizes;          // Dot radius in texture pixels.
        float[] glow;
        List<int>[] members;
        Texture2D head;
        Texture2D dot;
        GUIStyle small;
        System.Random random = new System.Random(11);
        int firing;

        void OnDestroy()
        {
            if (head != null) Destroy(head);
            if (dot != null) Destroy(dot);
            UiHit.Clear("neural");
        }

        // Called with each decision from the brain. Live brains say exactly which neurons fired; the fallback table
        // only gives counts per group, so then the same number of neurons in that group flash.
        public void Feed(int[] active, int[] spikes)
        {
            if (glow == null) return;
            if (active != null && active.Length > 0)
            {
                foreach (var index in active)
                    if (index >= 0 && index < glow.Length) glow[index] = 1f;
                return;
            }
            if (spikes == null) return;
            for (var group = 0; group < spikes.Length && group < members.Length; group++)
            {
                var list = members[group];
                for (var i = 0; i < spikes[group] && i < list.Count; i++) glow[list[random.Next(list.Count)]] = 1f;
            }
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.xKey.wasPressedThisFrame && !MainMenu.Active) open = !open;

            var client = FlyAgent.BrainClient;
            if (client != null && client.NeuronGroups != null && (groupIds == null || groupIds.Length != client.NeuronGroups.Length))
                Build(client.NeuronGroups, client.GroupNames);

            if (glow == null) return;
            var count = 0;
            for (var i = 0; i < glow.Length; i++)
            {
                if (glow[i] <= 0f) continue;
                glow[i] = Mathf.Max(0f, glow[i] - Time.unscaledDeltaTime * FadePerSecond);
                if (glow[i] > 0.05f) count++;
            }
            firing = count;
        }

        // --- Layout -------------------------------------------------------------------------------------

        void Build(int[] ids, string[] names)
        {
            groupIds = ids;
            groupNames = names;
            var n = ids.Length;
            positions = new Vector2[n];
            sizes = new float[n];
            glow = new float[n];
            members = new List<int>[names.Length];
            for (var g = 0; g < names.Length; g++) members[g] = new List<int>();

            var leftEye = new List<int>();
            var rightEye = new List<int>();
            var middle = new List<int>();
            var leftEar = new List<int>();
            var rightEar = new List<int>();
            var upper = new List<int>();
            var dnSlots = new Dictionary<string, int> { { "DNp01", 0 }, { "DNp02", 1 }, { "DNp04", 2 }, { "DNp06", 3 }, { "DNp11", 4 } };
            for (var i = 0; i < n; i++)
            {
                var group = ids[i];
                if (group < 0 || group >= names.Length) continue;
                members[group].Add(i);
                var name = names[group];
                if (name.StartsWith("DNp"))
                {
                    var slot = dnSlots.TryGetValue(name.Substring(0, 5), out var s) ? s : 4;
                    var side = name.EndsWith("_L") ? -1f : 1f;
                    positions[i] = new Vector2(side * 0.1f, 0.4f + slot * 0.09f);
                    sizes[i] = slot == 0 ? 7f : 5f; // The Giant Fiber is the biggest.
                }
                else if ((name.StartsWith("LPLC2") || name.StartsWith("LC4")) && name.EndsWith("_L")) leftEye.Add(i);
                else if ((name.StartsWith("LPLC2") || name.StartsWith("LC4")) && name.EndsWith("_R")) rightEye.Add(i);
                else if (name == "LPLC1") (i % 2 == 0 ? leftEye : rightEye).Add(i);
                else if (name == "JO_L") leftEar.Add(i);
                else if (name == "JO_R") rightEar.Add(i);
                else if (name == "other_inter") middle.Add(i);
                else upper.Add(i);
            }

            var seed = new System.Random(7);
            Shuffle(leftEye, seed);
            Shuffle(rightEye, seed);
            Shuffle(middle, seed);
            Place(leftEye, new Vector2(-0.52f, -0.2f), 0.31f, 0.43f, 0.2f);
            Place(rightEye, new Vector2(0.52f, -0.2f), 0.31f, 0.43f, -0.2f);
            Place(middle, new Vector2(0f, -0.2f), 0.14f, 0.5f, 0f);
            Shuffle(leftEar, seed);
            Shuffle(rightEar, seed);
            Place(leftEar, new Vector2(-0.3f, -0.82f), 0.2f, 0.1f, 0.3f);
            Place(rightEar, new Vector2(0.3f, -0.82f), 0.2f, 0.1f, -0.3f);
            Place(upper, new Vector2(0f, -0.55f), 0.1f, 0.08f, 0f);
            foreach (var i in leftEye) sizes[i] = 1.9f;
            foreach (var i in rightEye) sizes[i] = 1.9f;
            foreach (var i in middle) sizes[i] = 1.7f;
            foreach (var i in leftEar) sizes[i] = 1.8f;
            foreach (var i in rightEar) sizes[i] = 1.8f;
            foreach (var i in upper) sizes[i] = 3f;

            BuildTextures();
        }

        static void Shuffle(List<int> list, System.Random random)
        {
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        // Evenly spread points inside an ellipse (a sunflower pattern).
        void Place(List<int> list, Vector2 centre, float rx, float ry, float tilt)
        {
            var cos = Mathf.Cos(tilt);
            var sin = Mathf.Sin(tilt);
            for (var k = 0; k < list.Count; k++)
            {
                var radius = Mathf.Sqrt((k + 0.5f) / list.Count);
                var angle = k * 2.39996f;
                var x = Mathf.Cos(angle) * rx * radius;
                var y = Mathf.Sin(angle) * ry * radius;
                positions[list[k]] = centre + new Vector2(x * cos - y * sin, x * sin + y * cos);
            }
        }

        // --- The head picture (drawn once) ----------------------------------------------------------------

        static Vector2 ToPixel(Vector2 p) => new Vector2((p.x * 0.5f + 0.5f) * TextureSize, (p.y * 0.5f + 0.5f) * TextureSize);

        void BuildTextures()
        {
            if (head != null) Destroy(head);
            if (dot == null)
            {
                dot = new Texture2D(32, 32, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "Neuron" };
                var pixels = new Color32[32 * 32];
                for (var y = 0; y < 32; y++)
                    for (var x = 0; x < 32; x++)
                    {
                        var d = new Vector2(x - 15.5f, y - 15.5f).magnitude / 16f;
                        var core = Mathf.Clamp01((0.45f - d) * 12f);
                        var halo = Mathf.Clamp01(1f - d) * 0.35f;
                        pixels[y * 32 + x] = new Color32(255, 255, 255, (byte)(Mathf.Max(core, halo) * 255f));
                    }
                dot.SetPixels32(pixels);
                dot.Apply();
            }

            var canvas = new MenuArt.Canvas(new Rect(0f, 0f, TextureSize, TextureSize), 1f);
            var half = TextureSize * 0.5f;
            Vector2 P(float x, float y) => ToPixel(new Vector2(x, y));
            var outline = MenuArt.C(0.82f, 0.78f, 0.7f, 0.9f);

            canvas.Fill(MenuArt.RoundRect(half, half, half - 2f, half - 2f, 26f), MenuArt.C(0.05f, 0.06f, 0.08f, 0.82f));

            // Proboscis and neck, then the head and its two big compound eyes.
            var neck = P(0f, 0.55f);
            canvas.Fill(MenuArt.Capsule(neck.x, neck.y, neck.x, P(0f, 0.76f).y, 26f), MenuArt.C(0.28f, 0.25f, 0.22f, 0.55f));
            var lip = P(0f, 0.86f);
            canvas.Fill(MenuArt.Ellipse(lip.x, lip.y, 40f, 26f), MenuArt.C(0.32f, 0.27f, 0.24f, 0.7f));
            canvas.Stroke(MenuArt.Ellipse(lip.x, lip.y, 40f, 26f), 2f, outline);
            var headCentre = P(0f, -0.24f);
            var headShape = MenuArt.Ellipse(headCentre.x, headCentre.y, 0.9f * half, 0.7f * half);
            canvas.Fill(headShape, MenuArt.C(0.5f, 0.46f, 0.4f, 0.28f));
            canvas.Stroke(headShape, 3f, outline);
            foreach (var side in new[] { -1f, 1f })
            {
                var eye = P(side * 0.52f, -0.2f);
                var eyeShape = MenuArt.Ellipse(eye.x, eye.y, 0.36f * half, 0.5f * half, side * 0.2f);
                canvas.Fill(eyeShape, MenuArt.C(0.55f, 0.1f, 0.1f, 0.38f));
                canvas.Stroke(eyeShape, 2.5f, MenuArt.C(0.85f, 0.4f, 0.35f, 0.9f));

                // Antennae and their bristles on top.
                var ant = P(side * 0.1f, -0.66f);
                canvas.Fill(MenuArt.Ellipse(ant.x, ant.y, 11f, 17f, side * 0.4f), MenuArt.C(0.55f, 0.5f, 0.42f, 0.85f));
                canvas.Fill(MenuArt.Capsule(ant.x, ant.y - 14f, ant.x + side * 28f, ant.y - 34f, 1.6f), outline);
            }
            // Three small simple eyes (ocelli).
            foreach (var o in new[] { new Vector2(0f, -0.86f), new Vector2(-0.07f, -0.8f), new Vector2(0.07f, -0.8f) })
            {
                var px = ToPixel(o);
                canvas.Fill(MenuArt.Ellipse(px.x, px.y, 5f, 5f), MenuArt.C(0.9f, 0.8f, 0.6f, 0.9f));
            }

            // Faint, clear neurons.
            var faint = MenuArt.C(0.82f, 0.88f, 0.95f, 0.3f);
            for (var i = 0; i < positions.Length; i++)
            {
                var px = ToPixel(positions[i]);
                canvas.Fill(MenuArt.Ellipse(px.x, px.y, sizes[i], sizes[i]), faint);
            }
            head = canvas.ToTexture("Neural View");
        }

        // --- Screen -------------------------------------------------------------------------------------

        void OnGUI()
        {
            if (MainMenu.Active || FlyAgent.Current == null) return;
            if (small == null) small = new GUIStyle(GUI.skin.label) { fontSize = 12, richText = true };

            var button = new Rect(Screen.width - 166f, Screen.height - 42f, 150f, 28f);
            UiHit.Set("neural", button);
            if (GUI.Button(button, open ? "Neural net  [X] hide" : "Neural net  [X] show")) open = !open;
            if (!open) return;

            var size = Mathf.Min(Screen.height * 0.52f, 440f);
            var panel = new Rect(Screen.width - size - 16f, Screen.height - size - 50f, size, size);
            if (head == null || glow == null)
            {
                GUI.Label(new Rect(panel.x, panel.y + size - 24f, size, 22f), "Waiting for the fly brain...", small);
                return;
            }

            GUI.DrawTexture(panel, head);
            var scale = size / TextureSize;
            var previous = GUI.color;
            for (var i = 0; i < glow.Length; i++)
            {
                if (glow[i] <= 0.02f) continue;
                var centre = panel.position + ToPixel(positions[i]) * scale;
                var radius = sizes[i] * scale * (2.2f + glow[i] * 0.8f);
                GUI.color = new Color(1f, 0.12f + (1f - glow[i]) * 0.1f, 0.1f, Mathf.Clamp01(glow[i] * 1.2f));
                GUI.DrawTexture(new Rect(centre.x - radius, centre.y - radius, radius * 2f, radius * 2f), dot);
            }
            GUI.color = previous;

            small.normal.textColor = new Color(0.9f, 0.92f, 0.95f);
            GUI.Label(new Rect(panel.x + 14f, panel.y + 8f, size, 20f), $"Fly brain: {glow.Length:N0} neurons", small);
            GUI.Label(new Rect(panel.xMax - 130f, panel.y + 8f, 120f, 20f), $"<color=#FF6A5A>{firing} firing</color>", small);
            GUI.Label(new Rect(panel.x + 12f, panel.y + size * 0.72f, size * 0.3f, 36f), "Left eye\n(looming)", small);
            GUI.Label(new Rect(panel.xMax - size * 0.3f, panel.y + size * 0.72f, size * 0.3f, 36f), "Right eye\n(looming)", small);
            GUI.Label(new Rect(panel.center.x + size * 0.1f, panel.y + size * 0.72f, size * 0.4f, 36f), "Neck: escape\nneurons", small);
        }
    }
}
