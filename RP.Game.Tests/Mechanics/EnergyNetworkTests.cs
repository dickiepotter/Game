namespace RP.Game.Tests.Mechanics
{
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Mechanics.Power;

    /// <summary>
    /// The power grid: connectivity, fair sharing under shortage, and storage behaving like a buffer.
    /// </summary>
    [TestClass]
    public sealed class EnergyNetworkTests
    {
        private static EnergyNode Source(EnergyNetwork grid, long id, double rate)
        {
            EnergyNode node = grid.Attach(id);
            node.Role = EnergyRole.Source;
            node.SupplyRate = rate;
            return node;
        }

        private static EnergyNode Load(EnergyNetwork grid, long id, double rate)
        {
            EnergyNode node = grid.Attach(id);
            node.Role = EnergyRole.Load;
            node.DemandRate = rate;
            return node;
        }

        private static EnergyNode Store(EnergyNetwork grid, long id, double capacity, double stored = 0)
        {
            EnergyNode node = grid.Attach(id);
            node.Role = EnergyRole.Store;
            node.Capacity = capacity;
            node.Stored = stored;
            return node;
        }

        [TestMethod]
        public void UnconnectedNodesFormSeparateGrids()
        {
            var grid = new EnergyNetwork();
            Source(grid, 1, 100);
            Load(grid, 2, 50);

            grid.NetworkCount.Should().Be(2, "nothing links them yet");

            grid.Link(1, 2);
            grid.NetworkCount.Should().Be(1);
        }

        [TestMethod]
        public void PowerDoesNotCrossBetweenSeparateGrids()
        {
            // The failure that would make wiring meaningless: a machine running off a generator it is not
            // connected to.
            var grid = new EnergyNetwork();
            Source(grid, 1, 100);
            EnergyNode isolated = Load(grid, 2, 50);

            grid.Tick(1.0);

            isolated.Received.Should().Be(0);
            isolated.Satisfied.Should().BeFalse();
        }

        [TestMethod]
        public void AConnectedLoadIsServedInFull()
        {
            var grid = new EnergyNetwork();
            Source(grid, 1, 100);
            EnergyNode load = Load(grid, 2, 40);
            grid.Link(1, 2);

            grid.Tick(1.0);

            load.Received.Should().BeApproximately(40, 1e-9);
            load.Satisfied.Should().BeTrue();
        }

        [TestMethod]
        public void ConduitsJoinNodesThatDoNotTouch()
        {
            var grid = new EnergyNetwork();
            Source(grid, 1, 100);
            grid.Attach(2).Role = EnergyRole.Conduit;
            grid.Attach(3).Role = EnergyRole.Conduit;
            EnergyNode load = Load(grid, 4, 30);

            grid.Link(1, 2);
            grid.Link(2, 3);
            grid.Link(3, 4);

            grid.Tick(1.0);

            grid.NetworkCount.Should().Be(1);
            load.Received.Should().BeApproximately(30, 1e-9);
        }

        [TestMethod]
        public void ShortageIsSharedEquallyRatherThanFirstComeFirstServed()
        {
            // The behaviour players hate most in a factory game is a base that works until it is one
            // generator short, at which point some machines stop entirely and which ones depends on
            // ordering nobody can see. Everything should slow together instead.
            var grid = new EnergyNetwork();
            Source(grid, 1, 60);
            EnergyNode a = Load(grid, 2, 40);
            EnergyNode b = Load(grid, 3, 40);
            EnergyNode c = Load(grid, 4, 40);

            grid.Link(1, 2);
            grid.Link(1, 3);
            grid.Link(1, 4);

            grid.Tick(1.0);

            // 60 supplied against 120 demanded: everyone gets half.
            a.Received.Should().BeApproximately(20, 1e-9);
            b.Received.Should().BeApproximately(20, 1e-9);
            c.Received.Should().BeApproximately(20, 1e-9);

            a.Satisfied.Should().BeFalse();
            b.Satisfied.Should().BeFalse();
            c.Satisfied.Should().BeFalse();
        }

        [TestMethod]
        public void SurplusChargesStorage()
        {
            var grid = new EnergyNetwork();
            Source(grid, 1, 100);
            Load(grid, 2, 30);
            EnergyNode battery = Store(grid, 3, capacity: 500);

            grid.Link(1, 2);
            grid.Link(1, 3);

            grid.Tick(1.0);

            battery.Stored.Should().BeApproximately(70, 1e-9, "the surplus should have gone into the battery");
        }

        [TestMethod]
        public void StorageCoversAShortfallBeforeAnyLoadIsStarved()
        {
            // The entire reason to build a bank of accumulators: it smooths over a generator that burns in
            // bursts. Serving loads first would leave a full battery beside a stopped machine.
            var grid = new EnergyNetwork();
            Source(grid, 1, 10);
            EnergyNode load = Load(grid, 2, 50);
            EnergyNode battery = Store(grid, 3, capacity: 500, stored: 200);

            grid.Link(1, 2);
            grid.Link(1, 3);

            grid.Tick(1.0);

            load.Received.Should().BeApproximately(50, 1e-9, "the battery should have made up the difference");
            load.Satisfied.Should().BeTrue();
            battery.Stored.Should().BeApproximately(160, 1e-9);
        }

        [TestMethod]
        public void StorageWillNotOverfill()
        {
            var grid = new EnergyNetwork();
            Source(grid, 1, 1000);
            EnergyNode battery = Store(grid, 2, capacity: 100, stored: 90);
            grid.Link(1, 2);

            grid.Tick(1.0);

            battery.Stored.Should().BeApproximately(100, 1e-9);
        }

        [TestMethod]
        public void RatesArePerSecondSoTheTimestepMatters()
        {
            var grid = new EnergyNetwork();
            Source(grid, 1, 100);
            EnergyNode load = Load(grid, 2, 60);
            grid.Link(1, 2);

            grid.Tick(0.5);

            load.Received.Should().BeApproximately(30, 1e-9, "half a second of a 60-per-second demand");
        }

        [TestMethod]
        public void DetachingANodeSplitsTheGrid()
        {
            var grid = new EnergyNetwork();
            Source(grid, 1, 100);
            grid.Attach(2).Role = EnergyRole.Conduit;
            EnergyNode load = Load(grid, 3, 30);

            grid.Link(1, 2);
            grid.Link(2, 3);
            grid.Tick(1.0);
            load.Received.Should().BeApproximately(30, 1e-9);

            // Cut the cable in the middle.
            grid.Detach(2);
            grid.Tick(1.0);

            grid.NetworkCount.Should().Be(2);
            load.Received.Should().Be(0, "the link is gone");
        }

        [TestMethod]
        public void TotalsAreReportedForDiagnostics()
        {
            var grid = new EnergyNetwork();
            Source(grid, 1, 80);
            Load(grid, 2, 30);
            Load(grid, 3, 30);
            grid.Link(1, 2);
            grid.Link(1, 3);

            grid.Tick(1.0);

            grid.LastSupplied.Should().BeApproximately(80, 1e-9);
            grid.LastDemanded.Should().BeApproximately(60, 1e-9);
        }

        [TestMethod]
        public void ALargeGridSolvesQuickly()
        {
            // A real base is thousands of nodes and this runs every tick, so it has to stay linear.
            var grid = new EnergyNetwork();
            const int Count = 5000;

            Source(grid, 0, 1_000_000);
            for (long i = 1; i < Count; i++)
            {
                Load(grid, i, 10);
                grid.Link(i - 1, i);
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int t = 0; t < 60; t++) grid.Tick(1.0 / 60.0);
            watch.Stop();

            grid.NetworkCount.Should().Be(1);
            watch.ElapsedMilliseconds.Should().BeLessThan(500, "sixty ticks over five thousand nodes");
        }
    }
}
