using System;
using System.Collections.Generic;
using UnityEngine;

namespace FlyAI
{
    // Every game sound is generated here, so no audio files are needed.
    public static class FlySounds
    {
        const int Rate = 22050;
        static readonly Dictionary<string, AudioClip> Cache = new Dictionary<string, AudioClip>();

        public static readonly string[] AllNames =
        {
            "room_tone", "fridge_hum", "tv_static", "radio_static", "dripping_tap", "clock_tick", "washer_rumble",
            "dishwasher_slosh", "oven_tick", "fire_crackle", "toilet_drip", "wind_outside", "rain_outside",
            "surge_whine", "light_buzz", "car_pass", "thunder", "footstep_0", "footstep_1", "footstep_2", "swing_whoosh",
            "impact_wood", "impact_wall", "impact_glass", "impact_ceramic", "impact_metal", "impact_tech", "impact_soft",
            "impact_squish", "window_smash", "fly_buzz",
        };

        const string SoundFolder = "FlyAISounds/";

        // Clips loaded from a theme folder, keyed "folder/name". A null value records a file
        // that does not exist, so Resources is only searched once per theme and name.
        static readonly Dictionary<string, AudioClip> ThemedCache = new Dictionary<string, AudioClip>();
        static string themeFolder;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            themeFolder = null;
            ThemedCache.Clear();
        }

        // Null or empty means the house: every sound is synthesised exactly as before.
        public static void SetTheme(string folder) => themeFolder = string.IsNullOrEmpty(folder) ? null : folder;

