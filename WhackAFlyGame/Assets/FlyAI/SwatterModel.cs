using UnityEngine;
using UnityEngine.Rendering;

namespace FlyAI
{
    // A proper fly swatter: rubber grip, wire shaft, and a mesh head with a rim.
    public static class SwatterModel
    {
        const float HeadWidth = 0.165f;
        const float HeadHeight = 0.195f;
        const float HeadCentre = 0.455f;

        public static void Build(Transform parent)
        {
            var grip = RoomVisuals.LitMaterial(new Color(0.09f, 0.09f, 0.1f), 0.28f);
            var wire = RoomVisuals.LitMaterial(new Color(0.62f, 0.63f, 0.64f), 0.72f);
            var plastic = RoomVisuals.LitMaterial(new Color(0.08f, 0.5f, 0.27f), 0.46f);

            Add(parent, PropMeshes.Lathe(new[]
            {
                new Vector2(0.0005f, 0f), new Vector2(0.0125f, 0.002f), new Vector2(0.0145f, 0.012f), new Vector2(0.0125f, 0.04f),
                new Vector2(0.0142f, 0.065f), new Vector2(0.0122f, 0.095f), new Vector2(0.0135f, 0.12f), new Vector2(0.0085f, 0.138f),
                new Vector2(0.0005f, 0.142f),
            }, "Swatter Grip"), Vector3.zero, Quaternion.identity, grip);

            Add(parent, PropMeshes.Lathe(new[]
            {
                new Vector2(0.0035f, 0.13f), new Vector2(0.0032f, 0.25f), new Vector2(0.003f, 0.335f), new Vector2(0.0005f, 0.337f),
            }, "Swatter Shaft", 10), Vector3.zero, Quaternion.identity, wire);

            Add(parent, PropMeshes.Lathe(new[]
            {
                new Vector2(0.0048f, 0.325f), new Vector2(0.015f, 0.346f), new Vector2(0.021f, 0.36f), new Vector2(0.0005f, 0.362f),
            }, "Swatter Collar", 12), Vector3.zero, Quaternion.identity, plastic);

            var upright = Quaternion.Euler(90f, 0f, 0f);
            Add(parent, PropMeshes.Frame(HeadWidth, HeadHeight, 0.032f, 0.009f, 0.006f, "Swatter Rim"),
                new Vector3(0f, HeadCentre, 0.003f), upright, plastic);

            var spine = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Finish(spine, parent, new Vector3(0f, HeadCentre - 0.02f, 0f), Quaternion.identity, new Vector3(0.006f, HeadHeight * 0.82f, 0.0035f), plastic);

            var mesh = GameObject.CreatePrimitive(PrimitiveType.Quad);
            var meshMaterial = CutoutMaterial(new Color(0.12f, 0.62f, 0.34f));
            Finish(mesh, parent, new Vector3(0f, HeadCentre, 0f), Quaternion.identity, new Vector3(HeadWidth - 0.012f, HeadHeight - 0.012f, 1f), meshMaterial);
        }

        static void Add(Transform parent, Mesh mesh, Vector3 position, Quaternion rotation, Material material)
        {
            var go = new GameObject(mesh.name);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>();
            Finish(go, parent, position, rotation, Vector3.one, material);
        }

        static void Finish(GameObject go, Transform parent, Vector3 position, Quaternion rotation, Vector3 scale, Material material)
        {
            var hitBox = go.GetComponent<Collider>();
            if (hitBox != null) Object.DestroyImmediate(hitBox);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localRotation = rotation;
            go.transform.localScale = scale;
            var renderer = go.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
        }

        static Material CutoutMaterial(Color color)
        {
            var material = RoomVisuals.LitMaterial(color, 0.3f);
            material.SetTexture("_BaseMap", CreateGrid());
            material.SetTextureScale("_BaseMap", new Vector2(9f, 11f));
            material.SetFloat("_AlphaClip", 1f);
            material.SetFloat("_Cutoff", 0.5f);
            material.SetFloat("_Cull", (float)CullMode.Off);
            material.EnableKeyword("_ALPHATEST_ON");
            material.renderQueue = (int)RenderQueue.AlphaTest;
            material.SetOverrideTag("RenderType", "TransparentCutout");
            return material;
        }

        static Texture2D CreateGrid()
        {
            const int size = 32;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, name = "Swatter Mesh" };
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var solid = x < size * 0.36f || y < size * 0.36f;
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(solid ? 255 : 0));
                }
            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }
    }
}
