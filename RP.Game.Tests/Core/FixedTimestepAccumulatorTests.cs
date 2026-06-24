namespace RP.Game.Tests.Core
{
    using System;
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Core;

    /// <summary>
    /// Tests for <see cref="FixedTimestepAccumulator"/> — the frame-rate-independence guarantee the
    /// whole simulation rests on. Each test names the property it pins down.
    /// </summary>
    [TestClass]
    public sealed class FixedTimestepAccumulatorTests
    {
        private const double Dt = 1.0 / 60.0;
        // Floating-point step arithmetic is not exact, so leftover-time comparisons use a tolerance.
        private const double Eps = 1e-9;

        // ---- Construction & validation -------------------------------------------------------------

        [TestMethod]
        public void Constructor_Defaults_AreSixtyHertzWithQuarterSecondClamp()
        {
            var acc = new FixedTimestepAccumulator();

            acc.FixedDeltaSeconds.Should().BeApproximately(Dt, Eps);
            acc.MaxFrameSeconds.Should().BeApproximately(0.25, Eps);
            acc.Accumulator.Should().Be(0.0);
            acc.Alpha.Should().Be(0.0);
        }

        [DataTestMethod]
        [DataRow(0.0)]
        [DataRow(-0.001)]
        [DataRow(double.NaN)]
        [DataRow(double.PositiveInfinity)]
        public void Constructor_RejectsNonPositiveOrNonFiniteStep(double badStep)
        {
            Action act = () => _ = new FixedTimestepAccumulator(badStep);
            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [TestMethod]
        public void Constructor_RejectsClampSmallerThanOneStep()
        {
            // A clamp below a single step would discard so much time that no step could ever run.
            Action act = () => _ = new FixedTimestepAccumulator(Dt, Dt / 2.0);
            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [TestMethod]
        public void Constructor_AllowsClampExactlyOneStep()
        {
            Action act = () => _ = new FixedTimestepAccumulator(Dt, Dt);
            act.Should().NotThrow();
        }

        // ---- Core stepping behaviour ---------------------------------------------------------------

        [TestMethod]
        public void Advance_ByExactlyOneStep_RunsOneStepAndLeavesNoRemainder()
        {
            var acc = new FixedTimestepAccumulator(Dt);

            acc.Advance(Dt).Should().Be(1);
            acc.Accumulator.Should().BeApproximately(0.0, Eps);
            acc.Alpha.Should().BeApproximately(0.0, Eps);
        }

        [TestMethod]
        public void Advance_ByTwoAndAHalfSteps_RunsTwoStepsAndKeepsHalfAsAlpha()
        {
            var acc = new FixedTimestepAccumulator(Dt);

            acc.Advance(2.5 * Dt).Should().Be(2);
            acc.Alpha.Should().BeApproximately(0.5, 1e-6);
        }

        [TestMethod]
        public void Advance_BelowOneStep_RunsNothingButAccumulates()
        {
            var acc = new FixedTimestepAccumulator(Dt);

            acc.Advance(0.4 * Dt).Should().Be(0);
            acc.Advance(0.4 * Dt).Should().Be(0);
            // 0.8 dt banked, still short of a step...
            acc.Advance(0.4 * Dt).Should().Be(1); // ...now 1.2 dt: exactly one step fires.
            acc.Alpha.Should().BeApproximately(0.2, 1e-6);
        }

        [TestMethod]
        public void Advance_ByZero_IsANoOp()
        {
            var acc = new FixedTimestepAccumulator(Dt);
            acc.Advance(0.6 * Dt);

            acc.Advance(0.0).Should().Be(0);
            acc.Alpha.Should().BeApproximately(0.6, 1e-6);
        }

        // ---- Frame-rate independence (the headline property) ---------------------------------------

        [TestMethod]
        public void Advance_ManySmallFramesVsFewLargeFrames_ProduceSameTotalSteps()
        {
            // The same wall-clock span must yield the same number of simulation steps whether it arrived
            // as many tiny frames (fast machine) or a few big ones (slow machine). This IS frame-rate
            // independence, expressed as a test.
            const double totalTime = 2.0; // seconds
            const double expected = totalTime / Dt;

            var fast = new FixedTimestepAccumulator(Dt);
            int fastSteps = 0;
            for (int i = 0; i < 1000; i++) fastSteps += fast.Advance(totalTime / 1000.0);

            var slow = new FixedTimestepAccumulator(Dt);
            int slowSteps = 0;
            for (int i = 0; i < 20; i++) slowSteps += slow.Advance(totalTime / 20.0);

            fastSteps.Should().Be(slowSteps);
            fastSteps.Should().BeInRange((int)expected - 1, (int)expected + 1);
        }

        // ---- Spiral-of-death clamp -----------------------------------------------------------------

        [TestMethod]
        public void Advance_ByAnEnormousHitch_IsClampedNotCatastrophic()
        {
            var acc = new FixedTimestepAccumulator(Dt, maxFrameSeconds: 0.25);

            // A 10-second stall must not unleash 600 steps; it is capped at the clamp (≈15 steps).
            int steps = acc.Advance(10.0);

            steps.Should().Be((int)(0.25 / Dt)); // 15
            acc.Accumulator.Should().BeLessThan(Dt);
        }

        [TestMethod]
        public void Advance_ClampDiscardsTime_SimulationFallsBehindRealTimeDeliberately()
        {
            var acc = new FixedTimestepAccumulator(Dt, maxFrameSeconds: Dt); // clamp to a single step

            // Three frames each ask for 5 steps' worth of time, but the clamp allows only one step each.
            acc.Advance(5 * Dt).Should().Be(1);
            acc.Advance(5 * Dt).Should().Be(1);
            acc.Advance(5 * Dt).Should().Be(1);
        }

        // ---- Reset ---------------------------------------------------------------------------------

        [TestMethod]
        public void Reset_DropsLeftoverTimeWithoutRunningSteps()
        {
            var acc = new FixedTimestepAccumulator(Dt);
            acc.Advance(0.7 * Dt);
            acc.Alpha.Should().BeApproximately(0.7, 1e-6);

            acc.Reset();

            acc.Accumulator.Should().Be(0.0);
            acc.Alpha.Should().Be(0.0);
        }

        // ---- Input validation on Advance -----------------------------------------------------------

        [DataTestMethod]
        [DataRow(-0.001)]
        [DataRow(double.NaN)]
        [DataRow(double.NegativeInfinity)]
        [DataRow(double.PositiveInfinity)]
        public void Advance_RejectsNegativeOrNonFiniteFrameTime(double bad)
        {
            var acc = new FixedTimestepAccumulator(Dt);
            Action act = () => acc.Advance(bad);
            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [TestMethod]
        public void Advance_AfterRejectedInput_AccumulatorIsUnpoisoned()
        {
            var acc = new FixedTimestepAccumulator(Dt);
            acc.Advance(0.5 * Dt);

            try { acc.Advance(double.NaN); } catch (ArgumentOutOfRangeException) { /* expected */ }

            // The bad call must not have corrupted state: the half-step is still banked and usable.
            acc.Alpha.Should().BeApproximately(0.5, 1e-6);
            acc.Advance(0.5 * Dt).Should().Be(1);
        }
    }
}
