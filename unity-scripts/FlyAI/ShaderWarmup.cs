using System.Collections.Generic;
using UnityEngine;

namespace FlyAI
{
    // Draws one tiny copy of every distinct material in a freshly built room, in front of the camera, for a few
    // frames. That makes the graphics driver compile their shaders now (behind the main menu) instead of causing
    // hitches the first time each one shows up during the round.
    public class ShaderWarmup : MonoBehaviour
    {
        const int Frames = 4;
        const int MaxMaterials = 250;

        readonly List<Material> materials = new List<Material>();
        Mesh quad;
        int frames;

        void Start()
        {
            // One material per distinct shader and keyword set is enough to compile every variant in use.
            var seen = new HashSet<string>();
            foreach (var renderer in GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is ParticleSystemRenderer) continue;
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null || material.shader == null) continue;
                    var key = material.shader.GetInstanceID() + "|" + string.Join(" ", material.shaderKeywords) + "|" + material.renderQueue;
                    if (seen.Add(key) && materials.Count < MaxMaterials) materials.Add(material);
                }
            }

            var holder = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad = holder.GetComponent<MeshFilter>().sharedMesh;
            Destroy(holder);
        }

        void Update()
        {
            var camera = Camera.main;
            if (camera == null || quad == null) return;
            if (frames++ >= Frames)
            {
                Destroy(this);
                return;
            }
            var transform = camera.transform;
            var matrix = Matrix4x4.TRS(transform.position + transform.forward * 2f, transform.rotation, Vector3.one * 0.02f);
            foreach (var material in materials)
                Graphics.DrawMesh(quad, matrix, material, 0, camera, 0, null, false, false);
        }
    }
}
