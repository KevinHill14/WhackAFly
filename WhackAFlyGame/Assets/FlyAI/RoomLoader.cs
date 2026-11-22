using System;
using System.IO;
using GLTFast;
using UnityEngine;
using UnityEngine.Rendering;

namespace FlyAI
{
    // Starts by itself on Play. Watches data/latest.json and loads each new scan.
    public class RoomLoader : MonoBehaviour
    {
        const float BoundaryThickness = 0.2f;
        const float SlabThickness = 2f;

        RoomTheme theme = RoomThemes.Main;
        bool themeChanged;

        // The main menu turns this on when Play is pressed. With the menu skipped (FLYAI_SKIP_MENU) it is on from the start.
        public bool AutoLoad = true;
        public string LoadedId => loadedId;
        public bool IsLoading => loading;
        public static event System.Action RoomLoaded;
        public static event System.Action<string> LoadFailed;
        public string DataDirectory => dataDirectory;

        string dataDirectory;
        string loadedId;
        string failedScanKey;
        bool loading;
        int loadGeneration;     // Raised to cancel a load that is in progress.
        string loadingKey;
        bool reloadPending;
        float nextCheck;
        GameObject currentRoot;
        GltfImport currentImport;
        GameObject debugBoxes;
        MarkerSet markerSet;
        GameObject propGroup;
        bool revealed;   // R: every kind of placement shown at once
        bool showingAi;
        string roomDirectory;
        GameObject ceilingOwner;

        class MarkerSet
        {
            public GameObject root;
            public GameObject items;
            public GameObject landing;
            public GameObject sound;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (FindFirstObjectByType<RoomLoader>() == null)
                new GameObject("RoomLoader").AddComponent<RoomLoader>();
        }

        void Awake()
        {
            dataDirectory = FindDataDirectory();
            theme = ThemeMenu.SavedTheme();
            FlySounds.SetTheme(theme.folder);
            gameObject.AddComponent<ThemeMenu>();
            ApplyLook();
            Debug.Log($"[FlyAI] Watching {dataDirectory}");
        }

