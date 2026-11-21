using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace FlyAI
{
    // Film grain and a pulsing vignette that build as the fly gets close.
    // The fly should call SetFlyDistance every frame once it exists.
    public class RoomTension : MonoBehaviour
    {
        const float FarDistance = 5f;
        const float NearDistance = 0.8f;
        const float MaxGrain = 0.5f;
        const float VignetteRise = 0.15f;
        const float VignettePulse = 0.07f;
        const float ResponseSpeed = 2.2f;

        public static RoomTension Current { get; private set; }

        FilmGrain grain;
        Vignette vignette;
        float baseVignette;
        float target;
        float level;
        float pulsePhase;
        float lastDistanceTime = -10f;
        bool debugHeld;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Current = null;

        public void MakeCurrent() => Current = this;

        public void Setup(FilmGrain filmGrain, Vignette roomVignette, float vignetteAmount)
        {
            grain = filmGrain;
            vignette = roomVignette;
            baseVignette = vignetteAmount;
        }

        public void SetFlyDistance(float metres)
        {
            target = 1f - Mathf.InverseLerp(NearDistance, FarDistance, metres);
            lastDistanceTime = Time.unscaledTime;
        }

        public void SetDebugHeld(bool held) => debugHeld = held;

        void OnDestroy()
        {
            if (Current == this) Current = null;
        }

        void Update()
        {
            var wanted = debugHeld ? 1f : Time.unscaledTime - lastDistanceTime < 0.25f ? target : 0f;
            level = Mathf.MoveTowards(level, wanted, ResponseSpeed * Time.unscaledDeltaTime);

            // Two quick beats and a rest, speeding up as the fly gets closer.
            pulsePhase = (pulsePhase + Time.unscaledDeltaTime * Mathf.Lerp(0.8f, 1.9f, level)) % 1f;
            var beat = Mathf.Exp(-Mathf.Pow((pulsePhase - 0.12f) / 0.06f, 2f))
                       + 0.65f * Mathf.Exp(-Mathf.Pow((pulsePhase - 0.34f) / 0.07f, 2f));

            grain.intensity.value = level * MaxGrain;
            vignette.intensity.value = Mathf.Clamp01(baseVignette + level * (VignetteRise + VignettePulse * beat));
        }
    }
}
