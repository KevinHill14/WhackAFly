using UnityEngine;
using UnityEngine.Rendering;

namespace FlyAI
{
    // A soft visible beam, plus a quick and infrequent flicker.
    public class FlashlightFx : MonoBehaviour
    {
        const float BeamLength = 4.2f;
        const float BeamOpacity = 0.009f;
        const int BeamSegments = 28;
        const int BeamShells = 20;

        Light flashlight;
        Material beamMaterial;
        Mesh beamMesh;
        Color beamColor;
        float baseIntensity;
        float nextFlicker;
        float flickerEnd;

        public void Setup(Light light)
        {
            flashlight = light;
            baseIntensity = light.intensity;
            nextFlicker = Time.time + Random.Range(25f, 70f);
            transform.localPosition = new Vector3(0.16f, -0.16f, 0.25f);

            var beam = new GameObject("Beam");
            beam.transform.SetParent(transform, false);
            beamMesh = CreateCone(Mathf.Tan(light.spotAngle * 0.5f * Mathf.Deg2Rad) * BeamLength * 0.82f);
            beam.AddComponent<MeshFilter>().sharedMesh = beamMesh;
            var renderer = beam.AddComponent<MeshRenderer>();
            beamMaterial = RoomAtmosphere.CreateParticleMaterial(null, true);
            beamMaterial.SetFloat("_Cull", (float)CullMode.Off);
            beamColor = new Color(light.color.r, light.color.g, light.color.b, BeamOpacity);
            beamMaterial.SetColor("_BaseColor", beamColor);
            renderer.sharedMaterial = beamMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        void OnDestroy()
        {
            if (beamMaterial != null) Destroy(beamMaterial);
            if (beamMesh != null) Destroy(beamMesh);
        }

        void Update()
        {
            var now = Time.time;
            if (now >= nextFlicker)
            {
                flickerEnd = now + Random.Range(0.12f, 0.35f);
                nextFlicker = flickerEnd + Random.Range(25f, 70f);
            }
            var level = now < flickerEnd && Mathf.PerlinNoise(now * 46f, 5.3f) < 0.58f ? 0.12f : 1f;
            flashlight.intensity = baseIntensity * level;
            var color = beamColor;
            color.a *= level;
            beamMaterial.SetColor("_BaseColor", color);
        }

        static Mesh CreateCone(float radius)
        {
            // Nested cones add up to a bright core with no hard outer edge.
            var ringVertices = (BeamSegments + 1) * 2;
            var vertices = new Vector3[ringVertices * BeamShells];
            var colors = new Color[vertices.Length];
            var triangles = new int[BeamSegments * 6 * BeamShells];
            for (var shell = 0; shell < BeamShells; shell++)
            {
                var size = (shell + 1f) / BeamShells;
                var strength = 1f - shell / (float)BeamShells;
                var first = shell * ringVertices;
                for (var i = 0; i <= BeamSegments; i++)
                {
                    var angle = i / (float)BeamSegments * Mathf.PI * 2f;
                    var ring = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
                    vertices[first + i * 2] = ring * (0.025f * size);
                    vertices[first + i * 2 + 1] = ring * (radius * size) + Vector3.forward * BeamLength;
                    colors[first + i * 2] = new Color(1f, 1f, 1f, strength);
                    colors[first + i * 2 + 1] = new Color(1f, 1f, 1f, 0f);
                }
                for (var i = 0; i < BeamSegments; i++)
                {
                    var t = (shell * BeamSegments + i) * 6;
                    var v = first + i * 2;
                    triangles[t] = v;
                    triangles[t + 1] = v + 1;
                    triangles[t + 2] = v + 2;
                    triangles[t + 3] = v + 2;
                    triangles[t + 4] = v + 1;
                    triangles[t + 5] = v + 3;
                }
            }
            var mesh = new Mesh { name = "Flashlight Beam", vertices = vertices, colors = colors, triangles = triangles };
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
