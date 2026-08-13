namespace RP.Game.Tests.Physics
{
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Physics;
    using RP.Math;

    /// <summary>The geometric collision queries: sphere-vs-AABB across all three contact regimes, and the
    /// swept segment-vs-sphere test with its surface entry point.</summary>
    [TestClass]
    public sealed class CollisionQueriesTests
    {
        private static readonly Vector3d Lo = new(-1, -1, -1);
        private static readonly Vector3d Hi = new(1, 1, 1);

        [TestMethod]
        public void SphereVsAabb_MissesWhenClear()
        {
            CollisionResolver.SphereVsAabb(new Vector3d(3, 0, 0), 0.5, Lo, Hi, out _, out _)
                .Should().BeFalse();
        }

        [TestMethod]
        public void SphereVsAabb_GrazingAFace_PushesOutAlongThatFace()
        {
            bool hit = CollisionResolver.SphereVsAabb(new Vector3d(1.3, 0, 0), 0.5, Lo, Hi,
                out Vector3d normal, out double pen);

            hit.Should().BeTrue();
            normal.Should().Be(new Vector3d(1, 0, 0));
            pen.Should().BeApproximately(0.2, 1e-9); // 0.5 radius − 0.3 gap
        }

        [TestMethod]
        public void SphereVsAabb_AtACorner_PushesAlongTheDiagonal()
        {
            bool hit = CollisionResolver.SphereVsAabb(new Vector3d(1.2, 1.2, 1.2), 0.5, Lo, Hi,
                out Vector3d normal, out double pen);

            hit.Should().BeTrue();
            normal.X.Should().BeApproximately(normal.Y, 1e-9);
            normal.Y.Should().BeApproximately(normal.Z, 1e-9);
            normal.Magnitude.Should().BeApproximately(1.0, 1e-9);
            pen.Should().BeGreaterThan(0);
        }

        [TestMethod]
        public void SphereVsAabb_CentreInside_ExitsThroughTheNearestFace()
        {
            // Nearest face is +X (0.2 away); a naive clamped-point test would report no contact here.
            bool hit = CollisionResolver.SphereVsAabb(new Vector3d(0.8, 0, 0), 0.5, Lo, Hi,
                out Vector3d normal, out double pen);

            hit.Should().BeTrue();
            normal.Should().Be(new Vector3d(1, 0, 0));
            pen.Should().BeApproximately(0.7, 1e-9); // 0.2 to the face + 0.5 radius
        }

        [TestMethod]
        public void SegmentSphere_ReportsTheSurfaceEntryPoint_EvenDeadCentre()
        {
            bool hit = CollisionResolver.SegmentIntersectsSphere(
                new Vector3d(-10, 0, 0), new Vector3d(10, 0, 0), Vector3d.Zero, 2.0,
                out double t, out Vector3d entry);

            hit.Should().BeTrue();
            t.Should().BeApproximately(0.5, 1e-9);                      // closest approach at the centre
            entry.Should().Be(new Vector3d(-2, 0, 0));                  // but the IMPACT is on the surface
        }

        [TestMethod]
        public void SegmentSphere_FastSegment_CannotTunnel()
        {
            // A tiny sphere crossed by a huge step: the swept test still connects.
            CollisionResolver.SegmentIntersectsSphere(
                new Vector3d(0, 0.5, -50_000), new Vector3d(0, 0.5, 50_000), Vector3d.Zero, 1.0,
                out _, out _).Should().BeTrue();
        }

        [TestMethod]
        public void SegmentSphere_StartingInside_ReportsTheStart()
        {
            CollisionResolver.SegmentIntersectsSphere(
                new Vector3d(0.5, 0, 0), new Vector3d(10, 0, 0), Vector3d.Zero, 2.0,
                out _, out Vector3d entry).Should().BeTrue();
            entry.Magnitude.Should().BeApproximately(2.0, 1e-9); // projected out to the surface
        }

        [TestMethod]
        public void SegmentSphere_CleanMiss()
        {
            CollisionResolver.SegmentIntersectsSphere(
                new Vector3d(-10, 5, 0), new Vector3d(10, 5, 0), Vector3d.Zero, 2.0,
                out _, out _).Should().BeFalse();
        }
    }
}
