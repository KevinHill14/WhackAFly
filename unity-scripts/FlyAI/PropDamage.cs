using System.Collections;
using UnityEngine;

namespace FlyAI
{
    // What happens to a prop when the swatter hits it.
    public class PropDamage : MonoBehaviour
    {
        enum Reaction { Wobble, Knock, Shatter }

        const int ShardCount = 13;

        string type;
        Reaction reaction;
        bool damaged;
        Quaternion? wobbleRest;

        public void Setup(string propType)
        {
            type = propType;
            reaction = ReactionFor(propType);
        }

        public string ImpactSound => SoundFor(type);

        static Reaction ReactionFor(string propType)
        {
            switch (propType)
            {
                case "cup":
                case "jar":
                case "jug":
                case "drinking_glass":
                case "picture_frame":
                case "picture_c":
                case "picture_d":
                case "picture_e":
                case "mirror":
                    return Reaction.Shatter;
                case "potted_plant":
                case "trash_bag":
                case "pillow":
                    return Reaction.Wobble;
                default:
                    return Reaction.Knock;
            }
        }

        static string SoundFor(string propType)
        {
            switch (propType)
            {
                case "drinking_glass":
                case "picture_frame":
                case "picture_c":
                case "picture_d":
                case "picture_e":
                case "mirror":
                    return "impact_glass";
                case "cup":
                case "jar":
                case "jug":
                    return "impact_ceramic";
                case "goblets":
                case "brass_vase":
                case "cutlery":
                    return "impact_metal";
                case "laptop":
                case "phone":
                case "airpods":
                case "radio":
                case "clock":
                case "water_bottle":
                    return "impact_tech";
                case "pillow":
                case "trash_bag":
                case "notebook":
                case "books":
                case "potted_plant":
                    return "impact_soft";
                default:
                    return "impact_wood";
            }
        }

        // Returns true the first time, so each prop only adds to the bill once.
        public bool Hit(Vector3 point, Vector3 direction, float strength)
        {
            var first = !damaged;
            damaged = true;
            switch (reaction)
            {
                case Reaction.Shatter:
                    if (first) Shatter(direction, strength);
                    break;
                case Reaction.Knock:
                    Knock(point, direction, strength);
                    break;
                default:
                    StopAllCoroutines();
                    StartCoroutine(Wobble(direction, strength));
                    break;
            }
            return first;
        }

        void Knock(Vector3 point, Vector3 direction, float strength)
        {
            var body = GetComponent<Rigidbody>();
            if (body == null)
            {
                body = gameObject.AddComponent<Rigidbody>();
                body.mass = 0.5f;
                body.linearDamping = 0.15f;
                body.angularDamping = 0.5f;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                body.interpolation = RigidbodyInterpolation.Interpolate;
            }
            var push = (direction.normalized + Vector3.up * 0.45f).normalized * Mathf.Lerp(0.7f, 1.8f, strength);
            body.AddForceAtPosition(push, point, ForceMode.Impulse);
        }

        IEnumerator Wobble(Vector3 direction, float strength)
        {
            // Kept from the first hit, so a second hit mid-wobble cannot leave it tilted.
            wobbleRest ??= transform.rotation;
            var rest = wobbleRest.Value;
            var axis = Vector3.Cross(Vector3.up, direction).normalized;
            if (axis.sqrMagnitude < 0.01f) axis = transform.right;
            var size = Mathf.Lerp(7f, 15f, strength);
            for (var t = 0f; t < 0.85f; t += Time.deltaTime)
            {
                var angle = Mathf.Sin(t * 19f) * size * Mathf.Exp(-t * 4.6f);
                transform.rotation = Quaternion.AngleAxis(angle, axis) * rest;
                yield return null;
            }
            transform.rotation = rest;
        }

