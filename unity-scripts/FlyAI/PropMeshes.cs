using System.Collections.Generic;
using UnityEngine;

namespace FlyAI
{
    // Small generated shapes for items that have no free model.
    public static class PropMeshes
    {
        public static List<Vector3> RoundedRect(float width, float length, float radius, float y, int cornerSteps = 5)
        {
            radius = Mathf.Min(radius, width * 0.5f, length * 0.5f);
            var points = new List<Vector3>();
            var cx = width * 0.5f - radius;
            var cz = length * 0.5f - radius;
            Vector2[] centres = { new(cx, cz), new(-cx, cz), new(-cx, -cz), new(cx, -cz) };
            for (var corner = 0; corner < 4; corner++)
                for (var step = 0; step <= cornerSteps; step++)
                {
                    var angle = (corner + step / (float)cornerSteps) * Mathf.PI * 0.5f;
                    points.Add(new Vector3(centres[corner].x + Mathf.Cos(angle) * radius, y, centres[corner].y + Mathf.Sin(angle) * radius));
                }
            return points;
        }

        // A rounded rectangle with thickness, sitting on y = 0.
        public static Mesh Slab(float width, float length, float radius, float thickness, string name)
        {
            var data = new RoomVisuals.MeshData();
            var bottom = RoundedRect(width, length, radius, 0f);
            var top = RoundedRect(width, length, radius, thickness);
            data.AddFace(top.ToArray(), Vector3.up);
            data.AddFace(bottom.ToArray(), Vector3.down);
            for (var i = 0; i < top.Count; i++)
            {
                var next = (i + 1) % top.Count;
                var outward = (bottom[i] + bottom[next]).normalized;
                var edge = bottom[next] - bottom[i];
                if (edge.sqrMagnitude < 1e-9f) continue;
                var normal = Vector3.Cross(Vector3.up, edge).normalized;
                if (Vector3.Dot(normal, outward) < 0f) normal = -normal;
                data.AddFace(new[] { bottom[i], bottom[next], top[next], top[i] }, normal);
            }
            return data.Build(name);
        }

        // A rounded ring, used for the swatter's rim.
        public static Mesh Frame(float width, float length, float radius, float border, float thickness, string name)
        {
            var data = new RoomVisuals.MeshData();
            var outerLow = RoundedRect(width, length, radius, 0f);
            var outerHigh = RoundedRect(width, length, radius, thickness);
            var innerLow = RoundedRect(width - border * 2f, length - border * 2f, Mathf.Max(0.002f, radius - border), 0f);
            var innerHigh = RoundedRect(width - border * 2f, length - border * 2f, Mathf.Max(0.002f, radius - border), thickness);
            for (var i = 0; i < outerLow.Count; i++)
            {
                var next = (i + 1) % outerLow.Count;
                data.AddFace(new[] { innerHigh[i], outerHigh[i], outerHigh[next], innerHigh[next] }, Vector3.up);
                data.AddFace(new[] { innerLow[i], outerLow[i], outerLow[next], innerLow[next] }, Vector3.down);
                var side = (outerLow[i] + outerLow[next]).normalized;
                data.AddFace(new[] { outerLow[i], outerLow[next], outerHigh[next], outerHigh[i] }, side);
                data.AddFace(new[] { innerLow[i], innerLow[next], innerHigh[next], innerHigh[i] }, -side);
            }
            return data.Build(name);
        }

        // Spins a (radius, height) profile around Y, with smooth shading.
        public static Mesh Lathe(Vector2[] profile, string name, int segments = 24)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var triangles = new List<int>();
            for (var j = 0; j < profile.Length; j++)
            {
                var before = profile[Mathf.Max(0, j - 1)];
                var after = profile[Mathf.Min(profile.Length - 1, j + 1)];
                var tangent = (after - before).normalized;
                for (var i = 0; i <= segments; i++)
                {
                    var angle = i / (float)segments * Mathf.PI * 2f;
                    var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                    vertices.Add(direction * profile[j].x + Vector3.up * profile[j].y);
                    normals.Add((direction * tangent.y + Vector3.up * -tangent.x).normalized);
                }
            }
            var ring = segments + 1;
            for (var j = 0; j + 1 < profile.Length; j++)
                for (var i = 0; i < segments; i++)
                {
                    var a = j * ring + i;
                    var b = a + 1;
                    var c = a + ring;
                    var d = c + 1;
                    var facing = Vector3.Cross(vertices[c] - vertices[a], vertices[b] - vertices[a]);
                    if (Vector3.Dot(facing, normals[a] + normals[d]) >= 0f)
                        triangles.AddRange(new[] { a, c, b, b, c, d });
                    else
                        triangles.AddRange(new[] { a, b, c, b, d, c });
                }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // A flat dinner fork, pointing along +Z and centred on its length.
        public static Mesh Fork(float length, string name)
        {
            const float thickness = 0.0022f;
            var parts = new List<(Mesh mesh, Vector3 offset)>
            {
                (Slab(length * 0.085f, length * 0.62f, length * 0.03f, thickness, "handle"), new Vector3(0f, 0f, -length * 0.19f)),
                (Slab(length * 0.15f, length * 0.16f, length * 0.05f, thickness, "head"), new Vector3(0f, 0f, length * 0.15f)),
            };
            for (var i = 0; i < 4; i++)
                parts.Add((Slab(length * 0.022f, length * 0.25f, length * 0.009f, thickness, "tine"),
                    new Vector3((i - 1.5f) * length * 0.042f, 0f, length * 0.36f)));

            var combine = new CombineInstance[parts.Count];
            for (var i = 0; i < parts.Count; i++)
                combine[i] = new CombineInstance { mesh = parts[i].mesh, transform = Matrix4x4.Translate(parts[i].offset) };
            var mesh = new Mesh { name = name };
            mesh.CombineMeshes(combine, true, true);
            foreach (var part in parts) Object.Destroy(part.mesh);
            return mesh;
        }
    }
}
