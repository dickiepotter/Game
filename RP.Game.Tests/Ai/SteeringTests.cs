namespace RP.Game.Tests.Ai
{
    using System.Collections.Generic;
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Ai;
    using RP.Math;

    [TestClass]
    public sealed class SteeringTests
    {
        [TestMethod]
        public void Seek_SteersTowardTheTarget()
        {
            Vector3d force = Steering.Seek(Vector3d.Origin, Vector3d.Zero, new Vector3d(10, 0, 0), maxSpeed: 5, maxForce: 100);
            force.X.Should().BeGreaterThan(0);
            force.Y.Should().BeApproximately(0, 1e-9);
        }

        [TestMethod]
        public void Flee_SteersAwayFromTheThreat()
        {
            Vector3d force = Steering.Flee(Vector3d.Origin, Vector3d.Zero, new Vector3d(10, 0, 0), maxSpeed: 5, maxForce: 100);
            force.X.Should().BeLessThan(0);
        }

        [TestMethod]
        public void Arrive_DesiresLessSpeedWhenCloseThanWhenFar()
        {
            var target = new Vector3d(1000, 0, 0);
            double far = Steering.Arrive(Vector3d.Origin, Vector3d.Zero, target, maxSpeed: 50, maxForce: 1e9, slowRadius: 50).Magnitude;

            var nearTarget = new Vector3d(10, 0, 0); // within the 50 m slow radius
            double near = Steering.Arrive(Vector3d.Origin, Vector3d.Zero, nearTarget, maxSpeed: 50, maxForce: 1e9, slowRadius: 50).Magnitude;

            near.Should().BeLessThan(far);
        }

        [TestMethod]
        public void Pursue_LeadsAheadOfAMovingTarget()
        {
            // Target at +X moving +Y: the pursuer should steer with a +Y component to cut it off.
            Vector3d force = Steering.Pursue(
                Vector3d.Origin, Vector3d.Zero,
                new Vector3d(100, 0, 0), new Vector3d(0, 10, 0), maxSpeed: 50, maxForce: 1000);
            force.Y.Should().BeGreaterThan(0);
        }

        [TestMethod]
        public void Separation_PushesAwayFromCloseNeighbours()
        {
            var neighbours = new List<Vector3d> { new Vector3d(1, 0, 0) };
            Vector3d force = Steering.Separation(Vector3d.Origin, neighbours, radius: 5, maxForce: 100);
            force.X.Should().BeLessThan(0); // away from the neighbour at +X
        }

        [TestMethod]
        public void Separation_IgnoresDistantNeighboursAndEmptySets()
        {
            Steering.Separation(Vector3d.Origin, new List<Vector3d>(), 5, 100).Should().Be(Vector3d.Origin);
            Steering.Separation(Vector3d.Origin, new List<Vector3d> { new Vector3d(100, 0, 0) }, 5, 100)
                .Should().Be(Vector3d.Origin);
        }
    }
}
