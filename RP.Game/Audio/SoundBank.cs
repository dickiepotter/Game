namespace RP.Game.Audio
{
    using System;

    /// <summary>
    /// Procedural sound synthesis: small generators that bake 16-bit mono PCM for the engine drone, weapon
    /// fire and explosions, so the game ships no audio files yet still has a voice (build brief S15). The
    /// looping drone is built from harmonics of the loop frequency, so it is exactly periodic and seams
    /// cleanly; the one-shots are noise/tone bursts shaped by a decay envelope. Pure functions — they can be
    /// regenerated deterministically and unit-tested for length/range without a sound card.
    /// </summary>
    public static class SoundBank
    {
        /// <summary>
        /// A seamless engine drone: a low fundamental plus a few harmonics, all integer multiples of the loop
        /// frequency (1 ÷ <paramref name="seconds"/>), so the waveform repeats exactly and loops without a
        /// click. Played back faster/slower (pitch) to track throttle.
        /// </summary>
        public static short[] GenerateEngineDrone(float seconds = 1.0f, int sampleRate = 44100, float amplitude = 0.5f)
        {
            int count = Math.Max(1, (int)(seconds * sampleRate));
            var samples = new short[count];

            // Partials in Hz (must be whole numbers to stay periodic over a 1 s loop): a deep rumble with a
            // couple of upper bands for "turbine" body. Weights fall off toward the top.
            (double hz, double w)[] partials =
            {
                (48, 1.00), (72, 0.55), (96, 0.35), (144, 0.20), (216, 0.10),
            };

            double norm = 0;
            foreach (var p in partials) norm += p.w;

            for (int i = 0; i < count; i++)
            {
                double t = (double)i / sampleRate;
                double v = 0;
                foreach (var p in partials)
                {
                    v += p.w * Math.Sin(2.0 * Math.PI * p.hz * t);
                }

                v = v / norm * amplitude;
                samples[i] = (short)(Math.Clamp(v, -1.0, 1.0) * short.MaxValue);
            }

            return samples;
        }

        /// <summary>A weapon-fire blip: a short, bright tone sweeping downward with a fast decay — a "pew".</summary>
        public static short[] GenerateZap(float seconds = 0.12f, int sampleRate = 44100, float amplitude = 0.5f)
        {
            int count = Math.Max(1, (int)(seconds * sampleRate));
            var samples = new short[count];

            for (int i = 0; i < count; i++)
            {
                double t = (double)i / sampleRate;
                double frac = (double)i / count;
                double freq = 900.0 - 600.0 * frac;            // sweep 900 -> 300 Hz
                double env = Math.Exp(-7.0 * frac);            // fast exponential decay
                double tone = Math.Sin(2.0 * Math.PI * freq * t);
                double square = tone >= 0 ? 0.6 : -0.6;        // a little edge/grit
                double v = (0.6 * tone + 0.4 * square) * env * amplitude;
                samples[i] = (short)(Math.Clamp(v, -1.0, 1.0) * short.MaxValue);
            }

            return samples;
        }

        /// <summary>A weapon impact: a short, bright noise crackle with a fast decay — the "zat" of a hit.</summary>
        public static short[] GenerateImpact(int seed = 1, float seconds = 0.16f, int sampleRate = 44100, float amplitude = 0.5f)
        {
            int count = Math.Max(1, (int)(seconds * sampleRate));
            var samples = new short[count];
            var rng = new Random(seed);
            double hp = 0; // crude high-pass state for a brighter crack
            for (int i = 0; i < count; i++)
            {
                double frac = (double)i / count;
                double env = Math.Exp(-12.0 * frac);
                double white = rng.NextDouble() * 2.0 - 1.0;
                hp = white - hp * 0.5;
                double tone = Math.Sin(2.0 * Math.PI * 520.0 * (i / (double)sampleRate));
                double v = (0.7 * hp + 0.3 * tone) * env * amplitude;
                samples[i] = (short)(Math.Clamp(v, -1.0, 1.0) * short.MaxValue);
            }

            return samples;
        }

        /// <summary>
        /// An explosion: filtered noise plus a low boom, under an exponential decay — a thump with a tail.
        /// Seeded so it is deterministic; vary <paramref name="seed"/> per call for non-identical blasts.
        /// </summary>
        public static short[] GenerateExplosion(int seed = 1, float seconds = 1.1f, int sampleRate = 44100, float amplitude = 0.8f)
        {
            int count = Math.Max(1, (int)(seconds * sampleRate));
            var samples = new short[count];
            var rng = new Random(seed);

            double low = 0; // one-pole low-pass state, to keep the noise from being thin/hissy
            for (int i = 0; i < count; i++)
            {
                double t = (double)i / sampleRate;
                double frac = (double)i / count;
                double env = Math.Exp(-3.2 * frac);

                double white = rng.NextDouble() * 2.0 - 1.0;
                low += (white - low) * 0.25;                    // ~low-pass
                double boom = Math.Sin(2.0 * Math.PI * (60.0 - 25.0 * frac) * t); // sinking sub boom

                double v = (0.7 * low + 0.5 * boom) * env * amplitude;
                samples[i] = (short)(Math.Clamp(v, -1.0, 1.0) * short.MaxValue);
            }

            return samples;
        }
    }
}
