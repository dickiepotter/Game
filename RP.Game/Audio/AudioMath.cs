namespace RP.Game.Audio
{
    using System;
    using RP.Math;

    /// <summary>
    /// Pure audio DSP maths: distance attenuation, the Doppler frequency shift, decibel/linear gain
    /// conversion, and bus-gain composition. These are the calculations behind 3D positional sound, and —
    /// being pure functions — they are unit-tested without a sound card (build brief S15.3), so the maths
    /// is trustworthy even though playback itself can only be checked by ear.
    /// </summary>
    public static class AudioMath
    {
        /// <summary>Converts a linear gain (0..1+) to decibels. Silence (≤0) maps to a floor of −144 dB.</summary>
        public static float LinearToDecibels(float linear) =>
            linear <= 0f ? -144f : 20f * MathF.Log10(linear);

        /// <summary>Converts decibels to a linear gain multiplier.</summary>
        public static float DecibelsToLinear(float decibels) => MathF.Pow(10f, decibels / 20f);

        /// <summary>
        /// The OpenAL <b>clamped inverse-distance</b> attenuation model — physically-flavoured falloff that
        /// halves roughly every doubling of distance past the reference. Gain is 1 at
        /// <paramref name="referenceDistance"/> and never re-grows past <paramref name="maxDistance"/>.
        /// </summary>
        /// <param name="rolloff">How aggressively sound falls off (1 = natural). Higher = quieter sooner.</param>
        public static float InverseDistanceClamped(
            float distance, float referenceDistance, float maxDistance, float rolloff = 1f)
        {
            if (referenceDistance <= 0f) return 1f;
            float d = Math.Clamp(distance, referenceDistance, MathF.Max(referenceDistance, maxDistance));
            float denominator = referenceDistance + rolloff * (d - referenceDistance);
            return denominator <= 0f ? 1f : referenceDistance / denominator;
        }

        /// <summary>
        /// A simple <b>linear</b> attenuation model: full volume at <paramref name="referenceDistance"/>,
        /// fading straight to silence at <paramref name="maxDistance"/>. Cheap and predictable; good for a
        /// stylised falloff.
        /// </summary>
        public static float LinearDistanceClamped(float distance, float referenceDistance, float maxDistance)
        {
            if (maxDistance <= referenceDistance) return distance <= referenceDistance ? 1f : 0f;
            float d = Math.Clamp(distance, referenceDistance, maxDistance);
            return 1f - (d - referenceDistance) / (maxDistance - referenceDistance);
        }

        /// <summary>
        /// The Doppler frequency ratio (observed ÷ emitted): a source moving toward the listener raises
        /// pitch (ratio &gt; 1), moving away lowers it. Direction is taken along the source→listener line.
        /// </summary>
        /// <param name="speedOfSound">Metres per second (≈343 in air; stylise freely for the game).</param>
        /// <param name="dopplerFactor">Exaggeration knob (1 = physical, 0 = disabled).</param>
        public static float DopplerRatio(
            Vector3 sourcePosition, Vector3 sourceVelocity,
            Vector3 listenerPosition, Vector3 listenerVelocity,
            float speedOfSound, float dopplerFactor = 1f)
        {
            Vector3 toListener = listenerPosition - sourcePosition;
            float distance = toListener.Length;
            if (distance < 1e-6f || speedOfSound <= 0f) return 1f;

            Vector3 dir = toListener / distance; // unit, source -> listener

            // Velocity components along that line. A positive source component means it chases the listener
            // (pitch up); a negative listener component means it approaches the source (pitch up).
            float vSource = Vector3.Dot(sourceVelocity, dir) * dopplerFactor;
            float vListener = Vector3.Dot(listenerVelocity, dir) * dopplerFactor;

            // Clamp below the speed of sound to keep the ratio finite (a real shock-wave is out of scope).
            float cap = speedOfSound * 0.999f;
            vSource = Math.Clamp(vSource, -cap, cap);
            vListener = Math.Clamp(vListener, -cap, cap);

            return (speedOfSound - vListener) / (speedOfSound - vSource);
        }

        /// <summary>Composes a chain of gains into one multiplier (e.g. master × music × source).</summary>
        public static float ComposeGain(params float[] gains)
        {
            float result = 1f;
            if (gains != null)
            {
                foreach (float g in gains) result *= g;
            }

            return result;
        }
    }
}
