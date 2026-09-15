namespace RP.Game.Tests.Voxels
{
    using System;
    using System.Collections.Generic;
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Voxels;
    using RP.Math;

    /// <summary>
    /// A block palette for tests, driven by simple id ranges so each test can say what it means without
    /// building a registry: 0 is air, 1..9 are opaque solids, 10..19 are transparent solids (glass),
    /// 20..29 are non-solid decoration (grass), 30..39 emit light, and 40..49 are translucent fluids.
    /// </summary>
    internal sealed class TestPalette : IVoxelPalette
    {
        public bool IsAir(ushort block) => block == 0;

        public bool IsOpaque(ushort block) => block >= 1 && block <= 9;

        public bool IsSolid(ushort block) => (block >= 1 && block <= 19) || (block >= 30 && block <= 39);

        public byte LightEmission(ushort block) => (byte)(block >= 30 && block <= 39 ? 14 : 0);

        public byte LightAttenuation(ushort block) => (byte)(block >= 40 && block <= 49 ? 3 : 1);

        public uint FaceAppearance(ushort block, BlockFace face) => block;

        public bool DrawsAgainstSelf(ushort block) => false;
    }

    /// <summary>
    /// The coordinate, storage and world-container behaviour the whole voxel stack rests on.
    /// </summary>
    [TestClass]
    public sealed class VoxelCoreTests
    {
        private static VoxelVolume NewWorld() => new VoxelVolume(new TestPalette());

        // ---- Coordinates: the flooring traps ---------------------------------------------------------

        [TestMethod]
        public void BlockPos_FromWorld_FloorsRatherThanTruncating()
        {
            // The block containing -0.3 is block -1, because block -1 spans [-1, 0). Truncation would
            // report 0 and make the two blocks either side of every axis overlap.
            BlockPos.FromWorld(new Vector3d(-0.3, -0.3, -0.3)).Should().Be(new BlockPos(-1, -1, -1));
            BlockPos.FromWorld(new Vector3d(0.3, 0.3, 0.3)).Should().Be(new BlockPos(0, 0, 0));
            BlockPos.FromWorld(new Vector3d(-1.0, -1.0, -1.0)).Should().Be(new BlockPos(-1, -1, -1));
            BlockPos.FromWorld(new Vector3d(-1.0001, 5.9999, -0.0001)).Should().Be(new BlockPos(-2, 5, -1));
        }

        [TestMethod]
        public void BlockPos_FromWorld_IsExactlyInverseOfToWorldAtCorners()
        {
            for (int i = -40; i <= 40; i++)
            {
                var p = new BlockPos(i, -i, i * 3);
                BlockPos.FromWorld(p.ToWorld()).Should().Be(p);
                BlockPos.FromWorld(p.Center()).Should().Be(p);
            }
        }

        [TestMethod]
        public void ChunkPos_FromBlock_FloorsAcrossTheOrigin()
        {
            // -1 / 32 is 0 in C# (division truncates toward zero) but block -1 belongs to chunk -1. The
            // arithmetic shift gets this right; a division does not.
            ChunkPos.FromBlock(new BlockPos(-1, -1, -1)).Should().Be(new ChunkPos(-1, -1, -1));
            ChunkPos.FromBlock(new BlockPos(0, 0, 0)).Should().Be(new ChunkPos(0, 0, 0));
            ChunkPos.FromBlock(new BlockPos(31, 31, 31)).Should().Be(new ChunkPos(0, 0, 0));
            ChunkPos.FromBlock(new BlockPos(32, 32, 32)).Should().Be(new ChunkPos(1, 1, 1));
            ChunkPos.FromBlock(new BlockPos(-32, -33, -1)).Should().Be(new ChunkPos(-1, -2, -1));
        }

        [TestMethod]
        public void ChunkPos_OriginAndToLocal_RoundTripEveryBlock()
        {
            for (int i = -70; i <= 70; i++)
            {
                var block = new BlockPos(i, i + 7, -i);
                ChunkPos cp = ChunkPos.FromBlock(block);
                BlockPos origin = cp.Origin();
                VoxelChunk.ToLocal(block, out int lx, out int ly, out int lz);

                VoxelChunk.InBounds(lx, ly, lz).Should().BeTrue();
                new BlockPos(origin.X + lx, origin.Y + ly, origin.Z + lz).Should().Be(block);
            }
        }

        // ---- Face tables ------------------------------------------------------------------------------

        [TestMethod]
        public void Faces_OppositeIsAnXorAndIsAnInvolution()
        {
            for (int f = 0; f < VoxelFaces.Count; f++)
            {
                var face = (BlockFace)f;
                BlockFace opposite = VoxelFaces.Opposite(face);

                opposite.Should().NotBe(face);
                VoxelFaces.Opposite(opposite).Should().Be(face);
                VoxelFaces.Axis(opposite).Should().Be(VoxelFaces.Axis(face));
                VoxelFaces.Normals[(int)opposite].Should().Be(-VoxelFaces.Normals[(int)face]);
            }
        }

        [TestMethod]
        public void Faces_OffsetsAgreeWithNormals()
        {
            for (int f = 0; f < VoxelFaces.Count; f++)
            {
                BlockPos offset = VoxelFaces.Offsets[f];
                Vector3 normal = VoxelFaces.Normals[f];

                ((float)offset.X).Should().Be(normal.X);
                ((float)offset.Y).Should().Be(normal.Y);
                ((float)offset.Z).Should().Be(normal.Z);
            }
        }

        [TestMethod]
        public void Faces_CornersAreWoundCounterClockwiseSeenFromOutside()
        {
            // The winding bug in one assertion. If a face's corners are listed the wrong way round, its
            // triangles face inward and the face vanishes under back-face culling — "my world has holes in
            // it, but only from some angles". Cross the first two edges and check the result points the
            // same way as the face's declared normal.
            for (int f = 0; f < VoxelFaces.Count; f++)
            {
                Vector3[] corners = VoxelFaces.Corners[f];
                corners.Should().HaveCount(4);

                var a = new Vector3d(corners[1].X - corners[0].X, corners[1].Y - corners[0].Y, corners[1].Z - corners[0].Z);
                var b = new Vector3d(corners[2].X - corners[0].X, corners[2].Y - corners[0].Y, corners[2].Z - corners[0].Z);
                Vector3d cross = a.CrossProduct(b);

                Vector3 normal = VoxelFaces.Normals[f];
                double dot = (cross.X * normal.X) + (cross.Y * normal.Y) + (cross.Z * normal.Z);

                dot.Should().BeGreaterThan(0.0, "face {0} must wind counter-clockwise from outside", (BlockFace)f);
            }
        }

        [TestMethod]
        public void Faces_CornersAreFlatAndUnitSized()
        {
            // Every corner must lie on the unit cube, and all four must share the value on the face's axis.
            for (int f = 0; f < VoxelFaces.Count; f++)
            {
                var face = (BlockFace)f;
                int axis = VoxelFaces.Axis(face);
                float expected = VoxelFaces.IsPositive(face) ? 1f : 0f;

                foreach (Vector3 c in VoxelFaces.Corners[f])
                {
                    foreach (float component in new[] { c.X, c.Y, c.Z })
                    {
                        component.Should().BeOneOf(0f, 1f);
                    }

                    float onAxis = axis == 0 ? c.X : (axis == 1 ? c.Y : c.Z);
                    onAxis.Should().Be(expected);
                }
            }
        }

        [TestMethod]
        public void Neighbour_StepsExactlyOneBlockAcrossEachFace()
        {
            var origin = new BlockPos(5, -3, 11);
            for (int f = 0; f < VoxelFaces.Count; f++)
            {
                BlockPos n = origin.Neighbour((BlockFace)f);
                n.ManhattanDistance(origin).Should().Be(1);
                n.Neighbour(VoxelFaces.Opposite((BlockFace)f)).Should().Be(origin);
            }
        }

        // ---- Chunk storage ----------------------------------------------------------------------------

        [TestMethod]
        public void Chunk_IndexIsAUniqueBijectionOverTheVolume()
        {
            var seen = new HashSet<int>();
            for (int y = 0; y < VoxelChunk.Size; y++)
            {
                for (int z = 0; z < VoxelChunk.Size; z++)
                {
                    for (int x = 0; x < VoxelChunk.Size; x++)
                    {
                        int i = VoxelChunk.Index(x, y, z);
                        i.Should().BeInRange(0, VoxelChunk.Volume - 1);
                        seen.Add(i).Should().BeTrue("index {0},{1},{2} collided", x, y, z);
                    }
                }
            }

            seen.Should().HaveCount(VoxelChunk.Volume);
        }

        [TestMethod]
        public void Chunk_StartsUniformAndAllocatesOnlyOnADifferingWrite()
        {
            var chunk = new VoxelChunk(new ChunkPos(0, 0, 0), fill: 3);

            chunk.IsUniform.Should().BeTrue();
            chunk.GetBlock(17, 4, 29).Should().Be(3);

            // Writing the value it already holds must not materialise the array, or a world of solid stone
            // would densify the moment anything re-asserted a block.
            chunk.SetBlock(1, 1, 1, 3).Should().BeFalse();
            chunk.IsUniform.Should().BeTrue();
            chunk.Revision.Should().Be(0);

            chunk.SetBlock(1, 1, 1, 7).Should().BeTrue();
            chunk.IsUniform.Should().BeFalse();
            chunk.Revision.Should().Be(1);
            chunk.GetBlock(1, 1, 1).Should().Be(7);
            chunk.GetBlock(2, 1, 1).Should().Be(3, "materialising must preserve the uniform fill");
        }

        [TestMethod]
        public void Chunk_TryCompact_CollapsesAUniformArrayBackDown()
        {
            var chunk = new VoxelChunk(new ChunkPos(0, 0, 0), fill: 3);
            chunk.SetBlock(5, 5, 5, 9);
            chunk.IsUniform.Should().BeFalse();

            chunk.TryCompact().Should().BeFalse("it is not uniform yet");

            chunk.SetBlock(5, 5, 5, 3);
            chunk.TryCompact().Should().BeTrue();
            chunk.IsUniform.Should().BeTrue();
            chunk.UniformBlock.Should().Be(3);
            chunk.GetBlock(5, 5, 5).Should().Be(3);
        }

        [TestMethod]
        public void Chunk_LightChannelsArePackedWithoutCrossTalk()
        {
            var chunk = new VoxelChunk(new ChunkPos(0, 0, 0));

            chunk.SetSkyLight(2, 3, 4, 15);
            chunk.SetBlockLight(2, 3, 4, 7);

            chunk.GetSkyLight(2, 3, 4).Should().Be(15);
            chunk.GetBlockLight(2, 3, 4).Should().Be(7);

            // Overwriting one channel must leave the other alone — they share a byte.
            chunk.SetSkyLight(2, 3, 4, 0);
            chunk.GetBlockLight(2, 3, 4).Should().Be(7);
            chunk.GetSkyLight(2, 3, 4).Should().Be(0);

            chunk.GetLight(2, 3, 4, out byte sky, out byte block);
            sky.Should().Be(0);
            block.Should().Be(7);
        }

        [TestMethod]
        public void Chunk_OutOfRangeAccessIsSafe()
        {
            var chunk = new VoxelChunk(new ChunkPos(0, 0, 0), fill: 5);

            chunk.GetBlock(-1, 0, 0).Should().Be(0);
            chunk.GetBlock(VoxelChunk.Size, 0, 0).Should().Be(0);
            chunk.SetBlock(-1, 0, 0, 9).Should().BeFalse();
            chunk.GetSkyLight(0, -1, 0).Should().Be(0);
        }

        // ---- The world container ----------------------------------------------------------------------

        [TestMethod]
        public void Volume_ReadsAndWritesAcrossChunkBoundaries()
        {
            VoxelVolume world = NewWorld();

            var inside = new BlockPos(31, 31, 31);
            var across = new BlockPos(32, 32, 32);
            var negative = new BlockPos(-1, -1, -1);

            world.SetBlock(inside, 1).Should().BeTrue();
            world.SetBlock(across, 2).Should().BeTrue();
            world.SetBlock(negative, 3).Should().BeTrue();

            world.GetBlock(inside).Should().Be(1);
            world.GetBlock(across).Should().Be(2);
            world.GetBlock(negative).Should().Be(3);
            world.GetBlock(new BlockPos(1000, 1000, 1000)).Should().Be(0, "unloaded regions read as air");
        }

        [TestMethod]
        public void Volume_EditOnAChunkBoundaryDirtiesTheNeighbour()
        {
            // The most common voxel rendering bug: the face that was hidden against this block is now
            // exposed in the *neighbouring* chunk's mesh, so that chunk must be re-meshed too. Missing this
            // leaves a seam of absent geometry along chunk borders wherever the player has dug.
            VoxelVolume world = NewWorld();
            world.DrainDirty();

            world.SetBlock(new BlockPos(0, 0, 0), 1);
            var dirty = new HashSet<ChunkPos>(world.DrainDirty());

            dirty.Should().Contain(new ChunkPos(0, 0, 0));
            dirty.Should().Contain(new ChunkPos(-1, 0, 0));
            dirty.Should().Contain(new ChunkPos(0, -1, 0));
            dirty.Should().Contain(new ChunkPos(0, 0, -1));
        }

        [TestMethod]
        public void Volume_EditInTheMiddleOfAChunkDirtiesOnlyThatChunk()
        {
            VoxelVolume world = NewWorld();
            world.DrainDirty();

            world.SetBlock(new BlockPos(16, 16, 16), 1);
            world.DrainDirty().Should().BeEquivalentTo(new[] { new ChunkPos(0, 0, 0) });
        }

        [TestMethod]
        public void Volume_RedundantWriteChangesNothingAndDirtiesNothing()
        {
            VoxelVolume world = NewWorld();
            world.SetBlock(new BlockPos(4, 4, 4), 1);
            world.DrainDirty();

            world.SetBlock(new BlockPos(4, 4, 4), 1).Should().BeFalse();
            world.DrainDirty().Should().BeEmpty();
        }

        [TestMethod]
        public void Volume_FillBox_CountsOnlyGenuineChanges()
        {
            VoxelVolume world = NewWorld();

            // A 3x3x3 box spanning a chunk boundary, to exercise the per-chunk clipping.
            var min = new BlockPos(30, 30, 30);
            var max = new BlockPos(32, 32, 32);

            world.FillBox(min, max, 1).Should().Be(27);
            world.FillBox(min, max, 1).Should().Be(0, "nothing changed the second time");

            world.GetBlock(new BlockPos(31, 31, 31)).Should().Be(1);
            world.GetBlock(new BlockPos(32, 32, 32)).Should().Be(1);
            world.GetBlock(new BlockPos(33, 32, 32)).Should().Be(0);
            world.GetBlock(new BlockPos(29, 30, 30)).Should().Be(0);
        }

        [TestMethod]
        public void Volume_ForEachBlock_VisitsEveryBlockOnceAcrossChunks()
        {
            VoxelVolume world = NewWorld();
            var min = new BlockPos(30, -2, 30);
            var max = new BlockPos(34, 2, 34);
            world.FillBox(min, max, 4);

            var visited = new HashSet<BlockPos>();
            int matching = 0;
            world.ForEachBlock(min, max, (p, b) =>
            {
                visited.Add(p).Should().BeTrue("block {0} was visited twice", p);
                if (b == 4) matching++;
            });

            int expected = 5 * 5 * 5;
            visited.Should().HaveCount(expected);
            matching.Should().Be(expected);
        }

        [TestMethod]
        public void Volume_HighestSolidY_FindsTheSurfaceAndReportsAnEmptyColumn()
        {
            VoxelVolume world = NewWorld();
            world.SetBlock(new BlockPos(3, 10, 3), 1);
            world.SetBlock(new BlockPos(3, 40, 3), 1);

            world.HighestSolidY(3, 3, 100, 0).Should().Be(40);
            world.HighestSolidY(3, 3, 30, 0).Should().Be(10);
            world.HighestSolidY(9, 9, 100, 0).Should().Be(int.MinValue);
        }

        [TestMethod]
        public void Volume_Generator_FillsAChunkOnceOnFirstTouch()
        {
            VoxelVolume world = NewWorld();
            var generated = new List<ChunkPos>();

            world.Generator = chunk =>
            {
                generated.Add(chunk.Position);
                chunk.Fill(1);
            };

            world.GetBlock(new BlockPos(5, 5, 5)).Should().Be(1);
            world.GetBlock(new BlockPos(6, 6, 6)).Should().Be(1);
            world.GetBlock(new BlockPos(40, 5, 5)).Should().Be(1);

            generated.Should().BeEquivalentTo(new[] { new ChunkPos(0, 0, 0), new ChunkPos(1, 0, 0) });
        }

        [TestMethod]
        public void Volume_PeekBlock_DoesNotTriggerGeneration()
        {
            VoxelVolume world = NewWorld();
            int calls = 0;
            world.Generator = chunk => { calls++; chunk.Fill(1); };

            world.PeekBlock(new BlockPos(5, 5, 5)).Should().Be(0);
            calls.Should().Be(0);
        }

        [TestMethod]
        public void Volume_RejectsANullPalette()
        {
            Action act = () => new VoxelVolume(null!);
            act.Should().Throw<ArgumentNullException>();
        }
    }
}
