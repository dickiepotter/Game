namespace RP.Game.Tests.Audio
{
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Audio;
    using RP.Math;

    [TestClass]
    public sealed class AudioMathTests
    {
        private const float Tol = 1e-4f;

        [TestMethod]
        public void Decibels_RoundTrip()
        {
            AudioMath.DecibelsToLinear(0f).Should().BeApproximately(1f, Tol);     // 0 dB = unity
            AudioMath.DecibelsToLinear(-6f).Should().BeApproximately(0.5012f, 1e-3f); // ~half
            AudioMath.LinearToDecibels(1f).Should().BeApproximately(0f, Tol);
            AudioMath.LinearToDecibels(0f).Should().Be(-144f); // silence floor
        }

        [TestMethod]
        public void InverseDistance_IsUnityAtReferenceAndFallsOff()
        {
            AudioMath.InverseDistanceClamped(1f, referenceDistance: 1f, maxDistance: 100f).Should().BeApproximately(1f, Tol);
            // At twice the reference distance, inverse model gives 1/2.
            AudioMath.InverseDistanceClamped(2f, 1f, 100f).Should().BeApproximately(0.5f, Tol);
            // Closer than reference is clamped to unity.
            AudioMath.InverseDistanceClamped(0.1f, 1f, 100f).Should().BeApproximately(1f, Tol);
            // Beyond max is clamped (no infinite quietening).
            AudioMath.InverseDistanceClamped(1000f, 1f, 100f).Should()
                .Be(AudioMath.InverseDistanceClamped(100f, 1f, 100f));
        }

        [TestMethod]
        public void LinearDistance_GoesFromOneToZero()
        {
            AudioMath.LinearDistanceClamped(1f, 1f, 11f).Should().BeApproximately(1f, Tol);
            AudioMath.LinearDistanceClamped(6f, 1f, 11f).Should().BeApproximately(0.5f, Tol);
            AudioMath.LinearDistanceClamped(11f, 1f, 11f).Should().BeApproximately(0f, Tol);
            AudioMath.LinearDistanceClamped(50f, 1f, 11f).Should().BeApproximately(0f, Tol);
        }

        [TestMethod]
        public void Doppler_SourceApproaching_RaisesPitch()
        {
            // Source at origin moving +X toward a listener sitting at +X.
            float ratio = AudioMath.DopplerRatio(
                sourcePosition: Vector3.Zero, sourceVelocity: new Vector3(30, 0, 0),
                listenerPosition: new Vector3(10, 0, 0), listenerVelocity: Vector3.Zero,
                speedOfSound: 343f);
            ratio.Should().BeGreaterThan(1f);
        }

        [TestMethod]
        public void Doppler_SourceReceding_LowersPitch()
        {
            float ratio = AudioMath.DopplerRatio(
                Vector3.Zero, new Vector3(-30, 0, 0),
                new Vector3(10, 0, 0), Vector3.Zero, 343f);
            ratio.Should().BeLessThan(1f);
        }

        [TestMethod]
        public void Doppler_Stationary_IsUnity()
        {
            AudioMath.DopplerRatio(Vector3.Zero, Vector3.Zero, new Vector3(10, 0, 0), Vector3.Zero, 343f)
                .Should().BeApproximately(1f, Tol);
        }

        [TestMethod]
        public void ComposeGain_MultipliesTheChain()
        {
            AudioMath.ComposeGain(0.5f, 0.5f, 0.5f).Should().BeApproximately(0.125f, Tol);
            AudioMath.ComposeGain().Should().Be(1f);
        }
    }
}
