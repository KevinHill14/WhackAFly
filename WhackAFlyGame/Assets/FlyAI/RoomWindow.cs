using UnityEngine;
using UnityEngine.Rendering;

namespace FlyAI
{
    public class WindowRain : MonoBehaviour
    {
        public Material material;
        float offset;

        void Update()
        {
            offset = Mathf.Repeat(offset + Time.deltaTime * 1.45f, 1f);
            material.SetTextureOffset("_BaseMap", new Vector2(offset * 0.06f, offset));
        }
    }

    // Builds a framed window with a rainy night outside, from the size calculated for this room.
    public static class RoomWindow
    {
        public static bool IsValid(WindowData window) =>
            window != null && window.center != null && window.center.Length == 3
            && window.normal != null && window.normal.Length == 3 && window.width > 0f && window.height > 0f;

        public static int PaneColumns(float width) => Mathf.Clamp(Mathf.RoundToInt(width / 0.6f), 2, 4);

        // Returns the sky material so headlights and lightning can brighten it.
        public static Material Build(Transform parent, RoomData room, RoomTheme theme, RoomAssets assets)
        {
            var data = room.window;
            if (!IsValid(data) || data.source != "generated") return null;

            var holder = new GameObject("Generated Window").transform;
            holder.SetParent(parent, false);
            holder.SetPositionAndRotation(RoomLoader.ToUnity(data.center), Quaternion.LookRotation(RoomLoader.ToUnity(data.normal)));

            var frame = assets.Keep(RoomVisuals.LitMaterial(theme.trim, 0.34f));
            var seed = Mathf.RoundToInt(data.center[0] * 97f + data.center[2] * 61f);
            var sky = assets.Keep(new Material(Shader.Find("Universal Render Pipeline/Unlit")));
            sky.SetTexture("_BaseMap", assets.Keep(CreateSky(seed)));
            sky.SetColor("_BaseColor", Color.white);

            var w = data.width;
            var h = data.height;
            var bar = Mathf.Clamp(Mathf.Min(w, h) * 0.05f, 0.035f, 0.07f);
            const float depth = 0.05f;

            Pane(holder, 0.012f, w, h, sky);
            var rain = assets.Keep(RoomAtmosphere.CreateParticleMaterial(assets.Keep(CreateRain(seed)), true));
            rain.DisableKeyword("_SOFTPARTICLES_ON");
            rain.DisableKeyword("_FADING_ON");
            rain.SetColor("_BaseColor", new Color(0.72f, 0.8f, 0.95f, 0.5f));
            rain.SetTextureScale("_BaseMap", new Vector2(w / 0.7f, h / 0.7f));
            Pane(holder, 0.016f, w, h, rain);
            holder.gameObject.AddComponent<WindowRain>().material = rain;

            Part(holder, new Vector3(0f, h / 2f, depth / 2f), new Vector3(w + bar, bar, depth), frame);
            Part(holder, new Vector3(0f, -h / 2f, depth / 2f), new Vector3(w + bar, bar, depth), frame);
            Part(holder, new Vector3(-w / 2f, 0f, depth / 2f), new Vector3(bar, h + bar, depth), frame);
            Part(holder, new Vector3(w / 2f, 0f, depth / 2f), new Vector3(bar, h + bar, depth), frame);

            var columns = PaneColumns(w);
            for (var i = 1; i < columns; i++)
                Part(holder, new Vector3(-w / 2f + w * i / columns, 0f, depth * 0.4f), new Vector3(bar * 0.55f, h, depth * 0.7f), frame);
            Part(holder, new Vector3(0f, 0f, depth * 0.4f), new Vector3(w, bar * 0.55f, depth * 0.7f), frame);

            var sillDepth = Mathf.Clamp(w * 0.09f, 0.09f, 0.16f);
            Part(holder, new Vector3(0f, -h / 2f - bar * 0.7f, sillDepth / 2f), new Vector3(w + bar * 3f, bar * 0.75f, sillDepth), frame);
            return sky;
        }

        static void Pane(Transform parent, float z, float width, float height, Material material)
        {
            var pane = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Object.DestroyImmediate(pane.GetComponent<Collider>());
            pane.transform.SetParent(parent, false);
            pane.transform.localPosition = new Vector3(0f, 0f, z);
            pane.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            pane.transform.localScale = new Vector3(width, height, 1f);
            var renderer = pane.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        static void Part(Transform parent, Vector3 position, Vector3 scale, Material material)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(part.GetComponent<Collider>());
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localRotation = Quaternion.identity;
            part.transform.localScale = scale;
            var renderer = part.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
        }

        // A dark stormy sky with a tree line along the bottom.
        static Texture2D CreateSky(int seed)
        {
            const int width = 256;
            const int height = 192;
            var random = new System.Random(seed);
            var shift = (float)random.NextDouble() * 80f;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "Night Sky" };
            var pixels = new Color32[width * height];
            var top = new Color(0.012f, 0.018f, 0.04f);
            var horizon = new Color(0.07f, 0.1f, 0.16f);
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var u = x / (float)width;
                    var v = y / (float)height;
                    var clouds = Mathf.PerlinNoise(u * 3.4f + shift, v * 5.2f + shift) * 0.65f + Mathf.PerlinNoise(u * 9f + shift, v * 13f) * 0.35f;
                    var color = Color.Lerp(horizon, top, Mathf.Pow(v, 0.7f)) * Mathf.Lerp(0.55f, 1.5f, clouds);
                    var trees = 0.17f + Mathf.PerlinNoise(u * 5f + shift, 2.2f) * 0.13f + Mathf.PerlinNoise(u * 24f + shift, 6.1f) * 0.05f;
                    if (v < trees) color = new Color(0.004f, 0.006f, 0.009f);
                    color.a = 1f;
                    pixels[y * width + x] = color;
                }
            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }

        static Texture2D CreateRain(int seed)
        {
            const int size = 128;
            var random = new System.Random(seed + 7);
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, name = "Rain" };
            var pixels = new Color32[size * size];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 0);
            for (var drop = 0; drop < 42; drop++)
            {
                var x = random.Next(size);
                var y = random.Next(size);
                var length = 7 + random.Next(17);
                var strength = 70 + random.Next(150);
                for (var step = 0; step < length; step++)
                {
                    var fade = 1f - step / (float)length;
                    pixels[Mathf.RoundToInt(Mathf.Repeat(y + step, size)) % size * size + x] = new Color32(255, 255, 255, (byte)(strength * fade));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }
    }
}
