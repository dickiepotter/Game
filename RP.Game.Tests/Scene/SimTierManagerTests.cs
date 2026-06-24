namespace RP.Game.Tests.Scene
{
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Scene;

    [TestClass]
    public sealed class SimTierManagerTests
    {
        [TestMethod]
        public void Classify_PutsEachDistanceInTheRightTier()
        {
            var mgr = new SimTierManager(nearRadius: 1000, midRadius: 5000);
            mgr.Classify(500).Should().Be(SimTier.Near);
            mgr.Classify(3000).Should().Be(SimTier.Mid);
            mgr.Classify(9000).Should().Be(SimTier.Far);
        }

        [TestMethod]
        public void Reclassify_StaysInTierWithinTheHysteresisBand()
        {
            var mgr = new SimTierManager(nearRadius: 1000, midRadius: 5000, hysteresis: 100);

            // Currently Near, just past the radius but within the band -> stays Near.
            mgr.Reclassify(SimTier.Near, 1050).Should().Be(SimTier.Near);
            // Clearly past the band -> demotes to Mid.
            mgr.Reclassify(SimTier.Near, 1200).Should().Be(SimTier.Mid);
        }

        [TestMethod]
        public void Reclassify_PromotionRequiresClearlyCrossingInward()
        {
            var mgr = new SimTierManager(1000, 5000, hysteresis: 100);
            // Currently Mid, just inside the near radius but within the band -> stays Mid (not promoted yet).
            mgr.Reclassify(SimTier.Mid, 950).Should().Be(SimTier.Mid);
            // Clearly inside -> promotes to Near.
            mgr.Reclassify(SimTier.Mid, 850).Should().Be(SimTier.Near);
        }

        [TestMethod]
        public void Reclassify_OscillatingOnABoundaryDoesNotThrash()
        {
            var mgr = new SimTierManager(1000, 5000, hysteresis: 100);
            SimTier tier = SimTier.Near;

            // Distance wobbles around the 1000 boundary inside the band: tier must stay put.
            foreach (double d in new[] { 1010.0, 990.0, 1040.0, 980.0, 1050.0 })
            {
                tier = mgr.Reclassify(tier, d);
                tier.Should().Be(SimTier.Near);
            }
        }
    }
}
