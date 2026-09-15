namespace RP.Game.Tests.Voxels
{
    using System.Collections.Generic;
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Voxels;
    using RP.Math;

    /// <summary>
    /// Grid traversal: the cast behind mining, placing and looking at things. The tests concentrate on the
    /// asymmetries, because a raycaster that is right in the +X direction and wrong in -X passes any casual
    /// look at it and then mis-targets every block the player faces west.
    /// </summary>
    [TestClass]
    public sealed class VoxelRaycastTests
    {
        private static VoxelVolume NewWorld() => new VoxelVolume(new TestPalette());

        [TestMethod]
        public void Cast_AlongPositiveX_HitsTheNearBlockThroughItsNegativeXFace()
        {
            VoxelVolume world = NewWorld();
            world.SetBlock(new BlockPos(5, 0, 0), 1);

            VoxelHit hit = VoxelRaycast.Cast(world, new Vector3d(0.5, 0.5, 0.5), new Vector3d(1, 0, 0), 20);

            hit.Hit.Should().BeTrue();
            hit.Block.Should().Be(new BlockPos(5, 0, 0));
            hit.Face.Should().Be(BlockFace.NegativeX);
            hit.Value.Should().Be(1);
            hit.Distance.Should().BeApproximately(4.5, 1e-9);
            hit.PlacementPosition.Should().Be(new BlockPos(4, 0, 0));
        }

        [TestMethod]
        public void Cast_AlongNegativeX_HitsThroughThePositiveXFaceAtTheRightDistance()
        {
            // The FirstCrossing asymmetry. Travelling forward, the next grid plane is the block's far edge;
            // travelling backward it is the block's own edge. One formula for both puts every backward ray a
            // whole block out of step -- and the symptom is only ever noticed facing west or down.
            VoxelVolume world = NewWorld();
            world.SetBlock(new BlockPos(-5, 0, 0), 1);

            VoxelHit hit = VoxelRaycast.Cast(world, new Vector3d(0.5, 0.5, 0.5), new Vector3d(-1, 0, 0), 20);

            hit.Hit.Should().BeTrue();
            hit.Block.Should().Be(new BlockPos(-5, 0, 0));
            hit.Face.Should().Be(BlockFace.PositiveX);
            hit.Distance.Should().BeApproximately(4.5, 1e-9);
            hit.PlacementPosition.Should().Be(new BlockPos(-4, 0, 0));
        }

        [TestMethod]
        public void Cast_AlongEveryAxisAndSign_ReportsTheFaceItEnteredThrough()
        {
            VoxelVolume world = NewWorld();

            for (int f = 0; f < VoxelFaces.Count; f++)
            {
                var face = (BlockFace)f;
                BlockPos step = VoxelFaces.Offsets[f];

                // Put a block five along the direction we will cast, then cast that way. The face entered
                // must be the one facing back toward us -- the opposite of the direction of travel.
                var target = new BlockPos(step.X * 5, step.Y * 5, step.Z * 5);
                VoxelVolume w = NewWorld();
                w.SetBlock(target, 1);

                var direction = new Vector3d(step.X, step.Y, step.Z);
                VoxelHit hit = VoxelRaycast.Cast(w, new Vector3d(0.5, 0.5, 0.5), direction, 20);

                hit.Hit.Should().BeTrue("casting toward {0}", face);
                hit.Block.Should().Be(target);
                hit.Face.Should().Be(VoxelFaces.Opposite(face), "casting toward {0}", face);
                hit.PlacementPosition.Should().Be(target.Neighbour(VoxelFaces.Opposite(face)));
            }

            world.Should().NotBeNull();
        }

        [TestMethod]
        public void Cast_StopsAtTheFirstBlockNotTheNearestOne()
        {
            VoxelVolume world = NewWorld();
            world.SetBlock(new BlockPos(3, 0, 0), 1);
            world.SetBlock(new BlockPos(7, 0, 0), 2);

            VoxelHit hit = VoxelRaycast.Cast(world, new Vector3d(0.5, 0.5, 0.5), new Vector3d(1, 0, 0), 20);
            hit.Block.Should().Be(new BlockPos(3, 0, 0));
            hit.Value.Should().Be(1);
        }

        [TestMethod]
        public void Cast_MissesWhenTheBlockIsBeyondRange()
        {
            VoxelVolume world = NewWorld();
            world.SetBlock(new BlockPos(20, 0, 0), 1);

            VoxelRaycast.Cast(world, new Vector3d(0.5, 0.5, 0.5), new Vector3d(1, 0, 0), 5).Hit.Should().BeFalse();
            VoxelRaycast.Cast(world, new Vector3d(0.5, 0.5, 0.5), new Vector3d(1, 0, 0), 25).Hit.Should().BeTrue();
        }

        [TestMethod]
        public void Cast_FromInsideABlock_ReportsThatBlockImmediately()
        {
            VoxelVolume world = NewWorld();
            world.SetBlock(new BlockPos(0, 0, 0), 1);

            VoxelHit hit = VoxelRaycast.Cast(world, new Vector3d(0.5, 0.5, 0.5), new Vector3d(1, 0, 0), 10);

            hit.Hit.Should().BeTrue();
            hit.Block.Should().Be(new BlockPos(0, 0, 0));
            hit.Distance.Should().Be(0.0);
        }

        [TestMethod]
        public void Cast_OnADiagonal_VisitsAContiguousPathOfBlocks()
        {
            // Every block a ray genuinely passes through must be visited, exactly once, in order -- that is
            // the whole point of grid traversal over stepping and sampling. Consecutive visits must
            // therefore always be face neighbours.
            VoxelVolume world = NewWorld();
            var visited = new List<BlockPos>();

            VoxelRaycast.Traverse(
                world,
                new Vector3d(0.5, 0.5, 0.5),
                new Vector3d(1, 0.61803, 0.37219).Normalize(),
                30,
                (p, b, f, d) => { visited.Add(p); return true; });

            visited.Should().HaveCountGreaterThan(20);
            for (int i = 1; i < visited.Count; i++)
            {
                visited[i].ManhattanDistance(visited[i - 1]).Should().Be(1, "step {0} must be a face neighbour", i);
            }

            visited.Should().OnlyHaveUniqueItems();
        }

        [TestMethod]
        public void Cast_DistanceIsMonotoneAlongTheRay()
        {
            VoxelVolume world = NewWorld();
            double previous = -1;

            VoxelRaycast.Traverse(
                world,
                new Vector3d(0.25, 0.75, 0.5),
                new Vector3d(0.7, 0.3, -0.5).Normalize(),
                25,
                (p, b, f, d) =>
                {
                    d.Should().BeGreaterThan(previous);
                    previous = d;
                    return true;
                });

            previous.Should().BeGreaterThan(20);
        }

        [TestMethod]
        public void Cast_PointOnTheHit_LiesOnTheFaceItReported()
        {
            VoxelVolume world = NewWorld();
            world.SetBlock(new BlockPos(6, 2, -3), 1);

            var origin = new Vector3d(0.5, 0.5, 0.5);
            var direction = new Vector3d(6.5 - 0.5, 2.5 - 0.5, -2.5 - 0.5).Normalize();
            VoxelHit hit = VoxelRaycast.Cast(world, origin, direction, 30);

            hit.Hit.Should().BeTrue();
            Vector3d point = hit.PointOn(origin, direction);

            // The entry point must sit on the plane of the face that was reported.
            int axis = VoxelFaces.Axis(hit.Face);
            double coordinate = axis == 0 ? point.X : (axis == 1 ? point.Y : point.Z);
            int block = axis == 0 ? hit.Block.X : (axis == 1 ? hit.Block.Y : hit.Block.Z);
            double plane = VoxelFaces.IsPositive(hit.Face) ? block + 1 : block;

            coordinate.Should().BeApproximately(plane, 1e-9);
        }

        [TestMethod]
        public void CastSolid_PassesThroughNonSolidBlocks()
        {
            VoxelVolume world = NewWorld();
            world.SetBlock(new BlockPos(3, 0, 0), 20); // decoration: not solid
            world.SetBlock(new BlockPos(6, 0, 0), 1);  // stone: solid

            VoxelHit hit = VoxelRaycast.CastSolid(world, new Vector3d(0.5, 0.5, 0.5), new Vector3d(1, 0, 0), 20);
            hit.Block.Should().Be(new BlockPos(6, 0, 0));
        }

        [TestMethod]
        public void CastOpaque_PassesThroughGlassButNotStone()
        {
            VoxelVolume world = NewWorld();
            world.SetBlock(new BlockPos(3, 0, 0), 10); // glass: solid, not opaque
            world.SetBlock(new BlockPos(6, 0, 0), 1);

            VoxelHit sight = VoxelRaycast.CastOpaque(world, new Vector3d(0.5, 0.5, 0.5), new Vector3d(1, 0, 0), 20);
            sight.Block.Should().Be(new BlockPos(6, 0, 0), "glass does not block a sightline");

            VoxelHit body = VoxelRaycast.CastSolid(world, new Vector3d(0.5, 0.5, 0.5), new Vector3d(1, 0, 0), 20);
            body.Block.Should().Be(new BlockPos(3, 0, 0), "glass does block a body");
        }

        [TestMethod]
        public void Cast_WithADegenerateDirection_Misses()
        {
            VoxelVolume world = NewWorld();
            world.SetBlock(new BlockPos(1, 0, 0), 1);

            VoxelRaycast.Cast(world, Vector3d.Origin, Vector3d.Zero, 10).Hit.Should().BeFalse();
            VoxelRaycast.Cast(world, Vector3d.Origin, new Vector3d(1, 0, 0), 0).Hit.Should().BeFalse();
        }
    }
}
