namespace RP.Game.Tests.Scene
{
    using System;
    using System.Linq;
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Scene;
    using RP.Math;

    /// <summary>
    /// Chunk streaming (build brief S13): a focus pages in nearby chunks and drops far ones, with a hysteresis
    /// band so a player loitering on a boundary does not thrash the same chunk in and out. The streamer is
    /// pure bookkeeping — these tests check what it decides to load/keep/drop.
    /// </summary>
    [TestClass]
    public sealed class ChunkStreamerTests
    {
        [TestMethod]
        public void CellOf_FloorsToTheContainingChunk()
        {
            var s = new ChunkStreamer(chunkSize: 100, loadRadius: 150, unloadRadius: 250);
            s.CellOf(new Vector3d(0, 0, 0)).Should().Be(new ChunkId(0, 0, 0));
            s.CellOf(new Vector3d(150, 250, 50)).Should().Be(new ChunkId(1, 2, 0));
            s.CellOf(new Vector3d(-1, 0, 0)).Should().Be(new ChunkId(-1, 0, 0)); // floor, not truncate
        }

        [TestMethod]
        public void FirstUpdate_LoadsTheNeighbourhood_AndNothingBeyondLoadRadius()
        {
            var s = new ChunkStreamer(chunkSize: 100, loadRadius: 150, unloadRadius: 250);
            ChunkResidencyChange change = s.Update(new Vector3d(50, 50, 50)); // centre of chunk (0,0,0)

            change.Loaded.Should().Contain(new ChunkId(0, 0, 0));
            change.Unloaded.Should().BeEmpty();

            // Every resident chunk's centre is genuinely within the load radius.
            foreach (ChunkId id in s.Resident)
            {
                (s.CentreOf(id) - new Vector3d(50, 50, 50)).Magnitude.Should().BeLessThanOrEqualTo(150);
            }
        }

        [TestMethod]
        public void ReUpdate_AtSamePlace_LoadsNothingNew()
        {
            var s = new ChunkStreamer(chunkSize: 100, loadRadius: 150, unloadRadius: 250);
            var focus = new Vector3d(50, 50, 50);
            s.Update(focus);

            ChunkResidencyChange again = s.Update(focus);
            again.Loaded.Should().BeEmpty();
            again.Unloaded.Should().BeEmpty();
        }

        [TestMethod]
        public void MovingAway_DropsChunksOnlyPastTheUnloadRadius()
        {
            var s = new ChunkStreamer(chunkSize: 100, loadRadius: 150, unloadRadius: 400);
            s.Update(new Vector3d(50, 50, 50));
            int initial = s.Resident.Count;

            // Step far enough that some original chunks pass the unload radius.
            ChunkResidencyChange change = s.Update(new Vector3d(650, 50, 50));

            change.Loaded.Should().NotBeEmpty();   // new neighbourhood paged in
            change.Unloaded.Should().NotBeEmpty(); // old, now-distant chunks dropped
            s.Resident.Count.Should().BeGreaterThan(0);

            // Nothing resident is beyond the unload radius after the update.
            foreach (ChunkId id in s.Resident)
            {
                (s.CentreOf(id) - new Vector3d(650, 50, 50)).Magnitude.Should().BeLessThanOrEqualTo(400 + 1e-6);
            }
        }

        [TestMethod]
        public void Hysteresis_KeepsBoundaryChunksResidentInsteadOfThrashing()
        {
            // A chunk that sits between the load and unload radii after a small nudge must stay resident
            // (it was loaded at the start) rather than flicker out.
            var s = new ChunkStreamer(chunkSize: 100, loadRadius: 150, unloadRadius: 350);
            s.Update(new Vector3d(50, 50, 50));

            // Nudge the focus by less than (unload - load); previously resident chunks within unloadRadius stay.
            ChunkResidencyChange change = s.Update(new Vector3d(180, 50, 50));
            change.Unloaded.Should().BeEmpty(); // still inside unload radius -> sticky, no thrash
        }

        [TestMethod]
        public void Constructor_RejectsUnloadSmallerThanLoad()
        {
            Action bad = () => new ChunkStreamer(chunkSize: 100, loadRadius: 200, unloadRadius: 100);
            bad.Should().Throw<ArgumentException>();
        }
    }
}
