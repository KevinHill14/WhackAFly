using UnityEngine;
using UnityEngine.Rendering;

namespace FlyAI
{
    // Cosmetic swatter imprints. Each object draws all of its marks as one mesh,
    // so they can be spammed freely; past the limit the oldest mark is reused.
    public class SwatMarks : MonoBehaviour
    {
        const int MaxMarks = 512;
        const float HeadWidth = 0.165f;
        const float HeadHeight = 0.195f;
        const float SurfaceOffset = 0.0035f;
        const float BoundaryHalfThickness = 0.1f;

        static Material material;

        readonly Vector3[] vertices = new Vector3[MaxMarks * 4];
        readonly Vector3[] normals = new Vector3[MaxMarks * 4];
        readonly Vector2[] uvs = new Vector2[MaxMarks * 4];
        Mesh mesh;
        int next;

        public int MarkCount { get; private set; }

        public static void Stamp(RoomPart part, Vector3 origin, Vector3 direction, Vector3 contact, Vector3 contactNormal, Vector3 viewUp, float reach)
        {
            var isProp = part.GetComponent<PropDamage>() != null;
            var visual = part.visual != null ? part.visual : isProp ? part.transform : null;
            Ray[] rays = { new Ray(origin, direction), new Ray(origin, (contact - origin).normalized) };

            var point = contact;
            var normal = contactNormal;
            var found = false;
            if (visual != null)
                foreach (var ray in rays)
                    if (TryMeshHit(visual, ray, reach + 0.3f, out point, out normal))
                    {
                        found = true;
                        break;
                    }

            if (!found)
            {
                // The swing passed through a gap in the furniture's shape, such as a chair back.
                if (part.visual != null && part.kind == RoomPartKind.Furniture) return;
                point = contact;
                normal = contactNormal;
                var hitBox = part.GetComponent<Collider>();
                foreach (var ray in rays)
                    if (hitBox != null && hitBox.enabled && hitBox.Raycast(ray, out var hit, reach + 0.3f))
                    {
                        point = hit.point;
                        normal = hit.normal;
                        break;
                    }
                if (part.kind == RoomPartKind.Boundary) point -= normal * BoundaryHalfThickness;
            }

            var scale = 1f;
            if (isProp && part.TryGetComponent<Collider>(out var propBox))
                scale = Mathf.Clamp(propBox.bounds.size.magnitude / 0.42f, 0.32f, 1f);
            For(part.transform).AddMark(point, normal, viewUp, scale);
        }

        static SwatMarks For(Transform target)
        {
            foreach (Transform child in target)
                if (child.TryGetComponent<SwatMarks>(out var existing)) return existing;
            var go = new GameObject("Swat Marks");
            go.transform.SetParent(target, false);
            return go.AddComponent<SwatMarks>();
        }

        void Awake()
        {
            mesh = new Mesh { name = "Swat Marks" };
            mesh.MarkDynamic();
            var triangles = new int[MaxMarks * 6];
            for (var i = 0; i < MaxMarks; i++)
            {
                var v = i * 4;
                var t = i * 6;
                triangles[t] = v;
                triangles[t + 1] = v + 1;
                triangles[t + 2] = v + 2;
                triangles[t + 3] = v;
                triangles[t + 4] = v + 2;
                triangles[t + 5] = v + 3;
            }
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = gameObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = SharedMaterial();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
        }

        void OnDestroy()
        {
            if (mesh != null) Destroy(mesh);
        }

        void AddMark(Vector3 point, Vector3 normal, Vector3 viewUp, float scale)
        {
            normal = normal.normalized;
            var up = Vector3.ProjectOnPlane(viewUp, normal);
            if (up.sqrMagnitude < 0.01f) up = Vector3.ProjectOnPlane(Vector3.forward, normal);
            up = Quaternion.AngleAxis(Random.Range(-20f, 20f), normal) * up.normalized;
            var right = Vector3.Cross(normal, up).normalized;
            var size = scale * Random.Range(0.94f, 1.06f);
            var halfRight = right * (HeadWidth * 0.5f * size);
            var halfUp = up * (HeadHeight * 0.5f * size);
            var centre = point + normal * (SurfaceOffset + next % 16 * 0.00012f);

            var v = next * 4;
            vertices[v] = transform.InverseTransformPoint(centre - halfRight - halfUp);
            vertices[v + 1] = transform.InverseTransformPoint(centre - halfRight + halfUp);
            vertices[v + 2] = transform.InverseTransformPoint(centre + halfRight + halfUp);
            vertices[v + 3] = transform.InverseTransformPoint(centre + halfRight - halfUp);
            var localNormal = transform.InverseTransformDirection(normal).normalized;
            for (var i = 0; i < 4; i++) normals[v + i] = localNormal;
            uvs[v] = new Vector2(0f, 0f);
            uvs[v + 1] = new Vector2(0f, 1f);
            uvs[v + 2] = new Vector2(1f, 1f);
            uvs[v + 3] = new Vector2(1f, 0f);

            next = (next + 1) % MaxMarks;
            MarkCount = Mathf.Min(MarkCount + 1, MaxMarks);
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.RecalculateBounds();
        }

