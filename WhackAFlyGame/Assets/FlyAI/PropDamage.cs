using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace FlyAI
{
    // What happens to a prop when the swatter hits it.
    public class PropDamage : MonoBehaviour
    {
        enum Reaction { Wobble, Knock, Shatter, Squish }

        const int ShardCount = 13;
        const int MaxSplats = 3;
        const float ShockRadius = 0.45f;

        static readonly Dictionary<Color, Material> SplatMaterials = new Dictionary<Color, Material>();
        static Texture2D splatTexture;

        string type;
        Reaction reaction;
        bool shattered;
        int squishes;
        Vector3 restScale;
        Quaternion? wobbleRest;

        public void Setup(string propType)
        {
            type = propType;
            reaction = ReactionFor(propType);
            restScale = transform.localScale;
        }

        public string ImpactSound => SoundFor(type);

        static Reaction ReactionFor(string propType)
        {
            switch (propType)
            {
                case "cup":
                case "jar":
                case "jug":
                case "plate":
                case "place_setting":
                case "small_plant":
                case "drinking_glass":
                case "picture_frame":
                case "picture_c":
                case "picture_d":
                case "picture_e":
                case "mirror":
                    return Reaction.Shatter;
                case "fruit_bowl":
                case "fruit_bowl_lemon":
                case "fruit_bowl_pear":
                case "fruit_basket":
                case "rotten_banana":
                case "takeout_box":
                case "water_bottle":
                case "trash_bag":
                case "pillow":
                    return Reaction.Squish;
                case "potted_plant":
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
                case "plate":
                case "place_setting":
                case "small_plant":
                    return "impact_ceramic";
                case "fruit_bowl":
                case "fruit_bowl_lemon":
                case "fruit_bowl_pear":
                case "fruit_basket":
                case "rotten_banana":
                    return "impact_squish";
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
                case "takeout_box":
                    return "impact_soft";
                default:
                    return "impact_wood";
            }
        }

        static bool SplatColor(string propType, out Color color)
        {
            switch (propType)
            {
                case "fruit_bowl": color = new Color(0.42f, 0.04f, 0.05f, 0.82f); return true;
                case "fruit_bowl_lemon": color = new Color(0.78f, 0.66f, 0.1f, 0.78f); return true;
                case "fruit_bowl_pear": color = new Color(0.56f, 0.6f, 0.18f, 0.78f); return true;
                case "fruit_basket": color = new Color(0.3f, 0.45f, 0.1f, 0.8f); return true;
                case "rotten_banana": color = new Color(0.5f, 0.38f, 0.1f, 0.84f); return true;
                case "water_bottle": color = new Color(0.45f, 0.58f, 0.68f, 0.42f); return true;
                default: color = default; return false;
            }
        }

        public void Hit(Vector3 point, Vector3 direction, float strength)
        {
            switch (reaction)
            {
                case Reaction.Shatter:
                    if (!shattered) Shatter(direction, strength);
                    break;
                case Reaction.Knock:
                    Knock(point, direction, strength);
                    break;
                case Reaction.Squish:
                    Squish();
                    break;
                default:
                    StartWobble(direction, strength);
                    break;
            }
        }

        // A hard hit rattles whatever is sitting nearby.
        public static void ShakeNearby(Vector3 point, float strength, PropDamage hitProp)
        {
            var seen = new HashSet<PropDamage>();
            foreach (var hitBox in Physics.OverlapSphere(point, ShockRadius, ~0, QueryTriggerInteraction.Ignore))
            {
                var prop = hitBox.GetComponent<PropDamage>();
                if (prop == null || prop == hitProp || !seen.Add(prop) || prop.shattered) continue;
                var away = prop.transform.position - point;
                away.y = 0f;
                if (prop.reaction == Reaction.Knock) prop.Knock(hitBox.bounds.center, away, strength * 0.35f);
                else prop.StartWobble(away.sqrMagnitude > 0.0001f ? away : Vector3.forward, strength * 0.5f);
            }
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

        void StartWobble(Vector3 direction, float strength)
        {
            StopAllCoroutines();
            StartCoroutine(Wobble(direction, strength));
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

        void Squish()
        {
            squishes++;
            var height = Mathf.Max(0.16f, Mathf.Pow(0.42f, squishes));
            var spread = 1f + (1f - height) * 0.35f;
            var target = new Vector3(restScale.x * spread, restScale.y * height, restScale.z * spread);
            StopAllCoroutines();
            StartCoroutine(SquishTo(target));
            if (squishes <= MaxSplats && SplatColor(type, out var color)) AddSplat(color);
        }

        IEnumerator SquishTo(Vector3 target)
        {
            var start = transform.localScale;
            for (var t = 0f; t < 1f; t += Time.unscaledDeltaTime / 0.07f)
            {
                transform.localScale = Vector3.Lerp(start, target, t);
                yield return null;
            }
            transform.localScale = target;
        }

        void AddSplat(Color color)
        {
            var ground = 0f;
            var nearest = float.MaxValue;
            foreach (var hit in Physics.RaycastAll(transform.position + Vector3.up * 0.2f, Vector3.down, 4f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider.transform == transform || hit.collider is CharacterController || hit.distance >= nearest) continue;
                nearest = hit.distance;
                ground = hit.point.y;
            }

            if (!SplatMaterials.TryGetValue(color, out var material) || material == null)
            {
                material = RoomProps.ClearSurface(color, null);
                material.SetFloat("_Smoothness", 0.62f);
                if (splatTexture == null) splatTexture = CreateSplatTexture();
                material.SetTexture("_BaseMap", splatTexture);
                SplatMaterials[color] = material;
            }

            var splat = GameObject.CreatePrimitive(PrimitiveType.Quad);
            splat.name = "Splat";
            DestroyImmediate(splat.GetComponent<Collider>());
            splat.transform.SetParent(transform.parent, true);
            var offset = Random.insideUnitCircle * 0.05f;
            splat.transform.SetPositionAndRotation(new Vector3(transform.position.x + offset.x, ground + 0.004f + squishes * 0.0005f, transform.position.z + offset.y),
                Quaternion.Euler(90f, Random.Range(0f, 360f), 0f));
            var size = Random.Range(0.24f, 0.36f);
            splat.transform.localScale = new Vector3(size, size * Random.Range(0.75f, 1f), 1f);
            var renderer = splat.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        static Texture2D CreateSplatTexture()
        {
            const int size = 128;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "Splat", wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var u = x / 63.5f - 1f;
                    var v = y / 63.5f - 1f;
                    var angle = Mathf.Atan2(v, u);
                    var edge = 0.56f + Mathf.Sin(angle * 5f + 0.7f) * 0.12f + Mathf.Sin(angle * 11f) * 0.09f + Mathf.Sin(angle * 23f + 2f) * 0.04f;
                    var alpha = Mathf.Clamp01((edge - Mathf.Sqrt(u * u + v * v)) * 14f);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }

        void Shatter(Vector3 direction, float strength)
        {
            var renderers = GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return;
            shattered = true;
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
            var material = renderers[0].sharedMaterial;
            foreach (var renderer in renderers) renderer.enabled = false;
            foreach (var hitBox in GetComponentsInChildren<Collider>()) hitBox.enabled = false;
            foreach (var audioSource in GetComponentsInChildren<AudioSource>()) audioSource.Stop();

            var size = Mathf.Clamp(bounds.size.magnitude * 0.13f, 0.012f, 0.06f);
            for (var i = 0; i < ShardCount; i++)
            {
                var position = bounds.center + Vector3.Scale(Random.insideUnitSphere, bounds.extents);
                var velocity = direction.normalized * Random.Range(0.3f, 1.4f) * strength
                               + Random.insideUnitSphere * 1.25f + Vector3.up * Random.Range(0.4f, 1.5f);
                ShardFx.Spawn(transform, position, size, material, velocity);
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

        public static void Spawn(Transform parent, Vector3 position, float size, Material material, Vector3 launchVelocity, float thickness = 1f)
        {
            var shard = GameObject.CreatePrimitive(PrimitiveType.Cube);
            DestroyImmediate(shard.GetComponent<Collider>());
            shard.name = "Shard";
            shard.transform.SetParent(parent, true);
            shard.transform.SetPositionAndRotation(position, Random.rotation);
            shard.transform.localScale = new Vector3(Random.Range(0.5f, 1.3f), Random.Range(0.15f, 0.45f) * thickness, Random.Range(0.4f, 1.1f)) * size;
            shard.GetComponent<Renderer>().sharedMaterial = material;
            shard.AddComponent<ShardFx>().Launch(launchVelocity);
        }

        void Launch(Vector3 launchVelocity)
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
