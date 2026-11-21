using UnityEngine;
using UnityEngine.Rendering;

namespace FlyAI
{
    // Turns the placements in room.json into real models.
    public static class RoomProps
    {
        const string ModelFolder = "FlyAIProps/";

        // The theme folder for the build in progress. Build runs synchronously, so every model
        // load inside it sees the same value without threading the theme through each helper.
        static string themeFolder;

        public static GameObject Build(Transform parent, RoomData room, RoomTheme theme)
        {
            themeFolder = theme != null ? theme.folder : null;
            try
            {
                return BuildProps(parent, room);
            }
            finally
            {
                themeFolder = null;
            }
        }

        static GameObject BuildProps(Transform parent, RoomData room)
        {
            var assets = parent.GetComponent<RoomAssets>();
            var group = new GameObject("Room Props");
            group.transform.SetParent(parent, false);

            if (room.props != null)
                foreach (var item in room.props)
                {
                    var prop = BuildProp(item.type, item.id, item.radius, item.height, assets);
                    if (prop == null) continue;
                    prop.transform.SetParent(group.transform, false);
                    prop.transform.SetPositionAndRotation(RoomLoader.ToUnity(item.position), Quaternion.Euler(0f, -item.rotationY, 0f));
                }

            if (room.soundAnchors != null)
            {
                var centre = RoomCentre(room);
                foreach (var anchor in room.soundAnchors)
                {
                    if (anchor.source == "prop")
                    {
                        PlaceSoundProp(group.transform, anchor, centre);
                    }
                    else
                    {
                        var emitter = new GameObject($"Scanned Sound {anchor.id}").transform;
                        emitter.SetParent(group.transform, false);
                        emitter.position = RoomLoader.ToUnity(anchor.position);
                        RoomAudio.AddEmitter(emitter, anchor);
                    }
                }
            }

            if (room.wallDecor != null)
                foreach (var decor in room.wallDecor)
                    PlaceWallDecor(group.transform, decor);

            foreach (Transform prop in group.transform)
                if (prop.name != "Prop mirror") SoftenMetal(prop.gameObject, assets);
            return group;
        }

        // The room has no reflections, so metallic art and mirrors would render black.
        static void SoftenMetal(GameObject holder, RoomAssets assets)
        {
            foreach (var renderer in holder.GetComponentsInChildren<Renderer>())
                foreach (var material in renderer.materials)
                {
                    if (assets != null) assets.Keep(material);
                    if (material.HasProperty("metallicFactor")) material.SetFloat("metallicFactor", Mathf.Min(material.GetFloat("metallicFactor"), 0.12f));
                    if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", Mathf.Min(material.GetFloat("_Metallic"), 0.12f));
                }
        }

        static void PlaceWallDecor(Transform parent, WallDecorData decor)
        {
            if (decor.center == null || decor.center.Length != 3 || decor.normal == null || decor.normal.Length != 3) return;
            var holder = new GameObject($"Prop {decor.type}");
            if (!AddWallModel(holder.transform, decor.type, decor.width, decor.height))
            {
                Object.Destroy(holder);
                return;
            }
            AddCollider(holder, decor.type);
            holder.transform.SetParent(parent, false);
            holder.transform.SetPositionAndRotation(RoomLoader.ToUnity(decor.center), Quaternion.LookRotation(RoomLoader.ToUnity(decor.normal)));
        }

        static Vector3 RoomCentre(RoomData room)
        {
            var outline = RoomVisuals.Outline(room);
            var sum = Vector3.zero;
            foreach (var point in outline) sum += point;
            return sum / Mathf.Max(1, outline.Count);
        }

