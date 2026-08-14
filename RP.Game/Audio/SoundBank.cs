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

        /// <summary>A metal-on-metal collision: a hard broadband crack with a ringing body — the CLANG of a
        /// hull scraping or slamming something solid. Deterministic per seed.</summary>
        public static short[] GenerateClang(int seed = 1, float seconds = 0.45f, int sampleRate = 44100, float amplitude = 0.7f)
        {
            int count = Math.Max(1, (int)(seconds * sampleRate));
            var samples = new short[count];
            var rng = new Random(seed);

            // Struck-plate ring: a few inharmonic partials (like a real plate's modes), each decaying at its
            // own rate, over a very short noise transient for the initial impact crack.
            (double hz, double w, double decay)[] modes =
            {
                (211, 1.00, 6.0), (487, 0.62, 8.5), (829, 0.38, 11.0), (1310, 0.22, 15.0), (2170, 0.12, 20.0),
            };

            for (int i = 0; i < count; i++)
            {
                double t = (double)i / sampleRate;
                double frac = (double)i / count;

                double v = 0;
                foreach (var m in modes)
                {
                    v += m.w * Math.Sin(2.0 * Math.PI * m.hz * t) * Math.Exp(-m.decay * frac * seconds * 2.2);
                }
                v *= 0.5;

                if (t < 0.02) // the first 20 ms: the impact crack itself
                {
                    v += (rng.NextDouble() * 2.0 - 1.0) * (1.0 - t / 0.02) * 0.9;
                }

                samples[i] = (short)(Math.Clamp(v * amplitude, -1.0, 1.0) * short.MaxValue);
            }

            return samples;
        }

        /// <summary>A shield taking a hit: a bright electric fizz — ring-modulated noise that sweeps down as
        /// the barrier sheds the energy. Distinct from the hull "zat" so the ear reads shield vs armour.</summary>
        public static short[] GenerateShieldFizz(int seed = 1, float seconds = 0.22f, int sampleRate = 44100, float amplitude = 0.5f)
        {
            int count = Math.Max(1, (int)(seconds * sampleRate));
            var samples = new short[count];
            var rng = new Random(seed);
            double lp = 0;
            for (int i = 0; i < count; i++)
            {
                double t = (double)i / sampleRate;
                double frac = (double)i / count;
                double env = Math.Exp(-9.0 * frac);
                double white = rng.NextDouble() * 2.0 - 1.0;
                lp += (white - lp) * 0.55;
                double carrier = Math.Sin(2.0 * Math.PI * (1600.0 - 900.0 * frac) * t); // sweeping shimmer
                double v = lp * carrier * env * amplitude * 1.4;
                samples[i] = (short)(Math.Clamp(v, -1.0, 1.0) * short.MaxValue);
            }

            return samples;
        }

        /// <summary>
        /// A cockpit warning: <paramref name="beeps"/> urgent square-ish pips at <paramref name="frequencyHz"/>.
        /// One generator covers the alarm family — missile lock (high/fast), collision (mid), hull critical
        /// (low/slow) — by varying pitch and count.
        /// </summary>
        public static short[] GenerateWarning(double frequencyHz = 880, int beeps = 3, float beepSeconds = 0.07f,
            float gapSeconds = 0.05f, int sampleRate = 44100, float amplitude = 0.4f)
        {
            int beepLen = Math.Max(1, (int)(beepSeconds * sampleRate));
            int gapLen = Math.Max(0, (int)(gapSeconds * sampleRate));
            int count = beeps * beepLen + Math.Max(0, beeps - 1) * gapLen;
            var samples = new short[count];

            for (int b = 0; b < beeps; b++)
            {
                int start = b * (beepLen + gapLen);
                for (int i = 0; i < beepLen; i++)
                {
                    double t = (double)i / sampleRate;
                    // Soft attack/release edges so the pip doesn't click.
                    double edge = Math.Min(1.0, Math.Min(i / (0.004 * sampleRate), (beepLen - i) / (0.004 * sampleRate)));
                    double tone = Math.Sin(2.0 * Math.PI * frequencyHz * t);
                    double square = tone >= 0 ? 0.5 : -0.5;
                    samples[start + i] = (short)(Math.Clamp((0.7 * tone + 0.3 * square) * edge * amplitude, -1.0, 1.0) * short.MaxValue);
                }
            }

            return samples;
        }

        /// <summary>
        /// Radio chatter: a short squelch-framed burst of band-limited syllable-rhythm noise — the sound of a
        /// voice on a distant channel without any actual words. Seeded, so each call sign "speaks" the same.
        /// </summary>
        public static short[] GenerateChatter(int seed = 1, float seconds = 0.9f, int sampleRate = 44100, float amplitude = 0.35f)
        {
            int count = Math.Max(1, (int)(seconds * sampleRate));
            var samples = new short[count];
            var rng = new Random(seed);

            // Syllable envelope: 4-8 bumps of varying length/strength, like speech cadence.
            int syllables = 4 + rng.Next(5);
            var centres = new double[syllables];
            var widths = new double[syllables];
            var strengths = new double[syllables];
            for (int sIx = 0; sIx < syllables; sIx++)
            {
                centres[sIx] = (sIx + 0.5 + (rng.NextDouble() - 0.5) * 0.4) / syllables;
                widths[sIx] = 0.03 + rng.NextDouble() * 0.05;
                strengths[sIx] = 0.5 + rng.NextDouble() * 0.5;
            }

            double bp = 0, bp2 = 0;
            double voicePitch = 95 + rng.NextDouble() * 60; // per-speaker buzz
            for (int i = 0; i < count; i++)
            {
                double t = (double)i / sampleRate;
                double frac = (double)i / count;

                double env = 0;
                for (int sIx = 0; sIx < syllables; sIx++)
                {
                    double d = (frac - centres[sIx]) / widths[sIx];
                    env += strengths[sIx] * Math.Exp(-d * d);
                }
                env = Math.Min(env, 1.0);

                // Voice-ish source: a glottal buzz amplitude-modulating band-passed noise.
                double white = rng.NextDouble() * 2.0 - 1.0;
                bp += (white - bp) * 0.35;   // low-pass
                bp2 += (bp - bp2) * 0.10;    // second pole
                double band = bp - bp2;      // crude band-pass, telephone-ish
                double buzz = 0.6 + 0.4 * Math.Sign(Math.Sin(2.0 * Math.PI * voicePitch * t));
                double v = band * buzz * env * 2.2;

                // Squelch clicks framing the transmission.
                if (i < 0.012 * sampleRate || i > count - 0.012 * sampleRate)
                {
                    v += (rng.NextDouble() * 2.0 - 1.0) * 0.5;
                }

                samples[i] = (short)(Math.Clamp(v * amplitude, -1.0, 1.0) * short.MaxValue);
            }

            return samples;
        }

        /// <summary>A missile leaving the rail: an igniting whoosh — noise swept from low to bright under a
        /// rising envelope, with a kick at ignition.</summary>
        public static short[] GenerateMissileLaunch(int seed = 1, float seconds = 0.7f, int sampleRate = 44100, float amplitude = 0.6f)
        {
            int count = Math.Max(1, (int)(seconds * sampleRate));
            var samples = new short[count];
            var rng = new Random(seed);
            double lp = 0;
            for (int i = 0; i < count; i++)
            {
                double t = (double)i / sampleRate;
                double frac = (double)i / count;
                // Motor roar: low-passed noise whose cutoff opens as the motor spools, then fades with distance.
                double cutoff = 0.08 + 0.5 * frac;
                double white = rng.NextDouble() * 2.0 - 1.0;
                lp += (white - lp) * cutoff;
                double env = frac < 0.15 ? frac / 0.15 : Math.Exp(-2.5 * (frac - 0.15));
                double kick = t < 0.05 ? Math.Sin(2.0 * Math.PI * 70.0 * t) * (1.0 - t / 0.05) * 0.8 : 0.0;
                samples[i] = (short)(Math.Clamp((lp * 1.6 * env + kick) * amplitude, -1.0, 1.0) * short.MaxValue);
            }

            return samples;
        }

        /// <summary>A steam/plasma vent: a broadband hiss swelling then trailing off — the voice of a
        /// ruptured line in a dead corridor. Deterministic per seed.</summary>
        public static short[] GenerateHiss(int seed = 1, float seconds = 1.1f, int sampleRate = 44100, float amplitude = 0.4f)
        {
            int count = Math.Max(1, (int)(seconds * sampleRate));
            var samples = new short[count];
            var rng = new Random(seed);
            double hp = 0;
            for (int i = 0; i < count; i++)
            {
                double frac = (double)i / count;
                double env = frac < 0.2 ? frac / 0.2 : Math.Exp(-2.2 * (frac - 0.2));
                double white = rng.NextDouble() * 2.0 - 1.0;
                hp = white - hp * 0.35; // brighten toward a "sss" rather than a rumble
                samples[i] = (short)(Math.Clamp(hp * 0.8 * env * amplitude, -1.0, 1.0) * short.MaxValue);
            }

            return samples;
        }

        /// <summary>The hit-marker tick: a tiny bright click confirming the player's own round connected.
        /// Deliberately dry and instant so it reads as UI, not world audio.</summary>
        public static short[] GenerateHitTick(float seconds = 0.05f, int sampleRate = 44100, float amplitude = 0.5f)
        {
            int count = Math.Max(1, (int)(seconds * sampleRate));
            var samples = new short[count];
            for (int i = 0; i < count; i++)
            {
                double t = (double)i / sampleRate;
                double frac = (double)i / count;
                double env = Math.Exp(-20.0 * frac);
                double v = (Math.Sin(2.0 * Math.PI * 1900.0 * t) * 0.7 + Math.Sin(2.0 * Math.PI * 2850.0 * t) * 0.3) * env;
                samples[i] = (short)(Math.Clamp(v * amplitude, -1.0, 1.0) * short.MaxValue);
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