        static bool TryMeshHit(Transform root, Ray ray, float maxDistance, out Vector3 point, out Vector3 normal)
        {
            point = default;
            normal = default;
            var best = maxDistance;
            var found = false;
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.GetComponent<SwatMarks>() != null) continue;
                var source = filter.sharedMesh;
                var renderer = filter.GetComponent<Renderer>();
                if (source == null || !source.isReadable || renderer == null || !renderer.enabled) continue;
                if (!renderer.bounds.IntersectRay(ray, out var entry) || entry > best) continue;

                var local = filter.transform;
                var origin = local.InverseTransformPoint(ray.origin);
                var direction = local.InverseTransformVector(ray.direction);
                var meshVertices = source.vertices;
                var triangles = source.triangles;
                for (var i = 0; i + 2 < triangles.Length; i += 3)
                {
                    var a = meshVertices[triangles[i]];
                    var edge1 = meshVertices[triangles[i + 1]] - a;
                    var edge2 = meshVertices[triangles[i + 2]] - a;
                    var p = Vector3.Cross(direction, edge2);
                    var determinant = Vector3.Dot(edge1, p);
                    if (Mathf.Abs(determinant) < 1e-9f) continue;
                    var inverse = 1f / determinant;
                    var s = origin - a;
                    var u = Vector3.Dot(s, p) * inverse;
                    if (u < 0f || u > 1f) continue;
                    var q = Vector3.Cross(s, edge1);
                    var v = Vector3.Dot(direction, q) * inverse;
                    if (v < 0f || u + v > 1f) continue;
                    var t = Vector3.Dot(edge2, q) * inverse;
                    if (t <= 0f) continue;
                    var world = local.TransformPoint(origin + direction * t);
                    var distance = Vector3.Distance(ray.origin, world);
                    if (distance >= best) continue;
                    best = distance;
                    point = world;
                    normal = local.TransformDirection(Vector3.Cross(edge1, edge2)).normalized;
                    found = true;
                }
            }
            if (found && Vector3.Dot(normal, ray.direction) > 0f) normal = -normal;
            return found;
        }

        static Material SharedMaterial()
        {
            if (material != null) return material;
            material = RoomProps.ClearSurface(new Color(0.025f, 0.02f, 0.016f, 0.74f), null);
            material.SetFloat("_Smoothness", 0.08f);
            material.SetFloat("_Cull", (float)CullMode.Off);
            material.SetTexture("_BaseMap", CreateTexture());
            return material;
        }

        // The swatter head: a solid rim, a spine and a mesh grid.
        static Texture2D CreateTexture()
        {
            const int width = 132;
            const int height = 156;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true) { name = "Swat Mark", wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
            var pixels = new Color32[width * height];
            const float corner = 25f;
            const float margin = 3f;
            const float rim = 7f;
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var dx = Mathf.Max(margin + corner - x, x - (width - 1 - margin - corner), 0f);
                    var dy = Mathf.Max(margin + corner - y, y - (height - 1 - margin - corner), 0f);
                    var outside = Mathf.Sqrt(dx * dx + dy * dy) - corner;
                    var alpha = 0f;
                    if (outside <= 0f)
                    {
                        var onRim = outside > -rim;
                        var onSpine = Mathf.Abs(x - width * 0.5f) < 2.2f && y > height * 0.12f;
                        var onGrid = x % 10 < 3 || y % 10 < 3;
                        var grain = 0.72f + Mathf.PerlinNoise(x * 0.17f, y * 0.17f) * 0.42f;
                        alpha = (onRim || onSpine ? 0.95f : onGrid ? 0.78f : 0.05f) * grain;
                    }
                    pixels[y * width + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(alpha) * 255f));
                }
            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }
    }
}