        void Shatter(Vector3 direction, float strength)
        {
            var renderers = GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return;
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
            var material = renderers[0].sharedMaterial;
            foreach (var renderer in renderers) renderer.enabled = false;
            foreach (var hitBox in GetComponentsInChildren<Collider>()) hitBox.enabled = false;
            foreach (var audioSource in GetComponentsInChildren<AudioSource>()) audioSource.Stop();

            var size = Mathf.Clamp(bounds.size.magnitude * 0.13f, 0.012f, 0.06f);
            for (var i = 0; i < ShardCount; i++)
            {
                var shard = GameObject.CreatePrimitive(PrimitiveType.Cube);
                DestroyImmediate(shard.GetComponent<Collider>());
                shard.name = "Shard";
                shard.transform.SetParent(transform, true);
                shard.transform.position = bounds.center + Vector3.Scale(Random.insideUnitSphere, bounds.extents);
                shard.transform.rotation = Random.rotation;
                shard.transform.localScale = new Vector3(Random.Range(0.5f, 1.3f), Random.Range(0.15f, 0.45f), Random.Range(0.4f, 1.1f)) * size;
                shard.GetComponent<Renderer>().sharedMaterial = material;
                var velocity = direction.normalized * Random.Range(0.3f, 1.4f) * strength
                               + Random.insideUnitSphere * 1.25f + Vector3.up * Random.Range(0.4f, 1.5f);
                shard.AddComponent<ShardFx>().Launch(velocity);
            }
        }
    }

    // Lightweight falling debris with no physics body.
    public class ShardFx : MonoBehaviour
    {
        const float Lifetime = 7f;

        Vector3 velocity;
        Vector3 spin;
        Vector3 startScale;
        float ground;
        float age;
        bool resting;

        public void Launch(Vector3 launchVelocity)
        {
            velocity = launchVelocity;
            spin = Random.insideUnitSphere * 540f;
            startScale = transform.localScale;
            ground = 0.004f;
            var nearest = float.MaxValue;
            foreach (var hit in Physics.RaycastAll(transform.position + Vector3.up * 0.06f, Vector3.down, 8f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider is CharacterController || hit.distance >= nearest) continue;
                nearest = hit.distance;
                ground = hit.point.y + 0.004f;
            }
        }

        void Update()
        {
            age += Time.deltaTime;
            if (age >= Lifetime)
            {
                Destroy(gameObject);
                return;
            }
            if (age > Lifetime - 1f) transform.localScale = startScale * (Lifetime - age);
            if (resting) return;

            velocity += Vector3.down * (9.81f * Time.deltaTime);
            var position = transform.position + velocity * Time.deltaTime;
            transform.Rotate(spin * Time.deltaTime, Space.World);
            if (position.y <= ground)
            {
                position.y = ground;
                velocity = new Vector3(velocity.x * 0.45f, -velocity.y * 0.3f, velocity.z * 0.45f);
                spin *= 0.5f;
                if (velocity.sqrMagnitude < 0.12f) resting = true;
            }
            transform.position = position;
        }
    }

    public class LampSway : MonoBehaviour
    {
        // Set to 0 to turn swaying off if it costs too much FPS.
        public static float MaxDegrees = 2.4f;

        float speedA;
        float speedB;
        float phaseA;
        float phaseB;
        Quaternion rest;

        void Start()
        {
            rest = transform.localRotation;
            var seed = transform.position.x * 3.17f + transform.position.z * 5.31f;
            speedA = 1.15f + Mathf.Repeat(seed, 0.5f);
            speedB = 0.8f + Mathf.Repeat(seed * 1.7f, 0.45f);
            phaseA = Mathf.Repeat(seed * 2.3f, Mathf.PI * 2f);
            phaseB = Mathf.Repeat(seed * 4.1f, Mathf.PI * 2f);
        }

        void Update()
        {
            if (MaxDegrees <= 0f) return;
            var gust = 0.55f + Mathf.PerlinNoise(phaseA, Time.time * 0.13f) * 0.75f;
            transform.localRotation = rest * Quaternion.Euler(
                Mathf.Sin(Time.time * speedA + phaseA) * MaxDegrees * gust, 0f,
                Mathf.Sin(Time.time * speedB + phaseB) * MaxDegrees * gust);
        }
    }
}
