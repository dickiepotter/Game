namespace RP.Game.Tests.Voxels
{
    using System.Collections.Generic;
    using System.Linq;
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Voxels;

    /// <summary>
    /// Blocks that have lost their support, falling over time rather than arriving instantly.
    /// </summary>
    [TestClass]
    public sealed class VoxelFallingBlockTests
    {
        private static readonly PhysicsPalette Palette = new PhysicsPalette();

        private const double Dt = 1.0 / 60.0;

        private static VoxelVolume WorldWithFloor()
        {
            var world = new VoxelVolume(Palette);
            world.FillBox(new BlockPos(-16, -4, -16), new BlockPos(16, 0, 16), PhysicsPalette.Stone);
            return world;
        }

        private static VoxelFallingBlocks NewSystem(VoxelVolume world) => new VoxelFallingBlocks(world);

        // ---- It takes time -----------------------------------------------------------------------------

        [TestMethod]
        public void ABlockTakesTimeToFallInsteadOfArrivingInstantly()
        {
            // The whole point. A solver that puts every unsupported block in its final place in one frame
            // is correct and unreadable: the player sees a different cliff and no way to tell what
            // happened or whether it is still happening.
            VoxelVolume world = WorldWithFloor();
            world.SetBlock(new BlockPos(0, 8, 0), PhysicsPalette.Sand);

            VoxelFallingBlocks falling = NewSystem(world);
            falling.Release(new BlockPos(0, 8, 0), Palette);

            falling.Tick(Dt);

            falling.Count.Should().Be(1, "still in the air after one frame");
            world.GetBlock(new BlockPos(0, 1, 0)).Should().Be(PhysicsPalette.Air, "and not yet on the floor");
        }

        [TestMethod]
        public void ADropOfSevenBlocksTakesAboutASecond()
        {
            // Slow enough to read, fast enough to matter. If a two-storey collapse were over in three
            // frames there would be nothing to watch, and if it took five seconds it would be a cutscene.
            VoxelVolume world = WorldWithFloor();
            world.SetBlock(new BlockPos(0, 8, 0), PhysicsPalette.Sand);

            VoxelFallingBlocks falling = NewSystem(world);
            falling.Release(new BlockPos(0, 8, 0), Palette);

            int ticks = falling.RunToSettle(Dt);

            (ticks * Dt).Should().BeInRange(0.7, 2.0);
        }

        [TestMethod]
        public void ItLandsOnTopOfWhateverStoppedIt()
        {
            VoxelVolume world = WorldWithFloor();
            world.SetBlock(new BlockPos(0, 8, 0), PhysicsPalette.Sand);

            VoxelFallingBlocks falling = NewSystem(world);
            falling.Release(new BlockPos(0, 8, 0), Palette);
            falling.RunToSettle(Dt);

            world.GetBlock(new BlockPos(0, 1, 0)).Should().Be(PhysicsPalette.Sand);
            world.GetBlock(new BlockPos(0, 8, 0)).Should().Be(PhysicsPalette.Air, "it left where it started");
        }

        [TestMethod]
        public void ItLeavesTheWorldTheMomentItIsReleased()
        {
            // Not when it lands. A world that is briefly both holding a block up and dropping it can
            // support a structure on debris that is already in mid-air.
            VoxelVolume world = WorldWithFloor();
            world.SetBlock(new BlockPos(0, 8, 0), PhysicsPalette.Sand);

            VoxelFallingBlocks falling = NewSystem(world);
            falling.Release(new BlockPos(0, 8, 0), Palette);

            world.GetBlock(new BlockPos(0, 8, 0)).Should().Be(PhysicsPalette.Air);
        }

        [TestMethod]
        public void AFastBlockCannotPassThroughAThinFloor()
        {
            // At terminal speed a block covers a fifth of a block per frame, but a block released from a
            // great height with a high gravity would not, and testing the cell it is leaving rather than
            // every cell it crossed is how a boulder ends up under the bedrock.
            VoxelVolume world = WorldWithFloor();
            world.SetBlock(new BlockPos(0, 30, 0), PhysicsPalette.Sand);
            world.SetBlock(new BlockPos(0, 10, 0), PhysicsPalette.Stone);   // a one-block-thick shelf

            var falling = new VoxelFallingBlocks(world) { Gravity = 200.0, TerminalSpeed = 400.0 };
            falling.Release(new BlockPos(0, 30, 0), Palette);
            falling.RunToSettle(Dt);

            world.GetBlock(new BlockPos(0, 11, 0)).Should().Be(PhysicsPalette.Sand, "it should rest on the shelf");
        }

        // ---- Collapses cascade -------------------------------------------------------------------------

        [TestMethod]
        public void ATowerComesApartFromTheBottomUpward()
        {
            // A column that dissolves all at once reads as a rendering fault. Releasing the bottom first
            // and letting what was on top follow is what makes it read as a collapse.
            VoxelVolume world = WorldWithFloor();
            var column = new List<BlockPos>();
            for (int y = 4; y <= 12; y++)
            {
                world.SetBlock(new BlockPos(0, y, 0), PhysicsPalette.Stone);
                column.Add(new BlockPos(0, y, 0));
            }

            VoxelFallingBlocks falling = NewSystem(world);
            falling.Release(column, Palette);

            falling.Count.Should().Be(9);
            falling.Falling.Count(f => f.Waiting).Should().Be(8, "only the lowest lets go immediately");

            FallingBlock lowest = falling.Falling.OrderBy(f => f.Origin.Y).First();
            FallingBlock highest = falling.Falling.OrderByDescending(f => f.Origin.Y).First();
            highest.Delay.Should().BeGreaterThan(lowest.Delay);
        }

        [TestMethod]
        public void AColumnEndsUpStackedInTheOrderItFell()
        {
            VoxelVolume world = WorldWithFloor();
            var column = new List<BlockPos>();
            for (int y = 6; y <= 10; y++)
            {
                world.SetBlock(new BlockPos(0, y, 0), PhysicsPalette.Stone);
                column.Add(new BlockPos(0, y, 0));
            }

            VoxelFallingBlocks falling = NewSystem(world);
            falling.Release(column, Palette);
            falling.RunToSettle(Dt);

            for (int y = 1; y <= 5; y++)
            {
                world.GetBlock(new BlockPos(0, y, 0)).Should().Be(PhysicsPalette.Stone, "y = {0} should be filled", y);
            }

            world.GetBlock(new BlockPos(0, 6, 0)).Should().Be(PhysicsPalette.Air, "and nothing above the heap");
        }

        [TestMethod]
        public void NoBlockIsMadeOrLostInACollapse()
        {
            VoxelVolume world = WorldWithFloor();
            var released = new List<BlockPos>();
            for (int y = 3; y <= 20; y++)
            {
                world.SetBlock(new BlockPos(0, y, 0), PhysicsPalette.Stone);
                released.Add(new BlockPos(0, y, 0));
            }

            VoxelFallingBlocks falling = NewSystem(world);
            int count = falling.Release(released, Palette);
            falling.RunToSettle(Dt);

            int standing = 0;
            for (int y = 1; y <= 40; y++)
            {
                if (world.GetBlock(new BlockPos(0, y, 0)) == PhysicsPalette.Stone) standing++;
            }

            standing.Should().Be(count);
        }

        // ---- Landing in an occupied cell -----------------------------------------------------------------

        [TestMethod]
        public void ABlockThatCannotLandIsReportedRatherThanOverwriting()
        {
            // Something else got there first, or the player filled the hole in while it was in the air.
            // Placing anyway would silently delete whatever was there.
            VoxelVolume world = WorldWithFloor();
            world.SetBlock(new BlockPos(0, 8, 0), PhysicsPalette.Sand);

            VoxelFallingBlocks falling = NewSystem(world);
            falling.Release(new BlockPos(0, 8, 0), Palette);

            for (int i = 0; i < 20; i++) falling.Tick(Dt);

            // Fill the column up through the cell the block is passing through, so its resting place is
            // taken by the time it gets there. This is the player walling a shaft in while debris is
            // still coming down it.
            int occupied = (int)System.Math.Floor(falling.Falling[0].Position.Y);
            world.FillBox(new BlockPos(0, 1, 0), new BlockPos(0, occupied, 0), PhysicsPalette.Wood);

            var landings = new List<LandedBlock>();
            for (int i = 0; i < 600 && !falling.IsSettled; i++)
            {
                falling.Tick(Dt);
                landings.AddRange(falling.Landings);
            }

            LandedBlock landing = landings.Should().ContainSingle().Subject;
            landing.Placed.Should().BeFalse("there was nowhere for it to go");
            landing.Block.Should().Be(PhysicsPalette.Sand, "so the game can hand it over as an item");
            world.GetBlock(new BlockPos(0, 2, 0)).Should().Be(PhysicsPalette.Wood, "and nothing was overwritten");
            landing.To.Y.Should().Be(occupied, "reported at the cell it could not take");
        }

        [TestMethod]
        public void ABlockDisplacesFluidRatherThanRestingOnIt()
        {
            // A boulder rolled into a pond sinks. Treating water as a floor leaves a suspiciously dry hole
            // in the middle of a lake.
            var world = new VoxelVolume(Palette);
            world.FillBox(new BlockPos(-8, -1, -8), new BlockPos(8, 0, 8), PhysicsPalette.Stone);
            world.FillBox(new BlockPos(-8, 1, -8), new BlockPos(8, 5, 8), PhysicsPalette.WaterSource);
            world.SetBlock(new BlockPos(0, 10, 0), PhysicsPalette.Sand);

            VoxelFallingBlocks falling = NewSystem(world);
            falling.Release(new BlockPos(0, 10, 0), Palette);
            falling.RunToSettle(Dt);

            world.GetBlock(new BlockPos(0, 1, 0)).Should().Be(PhysicsPalette.Sand, "down through the water to the bed");
        }

        // ---- Budgets ------------------------------------------------------------------------------------

        [TestMethod]
        public void TheNumberInTheAirIsCapped()
        {
            // Undermining a mountain must not stall the game, and leaving the excess standing is a better
            // failure than dropping every block in a cliff face at once.
            VoxelVolume world = WorldWithFloor();
            var many = new List<BlockPos>();
            for (int y = 1; y <= 300; y++)
            {
                world.SetBlock(new BlockPos(0, y, 0), PhysicsPalette.Stone);
                many.Add(new BlockPos(0, y, 0));
            }

            var falling = new VoxelFallingBlocks(world) { MaxFalling = 16 };
            falling.Release(many, Palette);

            falling.Count.Should().Be(16);
            world.GetBlock(new BlockPos(0, 100, 0)).Should().Be(PhysicsPalette.Stone, "the rest is still standing");
        }

        [TestMethod]
        public void ReleasingAirDoesNothing()
        {
            VoxelVolume world = WorldWithFloor();
            VoxelFallingBlocks falling = NewSystem(world);

            falling.Release(new BlockPos(0, 8, 0), Palette).Should().BeFalse();
            falling.Count.Should().Be(0);
        }
    }
}