        static void PlaceSoundProp(Transform parent, SoundAnchorData anchor, Vector3 centre)
        {
            var position = RoomLoader.ToUnity(anchor.position);
            var onWall = anchor.surfaceKind == "wall";
            var holder = new GameObject($"Prop {anchor.prop}");
            string model = anchor.prop == "radio" ? "radio" : onWall ? "wall_clock" : "table_clock";
            var built = onWall
                ? AddWallModel(holder.transform, model, anchor.radius * 2f, anchor.radius * 2f)
                : AddModel(holder.transform, model, anchor.radius * 2f, anchor.height, Vector3.zero);
            if (!built)
            {
                Object.Destroy(holder);
                return;
            }
            AddCollider(holder, anchor.prop);
            holder.transform.SetParent(parent, false);

            Vector3 facing;
            if (onWall)
            {
                facing = RoomLoader.ToUnity(anchor.normal);
            }
            else
            {
                facing = centre - position;
                facing.y = 0f;
            }
            holder.transform.SetPositionAndRotation(position, Quaternion.LookRotation(facing.sqrMagnitude > 0.001f ? facing.normalized : Vector3.forward));
            RoomAudio.AddEmitter(holder.transform, anchor);
        }

        static GameObject BuildProp(string type, string id, float radius, float height, RoomAssets assets)
        {
            var holder = new GameObject($"Prop {type}");
            var width = radius * 2f;
            var solid = true;
            var built = true;
            switch (type)
            {
                case "fruit_bowl":
                    built = AddFruitContainer(holder.transform, "bowl", "apple", width, height, radius, 3);
                    break;
                case "fruit_bowl_lemon":
                    built = AddFruitContainer(holder.transform, "bowl", "lemon", width, height, radius, 4);
                    break;
                case "fruit_bowl_pear":
                    built = AddFruitContainer(holder.transform, "bowl_large", "pear", width, height, radius, 3);
                    break;
                case "fruit_basket":
                    built = AddModel(holder.transform, "basket", width, height * 0.85f, Vector3.zero);
                    if (built) AddFruitRing(holder.transform, "kiwi", radius, height * 0.6f, 3);
                    break;
                case "place_setting":
                    built = AddModel(holder.transform, "plate", radius * 1.25f, height, Vector3.zero);
                    if (built)
                    {
                        AddTurnedModel(holder.transform, "knife", radius * 0.95f, 0.025f, new Vector3(radius * 0.78f, 0f, 0f), 90f);
                        AddFork(holder.transform, radius * 0.95f, new Vector3(-radius * 0.8f, 0.001f, 0f), assets);
                    }
                    break;
                case "cutlery":
                    built = AddTurnedModel(holder.transform, "knife", width * 0.85f, height, new Vector3(radius * 0.3f, 0f, 0f), 90f);
                    if (built)
                    {
                        AddTurnedModel(holder.transform, "spoon", width * 0.85f, height, new Vector3(-radius * 0.05f, 0f, 0f), 0f);
                        AddFork(holder.transform, width * 0.85f, new Vector3(-radius * 0.45f, 0.001f, 0f), assets);
                    }
                    break;
                case "drinking_glass":
                    AddDrinkingGlass(holder.transform, radius, height, assets);
                    break;
                case "phone":
                    AddPhone(holder.transform, radius, height, assets);
                    break;
                case "airpods":
                    AddEarbudCase(holder.transform, radius, height, assets);
                    break;
                case "crumbs":
                    AddCrumbs(holder.transform, id, radius, assets);
                    solid = false;
                    break;
                case "spilled_juice":
                    AddSpill(holder.transform, radius, assets);
                    solid = false;
                    break;
                case "napkin":
                    AddNapkin(holder.transform, radius, assets);
                    solid = false;
                    break;
                default:
                    built = AddModel(holder.transform, type, width, height, Vector3.zero);
                    break;
            }
            if (!built)
            {
                Object.Destroy(holder);
                return null;
            }
            if (solid) AddCollider(holder, type);
            return holder;
        }

        static bool AddFruitContainer(Transform holder, string container, string fruit, float width, float height, float radius, int count)
        {
            if (!AddModel(holder, container, width, height * 0.72f, Vector3.zero)) return false;
            AddFruitRing(holder, fruit, radius, height * 0.3f, count);
            return true;
        }

