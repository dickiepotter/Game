namespace RP.Game.Tests.Core
{
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Core;

    [TestClass]
    public sealed class GameTimeTests
    {
        private const double Dt = 1.0 / 60.0;

        [TestMethod]
        public void Advanced_AddsOneStepAndOneDeltaOfTime()
        {
            var t0 = new GameTime(Dt, totalSeconds: 0.0, stepCount: 0);

            var t1 = t0.Advanced();

            t1.StepCount.Should().Be(1);
            t1.TotalSeconds.Should().BeApproximately(Dt, 1e-12);
            t1.FixedDeltaSeconds.Should().Be(Dt);
        }

        [TestMethod]
        public void Advanced_IsPure_LeavesTheOriginalUnchanged()
        {
            var t0 = new GameTime(Dt, 0.0, 0);

            _ = t0.Advanced();

            t0.StepCount.Should().Be(0);
            t0.TotalSeconds.Should().Be(0.0);
        }

        [TestMethod]
        public void Advanced_ChainedSixtyTimes_ReachesOneSecond()
        {
            var t = new GameTime(Dt, 0.0, 0);

            for (int i = 0; i < 60; i++) t = t.Advanced();

            t.StepCount.Should().Be(60);
            t.TotalSeconds.Should().BeApproximately(1.0, 1e-9);
        }

        [TestMethod]
        public void Equality_ComparesAllThreeFields()
        {
            var a = new GameTime(Dt, 1.0, 60);
            var b = new GameTime(Dt, 1.0, 60);
            var c = new GameTime(Dt, 1.0, 61);

            a.Equals(b).Should().BeTrue();
            a.Equals(c).Should().BeFalse();
            a.GetHashCode().Should().Be(b.GetHashCode());
        }
    }
}
