namespace RP.Game.Tests.Scene
{
    using System.Collections.Generic;
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Scene;

    /// <summary>
    /// The reuse pool (build brief S2/S5): objects are handed back out instead of reallocated, so churning
    /// many short-lived objects never allocates more than the peak number live at once.
    /// </summary>
    [TestClass]
    public sealed class ObjectPoolTests
    {
        private sealed class Thing { public int Value; }

        [TestMethod]
        public void Prewarm_AllocatesUpFront()
        {
            var pool = new ObjectPool<Thing>(() => new Thing(), prewarm: 4);
            pool.FreeCount.Should().Be(4);
            pool.CreatedCount.Should().Be(4);
        }

        [TestMethod]
        public void ReturnedObjects_AreReused_WithoutNewAllocations()
        {
            var pool = new ObjectPool<Thing>(() => new Thing());
            Thing a = pool.Get();
            pool.CreatedCount.Should().Be(1);

            pool.Return(a);
            Thing b = pool.Get();
            b.Should().BeSameAs(a);          // same instance handed back
            pool.CreatedCount.Should().Be(1); // no second allocation
        }

        [TestMethod]
        public void ChurningManyObjects_AllocatesOnlyThePeakLiveCount()
        {
            var pool = new ObjectPool<Thing>(() => new Thing());

            // Repeatedly hold at most 3 live at a time, over many cycles.
            for (int cycle = 0; cycle < 50; cycle++)
            {
                var live = new List<Thing> { pool.Get(), pool.Get(), pool.Get() };
                foreach (Thing t in live) pool.Return(t);
            }

            pool.CreatedCount.Should().Be(3); // never more than the high-water mark
        }

        [TestMethod]
        public void OnReturn_ResetsTheObject()
        {
            var pool = new ObjectPool<Thing>(() => new Thing(), onReturn: t => t.Value = 0);
            Thing a = pool.Get();
            a.Value = 99;

            pool.Return(a);     // reset hook zeroes it
            a.Value.Should().Be(0);
        }
    }
}