        static void AddFruitRing(Transform holder, string fruit, float radius, float y, int count)
        {
            var size = radius * (count > 3 ? 0.52f : 0.62f);
            for (var i = 0; i < count; i++)
            {
                var angle = i * Mathf.PI * 2f / count + 0.4f;
                var offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * (radius * 0.36f);
                AddModel(holder, fruit, size, size, offset + Vector3.up * y);
            }
        }

        static bool AddTurnedModel(Transform holder, string name, float width, float height, Vector3 offset, float yaw)
        {
            var pivot = new GameObject(name + " pivot").transform;
            pivot.SetParent(holder, false);
            pivot.localPosition = offset;
            pivot.localRotation = Quaternion.Euler(0f, yaw, 0f);
            if (AddModel(pivot, name, width, height, Vector3.zero)) return true;
            Object.Destroy(pivot.gameObject);
            return false;
        }

        static void AddDrinkingGlass(Transform holder, float radius, float height, RoomAssets assets)
        {
            var water = ClearSurface(new Color(0.48f, 0.62f, 0.68f, 0.42f), assets);
            var glass = ClearSurface(new Color(0.78f, 0.86f, 0.9f, 0.22f), assets);
            AddMesh(holder, assets.Keep(PropMeshes.Lathe(new[]
            {
                new Vector2(0.0005f, height * 0.09f), new Vector2(radius * 0.8f, height * 0.09f), new Vector2(radius * 0.84f, height * 0.62f), new Vector2(0.0005f, height * 0.62f),
            }, "Water")), Vector3.zero, Quaternion.identity, water, false);
            AddMesh(holder, assets.Keep(PropMeshes.Lathe(new[]
            {
                new Vector2(0.0005f, 0f), new Vector2(radius * 0.78f, 0f), new Vector2(radius * 0.84f, height * 0.04f), new Vector2(radius * 0.92f, height * 0.55f),
                new Vector2(radius, height), new Vector2(radius * 0.93f, height), new Vector2(radius * 0.84f, height * 0.55f), new Vector2(radius * 0.76f, height * 0.08f),
                new Vector2(0.0005f, height * 0.08f),
            }, "Drinking Glass")), Vector3.zero, Quaternion.identity, glass, false);
        }

        static void AddPhone(Transform holder, float radius, float height, RoomAssets assets)
        {
            var body = Surface(new Color(0.16f, 0.16f, 0.18f), 0.68f, assets);
            var glass = Surface(new Color(0.012f, 0.014f, 0.02f), 0.96f, assets);
            var glow = assets.Keep(new Material(Shader.Find("Universal Render Pipeline/Unlit")));
            glow.SetColor("_BaseColor", new Color(0.05f, 0.09f, 0.2f));
            var length = radius * 1.9f;
            var width = length * 0.485f;
            AddMesh(holder, assets.Keep(PropMeshes.Slab(width, length, width * 0.17f, height, "Phone Body")), Vector3.zero, Quaternion.identity, body, true);
            AddMesh(holder, assets.Keep(PropMeshes.Slab(width * 0.95f, length * 0.975f, width * 0.15f, 0.0006f, "Phone Glass")), Vector3.up * height, Quaternion.identity, glass, false);
            AddMesh(holder, assets.Keep(PropMeshes.Slab(width * 0.86f, length * 0.91f, width * 0.11f, 0.0004f, "Phone Screen")), Vector3.up * (height + 0.0006f), Quaternion.identity, glow, false);
            AddMesh(holder, assets.Keep(PropMeshes.Slab(width * 0.3f, length * 0.035f, length * 0.017f, 0.0003f, "Phone Island")),
                new Vector3(0f, height + 0.001f, length * 0.415f), Quaternion.identity, glass, false);
        }