        // FLYAI_DATA overrides the location; otherwise search upward from the game.
        static string FindDataDirectory()
        {
            var configured = Environment.GetEnvironmentVariable("FLYAI_DATA");
            if (!string.IsNullOrEmpty(configured)) return Path.GetFullPath(configured);

            var fallback = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "data"));
            for (var folder = new DirectoryInfo(Application.dataPath); folder != null; folder = folder.Parent)
            {
                var candidate = Path.Combine(folder.FullName, "data");
                if (File.Exists(Path.Combine(candidate, "latest.json"))) return candidate;
            }
            return fallback;
        }

        void ApplyLook()
        {
            var camera = GetCamera();
            RoomVisuals.ApplyEnvironment(theme, camera);
            if (camera.GetComponent<FlyCamera>() == null) camera.gameObject.AddComponent<FlyCamera>();
        }

        static Camera GetCamera()
        {
            if (Camera.main != null) return Camera.main;
            var go = new GameObject("Main Camera") { tag = "MainCamera" };
            go.AddComponent<AudioListener>();
            return go.AddComponent<Camera>();
        }

        public RoomTheme Theme => theme;

        // Switches theme and rebuilds the current room with it through the normal reload.
        public void SetTheme(RoomTheme next)
        {
            if (next == null || next == theme) return;
            theme = next;
            FlySounds.SetTheme(theme.folder);
            ApplyLook();
            themeChanged = true;
            Debug.Log($"[FlyAI] Theme: {theme.name}");
        }

        void Update()
        {
            if (!AutoLoad) return;
            // Waits for any load in progress, then reloads the room with the new theme.
            if (themeChanged && !loading)
            {
                themeChanged = false;
                CheckForScan(true);
            }
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 1f;
            if (loading)
            {
                // A different level was chosen while this one is still loading: drop the old one, then load the new.
                var latestPath = Path.Combine(dataDirectory, "latest.json");
                if (File.Exists(latestPath) && File.GetLastWriteTimeUtc(latestPath).Ticks.ToString() != loadingKey) loadGeneration++;
                return;
            }
            CheckForScan(false);
        }

        public void ReloadScan()
        {
            if (!AutoLoad) return;
            if (loading)
            {
                loadGeneration++;
                reloadPending = true;
                return;
            }
            CheckForScan(true);
        }

        // Removes the loaded room (the main menu does this when going back to it).
        public void Unload()
        {
            loadGeneration++;
            if (currentRoot != null) Destroy(currentRoot);
            currentImport?.Dispose();
            currentRoot = null;
            currentImport = null;
            loadedId = null;
            failedScanKey = null;
            debugBoxes = null;
            markerSet = null;
            propGroup = null;
            ceilingOwner = null;
        }

        public string ThemeName => theme.name;

        // Shows or hides every placement together: object outlines, props and lights, fly landing spots, sound sources.
        public void ToggleReveal()
        {
            revealed = !revealed;
            ApplyMarkerVisibility();
        }

        void ApplyMarkerVisibility()
        {
            if (debugBoxes != null) debugBoxes.SetActive(revealed);
            if (markerSet == null) return;
            markerSet.items.SetActive(revealed);
            markerSet.landing.SetActive(revealed);
            markerSet.sound.SetActive(revealed);
        }

        // Switches props and markers between the rule-based and AI-cleaned layouts.
        public void ToggleLayout()
        {
            if (loading || currentRoot == null || string.IsNullOrEmpty(roomDirectory)) return;
            var aiPath = Path.Combine(roomDirectory, "room.ai.json");
            var rulesPath = Path.Combine(roomDirectory, "room.rules.json");
            if (!File.Exists(aiPath) || !File.Exists(rulesPath))
            {
                Debug.Log("[FlyAI] This scan has no AI layout to compare.");
                return;
            }

            try
            {
                var useAi = !showingAi;
                var room = JsonUtility.FromJson<RoomData>(File.ReadAllText(useAi ? aiPath : rulesPath));
                var markers = BuildMarkerSet(currentRoot.transform, room);
                if (propGroup != null) Destroy(propGroup);
                propGroup = RoomProps.Build(currentRoot.transform, room, theme);
                if (markerSet != null) Destroy(markerSet.root);
                markerSet = markers;
                showingAi = useAi;
                ApplyMarkerVisibility();
                Debug.Log($"[FlyAI] Showing {(useAi ? "AI-cleaned" : "rule-based")} layout.");
            }
            catch (Exception error)
            {
                Debug.LogError($"[FlyAI] Layout switch failed: {error}");
            }
        }

        public void ToggleCeiling()
        {
            if (ceilingOwner == null) return;
            foreach (var renderer in ceilingOwner.GetComponentsInChildren<Renderer>(true))
                if (IsCeiling(renderer.transform)) renderer.enabled = !renderer.enabled;
        }

        static bool IsCeiling(Transform transform)
        {
            for (var t = transform; t != null; t = t.parent)
                if (t.name.StartsWith("Ceiling", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        async void CheckForScan(bool force)
        {
            if (loading) return;
            var latestPath = Path.Combine(dataDirectory, "latest.json");
            if (!File.Exists(latestPath)) return;

            // A scan that failed is not retried until latest.json changes or R is pressed.
            var scanKey = File.GetLastWriteTimeUtc(latestPath).Ticks.ToString();
            if (!force && scanKey == failedScanKey) return;

            loading = true;
            var generation = loadGeneration;
            loadingKey = scanKey;
            GltfImport import = null;
            GameObject root = null;
            var camera = GetCamera();
            var previousFarClip = camera.farClipPlane;
            var buildTheme = theme;
            try
            {
                var latest = JsonUtility.FromJson<LatestScan>(File.ReadAllText(latestPath));
                if (latest == null || string.IsNullOrEmpty(latest.id) || (!force && latest.id == loadedId)) return;

                var room = JsonUtility.FromJson<RoomData>(File.ReadAllText(Path.Combine(dataDirectory, latest.room)));
                Validate(room);
                var bytes = File.ReadAllBytes(Path.Combine(dataDirectory, latest.glb));

                import = new GltfImport();
                if (!await import.Load(bytes)) throw new InvalidDataException("glTFast could not read the scan.");
                if (this == null || generation != loadGeneration) return; // Play mode ended, or another level was chosen.

                root = new GameObject($"Room {latest.id}");
                var mesh = new GameObject("Mesh");
                mesh.transform.SetParent(root.transform, false);
                if (!await import.InstantiateMainSceneAsync(mesh.transform))
                    throw new InvalidDataException("glTFast could not create the scene.");
                if (this == null || generation != loadGeneration) return;

                RoomVisuals.Apply(root.transform, mesh.transform, room, buildTheme, camera);
                BuildColliders(root.transform, room);
                LinkVisuals(root.transform, mesh.transform);
                var builtProps = RoomProps.Build(root.transform, room, buildTheme);
                var boxes = BuildDebugBoxes(root.transform, room);
                var markers = BuildMarkerSet(root.transform, room);
                PlacePlayer(room);

                // Nothing can fail past this point, so the new room takes over.
                root.GetComponent<RoomAtmosphere>().MakeCurrent();
                root.GetComponent<RoomEvents>().MakeCurrent();
                root.GetComponent<RoomTension>().MakeCurrent();
                FlyAgent.Spawn(root.transform, room);
                root.AddComponent<ShaderWarmup>();
                failedScanKey = null;
                if (currentRoot != null) Destroy(currentRoot);
                currentImport?.Dispose();
                currentRoot = root;
                currentImport = import;
                debugBoxes = boxes;
                markerSet = markers;
                propGroup = builtProps;
                showingAi = room.layoutSource == "ai";
                roomDirectory = Path.GetDirectoryName(Path.Combine(dataDirectory, latest.room));
                ApplyMarkerVisibility();
                ceilingOwner = mesh;
                loadedId = latest.id;
                root = null;
                import = null;
                Debug.Log($"[FlyAI] Loaded {latest.id}: {room.walls.Length} walls, {room.objects.Length} objects, {room.floor.area} m2");
                RoomLoaded?.Invoke();
            }
            catch (Exception error)
            {
                failedScanKey = scanKey;
                if (camera != null) camera.farClipPlane = previousFarClip;
                Debug.LogError($"[FlyAI] Scan load failed: {error}");
                LoadFailed?.Invoke(error.Message);
            }
            finally
            {
                if (root != null)
                {
                    root.SetActive(false);
                    if (Application.isPlaying) Destroy(root);
                }
                import?.Dispose();
                loading = false;
                if (reloadPending && this != null)
                {
                    reloadPending = false;
                    CheckForScan(true);
                }
            }
        }

        // Lets swatter imprints land on the real mesh instead of its collider box.
        static void LinkVisuals(Transform root, Transform meshRoot)
        {
            var nodes = new System.Collections.Generic.Dictionary<string, Transform>();
            foreach (var node in meshRoot.GetComponentsInChildren<Transform>(true)) nodes.TryAdd(node.name, node);
            foreach (var part in root.GetComponentsInChildren<RoomPart>(true))
                if ((part.kind == RoomPartKind.Wall || part.kind == RoomPartKind.Furniture)
                    && part.GetComponent<WindowBreak>() == null && nodes.TryGetValue(part.name, out var visual))
                    part.visual = visual;
        }

        // Stops a bad room.json before it can disturb the room already running.
        static void Validate(RoomData room)
        {
            var valid = room != null && room.ceilingHeight > 0f && room.floor != null && room.floor.polygonFlat != null
                        && room.floor.polygonFlat.Length >= 6 && room.floor.polygonFlat.Length % 2 == 0
                        && room.walls != null && room.objects != null
                        && room.playerStart != null && HasLength(room.playerStart.position, 3);
            if (valid)
            {
                foreach (var wall in room.walls) valid &= HasLength(wall.start, 2) && HasLength(wall.end, 2);
                foreach (var item in room.objects) valid &= HasLength(item.position, 3) && HasLength(item.size, 3);
            }
            if (!valid) throw new InvalidDataException("room.json is incomplete. Re-run ingest for this scan.");
        }

        static bool HasLength(float[] values, int length) => values != null && values.Length == length;

        // room.json is right-handed glTF space. This is the only conversion to Unity space.
        internal static Vector3 ToUnity(float x, float y, float z) => new Vector3(-x, y, z);
        internal static Vector3 ToUnity(float[] p) => ToUnity(p[0], p[1], p[2]);
        static Quaternion YawToUnity(float degrees) => Quaternion.Euler(0f, -degrees, 0f);

        void BuildColliders(Transform parent, RoomData room)
        {
            var group = new GameObject("Colliders").transform;
            group.SetParent(parent, false);

            foreach (var wall in room.walls)
                AddWall(group, wall.node, RoomPartKind.Wall, "wall", ToUnity(wall.start[0], 0, wall.start[1]), ToUnity(wall.end[0], 0, wall.end[1]), wall.height);

            var outline = room.floor.polygonFlat;
            var count = outline.Length / 2;
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            for (var i = 0; i < count; i++)
            {
                var a = ToUnity(outline[i * 2], 0, outline[i * 2 + 1]);
                var next = (i + 1) % count;
                var b = ToUnity(outline[next * 2], 0, outline[next * 2 + 1]);
                AddWall(group, $"Boundary_{i}", RoomPartKind.Boundary, "boundary", a, b, room.ceilingHeight);
                min = Vector2.Min(min, new Vector2(a.x, a.z));
                max = Vector2.Max(max, new Vector2(a.x, a.z));
            }

            var centre = (min + max) / 2f;
            var span = max - min;
            AddBox(group, "Floor", RoomPartKind.Floor, "floor", new Vector3(centre.x, -SlabThickness / 2f, centre.y), Quaternion.identity, new Vector3(span.x + 1f, SlabThickness, span.y + 1f));
            AddBox(group, "Ceiling", RoomPartKind.Ceiling, "ceiling", new Vector3(centre.x, room.ceilingHeight + SlabThickness / 2f, centre.y), Quaternion.identity, new Vector3(span.x + 1f, SlabThickness, span.y + 1f));

            foreach (var item in room.objects)
                AddBox(group, item.node, RoomPartKind.Furniture, item.label, ToUnity(item.position), YawToUnity(item.rotationY), new Vector3(item.size[0], item.size[1], item.size[2]));
        }

        static void AddWall(Transform parent, string name, RoomPartKind kind, string label, Vector3 a, Vector3 b, float height)
        {
            var direction = b - a;
            if (direction.sqrMagnitude < 0.0001f) return;
            var centre = (a + b) / 2f + Vector3.up * height / 2f;
            AddBox(parent, name, kind, label, centre, Quaternion.LookRotation(direction), new Vector3(BoundaryThickness, height, direction.magnitude));
        }

        static void AddBox(Transform parent, string name, RoomPartKind kind, string label, Vector3 position, Quaternion rotation, Vector3 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(position, rotation);
            go.AddComponent<BoxCollider>().size = size;
            var part = go.AddComponent<RoomPart>();
            part.kind = kind;
            part.label = label;
        }

        // Green outlines from room.json. If they match the mesh, the coordinates are right.
        static GameObject BuildDebugBoxes(Transform parent, RoomData room)
        {
            var group = new GameObject("Debug Boxes");
            group.transform.SetParent(parent, false);
            var material = new Material(Shader.Find("Sprites/Default"));
            foreach (var item in room.objects)
                AddOutline(group.transform, item, material);
            group.SetActive(false);
            return group;
        }

        static void AddOutline(Transform parent, ObjectData item, Material material)
        {
            var go = new GameObject($"{item.node} outline");
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(ToUnity(item.position), YawToUnity(item.rotationY));
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.sharedMaterial = material;
            line.startColor = line.endColor = Color.green;
            line.startWidth = line.endWidth = 0.015f;
            line.shadowCastingMode = ShadowCastingMode.Off;

            var e = new Vector3(item.size[0], item.size[1], item.size[2]) / 2f;
            Vector3[] path =
            {
                new(-e.x,-e.y,-e.z), new(e.x,-e.y,-e.z), new(e.x,-e.y,e.z), new(-e.x,-e.y,e.z), new(-e.x,-e.y,-e.z),
                new(-e.x,e.y,-e.z), new(e.x,e.y,-e.z), new(e.x,-e.y,-e.z), new(e.x,e.y,-e.z),
                new(e.x,e.y,e.z), new(e.x,-e.y,e.z), new(e.x,e.y,e.z),
                new(-e.x,e.y,e.z), new(-e.x,-e.y,e.z), new(-e.x,e.y,e.z), new(-e.x,e.y,-e.z),
            };
            line.positionCount = path.Length;
            line.SetPositions(path);
        }

        // Orange = props, yellow = lights, small violet spheres = fly landing spots,
        // large purple cubes = sound sources. Real assets replace these later.
        static MarkerSet BuildMarkerSet(Transform parent, RoomData room)
        {
            var root = new GameObject("Placement Markers");
            root.transform.SetParent(parent, false);
            var set = new MarkerSet
            {
                root = root,
                items = MarkerGroup(root.transform, "Props and Lights"),
                landing = MarkerGroup(root.transform, "Fly Landing Spots"),
                sound = MarkerGroup(root.transform, "Sound Sources"),
            };
            var landing = MarkerMaterial(new Color(0.78f, 0.58f, 1f));
            var sound = MarkerMaterial(new Color(0.48f, 0.05f, 0.9f));
            var prop = MarkerMaterial(new Color(1f, 0.5f, 0.1f));
            var light = MarkerMaterial(Color.yellow);

            if (room.landingSpots != null)
                foreach (var spot in room.landingSpots)
                    AddMarker(set.landing.transform, $"Landing {spot.id} {spot.surfaceKind}", PrimitiveType.Sphere, ToUnity(spot.position), Vector3.one * 0.05f, landing);

            if (room.soundAnchors != null)
                foreach (var anchor in room.soundAnchors)
                    AddMarker(set.sound.transform, $"Sound {anchor.id} {anchor.sound}", PrimitiveType.Cube, ToUnity(anchor.position), Vector3.one * 0.22f, sound);

            if (room.props != null)
                foreach (var item in room.props)
                {
                    var size = new Vector3(item.radius * 2f, Mathf.Max(item.height, 0.02f) / 2f, item.radius * 2f);
                    var centre = ToUnity(item.position) + Vector3.up * size.y;
                    AddMarker(set.items.transform, $"Prop {item.type}", PrimitiveType.Cylinder, centre, size, prop);
                }

            if (room.lights != null)
                foreach (var item in room.lights)
                    AddMarker(set.items.transform, $"Light {item.type}", PrimitiveType.Sphere, ToUnity(item.position), Vector3.one * 0.14f, light);

            return set;
        }

        static GameObject MarkerGroup(Transform parent, string name)
        {
            var group = new GameObject(name);
            group.transform.SetParent(parent, false);
            return group;
        }

        static Material MarkerMaterial(Color color) => new Material(Shader.Find("Sprites/Default")) { color = color };

        static void AddMarker(Transform parent, string name, PrimitiveType shape, Vector3 position, Vector3 scale, Material material)
        {
            var marker = GameObject.CreatePrimitive(shape);
            marker.name = name;
            Destroy(marker.GetComponent<Collider>());
            marker.transform.SetParent(parent, false);
            marker.transform.position = position;
            marker.transform.localScale = scale;
            var renderer = marker.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
        }

        void PlacePlayer(RoomData room)
        {
            var player = PlayerController.GetOrCreate(GetCamera());
            player.Spawn(ToUnity(room.playerStart.position), YawToUnity(room.playerStart.yaw), RoomVisuals.Outline(room));
        }

        void OnDestroy() => currentImport?.Dispose();
    }
}
