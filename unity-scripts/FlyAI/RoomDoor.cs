using UnityEngine;
using UnityEngine.Rendering;

namespace FlyAI
{
    // A closed panel door, built to the size of the scanned or calculated doorway.
    public static class RoomDoor
    {
        public static void Build(Transform parent, RoomData room, RoomTheme theme, RoomAssets assets)
        {
            if (room.doors == null || room.doors.Length == 0) return;
            var trim = assets.Keep(RoomVisuals.LitMaterial(theme.trim, 0.34f));
            var slab = RoomSurfaces.MaterialFor("table", theme, assets);
            var panel = assets.Keep(new Material(slab));
            panel.SetColor("_BaseColor", slab.GetColor("_BaseColor") * 0.78f);
            var brass = assets.Keep(RoomVisuals.LitMaterial(new Color(0.62f, 0.46f, 0.2f), 0.72f));
            brass.SetFloat("_Metallic", 0.25f); // The room has no reflections, so full metal renders black.

            foreach (var door in room.doors)
            {
                if (door.center == null || door.center.Length != 3 || door.normal == null || door.normal.Length != 3) continue;
                if (door.width <= 0f || door.height <= 0f) continue;
                var holder = new GameObject($"Door ({door.source})").transform;
                holder.SetParent(parent, false);
                holder.SetPositionAndRotation(RoomLoader.ToUnity(door.center), Quaternion.LookRotation(RoomLoader.ToUnity(door.normal)));
                BuildDoor(holder, door.width, door.height, trim, slab, panel, brass);
            }
        }

        static void BuildDoor(Transform holder, float w, float h, Material trim, Material slab, Material panel, Material brass)
        {
            const float frame = 0.075f;
            const float frameDepth = 0.11f;
            const float slabDepth = 0.085f;

            Part(holder, PrimitiveType.Cube, new Vector3(0f, 0f, slabDepth / 2f), new Vector3(w, h, slabDepth), slab);
            Part(holder, PrimitiveType.Cube, new Vector3(-(w + frame) / 2f, frame / 2f, frameDepth / 2f), new Vector3(frame, h + frame, frameDepth), trim);
            Part(holder, PrimitiveType.Cube, new Vector3((w + frame) / 2f, frame / 2f, frameDepth / 2f), new Vector3(frame, h + frame, frameDepth), trim);
            Part(holder, PrimitiveType.Cube, new Vector3(0f, (h + frame) / 2f, frameDepth / 2f), new Vector3(w + frame * 2f, frame, frameDepth), trim);

            var margin = w * 0.13f;
            var panelWidth = (w - margin * 3f) / 2f;
            float[] rowHeights = { 0.34f, 0.18f, 0.3f };
            var gap = h * 0.045f;
            var usable = h - gap * 4f;
            var y = -h / 2f + gap;
            for (var row = 0; row < rowHeights.Length; row++)
            {
                var rowHeight = usable * rowHeights[row] / 0.82f;
                for (var side = -1; side <= 1; side += 2)
                {
                    var x = side * (panelWidth + margin) / 2f;
                    Part(holder, PrimitiveType.Cube, new Vector3(x, y + rowHeight / 2f, slabDepth + 0.004f), new Vector3(panelWidth, rowHeight, 0.008f), panel);
                    Part(holder, PrimitiveType.Cube, new Vector3(x, y + rowHeight / 2f, slabDepth + 0.0095f), new Vector3(panelWidth * 0.76f, rowHeight * 0.8f, 0.006f), slab);
                }
                y += rowHeight + gap;
            }

            var handleHeight = Mathf.Clamp(1f - h / 2f, -h * 0.2f, h * 0.2f);
            var handleX = w / 2f - margin * 0.55f;
            Part(holder, PrimitiveType.Cube, new Vector3(handleX, handleHeight, slabDepth + 0.003f), new Vector3(0.045f, 0.13f, 0.006f), brass);
            var stem = Part(holder, PrimitiveType.Cylinder, new Vector3(handleX, handleHeight, slabDepth + 0.025f), new Vector3(0.018f, 0.024f, 0.018f), brass);
            stem.localRotation = Quaternion.Euler(90f, 0f, 0f);
            Part(holder, PrimitiveType.Sphere, new Vector3(handleX, handleHeight, slabDepth + 0.056f), new Vector3(0.058f, 0.058f, 0.04f), brass);
        }

        static Transform Part(Transform parent, PrimitiveType shape, Vector3 position, Vector3 scale, Material material)
        {
            var part = GameObject.CreatePrimitive(shape);
            Object.DestroyImmediate(part.GetComponent<Collider>());
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localRotation = Quaternion.identity;
            part.transform.localScale = scale;
            var renderer = part.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.On;
            return part.transform;
        }
    }
}
