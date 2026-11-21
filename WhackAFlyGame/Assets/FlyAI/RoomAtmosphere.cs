using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FlyAI
{
    // One lamp whose light and glowing fixture dim and stutter together.
    public class LightRig
    {
        public Light main;
        public Light fill;
        public Material glow;
        public Color glowBase;
        public float mainBase;
        public float fillBase;
        public float noise;
        public float nextBurst;
        public float burstEnd;
        public float burstDepth;
        public bool failing;
        public System.Random random;
    }

    // Drifting haze, breathing fog and unreliable lights. Sized from the scanned room.
    public class RoomAtmosphere : MonoBehaviour
    {
        const float HazePerSquareMetre = 0.7f;
        const int MinHaze = 10;
        const int MaxHaze = 90;
        const float DustPerSquareMetre = 5f;
        const int MinDust = 60;
        const int MaxDust = 450;
        const float PingDuration = 1.2f;
        const float DefaultChairLength = 0.6f;

        public static RoomAtmosphere Current { get; private set; }
        public static bool FogEnabled { get; private set; } = true;

        readonly List<LightRig> rigs = new List<LightRig>();
        List<Vector3> outline;
        System.Random random;
        ParticleSystem haze;
        ParticleSystem dust;
        RoomEvents events;
        Material dustMaterial;
        Color dustColor;
        int dustTarget;
        float fogStart;
        float fogFade;
        float fogClearDistance;
        Material hazeMaterial;
        Color hazeColor;
        RoomTheme theme;
        Vector3 boundsMin;
        Vector3 boundsMax;
        float ceiling;
        float largestPuff;
        float noiseSeed;
        float pingStartedAt = -10f;
        int hazeTarget;

        public static void ToggleFog()
        {
            FogEnabled = !FogEnabled;
            if (Current != null) Current.ApplyFogState();
            else RenderSettings.fog = FogEnabled;
            Debug.Log($"[FlyAI] Fog {(FogEnabled ? "on" : "off")}");
        }

        public void Ping() => pingStartedAt = Time.unscaledTime;

        public void Bind(RoomEvents roomEvents) => events = roomEvents;

        float Power => events != null ? events.Power : 1f;
        float LightBoost => events != null ? events.LightBoost : 1f;
        float FogLift => events != null ? events.FogLift : 0f;

        public void Setup(RoomData room, RoomTheme roomTheme, List<LightRig> lightRigs, RoomAssets assets)
        {
            Current = this;
            theme = roomTheme;
            ceiling = room.ceilingHeight;
            outline = RoomVisuals.Outline(room);

            var seed = 17;
            boundsMin = boundsMax = outline.Count > 0 ? outline[0] : Vector3.zero;
            foreach (var point in outline)
            {
                seed = unchecked(seed * 31 + Mathf.RoundToInt(point.x * 100f));
                seed = unchecked(seed * 31 + Mathf.RoundToInt(point.z * 100f));
                boundsMin = Vector3.Min(boundsMin, point);
                boundsMax = Vector3.Max(boundsMax, point);
            }
            random = new System.Random(seed);
            noiseSeed = (float)random.NextDouble() * 100f;

            // You can see this many chair lengths before things fade into the dark.
            fogStart = theme.visibleChairLengths * ChairLength(room);
            fogFade = Mathf.Max(theme.fogFadeDistance, fogStart * 1.6f);
            fogClearDistance = (boundsMax - boundsMin).magnitude + ceiling;
            RenderSettings.fogStartDistance = fogStart;
            RenderSettings.fogEndDistance = fogStart + fogFade;

            rigs.AddRange(lightRigs);
            var failing = rigs.Count > 1 ? random.Next(rigs.Count) : -1;
            for (var i = 0; i < rigs.Count; i++)
            {
                var rig = rigs[i];
                rig.random = new System.Random(unchecked(seed + i * 7919));
                rig.noise = (float)rig.random.NextDouble() * 100f;
                rig.failing = i == failing;
                rig.nextBurst = Time.time + NextGap(rig);
                rig.burstDepth = 0.3f;
            }

            BuildHaze(room, assets);
            ApplyFogState();
        }

        void OnDestroy()
        {
            if (Current == this) Current = null;
        }

        void ApplyFogState()
        {
            RenderSettings.fog = FogEnabled;
            if (haze != null) haze.gameObject.SetActive(FogEnabled);
            if (dust != null) dust.gameObject.SetActive(FogEnabled);
        }

        static float NextGap(LightRig rig)
        {
            var value = (float)rig.random.NextDouble();
            return rig.failing ? Mathf.Lerp(2.5f, 8f, value) : Mathf.Lerp(10f, 28f, value);
        }

        void Update()
        {
            UpdateLights();
            if (!FogEnabled || haze == null) return;
            UpdateFog();
            for (var i = 0; i < 2 && haze.particleCount < hazeTarget; i++) EmitPuff();
            for (var i = 0; i < 4 && dust.particleCount < dustTarget; i++) EmitDust();
        }

        void UpdateLights()
        {
            var now = Time.time;
            foreach (var rig in rigs)
            {
                if (now >= rig.nextBurst)
                {
                    var length = Mathf.Lerp(0.12f, rig.failing ? 1.1f : 0.5f, (float)rig.random.NextDouble());
                    rig.burstEnd = now + length;
                    rig.burstDepth = Mathf.Lerp(rig.failing ? 0.05f : 0.3f, 0.6f, (float)rig.random.NextDouble());
                    rig.nextBurst = rig.burstEnd + NextGap(rig);
                }

                var level = 1f + (Mathf.PerlinNoise(rig.noise, now * 0.35f) - 0.5f) * 0.16f;
                var calm = events != null && events.CalmLights;
                if (!calm && now < rig.burstEnd && Mathf.PerlinNoise(rig.noise + 9f, now * 34f) < 0.55f) level *= rig.burstDepth;
                level *= Power * LightBoost;

                rig.main.intensity = rig.mainBase * level;
                if (rig.fill != null) rig.fill.intensity = rig.fillBase * level;
                if (rig.glow != null) rig.glow.SetColor("_BaseColor", rig.glowBase * level);
            }
        }

        void UpdateFog()
        {
            var breathing = 1f + (Mathf.PerlinNoise(noiseSeed, Time.time * 0.07f) - 0.5f) * 0.14f;
            var pingAge = (Time.unscaledTime - pingStartedAt) / PingDuration;
            var clear = Mathf.Max(pingAge < 1f ? 1f - pingAge * pingAge : 0f, FogLift);
            RenderSettings.fogStartDistance = Mathf.Lerp(fogStart, fogClearDistance, clear);
            RenderSettings.fogEndDistance = Mathf.Lerp(fogStart + fogFade * breathing, fogClearDistance * 2f, clear);
            var color = hazeColor;
            var powerFade = Mathf.Lerp(0.22f, 1f, Power);
            color.a *= (1f - clear) * powerFade;
            hazeMaterial.SetColor("_BaseColor", color);
            var motes = dustColor;
            motes.a *= powerFade;
            dustMaterial.SetColor("_BaseColor", motes);
        }

        // The typical chair in this scan, so visibility matches the room's real scale.
        static float ChairLength(RoomData room)
        {
            var lengths = new List<float>();
            if (room.objects != null)
                foreach (var item in room.objects)
                    if ((item.label == "chair" || item.label == "stool") && item.size != null && item.size.Length == 3)
                        lengths.Add(Mathf.Max(item.size[0], item.size[2]));
            if (lengths.Count == 0) return DefaultChairLength;
            lengths.Sort();
            return Mathf.Clamp(lengths[lengths.Count / 2], 0.45f, 1f);
        }

        void BuildDust(RoomData room)
        {
            var go = new GameObject("Room Dust");
            go.transform.SetParent(transform, false);
            dust = go.AddComponent<ParticleSystem>();
            dust.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            dustTarget = Mathf.Clamp(Mathf.RoundToInt(room.floor.area * DustPerSquareMetre), MinDust, MaxDust);

            var main = dust.main;
            main.loop = true;
            main.playOnAwake = false;
            main.maxParticles = dustTarget;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var emission = dust.emission;
            emission.enabled = false;
            var shape = dust.shape;
            shape.enabled = false;

            var fade = dust.colorOverLifetime;
            fade.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.2f), new GradientAlphaKey(0.6f, 0.55f), new GradientAlphaKey(1f, 0.8f), new GradientAlphaKey(0f, 1f) });
            fade.color = gradient;

            var drift = dust.noise;
            drift.enabled = true;
            drift.strength = 0.035f;
            drift.frequency = 0.22f;
            drift.scrollSpeed = 0.08f;
            drift.quality = ParticleSystemNoiseQuality.Low;

            dustColor = new Color(0.95f, 0.9f, 0.78f, 0.34f);
            dustMaterial = hazeMaterial != null ? new Material(hazeMaterial) : null;
            GetComponent<RoomAssets>().Keep(dustMaterial);
            dustMaterial.SetFloat("_DstBlend", (float)BlendMode.One);
            dustMaterial.SetColor("_BaseColor", dustColor);

            var renderer = dust.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sortMode = ParticleSystemSortMode.None;
            renderer.sharedMaterial = dustMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            for (var i = 0; i < dustTarget; i++) EmitDust();
            var particles = new ParticleSystem.Particle[dust.particleCount];
            var count = dust.GetParticles(particles);
            for (var i = 0; i < count; i++)
                particles[i].remainingLifetime = particles[i].startLifetime * Mathf.Lerp(0.1f, 0.9f, (float)random.NextDouble());
            dust.SetParticles(particles, count);
            dust.Play();
        }

        void EmitDust()
        {
            if (!RandomFloorPoint(out var point)) return;
            point.y = Mathf.Lerp(0.08f, ceiling - 0.1f, (float)random.NextDouble());
            var parameters = new ParticleSystem.EmitParams
            {
                position = point,
                velocity = new Vector3(((float)random.NextDouble() - 0.5f) * 0.03f, ((float)random.NextDouble() - 0.6f) * 0.018f, ((float)random.NextDouble() - 0.5f) * 0.03f),
                startLifetime = Mathf.Lerp(12f, 30f, (float)random.NextDouble()),
                startSize = Mathf.Lerp(0.009f, 0.024f, (float)random.NextDouble()),
                startColor = Color.white,
            };
            dust.Emit(parameters, 1);
        }

        void BuildHaze(RoomData room, RoomAssets assets)
        {
            if (outline.Count < 3) return;
            var go = new GameObject("Room Haze");
            go.transform.SetParent(transform, false);
            haze = go.AddComponent<ParticleSystem>();
            haze.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            hazeTarget = Mathf.Clamp(Mathf.RoundToInt(room.floor.area * HazePerSquareMetre), MinHaze, MaxHaze);
            var shortSide = Mathf.Min(boundsMax.x - boundsMin.x, boundsMax.z - boundsMin.z);
            largestPuff = Mathf.Clamp(shortSide * 0.45f, 1.2f, 3.4f);

            var main = haze.main;
            main.loop = true;
            main.playOnAwake = false;
            main.maxParticles = hazeTarget;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var emission = haze.emission;
            emission.enabled = false;
            var shape = haze.shape;
            shape.enabled = false;

            var fade = haze.colorOverLifetime;
            fade.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.28f), new GradientAlphaKey(1f, 0.72f), new GradientAlphaKey(0f, 1f) });
            fade.color = gradient;

            var spin = haze.rotationOverLifetime;
            spin.enabled = true;
            spin.z = new ParticleSystem.MinMaxCurve(-0.07f, 0.07f);

            hazeColor = Color.Lerp(theme.fog, theme.ceiling, 0.07f);
            hazeColor.a = theme.hazeOpacity;
            hazeMaterial = assets.Keep(CreateParticleMaterial(assets.Keep(CreatePuffTexture()), false));
            hazeMaterial.SetColor("_BaseColor", hazeColor);

            var renderer = haze.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sortMode = ParticleSystemSortMode.Distance;
            renderer.maxParticleSize = 4f;
            renderer.sharedMaterial = hazeMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            for (var i = 0; i < hazeTarget; i++) EmitPuff();
            var particles = new ParticleSystem.Particle[haze.particleCount];
            var count = haze.GetParticles(particles);
            for (var i = 0; i < count; i++)
                particles[i].remainingLifetime = particles[i].startLifetime * Mathf.Lerp(0.15f, 0.85f, (float)random.NextDouble());
            haze.SetParticles(particles, count);
            haze.Play();
            BuildDust(room);
        }

        void EmitPuff()
        {
            if (!RandomFloorPoint(out var point)) return;
            var rise = (float)random.NextDouble();
            point.y = Mathf.Lerp(0.08f, ceiling * 0.55f, rise * rise);
            var angle = (float)random.NextDouble() * Mathf.PI * 2f;
            var speed = Mathf.Lerp(0.03f, 0.09f, (float)random.NextDouble());
            var parameters = new ParticleSystem.EmitParams
            {
                position = point,
                velocity = new Vector3(Mathf.Cos(angle) * speed, ((float)random.NextDouble() - 0.5f) * 0.012f, Mathf.Sin(angle) * speed),
                startLifetime = Mathf.Lerp(14f, 28f, (float)random.NextDouble()),
                startSize = Mathf.Lerp(largestPuff * 0.45f, largestPuff, (float)random.NextDouble()),
                rotation = (float)random.NextDouble() * 360f,
                startColor = Color.white,
            };
            haze.Emit(parameters, 1);
        }

        bool RandomFloorPoint(out Vector3 point)
        {
            for (var attempt = 0; attempt < 24; attempt++)
            {
                point = new Vector3(
                    Mathf.Lerp(boundsMin.x, boundsMax.x, (float)random.NextDouble()), 0f,
                    Mathf.Lerp(boundsMin.z, boundsMax.z, (float)random.NextDouble()));
                if (InsideOutline(point)) return true;
            }
            point = default;
            return false;
        }

        bool InsideOutline(Vector3 point)
        {
            var inside = false;
            for (int i = 0, j = outline.Count - 1; i < outline.Count; j = i++)
            {
                var a = outline[i];
                var b = outline[j];
                if ((a.z > point.z) != (b.z > point.z) && point.x < (b.x - a.x) * (point.z - a.z) / (b.z - a.z) + a.x)
                    inside = !inside;
            }
            return inside;
        }

        internal static Material CreateParticleMaterial(Texture2D texture, bool additive)
        {
            var material = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit"));
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", additive ? 2f : 0f);
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)(additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
            material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            material.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = (int)RenderQueue.Transparent;
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");

            // Fades puffs where they meet walls and furniture, and near the camera.
            material.EnableKeyword("_SOFTPARTICLES_ON");
            material.SetFloat("_SoftParticlesEnabled", 1f);
            material.SetVector("_SoftParticleFadeParams", new Vector4(0f, 1f / 0.8f, 0f, 0f));
            material.EnableKeyword("_FADING_ON");
            material.SetFloat("_CameraFadingEnabled", 1f);
            material.SetVector("_CameraFadeParams", new Vector4(0.25f, 1f / 1.1f, 0f, 0f));
            if (texture != null) material.SetTexture("_BaseMap", texture);
            return material;
        }

        static Texture2D CreatePuffTexture()
        {
            const int size = 128;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "Haze Puff" };
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var u = (x + 0.5f) / size * 2f - 1f;
                    var v = (y + 0.5f) / size * 2f - 1f;
                    var falloff = Mathf.Clamp01(1f - Mathf.Sqrt(u * u + v * v));
                    falloff = falloff * falloff * (3f - 2f * falloff);
                    var wisps = Mathf.PerlinNoise(u * 1.7f + 3.1f, v * 1.7f + 7.4f) * 0.65f
                        + Mathf.PerlinNoise(u * 4.3f + 11.2f, v * 4.3f + 2.6f) * 0.35f;
                    var alpha = Mathf.Clamp01(falloff * Mathf.Lerp(0.35f, 1.25f, wisps));
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }
    }
}