        static void AddEarbudCase(Transform holder, float radius, float height, RoomAssets assets)
        {
            var shell = Surface(new Color(0.92f, 0.92f, 0.9f), 0.76f, assets);
            var seam = Surface(new Color(0.34f, 0.34f, 0.33f), 0.3f, assets);
            var led = assets.Keep(new Material(Shader.Find("Universal Render Pipeline/Unlit")));
            led.SetColor("_BaseColor", new Color(0.15f, 1.1f, 0.25f));
            var width = radius * 1.9f;
            var depth = width * 0.68f;
            var corner = depth * 0.46f;
            AddMesh(holder, assets.Keep(PropMeshes.Slab(width, depth, corner, height * 0.62f, "Case Base")), Vector3.zero, Quaternion.identity, shell, true);
            AddMesh(holder, assets.Keep(PropMeshes.Slab(width * 0.985f, depth * 0.975f, corner, 0.0006f, "Case Seam")), Vector3.up * (height * 0.62f), Quaternion.identity, seam, false);
            AddMesh(holder, assets.Keep(PropMeshes.Slab(width, depth, corner, height * 0.36f, "Case Lid")), Vector3.up * (height * 0.64f), Quaternion.identity, shell, true);
            AddPrimitive(holder, PrimitiveType.Sphere, new Vector3(0f, height * 0.52f, depth * 0.49f), Vector3.one * 0.0022f, Quaternion.identity, led, false);
        }

        static void AddFork(Transform holder, float length, Vector3 position, RoomAssets assets)
        {
            var steel = Surface(new Color(0.68f, 0.69f, 0.7f), 0.82f, assets);
            steel.SetFloat("_Metallic", 0.9f);
            AddMesh(holder, assets.Keep(PropMeshes.Fork(length, "Fork")), position, Quaternion.identity, steel, true);
        }

        static void AddMesh(Transform parent, Mesh mesh, Vector3 position, Quaternion rotation, Material material, bool shadows)
        {
            var part = new GameObject(mesh.name);
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localRotation = rotation;
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = part.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
        }

        internal static Material ClearSurface(Color color, RoomAssets assets)
        {
            var material = RoomVisuals.LitMaterial(color, 0.94f);
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            material.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = (int)RenderQueue.Transparent;
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            return assets != null ? assets.Keep(material) : material;
        }

        static GameObject LoadModel(string name)
        {
            // A theme's own model wins; otherwise the house model of the same name.
            GameObject prefab = null;
            if (!string.IsNullOrEmpty(themeFolder)) prefab = Resources.Load<GameObject>(ModelFolder + themeFolder + "/" + name);
            if (prefab == null) prefab = Resources.Load<GameObject>(ModelFolder + name);
            if (prefab == null)
            {
                Debug.LogWarning($"[FlyAI] Missing prop model: {name}");
                return null;
            }
            var model = Object.Instantiate(prefab);
            model.name = name;
            model.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
            {
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;
            }
            return model;
        }

        static bool TryBounds(GameObject root, out Bounds bounds)
        {
            bounds = default;
            var found = false;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!found)
                {
                    bounds = renderer.bounds;
                    found = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }
            return found;
        }

        // Scales a model to fit its placement box, with its base centred on the holder.
        static bool AddModel(Transform holder, string name, float width, float height, Vector3 offset)
        {
            var model = LoadModel(name);
            if (model == null) return false;
            if (!TryBounds(model, out var bounds))
            {
                Object.Destroy(model);
                return false;
            }
            var scale = Mathf.Min(width / Mathf.Max(bounds.size.x, bounds.size.z, 0.001f), height / Mathf.Max(bounds.size.y, 0.001f));
            model.transform.localScale *= scale;
            model.transform.position = offset - new Vector3(bounds.center.x, bounds.min.y, bounds.center.z) * scale;
            model.transform.SetParent(holder, false);
            return true;
        }

