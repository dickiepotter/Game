namespace RP.Game.Tests.Rendering
{
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Rendering;
    using RP.Math;

    /// <summary>The pilot-head spring: lags opposite sustained acceleration, respects its travel limit,
    /// recentres without oscillating, and shake decays to true zero.</summary>
    [TestClass]
    public sealed class HeadMotionTests
    {
        private static void Run(HeadMotion head, Vector3d accel, double seconds)
        {
            for (double t = 0; t < seconds; t += 1.0 / 120.0) head.Update(accel, 1.0 / 120.0);
        }

        [TestMethod]
        public void SustainedThrust_PressesTheHeadOppositeTheAcceleration()
        {
            var head = new HeadMotion();
            Run(head, new Vector3d(0, 0, -45), 2.0); // hard forward burn (forward = -Z)

            head.Offset.Z.Should().BeGreaterThan(0.1, "the head lags backward under forward thrust");
            head.Offset.Z.Should().BeApproximately(45 * head.Compliance, 0.02); // settled at the spring target
            head.Offset.X.Should().BeApproximately(0, 1e-6);
        }

        [TestMethod]
        public void TheTravelLimit_IsNeverExceeded()
        {
            var head = new HeadMotion { MaxTravel = 0.2 };
            Run(head, new Vector3d(0, 0, -100000), 1.0); // absurd acceleration
            head.Offset.Magnitude.Should().BeLessThanOrEqualTo(0.2 + 1e-9);
        }

        [TestMethod]
        public void ReleasingTheStick_RecentresWithoutOvershoot()
        {
            var head = new HeadMotion();
            Run(head, new Vector3d(0, 0, -45), 2.0);

            // Once the load releases, the offset should decay monotonically toward zero (critical damping).
            double previous = head.Offset.Magnitude;
            bool overshot = false;
            for (int i = 0; i < 600; i++)
            {
                head.Update(Vector3d.Zero, 1.0 / 120.0);
                double now = head.Offset.Magnitude;
                if (now > previous + 1e-9) overshot = true;
                previous = now;
            }

            overshot.Should().BeFalse("critical damping must not oscillate");
            head.Offset.Magnitude.Should().BeLessThan(0.01);
        }

        [TestMethod]
        public void Reset_SnapsToCentre()
        {
            var head = new HeadMotion();
            Run(head, new Vector3d(30, 0, 0), 1.0);
            head.Reset();
            head.Offset.Should().Be(Vector3d.Zero);
        }

        [TestMethod]
        public void CameraShake_RampsWithTraumaSquared_AndDecaysToZero()
        {
            var shake = new CameraShake { Amplitude = 1.0, Decay = 1.0 };
            shake.AddTrauma(0.5);
            Vector3d small = shake.Update(0.001);

            var big = new CameraShake { Amplitude = 1.0, Decay = 1.0 };
            big.AddTrauma(1.0);
            Vector3d large = big.Update(0.001);

            large.Magnitude.Should().BeGreaterThan(small.Magnitude * 2, "response is trauma², not linear");

            for (int i = 0; i < 300; i++) big.Update(1.0 / 60.0);
            big.Trauma.Should().Be(0);
            big.Update(1.0 / 60.0).Should().Be(Vector3d.Zero);
        }
    }
}
