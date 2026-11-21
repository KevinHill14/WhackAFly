using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FlyAI
{
    // A small amount of age: stains, floor scuffs and cobwebs, sized and placed from the room.
    public static class RoomWear
    {
        const float DefaultWallThickness = 0.1f;

        public static void Build(Transform parent, RoomData room, RoomAssets assets)
        {
            var outline = RoomVisuals.Outline(room);
            if (outline.Count < 3) return;
            var seed = 41;
            foreach (var point in outline) seed = unchecked(seed * 43 + Mathf.RoundToInt(point.x * 100f) + Mathf.RoundToInt(point.z * 17f));
            var random = new System.Random(seed);
            var group = new GameObject("Room Wear").transform;
            group.SetParent(parent, false);

            var area = room.floor.area;
            var stain = Decal(CreateStainTexture(random), new Color(0.15f, 0.11f, 0.07f, 0.42f), assets);
            var scuff = Decal(CreateScuffTexture(random), new Color(0.07f, 0.06f, 0.05f, 0.34f), assets);
            var water = Decal(CreateStainTexture(random), new Color(0.32f, 0.26f, 0.17f, 0.32f), assets);
            var web = Decal(CreateWebTexture(), new Color(0.72f, 0.72f, 0.68f, 0.5f), assets);

            AddWallStains(group, room, outline, stain, Mathf.Clamp(Mathf.RoundToInt(area / 30f), 1, 4), random);
            AddFlatMarks(group, outline, 0.003f, 90f, scuff, Mathf.Clamp(Mathf.RoundToInt(area / 25f), 1, 4), 0.6f, 1.4f, random);
            AddFlatMarks(group, outline, room.ceilingHeight - 0.006f, -90f, water, Mathf.Clamp(Mathf.RoundToInt(area / 60f), 1, 2), 0.7f, 1.3f, random);
            AddCobwebs(group, room, outline, web, Mathf.Clamp(Mathf.RoundToInt(area / 40f), 1, 3), random);
        }

        static Material Decal(Texture2D texture, Color color, RoomAssets assets)
        {
            assets.Keep(texture);
            var material = RoomProps.ClearSurface(color, assets);
            material.SetFloat("_Smoothness", 0.05f);
            material.SetTexture("_BaseMap", texture);
            return material;
        }

        static void AddQuad(Transform parent, string name, Vector3 position, Quaternion rotation, Vector2 size, Material material)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = name;
            Object.DestroyImmediate(quad.GetComponent<Collider>());
            quad.transform.SetParent(parent, false);
            quad.transform.SetPositionAndRotation(position, rotation);
            quad.transform.localScale = new Vector3(size.x, size.y, 1f);
            var renderer = quad.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
        }

        static float Range(System.Random random, float low, float high) => Mathf.Lerp(low, high, (float)random.NextDouble());

        static bool Inside(List<Vector3> outline, Vector3 point)
        {
            var inside = false;
            for (int i = 0, j = outline.Count - 1; i < outline.Count; j = i++)
            {
                var a = outline[i];
                var b = outline[j];
                if ((a.z > point.z) != (b.z > point.z) && point.x < (b.x - a.x) * (point.z - a.z) / (b.z - a.z) + a.x)
                    inside = !inside;
            }
            return inside;
        }

        static void AddWallStains(Transform parent, RoomData room, List<Vector3> outline, Material material, int count, System.Random random)
        {
            var usable = new List<WallData>();
            foreach (var wall in room.walls)
                if ((RoomLoader.ToUnity(wall.end[0], 0f, wall.end[1]) - RoomLoader.ToUnity(wall.start[0], 0f, wall.start[1])).magnitude > 1.2f)
                    usable.Add(wall);
            if (usable.Count == 0) return;

            for (var i = 0; i < count; i++)
            {
                var wall = usable[random.Next(usable.Count)];
                var start = RoomLoader.ToUnity(wall.start[0], 0f, wall.start[1]);
                var end = RoomLoader.ToUnity(wall.end[0], 0f, wall.end[1]);
                var along = (end - start).normalized;
                var middle = (start + end) / 2f;
                var inward = new Vector3(-along.z, 0f, along.x);
                if (!Inside(outline, middle + inward * 0.25f)) inward = -inward;
                if (!Inside(outline, middle + inward * 0.25f)) continue;

                var length = (end - start).magnitude;
                var width = Mathf.Min(Range(random, 0.55f, 1.15f), length * 0.6f);
                var height = Mathf.Min(Range(random, 0.7f, 1.4f), wall.height * 0.5f);
                var offset = Range(random, width * 0.5f, length - width * 0.5f);
                var lowOnWall = random.NextDouble() < 0.6;
                var y = lowOnWall ? height * 0.5f + 0.14f : wall.height - height * 0.5f - 0.13f;
                var thickness = wall.thickness > 0f ? wall.thickness : DefaultWallThickness;
                var position = start + along * offset + inward * (thickness * 0.5f + 0.004f) + Vector3.up * y;
                if (RoomWindow.IsValid(room.window) && room.window.wall == wall.node)
                {
                    var toWindow = position - RoomLoader.ToUnity(room.window.center);
                    if (Mathf.Abs(Vector3.Dot(toWindow, along)) < (room.window.width + width) / 2f + 0.1f
                        && Mathf.Abs(toWindow.y) < (room.window.height + height) / 2f + 0.1f) continue;
                }
                AddQuad(parent, "Wall Stain", position, Quaternion.LookRotation(-inward), new Vector2(width, height), material);
            }
        }

        static void AddFlatMarks(Transform parent, List<Vector3> outline, float y, float tilt, Material material, int count, float small, float large, System.Random random)
        {
            var bounds = new Bounds(outline[0], Vector3.zero);
            foreach (var point in outline) bounds.Encapsulate(point);
            for (var i = 0; i < count; i++)
            {
                for (var attempt = 0; attempt < 24; attempt++)
                {
                    var point = new Vector3(Range(random, bounds.min.x, bounds.max.x), y, Range(random, bounds.min.z, bounds.max.z));
                    if (!Inside(outline, point)) continue;
                    var size = Range(random, small, large);
                    AddQuad(parent, "Surface Mark", point, Quaternion.Euler(tilt, Range(random, 0f, 360f), 0f), new Vector2(size, size * Range(random, 0.6f, 1f)), material);
                    break;
                }
            }
        }

        static void AddCobwebs(Transform parent, RoomData room, List<Vector3> outline, Material material, int count, System.Random random)
        {
            var area = 0f;
            for (var i = 0; i < outline.Count; i++)
            {
                var p = outline[i];
                var q = outline[(i + 1) % outline.Count];
                area += p.x * q.z - q.x * p.z;
            }
            var corners = new List<Vector3>();
            var directions = new List<Vector3>();
            for (var i = 0; i < outline.Count; i++)
            {
                var previous = outline[(i + outline.Count - 1) % outline.Count];
                var corner = outline[i];
                var next = outline[(i + 1) % outline.Count];
                var cross = (corner.x - previous.x) * (next.z - corner.z) - (corner.z - previous.z) * (next.x - corner.x);
                if (cross * area <= 0f) continue;
                corners.Add(corner);
                directions.Add(((previous - corner).normalized + (next - corner).normalized).normalized);
            }

            for (var i = 0; i < count && corners.Count > 0; i++)
            {
                var index = random.Next(corners.Count);
                var size = Range(random, 0.45f, 0.72f);
                var position = corners[index] + directions[index] * (size * 0.42f + 0.1f) + Vector3.up * (room.ceilingHeight - size * 0.42f - 0.12f);
                AddQuad(parent, "Cobweb", position, Quaternion.LookRotation(directions[index] * -1f + Vector3.up * 0.25f), new Vector2(size, size), material);
                corners.RemoveAt(index);
                directions.RemoveAt(index);
            }
        }

        static Texture2D NewTexture(string name) =>
            new Texture2D(128, 128, TextureFormat.RGBA32, true) { name = name, wrapMode = TextureWrapMode.Clamp };

        static Texture2D Finish(Texture2D texture, Color32[] pixels)
        {
            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }

        static Texture2D CreateStainTexture(System.Random random)
        {
            var texture = NewTexture("Stain");
            var pixels = new Color32[128 * 128];
            var ox = Range(random, 0f, 50f);
            var oy = Range(random, 0f, 50f);
            for (var y = 0; y < 128; y++)
                for (var x = 0; x < 128; x++)
                {
                    var u = x / 63.5f - 1f;
                    var v = y / 63.5f - 1f;
                    var edge = Mathf.Clamp01(1f - Mathf.Sqrt(u * u + v * v));
                    var cloud = Mathf.PerlinNoise(u * 2.2f + ox, v * 2.2f + oy) * 0.7f + Mathf.PerlinNoise(u * 6f + oy, v * 6f + ox) * 0.3f;
                    var alpha = Mathf.Clamp01((cloud - 0.42f) * 3f) * Mathf.SmoothStep(0f, 0.6f, edge);
                    pixels[y * 128 + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            return Finish(texture, pixels);
        }

        static Texture2D CreateScuffTexture(System.Random random)
        {
            var texture = NewTexture("Scuff");
            var pixels = new Color32[128 * 128];
            var ox = Range(random, 0f, 50f);
            for (var y = 0; y < 128; y++)
                for (var x = 0; x < 128; x++)
                {
                    var u = x / 63.5f - 1f;
                    var v = y / 63.5f - 1f;
                    var edge = Mathf.Clamp01(1f - Mathf.Sqrt(u * u + v * v));
                    var streak = Mathf.PerlinNoise(u * 1.1f + ox, v * 16f + ox);
                    var alpha = Mathf.Clamp01((streak - 0.5f) * 3.4f) * Mathf.SmoothStep(0f, 0.7f, edge);
                    pixels[y * 128 + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            return Finish(texture, pixels);
        }

        static Texture2D CreateWebTexture()
        {
            var texture = NewTexture("Cobweb");
            var pixels = new Color32[128 * 128];
            for (var y = 0; y < 128; y++)
                for (var x = 0; x < 128; x++)
                {
                    var u = x / 127f;
                    var v = 1f - y / 127f;
                    var distance = Mathf.Sqrt(u * u + v * v);
                    var angle = Mathf.Atan2(v, u) * 10f / (Mathf.PI * 0.5f);
                    var spoke = Mathf.Abs(angle - Mathf.Round(angle)) * distance;
                    var ring = Mathf.Abs(distance * 6f - Mathf.Round(distance * 6f)) / 6f;
                    var line = Mathf.Max(1f - spoke / 0.012f, 1f - ring / 0.006f);
                    var alpha = Mathf.Clamp01(line) * Mathf.Clamp01(1f - distance);
                    pixels[y * 128 + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            return Finish(texture, pixels);
        }
    }
}
