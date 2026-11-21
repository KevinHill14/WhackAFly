using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FlyAI
{
    // Timed room events: power blackouts and headlights passing outside.
    public class RoomEvents : MonoBehaviour
    {
        const float PowerDownTime = 0.95f;
        const float PowerUpTime = 0.75f;
        const float BlackoutAmbient = 0.22f;
        const int SampleRate = 44100;

        public static RoomEvents Current { get; private set; }

        public float Power { get; private set; } = 1f;

        System.Random random;
        AudioSource audioSource;
        AudioClip powerDownClip;
        Color baseAmbient;
        bool blackoutActive;
        float blackoutStart;
        float blackoutHold;
        float nextBlackout;

        Material windowPane;
        Light headlight;
        Vector3 headlightForward;
        float headlightBase;
        float headlightStart = -100f;
        float nextHeadlight;
        const float HeadlightDuration = 5f;
        const float HeadlightSweep = 78f;
        const float HeadlightFogLift = 0.72f;
        const float ShaftOpacity = 0.0045f;
        const int ShaftShells = 26;
        const float SurgeBuild = 3.6f;
        const float SurgePeak = 0.45f;
        const float SurgeSlam = 0.05f;
        const float SurgeDarkHold = 0.7f;
        const float SurgeRecover = 4.2f;
        const float SurgeBrightness = 4.5f;
        const float SurgeAmbient = 3f;
        const float SurgeExposure = 2.2f;
        const float SlamExposure = 1.2f;
        const float SurgeSaturation = -75f;
        const float SurgeContrast = -5f;
        const float SurgeBloom = 3.2f;
        const float SurgeBloomThreshold = 0.55f;
        const float MinEventGap = 8f;

        Material shaftMaterial;
        MeshRenderer shaftRenderer;
        Color shaftColor;
        float headlightLift;
        bool surgeActive;
        float surgeStart;
        float surgeDim;
        float nextSurge;
        ColorAdjustments grade;
        Bloom bloom;
        float baseExposure;
        float baseSaturation;
        float baseContrast;
        float baseBloom;
        float baseBloomThreshold;
        float quietUntil;

        public float Surge { get; private set; }
        public float LightningLevel => lightningLevel;
        public float HeadlightLevel => headlightLift;
        public bool CalmLights => surgeActive;
        public float LightBoost => 1f + Surge * SurgeBrightness;
        public float FogLift => Mathf.Max(Surge, Mathf.Max(headlightLift * HeadlightFogLift, lightningLevel * LightningFogLift));

        const float SkyBrightness = 0.8f;
        const float LightningFogLift = 0.34f;
        AudioSource carSource;
        Light lightningLight;
        float lightningBase;
        float lightningLevel;
        float lightningStart = -100f;
        float nextLightning;
        float thunderAt = -1f;
        bool buzzPlayed = true;
        bool slamPlayed = true;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Current = null;

        public void MakeCurrent() => Current = this;

        public void Setup(RoomData room, RoomTheme theme, RoomAssets assets, Material windowGlass)
        {
            windowPane = windowGlass;
            baseAmbient = theme.ambient * theme.ambientDim;
            var outline = RoomVisuals.Outline(room);
            var seed = 23;
            foreach (var point in outline) seed = unchecked(seed * 37 + Mathf.RoundToInt(point.x * 100f) + Mathf.RoundToInt(point.z * 10f));
            random = new System.Random(seed);
            nextBlackout = Time.time + Range(20f, 40f);
            nextHeadlight = Time.time + Range(6f, 14f);
            nextSurge = Time.time + Range(35f, 60f);
            nextLightning = Time.time + Range(14f, 35f);

            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.spatialBlend = 0f;
            audioSource.volume = 0.8f;
            powerDownClip = assets.Keep(CreatePowerDownClip());
            BuildHeadlight(room, outline, assets);
            if (theme.headlightColor.HasValue && headlight != null)
            {
                var tint = theme.headlightColor.Value;
                headlight.color = tint;
                shaftColor = new Color(tint.r, tint.g, tint.b, ShaftOpacity);
                shaftMaterial.SetColor("_BaseColor", shaftColor);
            }

            var volume = GetComponentInChildren<Volume>();
            if (volume != null && volume.sharedProfile != null)
            {
                volume.sharedProfile.TryGet(out grade);
                volume.sharedProfile.TryGet(out bloom);
            }
            if (grade != null)
            {
                baseExposure = grade.postExposure.value;
                baseSaturation = grade.saturation.value;
                baseContrast = grade.contrast.value;
            }
            if (bloom != null)
            {
                baseBloom = bloom.intensity.value;
                baseBloomThreshold = bloom.threshold.value;
            }
        }

        void ApplyGrade()
        {
            if (grade != null)
            {
                grade.postExposure.value = baseExposure + Surge * SurgeExposure - surgeDim * SlamExposure;
                grade.saturation.value = Mathf.Lerp(baseSaturation, SurgeSaturation, Surge);
                grade.contrast.value = Mathf.Lerp(baseContrast, SurgeContrast, Surge);
            }
            if (bloom != null)
            {
                bloom.intensity.value = Mathf.Lerp(baseBloom, SurgeBloom, Surge);
                bloom.threshold.value = Mathf.Lerp(baseBloomThreshold, SurgeBloomThreshold, Surge);
            }
        }

        void OnDestroy()
        {
            if (Current == this) Current = null;
        }

        float Range(float low, float high) => Mathf.Lerp(low, high, (float)random.NextDouble());

        public void TriggerBlackout()
        {
            if (blackoutActive || surgeActive) return;
            blackoutActive = true;
            blackoutStart = Time.time;
            blackoutHold = Range(3f, 6f);
            buzzPlayed = false;
            audioSource.PlayOneShot(powerDownClip);
        }

        // A slow build to blown-out white, a hard slam to black, then a slow recovery.
        // Never overlaps a blackout.
        public void TriggerSurge()
        {
            if (blackoutActive || surgeActive) return;
            surgeActive = true;
            surgeStart = Time.time;
            buzzPlayed = false;
            slamPlayed = false;
            audioSource.PlayOneShot(FlySounds.Get("surge_whine"), 0.8f);
        }

        void PlayLightsReturning()
        {
            if (buzzPlayed) return;
            buzzPlayed = true;
            audioSource.PlayOneShot(FlySounds.Get("light_buzz"), 0.5f);
        }

        public void TriggerLightning()
        {
            if (lightningLight == null) return;
            lightningStart = Time.time;
            thunderAt = Time.time + Range(0.5f, 1.9f);
            nextLightning = Time.time + Range(22f, 55f);
        }

        static float Flash(float t, float start, float decay) => t < start ? 0f : Mathf.Exp(-(t - start) * decay);

        void UpdateLightning(float now)
        {
            if (lightningLight == null) return;
            if (now >= nextLightning && !blackoutActive && !surgeActive && now - headlightStart >= HeadlightDuration) TriggerLightning();
            var t = now - lightningStart;
            lightningLevel = t > 1.4f ? 0f : Mathf.Clamp01(Mathf.Max(Flash(t, 0f, 17f), Mathf.Max(Flash(t, 0.16f, 12f) * 0.72f, Flash(t, 0.43f, 7f) * 0.46f)));
            var active = lightningLevel > 0.01f;
            if (lightningLight.enabled != active) lightningLight.enabled = active;
            if (active) lightningLight.intensity = lightningBase * lightningLevel;
            if (thunderAt > 0f && now >= thunderAt)
            {
                thunderAt = -1f;
                audioSource.PlayOneShot(FlySounds.Get("thunder"), 0.4f);
            }
        }

        void BuildLightning(Vector3 position, float span)
        {
            var go = new GameObject("Lightning");
            go.transform.SetParent(transform, false);
            go.transform.SetPositionAndRotation(position, Quaternion.LookRotation((headlightForward + Vector3.down * 0.18f).normalized));
            lightningLight = go.AddComponent<Light>();
            lightningLight.type = LightType.Spot;
            lightningLight.spotAngle = 138f;
            lightningLight.innerSpotAngle = 70f;
            lightningLight.color = new Color(0.72f, 0.82f, 1f);
            lightningLight.range = span + 1f;
            lightningLight.shadows = LightShadows.None;
            lightningBase = Mathf.Clamp(span * span * 0.1f, 4f, 45f);
            lightningLight.enabled = false;
        }

        void UpdateSurge(float now)
        {
            if (!surgeActive) return;
            var t = now - surgeStart;
            var slamAt = SurgeBuild + SurgePeak;
            var darkEnd = slamAt + SurgeSlam + SurgeDarkHold;
            if (t < SurgeBuild)
            {
                Surge = Mathf.SmoothStep(0f, 1f, t / SurgeBuild);
                surgeDim = 0f;
            }
            else if (t < slamAt)
            {
                Surge = 1f;
                surgeDim = 0f;
            }
            else if (t < slamAt + SurgeSlam)
            {
                if (!slamPlayed)
                {
                    slamPlayed = true;
                    var bang = FlySounds.Get("surge_slam"); // silent unless a recording exists
                    if (bang != null) audioSource.PlayOneShot(bang, 0.9f);
                }
                var slam = (t - slamAt) / SurgeSlam;
                Surge = 1f - slam;
                surgeDim = slam;
            }
            else if (t < darkEnd)
            {
                Surge = 0f;
                surgeDim = 1f;
            }
            else if (t < darkEnd + SurgeRecover)
            {
                Surge = 0f;
                PlayLightsReturning();
                surgeDim = 1f - Mathf.SmoothStep(0f, 1f, (t - darkEnd) / SurgeRecover);
            }
            else
            {
                Surge = 0f;
                surgeDim = 0f;
                surgeActive = false;
                nextSurge = now + Range(45f, 85f);
                quietUntil = now + MinEventGap;
            }
        }

        public void TriggerHeadlights()
        {
            if (headlight == null || surgeActive || Time.time - headlightStart < HeadlightDuration) return;
            headlightStart = Time.time;
            if (carSource != null) carSource.PlayOneShot(FlySounds.Get("car_pass"), 0.42f);
        }

        void Update()
        {
            var now = Time.time;
            if (!blackoutActive && !surgeActive && now >= quietUntil)
            {
                if (now >= nextBlackout) TriggerBlackout();
                else if (now >= nextSurge && now - headlightStart >= HeadlightDuration) TriggerSurge();
            }
            UpdateSurge(now);
            UpdatePower(now);
            if (now >= nextHeadlight)
            {
                TriggerHeadlights();
                nextHeadlight = now + Range(12f, 28f);
            }
            UpdateHeadlight(now);
            UpdateLightning(now);
            if (windowPane != null)
                windowPane.SetColor("_BaseColor", Color.white * (SkyBrightness + headlightLift * 1.1f + lightningLevel * 4.5f));
        }

        void UpdatePower(float now)
        {
            if (blackoutActive)
            {
                var t = now - blackoutStart;
                if (t < PowerDownTime)
                {
                    var fall = 1f - t / PowerDownTime;
                    Power = fall * fall * (Mathf.PerlinNoise(now * 42f, 3.7f) > 0.3f ? 1f : 0.55f);
                }
                else if (t < PowerDownTime + blackoutHold)
                {
                    Power = 0f;
                }
                else if (t < PowerDownTime + blackoutHold + PowerUpTime)
                {
                    PlayLightsReturning();
                    var rise = (t - PowerDownTime - blackoutHold) / PowerUpTime;
                    Power = rise * (Mathf.PerlinNoise(now * 30f, 8.1f) > 0.42f ? 1f : 0.2f);
                }
                else
                {
                    Power = 1f;
                    blackoutActive = false;
                    nextBlackout = now + Range(30f, 60f);
                    quietUntil = now + MinEventGap;
                }
            }
            if (!blackoutActive) Power = 1f - surgeDim;
            RenderSettings.ambientLight = baseAmbient * (Mathf.Lerp(BlackoutAmbient, 1f, Power) * (1f + Surge * SurgeAmbient));
            ApplyGrade();
        }

        // Comes from the room's window, or the longest wall if it has none.
        void BuildHeadlight(RoomData room, List<Vector3> outline, RoomAssets assets)
        {
            if (outline.Count < 3) return;
            var centre = Vector3.zero;
            foreach (var point in outline) centre += point;
            centre /= outline.Count;

            Vector3 source;
            var columns = 2;
            var hasWindow = RoomWindow.IsValid(room.window);
            if (hasWindow)
            {
                source = RoomLoader.ToUnity(room.window.center);
                columns = RoomWindow.PaneColumns(room.window.width);
            }
            else
            {
                WallData longest = null;
                var best = 0f;
                foreach (var wall in room.walls)
                {
                    var length = (RoomLoader.ToUnity(wall.end[0], 0f, wall.end[1]) - RoomLoader.ToUnity(wall.start[0], 0f, wall.start[1])).sqrMagnitude;
                    if (length > best)
                    {
                        best = length;
                        longest = wall;
                    }
                }
                if (longest == null) return;
                source = (RoomLoader.ToUnity(longest.start[0], 0f, longest.start[1]) + RoomLoader.ToUnity(longest.end[0], 0f, longest.end[1])) / 2f;
                source.y = Mathf.Min(1.6f, room.ceilingHeight * 0.6f);
            }

            headlightForward = hasWindow ? RoomLoader.ToUnity(room.window.normal) : centre - source;
            headlightForward.y = 0f;
            if (headlightForward.sqrMagnitude < 0.01f) return;
            headlightForward.Normalize();

            var go = new GameObject("Passing Headlights");
            go.transform.SetParent(transform, false);
            go.transform.position = source + headlightForward * 0.2f;
            headlight = go.AddComponent<Light>();
            headlight.type = LightType.Spot;
            headlight.spotAngle = 62f;
            headlight.innerSpotAngle = 46f;
            headlight.color = new Color(0.82f, 0.88f, 1f);
            var bounds = new Bounds(outline[0], Vector3.zero);
            foreach (var point in outline) bounds.Encapsulate(point);
            var span = new Vector2(bounds.size.x, bounds.size.z).magnitude;
            headlight.range = span + 1f;
            headlightBase = Mathf.Clamp(span * span * 0.09f, 3f, 40f);
            headlight.shadows = LightShadows.Soft;
            headlight.shadowStrength = 0.8f;
            headlight.shadowNearPlane = 0.1f;
            headlight.cookie = assets.Keep(CreateWindowCookie(columns));
            headlight.enabled = false;

            var shaft = new GameObject("Headlight Shaft");
            shaft.transform.SetParent(go.transform, false);
            var openingWidth = hasWindow ? room.window.width : span * 0.12f;
            var openingHeight = hasWindow ? room.window.height : room.ceilingHeight * 0.4f;
            shaft.AddComponent<MeshFilter>().sharedMesh = assets.Keep(CreateShaft(openingWidth, openingHeight, span * 0.9f));
            shaftRenderer = shaft.AddComponent<MeshRenderer>();
            shaftMaterial = assets.Keep(RoomAtmosphere.CreateParticleMaterial(null, true));
            shaftMaterial.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            shaftColor = new Color(0.72f, 0.8f, 1f, ShaftOpacity);
            shaftMaterial.SetColor("_BaseColor", shaftColor);
            shaftRenderer.sharedMaterial = shaftMaterial;
            shaftRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            shaftRenderer.receiveShadows = false;
            shaftRenderer.enabled = false;

            carSource = go.AddComponent<AudioSource>();
            carSource.playOnAwake = false;
            carSource.spatialBlend = 0.72f;
            carSource.rolloffMode = AudioRolloffMode.Linear;
            carSource.minDistance = 2f;
            carSource.maxDistance = span + 6f;
            carSource.dopplerLevel = 0f;
            if (hasWindow) BuildLightning(go.transform.position, span);
        }

        // Nested, widening boxes from the window, fading out with distance.
        static Mesh CreateShaft(float width, float height, float length)
        {
            var vertices = new List<Vector3>();
            var colors = new List<Color>();
            var triangles = new List<int>();
            var spreadX = Mathf.Tan(24f * Mathf.Deg2Rad) * length;
            var spreadY = Mathf.Tan(15f * Mathf.Deg2Rad) * length;
            for (var shell = 0; shell < ShaftShells; shell++)
            {
                var size = (shell + 1f) / ShaftShells;
                var strength = 1f - shell / (float)ShaftShells;
                var nearX = width * 0.5f * size;
                var nearY = height * 0.5f * size;
                var farX = (width * 0.5f + spreadX) * size;
                var farY = (height * 0.5f + spreadY) * size;
                Vector2[] corners = { new(-1f, -1f), new(1f, -1f), new(1f, 1f), new(-1f, 1f) };
                for (var side = 0; side < 4; side++)
                {
                    var a = corners[side];
                    var b = corners[(side + 1) % 4];
                    var first = vertices.Count;
                    vertices.Add(new Vector3(a.x * nearX, a.y * nearY, 0f));
                    vertices.Add(new Vector3(b.x * nearX, b.y * nearY, 0f));
                    vertices.Add(new Vector3(b.x * farX, b.y * farY, length));
                    vertices.Add(new Vector3(a.x * farX, a.y * farY, length));
                    colors.Add(new Color(1f, 1f, 1f, strength));
                    colors.Add(new Color(1f, 1f, 1f, strength));
                    colors.Add(new Color(1f, 1f, 1f, 0f));
                    colors.Add(new Color(1f, 1f, 1f, 0f));
                    triangles.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
                }
            }
            var mesh = new Mesh { name = "Headlight Shaft" };
            mesh.SetVertices(vertices);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        void UpdateHeadlight(float now)
        {
            if (headlight == null) return;
            var t = (now - headlightStart) / HeadlightDuration;
            if (t < 0f || t >= 1f)
            {
                if (headlight.enabled)
                {
                    headlight.enabled = false;
                    shaftRenderer.enabled = false;
                    headlightLift = 0f;
                }
                return;
            }
            headlight.enabled = true;
            shaftRenderer.enabled = true;
            var envelope = Mathf.Sin(t * Mathf.PI);
            headlightLift = envelope;
            var shaft = shaftColor;
            shaft.a *= envelope;
            shaftMaterial.SetColor("_BaseColor", shaft);
            var yaw = Mathf.Lerp(-HeadlightSweep, HeadlightSweep, t * t * (3f - 2f * t));
            var direction = Quaternion.Euler(0f, yaw, 0f) * headlightForward + Vector3.up * 0.04f;
            headlight.transform.rotation = Quaternion.LookRotation(direction.normalized);
            headlight.intensity = headlightBase * Mathf.Sin(t * Mathf.PI);
        }

        static Texture2D CreateWindowCookie(int columns)
        {
            const int size = 128;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "Window Cookie" };
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var u = (x + 0.5f) / size;
                    var v = (y + 0.5f) / size;
                    var inFrame = u > 0.2f && u < 0.8f && v > 0.16f && v < 0.84f;
                    var across = (u - 0.2f) / 0.6f * columns;
                    var onBar = Mathf.Abs(across - Mathf.Round(across)) < 0.03f * columns || Mathf.Abs(v - 0.5f) < 0.018f;
                    var value = (byte)(inFrame && !onBar ? 255 : 0);
                    pixels[y * size + x] = new Color32(value, value, value, value);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        // The classic "bu-dnnnn" of a building losing power, generated so no audio file is needed.
        static AudioClip CreatePowerDownClip()
        {
            const float length = 1.35f;
            var count = Mathf.RoundToInt(length * SampleRate);
            var samples = new float[count];
            var noise = new System.Random(4021);
            var phase = 0f;
            var filtered = 0f;
            for (var i = 0; i < count; i++)
            {
                var t = i / (float)SampleRate;

                // "bu": a short low thump as the breaker goes.
                var thump = t < 0.11f ? Mathf.Sin(2f * Mathf.PI * 68f * t) * Mathf.Exp(-t * 32f) : 0f;
                filtered += ((float)noise.NextDouble() * 2f - 1f - filtered) * 0.08f;
                var click = t < 0.03f ? filtered * (1f - t / 0.03f) * 1.4f : 0f;

                // "dnnnn": mains hum sliding down as everything spins out.
                var toneTime = Mathf.Max(0f, t - 0.09f);
                var progress = Mathf.Clamp01(toneTime / (length - 0.09f));
                var frequency = 122f * Mathf.Pow(24f / 122f, progress);
                phase += 2f * Mathf.PI * frequency / SampleRate;
                var saw = (phase / (2f * Mathf.PI) % 1f) * 2f - 1f;
                var tone = Mathf.Sin(phase) * 0.62f + Mathf.Sin(phase * 2f) * 0.2f + saw * 0.14f;
                var attack = Mathf.Clamp01(toneTime / 0.025f);
                var release = Mathf.Pow(1f - progress, 1.7f);

                var value = thump * 0.95f + click + tone * attack * release * 0.72f;
                samples[i] = (float)System.Math.Tanh(value * 1.25f) * 0.88f;
            }
            var clip = AudioClip.Create("Power Down", count, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
