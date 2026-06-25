namespace RP.Game.Tests.Scene
{
    using System;
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Scene;
    using RP.Math;

    /// <summary>
    /// The moving/tumbling local frame (build brief S13): local↔world transforms round-trip, a tumble carries
    /// fixed points around with the right tangential velocity, and advancing the frame drifts and rotates it
    /// consistently. This is the maths the wreck interior rides on.
    /// </summary>
    [TestClass]
    public sealed class ReferenceFrameTests
    {
        [TestMethod]
        public void ToWorldThenToLocal_RoundTrips()
        {
            var frame = new ReferenceFrame
            {
                Position = new Vector3d(12000, -500, 3000),
                Orientation = Quaternion.FromAxisAngle(new Vector3d(0, 1, 0).Normalize(), new Angle(0.7)),
            };

            var local = new Vector3d(100, -40, 250);
            Vector3d back = frame.ToLocal(frame.ToWorld(local));
            back.Distance(local).Should().BeLessThan(1e-9);
        }

        [TestMethod]
        public void Translation_OffsetsPointsButNotDirections()
        {
            var frame = new ReferenceFrame { Position = new Vector3d(1000, 0, 0) };

            frame.ToWorld(new Vector3d(5, 0, 0)).Should().Be(new Vector3d(1005, 0, 0));
            // A pure direction ignores the origin offset.
            frame.DirectionToWorld(new Vector3d(0, 0, -1)).Distance(new Vector3d(0, 0, -1)).Should().BeLessThan(1e-12);
        }

        [TestMethod]
        public void Orientation_RotatesLocalAxesIntoWorld()
        {
            // 90° about world +Z sends local +X to world +Y.
            var frame = new ReferenceFrame
            {
                Orientation = Quaternion.FromAxisAngle(new Vector3d(0, 0, 1), new Angle(Math.PI / 2)),
            };

            frame.DirectionToWorld(new Vector3d(1, 0, 0)).Distance(new Vector3d(0, 1, 0)).Should().BeLessThan(1e-9);
        }

        [TestMethod]
        public void PointVelocity_IsDriftPlusTangentialTumble()
        {
            // Spin about +Z at 2 rad/s, drifting at +X 10 m/s. A point 3 m out along local +X sits on world +X,
            // so its tumble velocity is ω × r = (0,0,2) × (3,0,0) = (0,6,0), plus the 10 m/s drift on X.
            var frame = new ReferenceFrame
            {
                Velocity = new Vector3d(10, 0, 0),
                AngularVelocity = new Vector3d(0, 0, 2),
            };

            Vector3d v = frame.PointVelocity(new Vector3d(3, 0, 0));
            v.Distance(new Vector3d(10, 6, 0)).Should().BeLessThan(1e-9);
        }

        [TestMethod]
        public void Advance_DriftsAndTumblesConsistently()
        {
            var frame = new ReferenceFrame
            {
                Velocity = new Vector3d(5, 0, 0),
                AngularVelocity = new Vector3d(0, 0, 1), // 1 rad/s about +Z
            };

            // Integrate a quarter-turn (π/2 s) in small steps.
            const double dt = 1.0 / 240.0;
            double t = 0;
            while (t < Math.PI / 2)
            {
                frame.Advance(dt);
                t += dt;
            }

            frame.Position.Distance(new Vector3d(5 * t, 0, 0)).Should().BeLessThan(1e-6);
            // After ~90° about +Z, local +X points to roughly world +Y.
            frame.DirectionToWorld(new Vector3d(1, 0, 0)).Distance(new Vector3d(0, 1, 0)).Should().BeLessThan(1e-2);
        }

        [TestMethod]
        public void StationaryFrame_LeavesPointsAndDirectionsUntouched()
        {
            var frame = new ReferenceFrame();
            var p = new Vector3d(3, -7, 11);
            frame.ToWorld(p).Should().Be(p);
            frame.ToLocal(p).Should().Be(p);
        }
    }
}
