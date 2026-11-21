using UnityEngine;

namespace FlyAI
{
    // Draws the fly the backend simulates. No game logic: it only smooths what it is sent.
    public class FlyView : MonoBehaviour
    {
        Transform body;
        Transform wingL;
        Transform wingR;
        AudioSource buzz;
        Vector3 targetPos;
        Vector3 heading = Vector3.forward;
        string state = "idle";
        bool placed;

        public static FlyView Create()
        {
            var go = new GameObject("Fly");
            return go.AddComponent<FlyView>();
        }

        void Awake()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var dark = new Material(shader) { color = new Color(0.04f, 0.04f, 0.045f) };
            var glass = new Material(shader) { color = new Color(0.7f, 0.75f, 0.8f) };

            body = Part(PrimitiveType.Sphere, transform, dark, Vector3.zero, new Vector3(0.022f, 0.018f, 0.04f));
            wingL = Part(PrimitiveType.Sphere, body, glass, new Vector3(-0.6f, 0.4f, 0f), new Vector3(0.9f, 0.05f, 0.5f));
            wingR = Part(PrimitiveType.Sphere, body, glass, new Vector3(0.6f, 0.4f, 0f), new Vector3(0.9f, 0.05f, 0.5f));

            buzz = gameObject.AddComponent<AudioSource>();
            buzz.clip = FlySounds.Get("light_buzz");
            buzz.loop = true;
            buzz.spatialBlend = 1f;
            buzz.minDistance = 0.3f;
            buzz.maxDistance = 6f;
            buzz.pitch = 2.4f;
            buzz.volume = 0.25f;
            buzz.Play();
        }

        static Transform Part(PrimitiveType type, Transform parent, Material material, Vector3 localPos, Vector3 scale)
        {
            var part = GameObject.CreatePrimitive(type);
            Destroy(part.GetComponent<Collider>());   // the fly is hit by the backend, not by physics
            part.GetComponent<Renderer>().sharedMaterial = material;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = localPos;
            part.transform.localScale = scale;
            return part.transform;
        }

        public void Apply(BackendMessage m)
        {
            if (m.pos == null || m.pos.Length != 3) return;
            targetPos = RoomLoader.ToUnity(m.pos[0], m.pos[1], m.pos[2]);
            if (m.heading != null && m.heading.Length == 3)
            {
                var h = RoomLoader.ToUnity(m.heading[0], m.heading[1], m.heading[2]);
                if (h.sqrMagnitude > 0.0001f) heading = h.normalized;
            }
            state = m.state;
            if (!placed)
            {
                transform.position = targetPos;
                placed = true;
            }
        }

        public void OnEvent(BackendMessage m)
        {
            if (m.kind == "escape" && buzz != null) buzz.pitch = 3.2f;
        }

        void Update()
        {
            if (!placed) return;
            var dead = state == "dead";
            body.gameObject.SetActive(!dead);
            if (buzz != null) buzz.mute = dead;
            if (dead) return;

            transform.position = Vector3.Lerp(transform.position, targetPos, 1f - Mathf.Exp(-18f * Time.deltaTime));
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(heading), 1f - Mathf.Exp(-10f * Time.deltaTime));

            var flying = state == "flying" || state == "fleeing";
            var flap = flying ? Mathf.Sin(Time.time * 90f) * 35f : 0f;
            wingL.localRotation = Quaternion.Euler(0f, 0f, flap);
            wingR.localRotation = Quaternion.Euler(0f, 0f, -flap);
            if (buzz != null) buzz.pitch = Mathf.MoveTowards(buzz.pitch, flying ? 2.8f : 1.6f, 4f * Time.deltaTime);
        }
    }
}
