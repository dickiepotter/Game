namespace RP.Game.Tests.Physics
{
    using System;
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Physics;
    using RP.Math;

    /// <summary>
    /// Physics integration correctness (build brief S20): no force → conserved momentum, constant thrust →
    /// expected motion, frame-rate independence, and that local force/torque rotate into world space.
    /// </summary>
    [TestClass]
    public sealed class RigidBodyTests
    {
        [TestMethod]
        public void NoForce_ConservesVelocityAndDriftsLinearly()
        {
            var body = new RigidBody { Velocity = new Vector3d(10, 0, 0), Mass = 5000 };

            for (int i = 0; i < 100; i++) body.Integrate(1.0 / 60.0);

            // Velocity unchanged; position advanced by v * totalTime exactly (a = 0).
            body.Velocity.Should().Be(new Vector3d(10, 0, 0));
            double t = 100.0 / 60.0;
            body.Position.Distance(new Vector3d(10 * t, 0, 0)).Should().BeLessThan(1e-9);
        }

        [TestMethod]
        public void ConstantThrust_VelocityIsExact_PositionNearAnalytic()
        {
            const double mass = 1000.0;
            var body = new RigidBody { Mass = mass };
            var force = new Vector3d(2000, 0, 0); // a = 2 m/s^2
            const double dt = 1.0 / 120.0;
            const int steps = 600; // 5 seconds
            double t = steps * dt;

            for (int i = 0; i < steps; i++)
            {
                body.ApplyForce(force);
                body.Integrate(dt);
            }

            double a = force.X / mass;
            body.Velocity.X.Should().BeApproximately(a * t, 1e-9);          // semi-implicit: velocity exact
            body.Position.X.Should().BeApproximately(0.5 * a * t * t, 0.1); // position within a small tolerance
        }

        [TestMethod]
        public void FrameRateIndependence_FinerStepLandsCloserToAnalytic()
        {
            double FinalErrorWith(double dt)
            {
                const double total = 4.0;
                int steps = (int)Math.Round(total / dt);
                var body = new RigidBody { Mass = 1.0 };
                var force = new Vector3d(0, 0, -3); // a = -3
                for (int i = 0; i < steps; i++)
                {
                    body.ApplyForce(force);
                    body.Integrate(dt);
                }

                double analyticZ = 0.5 * -3 * total * total;
                return Math.Abs(body.Position.Z - analyticZ);
            }

            FinalErrorWith(1.0 / 240.0).Should().BeLessThan(FinalErrorWith(1.0 / 30.0));
        }

        [TestMethod]
        public void ApplyForceLocal_PushesAlongTheBodysForward()
        {
            var body = new RigidBody { Mass = 1.0 };
            // Identity orientation: local forward is -Z, so a forward thrust accelerates along world -Z.
            body.ApplyForceLocal(new Vector3d(0, 0, -100));
            body.Integrate(1.0);

            body.Velocity.Z.Should().BeLessThan(0);
            body.Velocity.X.Should().BeApproximately(0, 1e-9);
        }

        [TestMethod]
        public void AngularVelocity_WithNoTorque_RotatesTheOrientationAndPersists()
        {
            var body = new RigidBody
            {
                AngularVelocity = new Vector3d(0, Math.PI / 2, 0), // 90 deg/s about Y
            };

            for (int i = 0; i < 60; i++) body.Integrate(1.0 / 60.0); // ~1 second -> ~90 degrees

            // Angular velocity persists (no torque), and forward (-Z) has yawed about +90 deg toward -X.
            body.AngularVelocity.Y.Should().BeApproximately(Math.PI / 2, 1e-9);
            Vector3d fwd = body.Forward;
            fwd.X.Should().BeApproximately(-1, 1e-2);
            Math.Abs(fwd.Z).Should().BeLessThan(1e-2);
        }

        [TestMethod]
        public void Torque_SpinsUpFromRest()
        {
            var body = new RigidBody { InertiaScalar = 2.0 };
            body.AngularVelocity.Should().Be(Vector3d.Zero);

            for (int i = 0; i < 10; i++)
            {
                body.ApplyTorque(new Vector3d(0, 4, 0));
                body.Integrate(0.1);
            }

            // angular accel = 4/2 = 2 rad/s^2 over ~1s -> ~2 rad/s.
            body.AngularVelocity.Y.Should().BeApproximately(2.0, 1e-9);
        }
    }
}
