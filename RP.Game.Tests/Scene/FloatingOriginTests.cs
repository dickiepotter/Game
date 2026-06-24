namespace RP.Game.Tests.Scene
{
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Scene;
    using RP.Math;

    [TestClass]
    public sealed class FloatingOriginTests
    {
        [TestMethod]
        public void WithinThreshold_DoesNotRebase()
        {
            var origin = new FloatingOrigin(rebaseThreshold: 4096);
            origin.MaybeRebase(new Vector3d(1000, 0, 0)).Should().BeFalse();
            origin.Origin.Should().Be(Vector3d.Origin);
        }

        [TestMethod]
        public void PastThreshold_RebasesToFocus()
        {
            var origin = new FloatingOrigin(rebaseThreshold: 4096);
            var focus = new Vector3d(5000, 0, 0);

            origin.MaybeRebase(focus).Should().BeTrue();
            origin.Origin.Should().Be(focus);
        }

        [TestMethod]
        public void ToRenderSpace_IsRelativeToOrigin()
        {
            var origin = new FloatingOrigin();
            origin.MaybeRebase(new Vector3d(10000, 0, 0)); // origin now at 10000

            Vector3 local = origin.ToRenderSpace(new Vector3d(10005, 0, 0));
            local.X.Should().BeApproximately(5f, 1e-3f);
        }

        [TestMethod]
        public void Rebase_PreservesRelativePositionsBetweenObjects()
        {
            var origin = new FloatingOrigin(rebaseThreshold: 100);
            var a = new Vector3d(20000, 0, 0);
            var b = new Vector3d(20003, 4, 0);

            // The vector between two objects in render space must be unchanged by a rebase — that is the
            // whole point: the world shifts as one piece.
            origin.MaybeRebase(a);
            Vector3 localABefore = origin.ToRenderSpace(a);
            Vector3 localBBefore = origin.ToRenderSpace(b);
            Vector3 deltaBefore = localBBefore - localABefore;

            origin.MaybeRebase(new Vector3d(40000, 0, 0)); // force a different origin
            Vector3 deltaAfter = origin.ToRenderSpace(b) - origin.ToRenderSpace(a);

            deltaAfter.ApproximatelyEquals(deltaBefore, 1e-2f).Should().BeTrue();
        }

        [TestMethod]
        public void RenderSpace_RoundTrips()
        {
            var origin = new FloatingOrigin();
            origin.MaybeRebase(new Vector3d(8192, -3000, 512));

            var truth = new Vector3d(8200, -2995, 520);
            Vector3 local = origin.ToRenderSpace(truth);
            Vector3d back = origin.FromRenderSpace(local);

            back.Distance(truth).Should().BeLessThan(1e-2);
        }
    }
}
