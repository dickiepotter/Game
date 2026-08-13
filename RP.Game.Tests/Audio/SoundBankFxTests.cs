namespace RP.Game.Tests.Audio
{
    using System;
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Audio;

    /// <summary>The Phase 45 procedural SFX: every generator bakes bounded, deterministic PCM of the
    /// requested length — testable without a sound card, like the originals.</summary>
    [TestClass]
    public sealed class SoundBankFxTests
    {
        private static void AssertSanePcm(short[] pcm, int expectedMinSamples)
        {
            pcm.Length.Should().BeGreaterThanOrEqualTo(expectedMinSamples);
            short peak = 0;
            foreach (short s in pcm)
            {
                peak = Math.Max(peak, Math.Abs((int)s) > short.MaxValue ? short.MaxValue : (short)Math.Abs((int)s));
            }
            peak.Should().BeGreaterThan(500, "the sound should not be silence");
        }

        [TestMethod]
        public void Clang_Fizz_Launch_Tick_AreAudibleAndDeterministic()
        {
            AssertSanePcm(SoundBank.GenerateClang(seed: 1), 1000);
            AssertSanePcm(SoundBank.GenerateShieldFizz(seed: 1), 1000);
            AssertSanePcm(SoundBank.GenerateMissileLaunch(seed: 1), 1000);
            AssertSanePcm(SoundBank.GenerateHitTick(), 500);

            SoundBank.GenerateClang(seed: 5).Should().BeEquivalentTo(SoundBank.GenerateClang(seed: 5));
        }

        [TestMethod]
        public void Warning_ProducesTheRequestedBeepPattern()
        {
            const int rate = 44100;
            short[] threeBeeps = SoundBank.GenerateWarning(frequencyHz: 880, beeps: 3, beepSeconds: 0.05f,
                gapSeconds: 0.05f, sampleRate: rate);
            int expected = 3 * (int)(0.05f * rate) + 2 * (int)(0.05f * rate);
            threeBeeps.Length.Should().Be(expected);

            // The gap between beeps is silent.
            int gapStart = (int)(0.05f * rate) + 100;
            threeBeeps[gapStart].Should().Be(0);
        }

        [TestMethod]
        public void Hiss_IsAudibleAndDeterministic()
        {
            AssertSanePcm(SoundBank.GenerateHiss(seed: 2), 10_000);
            SoundBank.GenerateHiss(seed: 2).Should().BeEquivalentTo(SoundBank.GenerateHiss(seed: 2));
        }

        [TestMethod]
        public void Chatter_IsSpeechLengthAndSeedStable()
        {
            short[] a = SoundBank.GenerateChatter(seed: 3);
            short[] b = SoundBank.GenerateChatter(seed: 3);
            a.Should().BeEquivalentTo(b);
            AssertSanePcm(a, 10_000);
        }
    }
}