        // Scales a flat model and rests its back against the wall.
        static bool AddWallModel(Transform holder, string name, float width, float height)
        {
            var model = LoadModel(name);
            if (model == null) return false;
            if (!TryBounds(model, out var bounds))
            {
                Object.Destroy(model);
                return false;
            }
            var scale = Mathf.Min(width / Mathf.Max(bounds.size.x, 0.001f), height / Mathf.Max(bounds.size.y, 0.001f));
            model.transform.localScale *= scale;
            model.transform.position = -new Vector3(bounds.center.x, bounds.center.y, bounds.min.z) * scale;
            model.transform.SetParent(holder, false);
            return true;
        }

        static void AddCollider(GameObject holder, string label)
        {
            if (!TryBounds(holder, out var bounds)) return;
            var collider = holder.AddComponent<BoxCollider>();
            collider.center = bounds.center;
            collider.size = bounds.size;
            var part = holder.AddComponent<RoomPart>();
            part.kind = RoomPartKind.Furniture;
            part.label = label;
            holder.AddComponent<PropDamage>().Setup(label);
        }

        static Material Surface(Color color, float smoothness, RoomAssets assets) =>
            assets != null ? assets.Keep(RoomVisuals.LitMaterial(color, smoothness)) : RoomVisuals.LitMaterial(color, smoothness);

        static void AddPrimitive(Transform parent, PrimitiveType shape, Vector3 position, Vector3 scale, Quaternion rotation, Material material, bool shadows)
        {
            var part = GameObject.CreatePrimitive(shape);
            Object.DestroyImmediate(part.GetComponent<Collider>());
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localRotation = rotation;
            part.transform.localScale = scale;
            var renderer = part.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
        }

        static void AddCrumbs(Transform holder, string id, float radius, RoomAssets assets)
        {
            var seed = 17;
            foreach (var c in id) seed = seed * 31 + c;
            var random = new System.Random(seed);
            var material = Surface(new Color(0.42f, 0.29f, 0.16f), 0.1f, assets);
            for (var i = 0; i < 12; i++)
            {
                var angle = (float)random.NextDouble() * Mathf.PI * 2f;
                var distance = Mathf.Sqrt((float)random.NextDouble()) * radius;
                var size = Mathf.Lerp(0.004f, 0.011f, (float)random.NextDouble());
                AddPrimitive(holder, PrimitiveType.Sphere, new Vector3(Mathf.Cos(angle) * distance, size * 0.3f, Mathf.Sin(angle) * distance),
                    new Vector3(size, size * 0.6f, size), Quaternion.identity, material, false);
            }
        }

        static void AddSpill(Transform holder, float radius, RoomAssets assets)
        {
            var material = Surface(new Color(0.55f, 0.2f, 0.05f), 0.86f, assets);
            AddPrimitive(holder, PrimitiveType.Cylinder, Vector3.up * 0.002f, new Vector3(radius * 1.7f, 0.002f, radius * 1.25f), Quaternion.identity, material, false);
            AddPrimitive(holder, PrimitiveType.Cylinder, new Vector3(radius * 0.55f, 0.0025f, radius * 0.35f), new Vector3(radius * 0.8f, 0.002f, radius * 0.65f), Quaternion.identity, material, false);
            AddPrimitive(holder, PrimitiveType.Cylinder, new Vector3(-radius * 0.5f, 0.0025f, -radius * 0.4f), new Vector3(radius * 0.55f, 0.002f, radius * 0.7f), Quaternion.identity, material, false);
        }

        static void AddNapkin(Transform holder, float radius, RoomAssets assets)
        {
            var material = Surface(new Color(0.8f, 0.77f, 0.7f), 0.12f, assets);
            AddPrimitive(holder, PrimitiveType.Cube, Vector3.up * 0.004f, new Vector3(radius * 1.5f, 0.005f, radius * 1.5f), Quaternion.identity, material, false);
            AddPrimitive(holder, PrimitiveType.Cube, Vector3.up * 0.009f, new Vector3(radius * 1.1f, 0.004f, radius * 1.1f), Quaternion.Euler(0f, 24f, 0f), material, false);
        }
    }
}