        public static AudioClip Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (themeFolder != null)
            {
                var themed = GetThemed(themeFolder, name);
                if (themed != null) return themed;
            }
            if (Cache.TryGetValue(name, out var cached) && cached != null) return cached;
            var samples = Synthesize(name);
            if (samples == null) return null;
            var clip = AudioClip.Create(name, samples.Length, 1, Rate, false);
            clip.SetData(samples, 0);
            Cache[name] = clip;
            return clip;
        }

        static AudioClip GetThemed(string folder, string name)
        {
            var key = folder + "/" + name;
            if (ThemedCache.TryGetValue(key, out var known))
            {
                // A destroyed clip compares equal to null; search again in that case.
                if (known != null || ReferenceEquals(known, null)) return known;
            }
            var clip = Resources.Load<AudioClip>(SoundFolder + key);
            ThemedCache[key] = clip;
            return clip;
        }

        static float[] Synthesize(string name)
        {
            switch (name)
            {
                case "room_tone": return Loop(RoomTone(), 0.4f, 0.75f);
                case "fridge_hum": return Loop(Hum(4f, 60f, 0.5f), 0.1f, 0.8f);
                case "tv_static": return Loop(Static(3f, 0.82f, 7600f), 0.1f, 0.5f);
                case "radio_static": return Loop(Radio(), 0.15f, 0.7f);
                case "dripping_tap": return Drips(3f, 1320f, 11);
                case "toilet_drip": return Drips(3f, 760f, 29);
                case "clock_tick": return ClockTick();
                case "washer_rumble": return Loop(Rumble(4f, 38f, 3f), 0.1f, 0.8f);
                case "dishwasher_slosh": return Loop(Slosh(), 0.2f, 0.7f);
                case "oven_tick": return OvenTicks();
                case "fire_crackle": return Loop(Fire(), 0.2f, 0.8f);
                case "wind_outside": return Loop(Wind(), 0.6f, 0.75f);
                case "rain_outside": return Loop(Static(5f, 0.45f, 0f), 0.3f, 0.6f);
                case "surge_whine": return SurgeWhine();
                case "light_buzz": return LightBuzz();
                case "car_pass": return CarPass();
                case "thunder": return Thunder();
                case "footstep_0": return Footstep(101, 86f);
                case "footstep_1": return Footstep(202, 74f);
                case "footstep_2": return Footstep(303, 96f);
                case "swing_whoosh": return Whoosh();
                case "impact_wood": return Impact(0.24f, 190f, 24f, 0.32f, 12, 1f, 2.76f, 5.4f);
                case "impact_wall": return Impact(0.28f, 92f, 20f, 0.4f, 13, 1f, 1.9f);
                case "impact_glass": return Glass();
                case "impact_ceramic": return Impact(0.42f, 1180f, 15f, 0.45f, 15, 1f, 1.62f, 2.71f, 4.18f);
                case "impact_metal": return Impact(0.55f, 720f, 8f, 0.18f, 16, 1f, 1.41f, 2.32f, 3.86f);
                case "impact_tech": return Impact(0.22f, 420f, 34f, 0.55f, 17, 1f, 2.3f);
                case "impact_soft": return Impact(0.17f, 110f, 34f, 0.3f, 18, 1f);
                case "impact_squish": return Squish();
                case "window_smash": return WindowSmash();
                case "fly_buzz": return FlyBuzz();
                default: return null;
            }
        }

        static float[] New(float seconds) => new float[Mathf.RoundToInt(seconds * Rate)];

        static float Noise(System.Random random) => (float)random.NextDouble() * 2f - 1f;

        static float[] Normalize(float[] data, float peak)
        {
            var largest = 0f;
            foreach (var value in data) largest = Mathf.Max(largest, Mathf.Abs(value));
            if (largest < 1e-5f) return data;
            for (var i = 0; i < data.Length; i++) data[i] = data[i] / largest * peak;
            return data;
        }

        // Blends the end into the start so looping has no click.
        static float[] Loop(float[] data, float fadeSeconds, float peak)
        {
            var fade = Mathf.Min(Mathf.RoundToInt(fadeSeconds * Rate), data.Length / 3);
            var output = new float[data.Length - fade];
            Array.Copy(data, output, output.Length);
            for (var i = 0; i < fade; i++)
            {
                var mix = i / (float)fade;
                output[i] = data[i] * mix + data[output.Length + i] * (1f - mix);
            }
            return Normalize(output, peak);
        }

        static float[] RoomTone()
        {
            var data = New(7f);
            var random = new System.Random(7);
            float brown = 0f, soft = 0f;
            for (var i = 0; i < data.Length; i++)
            {
                var t = i / (float)Rate;
                brown = Mathf.Clamp(brown + Noise(random) * 0.018f, -1f, 1f) * 0.998f;
                soft += (Noise(random) - soft) * 0.02f;
                data[i] = brown * 0.75f + soft * 0.3f + Mathf.Sin(2f * Mathf.PI * 58f * t) * 0.045f;
            }
            return data;
        }

        static float[] Hum(float seconds, float frequency, float wobble)
        {
            var data = New(seconds);
            var random = new System.Random(21);
            var soft = 0f;
            for (var i = 0; i < data.Length; i++)
            {
                var t = i / (float)Rate;
                soft += (Noise(random) - soft) * 0.05f;
                var shimmer = 1f + Mathf.Sin(2f * Mathf.PI * wobble * t) * 0.12f;
                data[i] = (Mathf.Sin(2f * Mathf.PI * frequency * t) * 0.6f + Mathf.Sin(2f * Mathf.PI * frequency * 2f * t) * 0.3f
                           + Mathf.Sin(2f * Mathf.PI * frequency * 3f * t) * 0.12f) * shimmer + soft * 0.14f;
            }
            return data;
        }

        static float[] Static(float seconds, float brightness, float whine)
        {
            var data = New(seconds);
            var random = new System.Random(31);
            float low = 0f, lower = 0f;
            for (var i = 0; i < data.Length; i++)
            {
                var noise = Noise(random);
                low += (noise - low) * brightness;
                lower += (noise - lower) * 0.08f;
                var value = low - lower * 0.7f;
                if (whine > 0f) value += Mathf.Sin(2f * Mathf.PI * whine * i / Rate) * 0.03f;
                data[i] = value;
            }
            return data;
        }

        static float[] Radio()
        {
            var data = New(6f);
            var random = new System.Random(41);
            float band = 0f, low = 0f;
            for (var i = 0; i < data.Length; i++)
            {
                var t = i / (float)Rate;
                var noise = Noise(random);
                low += (noise - low) * 0.5f;
                band += (low - band) * 0.12f;
                var speech = Mathf.Max(0f, Mathf.PerlinNoise(t * 3.3f, 1.7f) - 0.48f) * 3f;
                var voice = Mathf.Sin(2f * Mathf.PI * (190f + Mathf.PerlinNoise(t * 2.1f, 4.2f) * 120f) * t)
                            * Mathf.Sin(2f * Mathf.PI * 720f * t) * speech;
                data[i] = (low - band) * 0.34f + voice * 0.4f;
            }
            return data;
        }

        static void AddPlink(float[] data, int start, float frequency, float amount)
        {
            for (var i = start; i < data.Length; i++)
            {
                var t = (i - start) / (float)Rate;
                if (t > 0.2f) break;
                data[i] += Mathf.Sin(2f * Mathf.PI * frequency * (1f + t * 1.9f) * t) * Mathf.Exp(-t * 34f) * amount;
            }
        }

        static float[] Drips(float seconds, float frequency, int seed)
        {
            var data = New(seconds);
            var random = new System.Random(seed);
            AddPlink(data, Mathf.RoundToInt(0.2f * Rate), frequency, 1f);
            AddPlink(data, Mathf.RoundToInt((1.5f + (float)random.NextDouble() * 0.7f) * Rate), frequency * 1.12f, 0.75f);
            return Normalize(data, 0.8f);
        }

        static float[] ClockTick()
        {
            var data = New(2f);
            var random = new System.Random(51);
            for (var beat = 0; beat < 2; beat++)
            {
                var start = beat * Rate;
                var tone = beat == 0 ? 2500f : 2050f;
                for (var i = 0; i < Rate / 40; i++)
                {
                    var t = i / (float)Rate;
                    data[start + i] += (Noise(random) * 0.45f + Mathf.Sin(2f * Mathf.PI * tone * t)) * Mathf.Exp(-t * 420f);
                }
            }
            return Normalize(data, 0.7f);
        }

        static float[] Rumble(float seconds, float frequency, float beat)
        {
            var data = New(seconds);
            var random = new System.Random(61);
            var soft = 0f;
            for (var i = 0; i < data.Length; i++)
            {
                var t = i / (float)Rate;
                soft += (Noise(random) - soft) * 0.03f;
                data[i] = (Mathf.Sin(2f * Mathf.PI * frequency * t) + soft * 1.2f) * (0.7f + 0.3f * Mathf.Sin(2f * Mathf.PI * beat * t));
            }
            return data;
        }

        static float[] Slosh()
        {
            var data = New(5f);
            var random = new System.Random(71);
            var low = 0f;
            for (var i = 0; i < data.Length; i++)
            {
                var t = i / (float)Rate;
                low += (Noise(random) - low) * 0.16f;
                data[i] = low * (0.35f + 0.65f * Mathf.Pow(0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * 0.8f * t), 2f));
            }
            return data;
        }

        static float[] OvenTicks()
        {
            var data = New(4f);
            var random = new System.Random(81);
            for (var tick = 0; tick < 3; tick++)
            {
                var start = Mathf.RoundToInt((0.3f + tick * 1.1f + (float)random.NextDouble() * 0.6f) * Rate);
                for (var i = 0; i < Rate / 50 && start + i < data.Length; i++)
                    data[start + i] += Noise(random) * Mathf.Exp(-i / (float)Rate * 320f);
            }
            return Normalize(data, 0.6f);
        }

        static float[] Fire()
        {
            var data = New(6f);
            var random = new System.Random(91);
            var brown = 0f;
            for (var i = 0; i < data.Length; i++)
            {
                brown = Mathf.Clamp(brown + Noise(random) * 0.03f, -1f, 1f) * 0.997f;
                data[i] = brown * 0.5f;
                if (random.NextDouble() < 0.00045)
                    for (var j = 0; j < 90 && i + j < data.Length; j++)
                        data[i + j] += Noise(random) * Mathf.Exp(-j / 16f) * 0.8f;
            }
            return data;
        }

        static float[] Wind()
        {
            var data = New(8f);
            var random = new System.Random(101);
            float low = 0f, lower = 0f;
            for (var i = 0; i < data.Length; i++)
            {
                var t = i / (float)Rate;
                var noise = Noise(random);
                var cutoff = 0.015f + Mathf.PerlinNoise(t * 0.5f, 3.1f) * 0.05f;
                low += (noise - low) * cutoff;
                lower += (low - lower) * 0.004f;
                data[i] = (low - lower) * (0.5f + Mathf.PerlinNoise(t * 0.35f, 8.4f));
            }
            return data;
        }

        // Timed so the final thunk lands exactly on the surge's slam to black.
        static float[] SurgeWhine()
        {
            const float slam = 4.05f;
            var data = New(4.75f);
            var random = new System.Random(111);
            var phase = 0f;
            for (var i = 0; i < data.Length; i++)
            {
                var t = i / (float)Rate;
                if (t < slam)
                {
                    var progress = t / slam;
                    var frequency = 62f * Mathf.Pow(1450f / 62f, progress * progress);
                    phase += 2f * Mathf.PI * frequency / Rate;
                    var buzz = Mathf.Sin(phase) * 0.55f + Mathf.Sin(phase * 2f) * 0.25f + Mathf.Sin(phase * 3.01f) * 0.14f;
                    data[i] = buzz * Mathf.Pow(progress, 1.6f) * 0.72f + Noise(random) * progress * progress * 0.07f;
                }
                else
                {
                    var after = t - slam;
                    data[i] = (Mathf.Sin(2f * Mathf.PI * (62f - after * 28f) * after) * 1.1f + Noise(random) * Mathf.Exp(-after * 170f) * 0.7f)
                              * Mathf.Exp(-after * 8.5f);
                }
            }
            for (var i = 0; i < data.Length; i++) data[i] = (float)Math.Tanh(data[i] * 1.2f);
            return Normalize(data, 0.9f);
        }

        // A loop of wing buzz: whole cycles per half-second so it repeats without a click.
        static float[] FlyBuzz()
        {
            var data = New(0.5f);
            var random = new System.Random(77);
            for (var i = 0; i < data.Length; i++)
            {
                var t = i / (float)Rate;
                var wobble = 1f + 0.08f * Mathf.Sin(2f * Mathf.PI * 8f * t);
                var tone = Mathf.Sin(2f * Mathf.PI * 190f * t) * 0.5f + Mathf.Sin(2f * Mathf.PI * 380f * t) * 0.3f
                           + Mathf.Sin(2f * Mathf.PI * 570f * t) * 0.18f + Mathf.Sin(2f * Mathf.PI * 760f * t) * 0.08f;
                data[i] = tone * wobble * (0.8f + 0.2f * Mathf.Sin(2f * Mathf.PI * 4f * t)) + Noise(random) * 0.05f;
            }
            return Normalize(data, 0.5f);
        }

        static float[] LightBuzz()
        {
            var data = New(1.5f);
            var random = new System.Random(121);
            for (var i = 0; i < data.Length; i++)
            {
                var t = i / (float)Rate;
                var clicks = (t < 0.02f ? Noise(random) * Mathf.Exp(-t * 300f) : 0f)
                             + (t >= 0.14f && t < 0.16f ? Noise(random) * Mathf.Exp(-(t - 0.14f) * 300f) * 0.7f : 0f);
                var buzz = Mathf.Sign(Mathf.Sin(2f * Mathf.PI * 120f * t)) * 0.16f + Mathf.Sin(2f * Mathf.PI * 240f * t) * 0.2f;
                data[i] = clicks + buzz * Mathf.Clamp01(t / 0.08f) * Mathf.Exp(-t * 2.8f);
            }
            return Normalize(data, 0.7f);
        }

        static float[] CarPass()
        {
            var data = New(5f);
            var random = new System.Random(131);
            float low = 0f, phase = 0f;
            for (var i = 0; i < data.Length; i++)
            {
                var t = i / (float)Rate;
                var progress = t / 5f;
                var frequency = Mathf.Lerp(96f, 58f, Mathf.SmoothStep(0.3f, 0.7f, progress));
                phase += 2f * Mathf.PI * frequency / Rate;
                low += (Noise(random) - low) * Mathf.Lerp(0.07f, 0.04f, progress);
                var envelope = Mathf.Pow(Mathf.Sin(progress * Mathf.PI), 2.2f);
                data[i] = (Mathf.Sin(phase) * 0.42f + Mathf.Sin(phase * 2f) * 0.16f + low * 1.25f) * envelope;
            }
            return Normalize(data, 0.8f);
        }

        static float[] Thunder()
        {
            var data = New(4.2f);
            var random = new System.Random(141);
            float low = 0f, lower = 0f;
            for (var i = 0; i < data.Length; i++)
            {
                var t = i / (float)Rate;
                var noise = Noise(random);
                low += (noise - low) * 0.035f;
                lower += (low - lower) * 0.12f;
                var rolls = 0.45f + Mathf.PerlinNoise(t * 5.5f, 2.2f) * 1.1f;
                data[i] = lower * rolls * Mathf.Clamp01(t / 0.08f) * Mathf.Exp(-t * 1.25f);
            }
            return Normalize(data, 0.9f);
        }

        static float[] Footstep(int seed, float frequency)
        {
            var data = New(0.17f);
            var random = new System.Random(seed);
            var low = 0f;
            for (var i = 0; i < data.Length; i++)
            {
                var t = i / (float)Rate;
                low += (Noise(random) - low) * 0.22f;
                data[i] = Mathf.Sin(2f * Mathf.PI * frequency * t) * Mathf.Exp(-t * 44f) + low * Mathf.Exp(-t * 95f) * 0.55f
                          + Mathf.Sin(2f * Mathf.PI * frequency * 4.3f * t) * Mathf.Exp(-t * 90f) * 0.14f;
            }
            return Normalize(data, 0.7f);
        }

        static float[] Whoosh()
        {
            var data = New(0.3f);
            var random = new System.Random(151);
            float low = 0f, band = 0f;
            for (var i = 0; i < data.Length; i++)
            {
                var progress = i / (float)data.Length;
                var cutoff = Mathf.Lerp(0.05f, 0.42f, Mathf.Sin(progress * Mathf.PI));
                low += (Noise(random) - low) * cutoff;
                band += (low - band) * 0.05f;
                data[i] = (low - band) * Mathf.Pow(Mathf.Sin(progress * Mathf.PI), 1.6f);
            }
            return Normalize(data, 0.75f);
        }

        static float[] Impact(float seconds, float frequency, float decay, float noiseAmount, int seed, params float[] partials)
        {
            var data = New(seconds);
            var random = new System.Random(seed);
            for (var i = 0; i < data.Length; i++)
            {
                var t = i / (float)Rate;
                var tone = 0f;
                for (var p = 0; p < partials.Length; p++)
                    tone += Mathf.Sin(2f * Mathf.PI * frequency * partials[p] * t) * Mathf.Exp(-t * decay * (1f + p * 0.7f)) / (1f + p);
                data[i] = tone + Noise(random) * Mathf.Exp(-t * 110f) * noiseAmount;
            }
            return Normalize(data, 0.85f);
        }

        static float[] Squish()
        {
            var data = New(0.26f);
            var random = new System.Random(171);
            var low = 0f;
            for (var i = 0; i < data.Length; i++)
            {
                var t = i / (float)Rate;
                low += (Noise(random) - low) * Mathf.Lerp(0.5f, 0.06f, t / 0.26f);
                data[i] = low * Mathf.Exp(-t * 17f) + Mathf.Sin(2f * Mathf.PI * (210f - t * 520f) * t) * Mathf.Exp(-t * 26f) * 0.75f;
            }
            return Normalize(data, 0.8f);
        }

        // A heavy impact, then a long spill of falling glass.
        static float[] WindowSmash()
        {
            var data = New(1.7f);
            var random = new System.Random(181);
            for (var i = 0; i < data.Length; i++)
            {
                var t = i / (float)Rate;
                data[i] = Mathf.Sin(2f * Mathf.PI * 74f * t) * Mathf.Exp(-t * 15f) * 1.1f + Noise(random) * Mathf.Exp(-t * 19f) * 1.15f;
            }
            for (var shard = 0; shard < 150; shard++)
            {
                var start = Mathf.RoundToInt(Mathf.Pow((float)random.NextDouble(), 1.7f) * 1.35f * Rate);
                var frequency = Mathf.Lerp(1700f, 7600f, (float)random.NextDouble());
                var amount = Mathf.Lerp(0.14f, 0.82f, (float)random.NextDouble()) * Mathf.Exp(-start / (float)Rate * 1.5f);
                var decay = Mathf.Lerp(28f, 70f, (float)random.NextDouble());
                for (var i = start; i < data.Length; i++)
                {
                    var t = (i - start) / (float)Rate;
                    if (t > 0.17f) break;
                    data[i] += Mathf.Sin(2f * Mathf.PI * frequency * t) * Mathf.Exp(-t * decay) * amount;
                }
            }
            for (var i = 0; i < data.Length; i++) data[i] = (float)Math.Tanh(data[i] * 0.9f);
            return Normalize(data, 0.95f);
        }

        static float[] Glass()
        {
            var data = New(0.62f);
            var random = new System.Random(161);
            for (var shard = 0; shard < 18; shard++)
            {
                var start = Mathf.RoundToInt(Mathf.Pow((float)random.NextDouble(), 2f) * 0.32f * Rate);
                var frequency = Mathf.Lerp(1900f, 6200f, (float)random.NextDouble());
                var amount = Mathf.Lerp(0.25f, 1f, (float)random.NextDouble());
                for (var i = start; i < data.Length; i++)
                {
                    var t = (i - start) / (float)Rate;
                    if (t > 0.2f) break;
                    data[i] += Mathf.Sin(2f * Mathf.PI * frequency * t) * Mathf.Exp(-t * 38f) * amount;
                }
            }
            for (var i = 0; i < data.Length; i++) data[i] += Noise(random) * Mathf.Exp(-i / (float)Rate * 60f) * 1.4f;
            return Normalize(data, 0.85f);
        }
    }
}
