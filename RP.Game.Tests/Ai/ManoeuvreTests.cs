namespace RP.Game.Tests.Ai
{
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Ai;
    using RP.Game.Core;
    using RP.Math;

    /// <summary>The combat manoeuvres: break turns cross the threat line, extends leave it (slightly
    /// skewed), formation slots stack an echelon — plus the anti-collision light rhythms.</summary>
    [TestClass]
    public sealed class ManoeuvreTests
    {
        [TestMethod]
        public void BreakTurn_SteersAcrossTheThreatLine_NotAlongIt()
        {
            var position = new Vector3d(1000, 0, 0);
            var velocity = new Vector3d(0, 0, 50); // cruising slowly across the line
            Vector3d threat = Vector3d.Zero;       // shooter directly on -X; the threat line is X

            Vector3d force = Steering.BreakTurn(position, velocity, threat, maxSpeed: 200, maxForce: 500);

            force.Magnitude.Should().BeGreaterThan(0);
            // The desired direction is perpendicular to the threat line: the steering force should have
            // little component along X compared to across it.
            System.Math.Abs(force.X).Should().BeLessThan(force.Magnitude * 0.35);
        }

        [TestMethod]
        public void BreakTurn_PhaseFlipsTheDirection()
        {
            var position = new Vector3d(1000, 0, 0);
            var velocity = new Vector3d(0, 0, 50);
            Vector3d plus = Steering.BreakTurn(position, velocity, Vector3d.Zero, 200, 500, phase: 1);
            Vector3d minus = Steering.BreakTurn(position, velocity, Vector3d.Zero, 200, 500, phase: -1);

            plus.DotProduct(minus).Should().BeLessThan(0, "opposite phases must jink opposite ways");
        }

        [TestMethod]
        public void Extend_FleesTheThreat_WithASkew()
        {
            var position = new Vector3d(500, 0, 0);
            Vector3d force = Steering.Extend(position, Vector3d.Zero, Vector3d.Zero, maxSpeed: 200, maxForce: 500);

            force.X.Should().BeGreaterThan(0, "extending means opening the range");
            // But not a pure flee line: some cross-component keeps the pursuer's nose working.
            (System.Math.Abs(force.Y) + System.Math.Abs(force.Z)).Should().BeGreaterThan(force.Magnitude * 0.05);
        }

        [TestMethod]
        public void FormationSlots_StackAnAlternatingSteppedEchelon()
        {
            Steering.FormationSlotLocal(0, 100).Should().Be(Vector3d.Zero); // the leader

            Vector3d one = Steering.FormationSlotLocal(1, 100);
            Vector3d two = Steering.FormationSlotLocal(2, 100);
            Vector3d three = Steering.FormationSlotLocal(3, 100);

            one.X.Should().BeGreaterThan(0);      // first wingman starboard...
            two.X.Should().BeLessThan(0);         // ...second port...
            three.X.Should().BeGreaterThan(one.X); // ...third further out starboard
            one.Z.Should().BeGreaterThan(0);      // everyone stepped back (aft = +Z)
            one.Y.Should().BeLessThan(0);         // and slightly low
        }

        [TestMethod]
        public void BlinkPatterns_HaveTheirSignatures()
        {
            // Beacon: exactly one lit window per second.
            BlinkPattern.Beacon(0.05).Should().BeTrue();
            BlinkPattern.Beacon(0.5).Should().BeFalse();
            BlinkPattern.Beacon(1.06).Should().BeTrue(); // periodic

            // Strobe: double-flash — lit, gap, lit, then long dark.
            BlinkPattern.Strobe(0.03).Should().BeTrue();
            BlinkPattern.Strobe(0.12).Should().BeFalse();
            BlinkPattern.Strobe(0.20).Should().BeTrue();
            BlinkPattern.Strobe(0.8).Should().BeFalse();

            // Distress: long deliberate burn.
            BlinkPattern.Distress(0.3).Should().BeTrue();
            BlinkPattern.Distress(1.2).Should().BeFalse();

            // Phase shifts the cycle; negative times stay well-defined.
            BlinkPattern.Beacon(0.5, phase: 0.55).Should().BeTrue();
            BlinkPattern.Strobe(-0.02, phase: 0.05).Should().BeTrue();
            BlinkPattern.Steady(123.4).Should().BeTrue();
        }
    }
}
