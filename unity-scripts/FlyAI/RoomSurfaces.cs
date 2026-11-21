using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FlyAI
{
    // Textures, generated UVs and wall trim. Everything is sized from the scanned room.
    public static class RoomSurfaces
    {
        const string TextureFolder = "FlyAITextures/";
        const float BaseboardFront = 0.095f;
        const float BaseboardHeight = 0.125f;
        const float CrownDepth = 0.12f;
        const float CrownHeight = 0.12f;
        const float WallFace = 0.075f;

        static string TextureSet(string category)
        {
            switch (category)
            {
                case "wall": return "Wall";
                case "floor": return "Floor";
                case "ceiling": return "Ceiling";
                case "table":
                case "seat":
                case "storage": return "Wood";
                default: return null;
            }
        }

        static float TileSize(string category)
        {
            switch (category)
            {
                case "wall": return 2.4f;
                case "floor": return 1.8f;
                case "ceiling": return 2.6f;
                default: return 1.1f;
            }
        }

        static Color Tint(string category, RoomTheme theme)
        {
            var color = RoomVisuals.ColorFor(category, theme);
            switch (category)
            {
                case "floor": return Color.Lerp(Color.white, color, 0.3f);
                case "ceiling": return Color.Lerp(Color.white, color, 0.7f);
                case "table":
                case "seat":
                case "storage": return Color.Lerp(Color.white, color, 0.15f);
                case "wall": return Color.Lerp(Color.white, color, 0.75f);
                default: return color;
            }
        }

        // Falls back to a flat theme colour if a texture is missing.
        public static Material MaterialFor(string category, RoomTheme theme, RoomAssets assets)
        {
            var set = TextureSet(category);
            var colorMap = set == null ? null : LoadTexture(set + "_Color", theme);
            var tint = colorMap != null ? Tint(category, theme) : RoomVisuals.ColorFor(category, theme);
            var material = assets.Keep(RoomVisuals.LitMaterial(tint, RoomVisuals.SmoothnessFor(category)));
            if (colorMap == null) return material;

            material.SetTexture("_BaseMap", colorMap);
            var normalMap = LoadTexture(set + "_Normal", theme);
            if (normalMap != null)
            {
                material.SetTexture("_BumpMap", normalMap);
                material.SetFloat("_BumpScale", 0.8f);
                material.EnableKeyword("_NORMALMAP");
            }
            return material;
        }

        // A theme's own texture wins; otherwise the house texture. Themes without a folder load the house one directly.
        static Texture2D LoadTexture(string file, RoomTheme theme)
        {
            if (!string.IsNullOrEmpty(theme.folder))
            {
                var themed = Resources.Load<Texture2D>(TextureFolder + theme.folder + "/" + file);
                if (themed != null) return themed;
            }
            return Resources.Load<Texture2D>(TextureFolder + file);
        }

        // The direction of the room's longest scanned wall, so floorboards follow the room.
        public static Vector3 MainAxis(RoomData room)
        {
            var best = Vector3.right;
            var longest = 0f;
            foreach (var wall in room.walls)
            {
                var direction = RoomLoader.ToUnity(wall.end[0], 0f, wall.end[1]) - RoomLoader.ToUnity(wall.start[0], 0f, wall.start[1]);
                if (direction.sqrMagnitude <= longest) continue;
                longest = direction.sqrMagnitude;
                best = direction.normalized;
            }
            return best;
        }

        public static Vector3 AxisFor(Transform part, Transform root, string category, RoomData room, Vector3 mainAxis)
        {
            if (category == "floor" || category == "ceiling" || category == "wall") return mainAxis;
            for (var t = part; t != null && t != root; t = t.parent)
                foreach (var item in room.objects)
                    if (item.node == t.name)
                        return Quaternion.Euler(0f, -item.rotationY, 0f) * Vector3.right;
            return mainAxis;
        }

        // Scan meshes have no usable UVs, so each triangle is projected onto its own plane.
        public static Mesh WithProjectedUVs(Mesh source, Transform transform, string category, Vector3 axis)
        {
            var tile = TileSize(category);
            var across = Vector3.Cross(Vector3.up, axis).normalized;
            var sourceVertices = source.vertices;
            var sourceNormals = source.normals;
            var hasNormals = sourceNormals.Length == sourceVertices.Length;
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var submeshes = new List<int[]>();

            for (var sub = 0; sub < source.subMeshCount; sub++)
            {
                var triangles = source.GetTriangles(sub);
                var output = new int[triangles.Length];
                for (var i = 0; i + 2 < triangles.Length; i += 3)
                {
                    var a = transform.TransformPoint(sourceVertices[triangles[i]]);
                    var b = transform.TransformPoint(sourceVertices[triangles[i + 1]]);
                    var c = transform.TransformPoint(sourceVertices[triangles[i + 2]]);
                    var face = Vector3.Cross(b - a, c - a).normalized;
                    var flat = Mathf.Abs(face.y) > 0.7f;
                    var side = flat ? across : Vector3.Cross(Vector3.up, face).normalized;
                    var along = flat ? axis : Vector3.up;
                    var localFace = transform.InverseTransformDirection(face);

                    for (var corner = 0; corner < 3; corner++)
                    {
                        var index = triangles[i + corner];
                        var world = corner == 0 ? a : corner == 1 ? b : c;
                        output[i + corner] = vertices.Count;
                        vertices.Add(sourceVertices[index]);
                        normals.Add(hasNormals ? sourceNormals[index] : localFace);
                        uvs.Add(new Vector2(Vector3.Dot(world, side), Vector3.Dot(world, along)) / tile);
                    }
                }
                submeshes.Add(output);
            }

            var mesh = new Mesh { name = source.name + " Textured" };
            if (vertices.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = submeshes.Count;
            for (var sub = 0; sub < submeshes.Count; sub++) mesh.SetTriangles(submeshes[sub], sub);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        // Baseboards and crown molding that follow the room's floor outline.
        public static void BuildTrim(Transform parent, RoomData room, RoomTheme theme, RoomAssets assets)
        {
            var outline = RoomVisuals.Outline(room);
            if (outline.Count < 3) return;
            var height = room.ceilingHeight;
            var data = new RoomVisuals.MeshData();
            Extrude(data, outline, new[]
            {
                new Vector2(BaseboardFront, 0f),
                new Vector2(BaseboardFront, BaseboardHeight - 0.03f),
                new Vector2(BaseboardFront - 0.008f, BaseboardHeight - 0.01f),
                new Vector2(WallFace, BaseboardHeight),
                new Vector2(0f, BaseboardHeight),
            });
            Extrude(data, outline, new[]
            {
                new Vector2(0f, height - CrownHeight),
                new Vector2(WallFace, height - CrownHeight),
                new Vector2(WallFace + 0.03f, height - CrownHeight * 0.5f),
                new Vector2(CrownDepth, height),
            });

            var go = new GameObject("Trim");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = assets.Keep(data.Build("Trim"));
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = assets.Keep(RoomVisuals.LitMaterial(theme.trim, 0.38f));
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;
        }

        // profile points are (distance in from the wall line, height).
        static void Extrude(RoomVisuals.MeshData data, List<Vector3> outline, Vector2[] profile)
        {
            var count = outline.Count;
            var area = 0f;
            for (var i = 0; i < count; i++)
            {
                var p = outline[i];
                var q = outline[(i + 1) % count];
                area += p.x * q.z - q.x * p.z;
            }
            var sign = area > 0f ? 1f : -1f;
            var inward = new Vector3[count];
            for (var i = 0; i < count; i++)
            {
                var along = (outline[(i + 1) % count] - outline[i]).normalized;
                inward[i] = new Vector3(-along.z, 0f, along.x) * sign;
            }

            Vector3 Ring(int corner, Vector2 point)
            {
                var previous = inward[(corner + count - 1) % count];
                var next = inward[corner];
                var miter = (previous + next).normalized;
                var scale = 1f / Mathf.Max(0.35f, Vector3.Dot(miter, next));
                return outline[corner] + miter * (point.x * scale) + Vector3.up * point.y;
            }

            for (var edge = 0; edge < count; edge++)
            {
                var following = (edge + 1) % count;
                for (var j = 0; j + 1 < profile.Length; j++)
                {
                    var step = profile[j + 1] - profile[j];
                    var normal = (inward[edge] * step.y + Vector3.up * -step.x).normalized;
                    data.AddFace(new[]
                    {
                        Ring(edge, profile[j]),
                        Ring(following, profile[j]),
                        Ring(following, profile[j + 1]),
                        Ring(edge, profile[j + 1]),
                    }, normal);
                }
            }
        }
    }
}
