using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FlyAI
{
    // Frees generated materials, meshes and profiles when a room is replaced.
    public class RoomAssets : MonoBehaviour
    {
        public readonly List<Object> owned = new List<Object>();

        public T Keep<T>(T asset) where T : Object
        {
            owned.Add(asset);
            return asset;
        }

        void OnDestroy()
        {
            foreach (var asset in owned)
                if (asset != null) Destroy(asset);
        }
    }

    public class LightFlicker : MonoBehaviour
    {
        Light target;
        float baseIntensity;
        float seed;

        void Start()
        {
            target = GetComponent<Light>();
            baseIntensity = target.intensity;
            seed = transform.position.x * 13.1f + transform.position.z * 7.7f;
        }

        void Update()
        {
            var noise = Mathf.PerlinNoise(seed, Time.time * 7f) - 0.5f;
            target.intensity = baseIntensity * (1f + noise * 0.45f);
        }
    }

    // Builds the room's look from room.json: theme colours, enclosure, lights, fog and post-processing.
    public static class RoomVisuals
    {
        const float ShellOffset = 0.005f;
        const float ReferenceCeiling = 2.7f;
        const int MaxShadowLights = 6;
        const float FarClipMargin = 2f;

        public static void ApplyEnvironment(RoomTheme theme, Camera camera)
        {
            foreach (var light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (light.type == LightType.Directional) light.enabled = false;

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = theme.fog;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = theme.ambient * theme.ambientDim;
            RenderSettings.skybox = null;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.reflectionIntensity = 0f;
            DynamicGI.UpdateEnvironment();

            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = theme.fog;
            camera.nearClipPlane = 0.03f;
            var data = camera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.renderShadows = true;
            data.requiresDepthOption = CameraOverrideOption.On;
        }

        public static void Apply(Transform root, Transform meshRoot, RoomData room, RoomTheme theme, Camera camera)
        {
            var assets = root.gameObject.AddComponent<RoomAssets>();
            ApplyMaterials(meshRoot, room, theme, assets);
            BuildEnclosure(meshRoot, room, theme, assets);
            RoomSurfaces.BuildTrim(meshRoot, room, theme, assets);
            var rigs = BuildLights(root, room, theme, assets);
            BuildPostProcessing(root, theme, assets);
            var atmosphere = root.gameObject.AddComponent<RoomAtmosphere>();
            atmosphere.Setup(room, theme, rigs, assets);
            var events = root.gameObject.AddComponent<RoomEvents>();
            events.Setup(room, theme, assets, RoomWindow.Build(meshRoot, room, theme, assets));
            atmosphere.Bind(events);
            RoomWear.Build(meshRoot, room, assets);
            RoomDoor.Build(meshRoot, room, theme, assets);
            RoomAudio.BuildAmbience(root, room);
            camera.farClipPlane = RoomDiagonal(room) + FarClipMargin;
        }

        static void ApplyMaterials(Transform meshRoot, RoomData room, RoomTheme theme, RoomAssets assets)
        {
            var materials = new Dictionary<string, Material>();
            var mainAxis = RoomSurfaces.MainAxis(room);
            var labels = new Dictionary<string, string>();
            foreach (var item in room.objects) labels[item.node] = item.label;
            foreach (var renderer in meshRoot.GetComponentsInChildren<Renderer>(true))
            {
                var category = Category(renderer.transform, meshRoot, labels);
                if (!materials.TryGetValue(category, out var material))
                {
                    material = RoomSurfaces.MaterialFor(category, theme, assets);
                    materials[category] = material;
                }
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null && filter.sharedMesh.isReadable)
                {
                    var axis = RoomSurfaces.AxisFor(renderer.transform, meshRoot, category, room, mainAxis);
                    filter.sharedMesh = assets.Keep(RoomSurfaces.WithProjectedUVs(filter.sharedMesh, renderer.transform, category, axis));
                }
                var slots = renderer.sharedMaterials;
                for (var i = 0; i < slots.Length; i++) slots[i] = material;
                renderer.sharedMaterials = slots;
                renderer.shadowCastingMode = ShadowCastingMode.TwoSided;
                renderer.receiveShadows = true;
            }
        }

        static string Category(Transform part, Transform root, Dictionary<string, string> labels)
        {
            for (var t = part; t != null && t != root; t = t.parent)
            {
                var name = t.name;
                // Scanned objects use the label worked out by the Python pipeline.
                var hasLabel = labels.TryGetValue(name, out var label);
                if (!hasLabel)
                {
                    if (HasPrefix(name, "Wall") || HasPrefix(name, "Joint")) return "wall";
                    if (HasPrefix(name, "Floor")) return "floor";
                    if (HasPrefix(name, "Ceiling")) return "ceiling";
                    label = name.Split('_')[0].ToLowerInvariant();
                }
                switch (label)
                {
                    case "table":
                    case "desk":
                    case "counter":
                    case "island":
                    case "nightstand": return "table";
                    case "chair":
                    case "stool":
                    case "bench": return "seat";
                    case "sofa":
                    case "couch":
                    case "bed": return "soft";
                    case "storage":
                    case "shelf":
                    case "shelving":
                    case "bookshelf":
                    case "cabinet":
                    case "dresser": return "storage";
                    case "television":
                    case "tv":
                    case "refrigerator":
                    case "fridge":
                    case "oven":
                    case "stove":
                    case "dishwasher":
                    case "washer":
                    case "washerdryer":
                    case "sink":
                    case "toilet":
                    case "bathtub": return "appliance";
                }
            }
            return "other";
        }

        static bool HasPrefix(string name, string prefix) => name.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase);

        internal static Color ColorFor(string category, RoomTheme theme)
        {
            switch (category)
            {
                case "wall": return theme.wall;
                case "floor": return theme.floor;
                case "ceiling": return theme.ceiling;
                case "table": return theme.table;
                case "seat": return theme.seat;
                case "soft": return theme.soft;
                case "storage": return theme.storage;
                case "appliance": return theme.appliance;
                default: return theme.other;
            }
        }

        internal static float SmoothnessFor(string category)
        {
            switch (category)
            {
                case "table": return 0.3f;
                case "floor": return 0.26f;
                case "appliance": return 0.55f;
                case "soft": return 0.08f;
                default: return 0.16f;
            }
        }

        internal static Material LitMaterial(Color color, float smoothness)
        {
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", smoothness);
            material.SetFloat("_Metallic", 0f);
            return material;
        }

        internal static List<Vector3> Outline(RoomData room)
        {
            var flat = room.floor.polygonFlat;
            var points = new List<Vector3>();
            for (var i = 0; i + 1 < flat.Length; i += 2) points.Add(RoomLoader.ToUnity(flat[i], 0f, flat[i + 1]));
            return points;
        }

        static float RoomDiagonal(RoomData room)
        {
            var points = Outline(room);
            var bounds = new Bounds(points[0], Vector3.zero);
            foreach (var point in points) bounds.Encapsulate(point);
            bounds.Encapsulate(points[0] + Vector3.up * room.ceilingHeight);
            return bounds.size.magnitude;
        }

        // Closes gaps in the scan so the fog and light stay inside the room.
        static void BuildEnclosure(Transform parent, RoomData room, RoomTheme theme, RoomAssets assets)
        {
            var outline = Outline(room);
            if (outline.Count < 3) return;
            var area = 0f;
            for (var i = 0; i < outline.Count; i++)
            {
                var a = outline[i];
                var b = outline[(i + 1) % outline.Count];
                area += a.x * b.z - b.x * a.z;
            }
            var outwardSign = area > 0f ? -1f : 1f;

            var wallMesh = new MeshData();
            for (var i = 0; i < outline.Count; i++)
            {
                var a = outline[i];
                var b = outline[(i + 1) % outline.Count];
                var along = (b - a).normalized;
                var outward = new Vector3(-along.z, 0f, along.x) * outwardSign;
                var offset = outward * ShellOffset;
                var start = a + offset - along * ShellOffset;
                var end = b + offset + along * ShellOffset;
                var low = Vector3.down * 0.12f;
                var high = Vector3.up * (room.ceilingHeight + 0.04f);
                wallMesh.AddTwoSidedQuad(start + low, end + low, end + high, start + high, -outward);
            }
            var mainAxis = RoomSurfaces.MainAxis(room);
            AddShell(parent, "Wall Shell", wallMesh, "wall", mainAxis, theme, assets);

            var ceilingMesh = new MeshData();
            var height = Vector3.up * (room.ceilingHeight + 0.02f);
            foreach (var triangle in Triangulate(outline))
                ceilingMesh.AddTwoSidedTriangle(outline[triangle.a] + height, outline[triangle.b] + height, outline[triangle.c] + height, Vector3.down);
            AddShell(parent, "Ceiling Shell", ceilingMesh, "ceiling", mainAxis, theme, assets);
        }

        static void AddShell(Transform parent, string name, MeshData data, string category, Vector3 axis, RoomTheme theme, RoomAssets assets)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var plain = data.Build(name);
            go.AddComponent<MeshFilter>().sharedMesh = assets.Keep(RoomSurfaces.WithProjectedUVs(plain, go.transform, category, axis));
            Object.Destroy(plain);
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = RoomSurfaces.MaterialFor(category, theme, assets);
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;
        }

        static List<(int a, int b, int c)> Triangulate(List<Vector3> polygon)
        {
            var result = new List<(int, int, int)>();
            var indices = new List<int>();
            for (var i = 0; i < polygon.Count; i++) indices.Add(i);
            var area = 0f;
            for (var i = 0; i < polygon.Count; i++)
            {
                var p = polygon[i];
                var q = polygon[(i + 1) % polygon.Count];
                area += p.x * q.z - q.x * p.z;
            }
            var sign = Mathf.Sign(area);

            var guard = polygon.Count * polygon.Count;
            while (indices.Count > 3 && guard-- > 0)
            {
                var clipped = false;
                for (var i = 0; i < indices.Count; i++)
                {
                    var previous = indices[(i + indices.Count - 1) % indices.Count];
                    var current = indices[i];
                    var next = indices[(i + 1) % indices.Count];
                    if (Cross(polygon[previous], polygon[current], polygon[next]) * sign <= 0f) continue;
                    var blocked = false;
                    foreach (var other in indices)
                    {
                        if (other == previous || other == current || other == next) continue;
                        if (InTriangle(polygon[other], polygon[previous], polygon[current], polygon[next], sign))
                        {
                            blocked = true;
                            break;
                        }
                    }
                    if (blocked) continue;
                    result.Add((previous, current, next));
                    indices.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped) break;
            }
            if (indices.Count == 3) result.Add((indices[0], indices[1], indices[2]));
            return result;
        }

        static float Cross(Vector3 a, Vector3 b, Vector3 c) => (b.x - a.x) * (c.z - a.z) - (b.z - a.z) * (c.x - a.x);

        static bool InTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, float sign) =>
            Cross(a, b, p) * sign >= 0f && Cross(b, c, p) * sign >= 0f && Cross(c, a, p) * sign >= 0f;

        internal class MeshData
        {
            readonly List<Vector3> vertices = new List<Vector3>();
            readonly List<Vector3> normals = new List<Vector3>();
            readonly List<int> triangles = new List<int>();

            public void AddFace(Vector3[] corners, Vector3 normal)
            {
                var start = vertices.Count;
                vertices.AddRange(corners);
                foreach (var _ in corners) normals.Add(normal);
                var computed = Vector3.Cross(corners[1] - corners[0], corners[2] - corners[0]);
                var flip = Vector3.Dot(computed, normal) < 0f;
                for (var i = 1; i + 1 < corners.Length; i++)
                {
                    triangles.Add(start);
                    triangles.Add(start + (flip ? i + 1 : i));
                    triangles.Add(start + (flip ? i : i + 1));
                }
            }

            public void AddTwoSidedQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
            {
                AddFace(new[] { a, b, c, d }, normal);
                AddFace(new[] { a, b, c, d }, -normal);
            }

            public void AddTwoSidedTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
            {
                AddFace(new[] { a, b, c }, normal);
                AddFace(new[] { a, b, c }, -normal);
            }

            public Mesh Build(string name)
            {
                var mesh = new Mesh { name = name };
                mesh.SetVertices(vertices);
                mesh.SetNormals(normals);
                mesh.SetTriangles(triangles, 0);
                mesh.RecalculateBounds();
                return mesh;
            }
        }

        static List<LightRig> BuildLights(Transform root, RoomData room, RoomTheme theme, RoomAssets assets)
        {
            var rigs = new List<LightRig>();
            var shadowLights = 0;
            if (room.lights == null) return rigs;
            var group = new GameObject("Room Lights").transform;
            group.SetParent(root, false);
            foreach (var data in room.lights)
            {
                ColorUtility.TryParseHtmlString(data.color, out var color);
                var go = new GameObject($"{data.id} {data.type}");
                go.transform.SetParent(group, false);
                go.transform.position = RoomLoader.ToUnity(data.position);
                var light = go.AddComponent<Light>();
                light.color = color;
                light.shadowNearPlane = 0.08f;
                light.shadowStrength = 0.85f;
                var scale = data.intensity * theme.lightScale * theme.lightDim;

                var position = go.transform.position;
                var mount = data.mount != null && data.mount.Length == 3 ? RoomLoader.ToUnity(data.mount) : position;
                switch (data.type)
                {
                    case "ceiling_lamp":
                        // Light falls off with distance squared, so taller rooms need stronger lamps.
                        var heightBoost = Mathf.Clamp(room.ceilingHeight * room.ceilingHeight / (ReferenceCeiling * ReferenceCeiling), 0.6f, 4f);
                        Spot(light, 145f, Mathf.Max(data.range * 1.7f, room.ceilingHeight * 2.6f), scale * 13f * heightBoost);
                        var ceilingFill = AddFill(go.transform, color, Mathf.Max(data.range * 1.9f, room.ceilingHeight * 2.8f), scale * 7f * heightBoost, room.ceilingHeight * 0.28f);
                        var sway = new GameObject($"{data.id} sway").transform;
                        sway.SetParent(group, false);
                        sway.position = mount;
                        go.transform.SetParent(sway, true);
                        rigs.Add(Rig(light, ceilingFill, AddPendant(sway, mount, position, color, assets)));
                        sway.gameObject.AddComponent<LampSway>();
                        break;
                    case "table_lamp":
                    case "floor_lamp":
                        Spot(light, 150f, data.range * 1.4f, scale * (data.type == "table_lamp" ? 2.6f : 5f));
                        var lampFill = AddFill(go.transform, color, data.range * 1.5f, scale * 2.6f, 0f);
                        rigs.Add(Rig(light, lampFill, AddStandingLamp(group, mount, position, color, data.type == "floor_lamp", assets)));
                        break;
                    case "candle":
                        Point(light, data.range * 1.3f, scale * 3.2f);
                        go.AddComponent<LightFlicker>();
                        AddCandle(group, mount, position, color, assets);
                        break;
                    default:
                        Point(light, data.range * 1.4f, scale * 4f);
                        break;
                }

                // Rooms can have many lamps, so only the first few pay for shadows.
                if (light.shadows != LightShadows.None)
                {
                    if (shadowLights >= MaxShadowLights) light.shadows = LightShadows.None;
                    else shadowLights++;
                }
            }
            return rigs;
        }

        static LightRig Rig(Light main, Light fill, Material glow) => new LightRig
        {
            main = main,
            fill = fill,
            glow = glow,
            glowBase = glow.GetColor("_BaseColor"),
            mainBase = main.intensity,
            fillBase = fill.intensity,
        };

        // Unshadowed light so walls and ceilings are not lost outside the main pool of light.
        static Light AddFill(Transform parent, Color color, float range, float intensity, float drop)
        {
            var go = new GameObject("Fill");
            go.transform.SetParent(parent, false);
            go.transform.position = parent.position + Vector3.down * drop;
            var fill = go.AddComponent<Light>();
            fill.type = LightType.Point;
            fill.color = color;
            fill.range = range;
            fill.intensity = intensity;
            fill.shadows = LightShadows.None;
            return fill;
        }

        static void Spot(Light light, float angle, float range, float intensity)
        {
            light.type = LightType.Spot;
            light.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            light.spotAngle = angle;
            light.innerSpotAngle = angle * 0.45f;
            light.range = range;
            light.intensity = intensity;
            light.shadows = LightShadows.Soft;
        }

        static void Point(Light light, float range, float intensity)
        {
            light.type = LightType.Point;
            light.range = range;
            light.intensity = intensity;
            light.shadows = LightShadows.None;
        }

        static Material GlowMaterial(Color color, float glow, RoomAssets assets)
        {
            var material = assets.Keep(new Material(Shader.Find("Universal Render Pipeline/Unlit")));
            material.SetColor("_BaseColor", color * glow);
            return material;
        }

        static void AddFixturePart(Transform parent, string name, PrimitiveType shape, Vector3 position, Vector3 scale, Material material)
        {
            var part = GameObject.CreatePrimitive(shape);
            part.name = name;
            Object.DestroyImmediate(part.GetComponent<Collider>());
            part.transform.SetParent(parent, false);
            part.transform.position = position;
            part.transform.localScale = scale;
            var renderer = part.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        // Unity's cylinder is 2 units tall, so its Y scale is half the wanted height.
        static void AddRod(Transform parent, Vector3 bottom, Vector3 top, float width, Material material)
        {
            AddFixturePart(parent, "Rod", PrimitiveType.Cylinder, (bottom + top) / 2f,
                new Vector3(width, Mathf.Max(0.001f, (top.y - bottom.y) / 2f), width), material);
        }

        static Material AddPendant(Transform parent, Vector3 mount, Vector3 bulb, Color color, RoomAssets assets)
        {
            var metal = assets.Keep(LitMaterial(new Color(0.11f, 0.1f, 0.09f), 0.35f));
            AddRod(parent, bulb, mount, 0.012f, metal);
            AddFixturePart(parent, "Shade", PrimitiveType.Sphere, bulb + Vector3.up * 0.05f, new Vector3(0.42f, 0.14f, 0.42f), metal);
            var glow = GlowMaterial(color, 2.2f, assets);
            AddFixturePart(parent, "Bulb", PrimitiveType.Sphere, bulb - Vector3.up * 0.015f, new Vector3(0.13f, 0.09f, 0.13f), glow);
            return glow;
        }

        static Material AddStandingLamp(Transform parent, Vector3 mount, Vector3 bulb, Color color, bool tall, RoomAssets assets)
        {
            var metal = assets.Keep(LitMaterial(new Color(0.16f, 0.13f, 0.1f), 0.4f));
            var baseWidth = tall ? 0.3f : 0.15f;
            AddFixturePart(parent, "Base", PrimitiveType.Cylinder, mount + Vector3.up * 0.012f, new Vector3(baseWidth, 0.012f, baseWidth), metal);
            AddRod(parent, mount, bulb, tall ? 0.028f : 0.022f, metal);
            var shadeSize = tall ? 0.36f : 0.25f;
            var glow = GlowMaterial(color, 0.72f, assets);
            AddFixturePart(parent, "Shade", PrimitiveType.Cylinder, bulb, new Vector3(shadeSize, shadeSize * 0.34f, shadeSize), glow);
            return glow;
        }

        static void AddCandle(Transform parent, Vector3 mount, Vector3 flame, Color color, RoomAssets assets)
        {
            var wax = assets.Keep(LitMaterial(new Color(0.84f, 0.78f, 0.66f), 0.25f));
            var top = flame - Vector3.up * 0.025f;
            AddRod(parent, mount, top, 0.04f, wax);
            AddFixturePart(parent, "Flame", PrimitiveType.Sphere, flame, new Vector3(0.022f, 0.05f, 0.022f), GlowMaterial(color, 3.2f, assets));
        }

        static void BuildPostProcessing(Transform root, RoomTheme theme, RoomAssets assets)
        {
            var go = new GameObject("Post Processing");
            go.transform.SetParent(root, false);
            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 100f;
            var profile = assets.Keep(ScriptableObject.CreateInstance<VolumeProfile>());
            volume.sharedProfile = profile;

            var tonemapping = profile.Add<Tonemapping>(true);
            tonemapping.mode.Override(TonemappingMode.ACES);

            var bloom = profile.Add<Bloom>(true);
            bloom.threshold.Override(1.05f);
            bloom.intensity.Override(theme.bloom);
            bloom.scatter.Override(0.62f);

            var color = profile.Add<ColorAdjustments>(true);
            color.postExposure.Override(theme.exposure);
            color.contrast.Override(theme.contrast);
            color.saturation.Override(theme.saturation);

            var vignette = profile.Add<Vignette>(true);
            vignette.intensity.Override(theme.vignette);
            vignette.smoothness.Override(0.42f);
            vignette.color.Override(Color.black);

            var grain = profile.Add<FilmGrain>(true);
            grain.type.Override(FilmGrainLookup.Medium1);
            grain.intensity.Override(0f);
            grain.response.Override(0.75f);
            root.gameObject.AddComponent<RoomTension>().Setup(grain, vignette, theme.vignette);
        }
    }
}
