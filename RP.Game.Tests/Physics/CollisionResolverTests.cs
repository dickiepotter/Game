namespace RP.Game.Tests.Physics
{
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Physics;
    using RP.Math;

    /// <summary>
    /// Collision/impulse correctness (build brief S20): reduced mass, momentum conservation, impulse
    /// symmetry, separating bodies left alone, and the light-vs-heavy asymmetry.
    /// </summary>
    [TestClass]
    public sealed class CollisionResolverTests
    {
        [TestMethod]
        public void ReducedMass_EqualMasses_IsHalf()
        {
            CollisionResolver.ReducedMass(1000, 1000).Should().BeApproximately(500, 1e-9);
            // Dominated by the lighter body.
            CollisionResolver.ReducedMass(10, 1_000_000).Should().BeApproximately(9.9999, 1e-3);
        }

        [TestMethod]
        public void SpheresOverlap_DetectsHitAndMiss()
        {
            CollisionResolver.SpheresOverlap(Vector3d.Origin, 2, new Vector3d(3, 0, 0), 2, out var n, out double pen)
                .Should().BeTrue();
            n.X.Should().BeApproximately(1, 1e-9); // A->B along +x
            pen.Should().BeApproximately(1, 1e-9); // (2+2) - 3

            CollisionResolver.SpheresOverlap(Vector3d.Origin, 1, new Vector3d(5, 0, 0), 1, out _, out _)
                .Should().BeFalse();
        }

        [TestMethod]
        public void Resolve_ConservesMomentum()
        {
            var a = new RigidBody { Mass = 1000, Velocity = new Vector3d(100, 0, 0) };
            var b = new RigidBody { Mass = 3000, Velocity = new Vector3d(-20, 0, 0) };

            Vector3d before = a.Velocity * a.Mass + b.Velocity * b.Mass;
            CollisionResolver.Resolve(a, b, new Vector3d(1, 0, 0), restitution: 0.5);
            Vector3d after = a.Velocity * a.Mass + b.Velocity * b.Mass;

            after.Distance(before).Should().BeLessThan(1e-6);
        }

        [TestMethod]
        public void Resolve_ElasticEqualMassHeadOn_SwapsVelocities()
        {
            var a = new RigidBody { Mass = 1000, Velocity = new Vector3d(100, 0, 0) };
            var b = new RigidBody { Mass = 1000, Velocity = new Vector3d(-100, 0, 0) };

            CollisionResolver.Resolve(a, b, new Vector3d(1, 0, 0), restitution: 1.0);

            a.Velocity.X.Should().BeApproximately(-100, 1e-6);
            b.Velocity.X.Should().BeApproximately(100, 1e-6);
        }

        [TestMethod]
        public void Resolve_SeparatingBodies_DoNothing()
        {
            var a = new RigidBody { Mass = 1000, Velocity = new Vector3d(-10, 0, 0) }; // moving away from B
            var b = new RigidBody { Mass = 1000, Velocity = new Vector3d(10, 0, 0) };
            Vector3d va = a.Velocity, vb = b.Velocity;

            double energy = CollisionResolver.Resolve(a, b, new Vector3d(1, 0, 0));

            energy.Should().Be(0);
            a.Velocity.Should().Be(va);
            b.Velocity.Should().Be(vb);
        }

        [TestMethod]
        public void Resolve_LightHitsHeavy_LightBouncesHeavyBarelyMoves()
        {
            var light = new RigidBody { Mass = 100, Velocity = new Vector3d(200, 0, 0) };
            var heavy = new RigidBody { Mass = 5_000_000, Velocity = Vector3d.Zero };

            CollisionResolver.Resolve(light, heavy, new Vector3d(1, 0, 0), restitution: 0.5);

            light.Velocity.X.Should().BeLessThan(0);          // flung back
            heavy.Velocity.X.Should().BeGreaterThan(0);
            heavy.Velocity.X.Should().BeLessThan(0.02);       // barely twitches
        }

        [TestMethod]
        public void ImpactEnergy_ScalesWithClosingSpeedSquared()
        {
            var a = new RigidBody { Mass = 1000, Velocity = new Vector3d(10, 0, 0) };
            var b = new RigidBody { Mass = 1000, Velocity = Vector3d.Zero };
            double e1 = CollisionResolver.ImpactEnergy(a, b);

            a.Velocity = new Vector3d(20, 0, 0); // double the closing speed -> 4x the energy
            double e2 = CollisionResolver.ImpactEnergy(a, b);

            (e2 / e1).Should().BeApproximately(4, 1e-6);
        }
    }
}
