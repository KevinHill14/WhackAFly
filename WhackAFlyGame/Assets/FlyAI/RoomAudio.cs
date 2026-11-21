using UnityEngine;

namespace FlyAI
{
    // Electric sounds die in a blackout and return with the power.
    public class PoweredSound : MonoBehaviour
    {
        public AudioSource source;
        public float baseVolume;
        public bool electric;

        void Update()
        {
            var power = electric && RoomEvents.Current != null ? Mathf.Clamp01(RoomEvents.Current.Power) : 1f;
            source.volume = baseVolume * power;
        }
    }

    public static class RoomAudio
    {
        const float RoomToneVolume = 0.13f;
        const float RainVolume = 0.34f;

        public static void AddEmitter(Transform parent, SoundAnchorData anchor)
        {
            var clip = FlySounds.Get(anchor.sound);
            if (clip == null) return;
            var go = new GameObject($"Sound {anchor.sound}");
            go.transform.SetParent(parent, false);
            var source = Spatial(go, clip, anchor.volume, Mathf.Max(anchor.range * 1.6f, 4f));
            source.time = Mathf.Abs(anchor.id.GetHashCode() % 1000) / 1000f * clip.length * 0.95f;
            source.Play();
            var powered = go.AddComponent<PoweredSound>();
            powered.source = source;
            powered.baseVolume = anchor.volume;
            powered.electric = anchor.sound == "fridge_hum" || anchor.sound == "tv_static" || anchor.sound == "radio_static"
                               || anchor.sound == "washer_rumble" || anchor.sound == "dishwasher_slosh" || anchor.sound == "oven_tick";
        }

        public static void BuildAmbience(Transform root, RoomData room)
        {
            var tone = new GameObject("Room Tone");
            tone.transform.SetParent(root, false);
            var toneSource = tone.AddComponent<AudioSource>();
            toneSource.clip = FlySounds.Get("room_tone");
            toneSource.loop = true;
            toneSource.spatialBlend = 0f;
            toneSource.volume = RoomToneVolume;
            toneSource.Play();

            if (!RoomWindow.IsValid(room.window)) return;
            var rain = new GameObject("Rain Outside");
            rain.transform.SetParent(root, false);
            rain.transform.position = RoomLoader.ToUnity(room.window.center);
            var rainSource = Spatial(rain, FlySounds.Get("rain_outside"), RainVolume, 11f);
            rainSource.Play();
        }

        static AudioSource Spatial(GameObject go, AudioClip clip, float volume, float maxDistance)
        {
            var source = go.AddComponent<AudioSource>();
            source.clip = clip;
            source.loop = true;
            source.playOnAwake = false;
            source.volume = volume;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = 0.7f;
            source.maxDistance = maxDistance;
            source.dopplerLevel = 0f;
            return source;
        }
    }
}
