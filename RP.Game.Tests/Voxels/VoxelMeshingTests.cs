namespace RP.Game.Tests.Voxels
{
    using System;
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Voxels;
    using RP.Math;

    /// <summary>
    /// The mesher and the lighting flood-fill: the two passes that turn stored voxels into something a GPU
    /// can draw. Both are verified by properties rather than by golden output, because the exact triangle
    /// list is an implementation detail while "the surface is the right shape, wound the right way, and
    /// merging changed nothing visible" is the contract.
    /// </summary>
    [TestClass]
    public sealed class VoxelMeshingTests
    {
        private const ushort Air = 0;
        private const ushort Stone = 1;
        private const ushort Granite = 2;
        private const ushort Glass = 10;
        private const ushort Torch = 30;

        private static VoxelVolume NewWorld() => new VoxelVolume(new TestPalette());

        private static VoxelMeshData MeshOf(VoxelVolume world, ChunkPos chunk, bool greedy = true, bool ao = true)
        {
            var mesher = new VoxelMesher { GreedyMerge = greedy, AmbientOcclusion = ao };
            var data = new VoxelMeshData();
            mesher.Mesh(world, chunk, data);
            return data;
        }

        private static int QuadCount(VoxelMeshData mesh) => mesh.Indices.Count / 6;

        /// <summary>Total area of every triangle, which merging must preserve exactly.</summary>
        private static double SurfaceArea(VoxelMeshData mesh)
        {
            double total = 0;
            for (int i = 0; i < mesh.Indices.Count; i += 3)
            {
                Vector3 a = mesh.Vertices[(int)mesh.Indices[i]].Position;
                Vector3 b = mesh.Vertices[(int)mesh.Indices[i + 1]].Position;
                Vector3 c = mesh.Vertices[(int)mesh.Indices[i + 2]].Position;

                var ab = new Vector3d(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
                var ac = new Vector3d(c.X - a.X, c.Y - a.Y, c.Z - a.Z);
                total += ab.CrossProduct(ac).Magnitude * 0.5;
            }

            return total;
        }

        // ---- What gets meshed at all ------------------------------------------------------------------

        [TestMethod]
        public void Mesher_EmptyChunk_ProducesNothing()
        {
            VoxelVolume world = NewWorld();
            world.GetOrCreateChunk(new ChunkPos(0, 0, 0));

            MeshOf(world, new ChunkPos(0, 0, 0)).IsEmpty.Should().BeTrue();
        }

        [TestMethod]
        public void Mesher_SingleBlock_ProducesExactlySixQuads()
        {
            VoxelVolume world = NewWorld();
            world.SetBlock(new BlockPos(16, 16, 16), Stone);

            VoxelMeshData mesh = MeshOf(world, new ChunkPos(0, 0, 0));

            QuadCount(mesh).Should().Be(6);
            mesh.Vertices.Should().HaveCount(24);
            mesh.Indices.Should().HaveCount(36);
            SurfaceArea(mesh).Should().BeApproximately(6.0, 1e-5, "a unit cube has six unit faces");
        }

        [TestMethod]
        public void Mesher_BlockBuriedInOpaqueRock_ProducesNothing()
        {
            // The saving that makes voxel rendering possible at all: a face hidden by an opaque neighbour is
            // never emitted, so a solid world costs only its shell.
            VoxelVolume world = NewWorld();
            world.Generator = chunk => chunk.Fill(Stone);

            // Touch the chunk and its six neighbours so they all exist and are solid.
            var centre = new ChunkPos(0, 0, 0);
            world.GetBlock(centre.Origin());
            for (int f = 0; f < VoxelFaces.Count; f++) world.GetBlock(centre.Neighbour((BlockFace)f).Origin());

            MeshOf(world, centre).IsEmpty.Should().BeTrue("every face is hidden by solid rock");
        }

        [TestMethod]
        public void Mesher_FaceAgainstGlass_IsStillDrawn()
        {
            // Glass is solid but not opaque, so the rock behind it must still be meshed - otherwise looking
            // through a window shows a hole in the world.
            VoxelVolume world = NewWorld();
            world.SetBlock(new BlockPos(16, 16, 16), Stone);
            world.SetBlock(new BlockPos(17, 16, 16), Glass);

            VoxelMeshData mesh = MeshOf(world, new ChunkPos(0, 0, 0));

            // Eleven, not twelve. All six faces of the stone are drawn, because glass hides nothing. But
            // the glass's own face against the stone is *not* drawn: it would be exactly coplanar with the
            // stone face already occupying that plane, and two coplanar surfaces z-fight into a shimmering
            // mess. An opaque neighbour hides the face pointing at it whatever the near block is made of.
            QuadCount(mesh).Should().Be(11);
        }

        [TestMethod]
        public void Mesher_TwoAdjacentBlocksOfTheSameType_HideTheFaceBetweenThem()
        {
            VoxelVolume world = NewWorld();
            world.SetBlock(new BlockPos(16, 16, 16), Stone);
            world.SetBlock(new BlockPos(17, 16, 16), Stone);

            VoxelMeshData mesh = MeshOf(world, new ChunkPos(0, 0, 0));

            // 12 faces on two cubes, minus the two that face each other.
            SurfaceArea(mesh).Should().BeApproximately(10.0, 1e-5);
        }

        [TestMethod]
        public void Mesher_MeshesTheFaceExposedByANeighbouringChunk()
        {
            // The chunk-boundary case. A block on the very edge of a chunk must still draw the face that
            // borders the next chunk along, reading that neighbour through the world rather than assuming
            // air or assuming solid.
            VoxelVolume world = NewWorld();
            world.SetBlock(new BlockPos(31, 16, 16), Stone);
            world.SetBlock(new BlockPos(32, 16, 16), Stone); // in chunk (1,0,0)

            VoxelMeshData mesh = MeshOf(world, new ChunkPos(0, 0, 0));

            // Five faces: the +X face is hidden by the block in the neighbouring chunk.
            QuadCount(mesh).Should().Be(5);
            foreach (VoxelVertex v in mesh.Vertices)
            {
                v.Normal.Should().NotBe(new Vector3(1, 0, 0), "the +X face is hidden across the chunk boundary");
            }
        }

        // ---- Greedy merging -----------------------------------------------------------------------------

        [TestMethod]
        public void Greedy_FlatSlab_MergesEachSideIntoOneQuad()
        {
            VoxelVolume world = NewWorld();
            world.FillBox(new BlockPos(0, 0, 0), new BlockPos(31, 0, 31), Stone);

            VoxelMeshData merged = MeshOf(world, new ChunkPos(0, 0, 0), greedy: true);
            VoxelMeshData unmerged = MeshOf(world, new ChunkPos(0, 0, 0), greedy: false);

            // Top, bottom and four unit-height sides: six quads, down from 32*32*2 + 32*4.
            QuadCount(merged).Should().Be(6);
            QuadCount(unmerged).Should().Be((32 * 32 * 2) + (32 * 4));
        }

        [TestMethod]
        public void Greedy_PreservesSurfaceAreaExactly()
        {
            // Merging is only sound if it changes nothing about the surface. Comparing total triangle area
            // catches both failure modes at once: a dropped face loses area, a duplicated one gains it.
            VoxelVolume world = NewWorld();
            var rng = new Random(4242);
            for (int i = 0; i < 4000; i++)
            {
                world.SetBlock(new BlockPos(rng.Next(0, 32), rng.Next(0, 32), rng.Next(0, 32)), Stone);
            }

            double merged = SurfaceArea(MeshOf(world, new ChunkPos(0, 0, 0), greedy: true, ao: false));
            double unmerged = SurfaceArea(MeshOf(world, new ChunkPos(0, 0, 0), greedy: false, ao: false));

            merged.Should().BeApproximately(unmerged, 1e-6);
            merged.Should().BeGreaterThan(0);
        }

        [TestMethod]
        public void Greedy_DoesNotMergeAcrossDifferentMaterials()
        {
            VoxelVolume world = NewWorld();
            world.FillBox(new BlockPos(0, 0, 0), new BlockPos(31, 0, 15), Stone);
            world.FillBox(new BlockPos(0, 0, 16), new BlockPos(31, 0, 31), Granite);

            VoxelMeshData mesh = MeshOf(world, new ChunkPos(0, 0, 0));

            // Two materials means two top quads and two bottom quads, not one of each.
            int topQuads = 0;
            for (int i = 0; i < mesh.Indices.Count; i += 6)
            {
                if (mesh.Vertices[(int)mesh.Indices[i]].Normal.Y > 0.5) topQuads++;
            }

            topQuads.Should().Be(2);
        }

        [TestMethod]
        public void Greedy_TexCoordsSpanTheMergedQuadRatherThanStretching()
        {
            // Without this, a merged 32x32 floor would stretch one texel across the whole surface, which is
            // why some engines abandon merging. The UVs must run 0..width and 0..height so the material
            // tiles instead.
            VoxelVolume world = NewWorld();
            world.FillBox(new BlockPos(0, 0, 0), new BlockPos(31, 0, 31), Stone);

            VoxelMeshData mesh = MeshOf(world, new ChunkPos(0, 0, 0));

            float maxU = 0, maxV = 0;
            foreach (VoxelVertex v in mesh.Vertices)
            {
                if (v.TexCoord.X > maxU) maxU = v.TexCoord.X;
                if (v.TexCoord.Y > maxV) maxV = v.TexCoord.Y;
            }

            maxU.Should().Be(32);
            maxV.Should().Be(32);
        }

        // ---- Geometric correctness ----------------------------------------------------------------------

        [TestMethod]
        public void Mesh_EveryTriangleFacesTheWayItsVerticesClaim()
        {
            // The winding contract, checked on real output rather than on the corner table alone. A triangle
            // whose geometric normal opposes its vertex normal is invisible under back-face culling.
            VoxelVolume world = NewWorld();
            var rng = new Random(99);
            for (int i = 0; i < 3000; i++)
            {
                world.SetBlock(new BlockPos(rng.Next(0, 32), rng.Next(0, 32), rng.Next(0, 32)), Stone);
            }

            VoxelMeshData mesh = MeshOf(world, new ChunkPos(0, 0, 0));
            mesh.Indices.Should().NotBeEmpty();

            for (int i = 0; i < mesh.Indices.Count; i += 3)
            {
                VoxelVertex va = mesh.Vertices[(int)mesh.Indices[i]];
                Vector3 a = va.Position;
                Vector3 b = mesh.Vertices[(int)mesh.Indices[i + 1]].Position;
                Vector3 c = mesh.Vertices[(int)mesh.Indices[i + 2]].Position;

                var ab = new Vector3d(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
                var ac = new Vector3d(c.X - a.X, c.Y - a.Y, c.Z - a.Z);
                Vector3d cross = ab.CrossProduct(ac);

                double dot = (cross.X * va.Normal.X) + (cross.Y * va.Normal.Y) + (cross.Z * va.Normal.Z);
                dot.Should().BeGreaterThan(0.0, "triangle at index {0} is wound backwards", i);
            }
        }

        [TestMethod]
        public void Mesh_ContainsNoDegenerateTrianglesAndNoOutOfRangeIndices()
        {
            VoxelVolume world = NewWorld();
            var rng = new Random(7);
            for (int i = 0; i < 2000; i++)
            {
                world.SetBlock(new BlockPos(rng.Next(0, 32), rng.Next(0, 32), rng.Next(0, 32)), Stone);
            }

            VoxelMeshData mesh = MeshOf(world, new ChunkPos(0, 0, 0));
            (mesh.Indices.Count % 3).Should().Be(0);

            foreach (uint index in mesh.Indices)
            {
                index.Should().BeLessThan((uint)mesh.Vertices.Count);
            }

            for (int i = 0; i < mesh.Indices.Count; i += 3)
            {
                uint a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
                (a == b || b == c || a == c).Should().BeFalse("triangle at {0} is degenerate", i);
            }
        }

        [TestMethod]
        public void Mesh_AllVerticesLieWithinTheChunkPlusItsSurface()
        {
            VoxelVolume world = NewWorld();
            world.FillBox(new BlockPos(0, 0, 0), new BlockPos(31, 31, 31), Stone);

            foreach (VoxelVertex v in MeshOf(world, new ChunkPos(0, 0, 0)).Vertices)
            {
                v.Position.X.Should().BeInRange(0, VoxelChunk.Size);
                v.Position.Y.Should().BeInRange(0, VoxelChunk.Size);
                v.Position.Z.Should().BeInRange(0, VoxelChunk.Size);
            }
        }

        [TestMethod]
        public void AmbientOcclusion_DarkensAnInsideCornerAndLeavesOpenGroundAlone()
        {
            // A flat open floor should be uniformly unoccluded; adding a wall must darken the vertices that
            // tuck into the corner where the two meet.
            VoxelVolume world = NewWorld();
            world.FillBox(new BlockPos(0, 0, 0), new BlockPos(31, 0, 31), Stone);

            foreach (VoxelVertex v in MeshOf(world, new ChunkPos(0, 0, 0)).Vertices)
            {
                if (v.Normal.Y > 0.5) v.AmbientOcclusion.Should().Be(1f, "open ground is never occluded");
            }

            world.FillBox(new BlockPos(10, 1, 0), new BlockPos(10, 4, 31), Stone);

            bool anyDarkened = false;
            foreach (VoxelVertex v in MeshOf(world, new ChunkPos(0, 0, 0)).Vertices)
            {
                if (v.Normal.Y > 0.5 && v.AmbientOcclusion < 1f) anyDarkened = true;
            }

            anyDarkened.Should().BeTrue("the floor beside a wall must be occluded by it");
        }

        [TestMethod]
        public void Mesher_RejectsNullArguments()
        {
            var mesher = new VoxelMesher();
            Action nullWorld = () => mesher.Mesh(null!, new ChunkPos(0, 0, 0), new VoxelMeshData());
            Action nullTarget = () => mesher.Mesh(NewWorld(), new ChunkPos(0, 0, 0), null!);

            nullWorld.Should().Throw<ArgumentNullException>();
            nullTarget.Should().Throw<ArgumentNullException>();
        }

        // ---- Lighting -----------------------------------------------------------------------------------

        [TestMethod]
        public void BlockLight_FallsOffOneLevelPerStep()
        {
            VoxelVolume world = NewWorld();
            var min = new BlockPos(0, 0, 0);
            var max = new BlockPos(31, 31, 31);
            world.FillBox(min, max, Air);
            world.SetBlock(new BlockPos(16, 16, 16), Torch);

            VoxelLight.RelightBox(world, min, max, skyTop: -1000); // no sky, so block light only

            LightAt(world, new BlockPos(16, 16, 16)).Should().Be(14);
            LightAt(world, new BlockPos(17, 16, 16)).Should().Be(13);
            LightAt(world, new BlockPos(20, 16, 16)).Should().Be(10);
            LightAt(world, new BlockPos(16, 16, 16).Neighbour(BlockFace.PositiveY)).Should().Be(13);

            // It fades to nothing at exactly its range, not beyond and not short of it.
            LightAt(world, new BlockPos(29, 16, 16)).Should().Be(1);
            LightAt(world, new BlockPos(30, 16, 16)).Should().Be(0);
        }

        [TestMethod]
        public void BlockLight_SpreadsByTheShortestPathNotTheStraightLine()
        {
            // Light goes round corners, losing a level per step of the path it actually takes. A
            // straight-line falloff would light the far side of a wall.
            VoxelVolume world = NewWorld();
            var min = new BlockPos(0, 0, 0);
            var max = new BlockPos(31, 31, 31);

            world.FillBox(new BlockPos(10, 10, 10), new BlockPos(10, 20, 20), Stone); // a wall at x = 10
            world.SetBlock(new BlockPos(8, 15, 15), Torch);

            VoxelLight.RelightBox(world, min, max, skyTop: -1000);

            LightAt(world, new BlockPos(9, 15, 15)).Should().Be(13, "adjacent to the torch");
            LightAt(world, new BlockPos(10, 15, 15)).Should().Be(0, "inside the wall");

            // Directly behind the wall is three straight-line steps away, but the shortest open path has to
            // go round the wall's edge, so it must be dimmer than 11.
            LightAt(world, new BlockPos(11, 15, 15)).Should().BeLessThan(11);
        }

        [TestMethod]
        public void SkyLight_ReachesTheGroundAtFullStrengthDownAnOpenColumn()
        {
            // The vertical shortcut: direct sunlight does not attenuate falling through open air, however
            // far it falls. Without this a deep shaft would be dark at the bottom on a clear day.
            VoxelVolume world = NewWorld();
            world.FillBox(new BlockPos(0, 0, 0), new BlockPos(31, 0, 31), Stone);

            VoxelLight.RelightBox(world, new BlockPos(0, 0, 0), new BlockPos(31, 31, 31), skyTop: 31);

            SkyAt(world, new BlockPos(16, 31, 16)).Should().Be(VoxelLight.MaxLevel);
            SkyAt(world, new BlockPos(16, 1, 16)).Should().Be(VoxelLight.MaxLevel, "still in direct sun");
            SkyAt(world, new BlockPos(16, 0, 16)).Should().Be(0, "inside the ground");
        }

        [TestMethod]
        public void SkyLight_FadesSidewaysUnderAnOverhang()
        {
            VoxelVolume world = NewWorld();
            world.FillBox(new BlockPos(0, 0, 0), new BlockPos(31, 0, 31), Stone);   // floor
            world.FillBox(new BlockPos(0, 5, 0), new BlockPos(20, 5, 31), Stone);   // roof over x <= 20

            VoxelLight.RelightBox(world, new BlockPos(0, 0, 0), new BlockPos(31, 31, 31), skyTop: 31);

            SkyAt(world, new BlockPos(25, 2, 16)).Should().Be(VoxelLight.MaxLevel, "open to the sky");

            // Under the roof, daylight has to travel sideways, and now it does attenuate: one level per
            // block from the roof's edge at x = 21, which is the last column open to the sky.
            SkyAt(world, new BlockPos(20, 2, 16)).Should().Be(14);
            SkyAt(world, new BlockPos(15, 2, 16)).Should().Be(9);
            SkyAt(world, new BlockPos(10, 2, 16)).Should().Be(4);

            // Sixteen blocks in, it has run out entirely - which is what makes a cave dark however wide
            // its mouth is.
            SkyAt(world, new BlockPos(5, 2, 16)).Should().Be(0);
        }

        [TestMethod]
        public void Light_Combine_TakesTheStrongerChannelNotTheSum()
        {
            // Two level-8 sources do not make daylight. Summing would also make a torch-lit room brighten as
            // the sun rose outside a wall it cannot see through.
            VoxelLight.Combine(8, 8, 1.0).Should().BeApproximately(8.0 / 15.0, 1e-9);
            VoxelLight.Combine(15, 0, 1.0).Should().BeApproximately(1.0, 1e-9);
            VoxelLight.Combine(15, 0, 0.0).Should().BeApproximately(0.0, 1e-9, "sky light goes out at night");
            VoxelLight.Combine(0, 15, 0.0).Should().BeApproximately(1.0, 1e-9, "torches do not");
        }

        [TestMethod]
        public void Light_RelightIsIdempotent()
        {
            VoxelVolume world = NewWorld();
            world.FillBox(new BlockPos(0, 0, 0), new BlockPos(31, 0, 31), Stone);
            world.SetBlock(new BlockPos(16, 3, 16), Torch);

            var min = new BlockPos(0, 0, 0);
            var max = new BlockPos(31, 31, 31);

            VoxelLight.RelightBox(world, min, max, skyTop: 31);
            byte first = LightAt(world, new BlockPos(18, 3, 16));
            byte firstSky = SkyAt(world, new BlockPos(18, 3, 16));

            VoxelLight.RelightBox(world, min, max, skyTop: 31);

            LightAt(world, new BlockPos(18, 3, 16)).Should().Be(first, "relighting must not accumulate");
            SkyAt(world, new BlockPos(18, 3, 16)).Should().Be(firstSky);
        }

        [TestMethod]
        public void RemoveBlockLight_LeavesNoDarkScarBehindIt()
        {
            // The half that is usually implemented wrongly: clearing the region a light lit, then
            // re-propagating from the sources that survive. Skip the second pass and a permanent hole
            // appears where the removed light and a surviving one overlapped.
            VoxelVolume world = NewWorld();
            var min = new BlockPos(0, 0, 0);
            var max = new BlockPos(31, 31, 31);

            world.SetBlock(new BlockPos(10, 16, 16), Torch);
            world.SetBlock(new BlockPos(16, 16, 16), Torch);
            VoxelLight.RelightBox(world, min, max, skyTop: -1000);

            byte before = LightAt(world, new BlockPos(13, 16, 16));
            before.Should().BeGreaterThan(0);

            // Take one torch away, then remove its light.
            world.SetBlock(new BlockPos(16, 16, 16), Air);
            VoxelLight.RemoveBlockLight(world, new BlockPos(16, 16, 16));

            // The surviving torch still lights the midpoint, at exactly its own falloff.
            LightAt(world, new BlockPos(11, 16, 16)).Should().Be(13);
            LightAt(world, new BlockPos(13, 16, 16)).Should().Be(11);
            LightAt(world, new BlockPos(16, 16, 16)).Should().Be(8, "still within the surviving torch's reach");
        }

        private static byte LightAt(VoxelVolume world, BlockPos p)
        {
            world.TryGetChunk(ChunkPos.FromBlock(p), out VoxelChunk chunk).Should().BeTrue();
            VoxelChunk.ToLocal(p, out int lx, out int ly, out int lz);
            return chunk.GetBlockLight(lx, ly, lz);
        }

        private static byte SkyAt(VoxelVolume world, BlockPos p)
        {
            world.TryGetChunk(ChunkPos.FromBlock(p), out VoxelChunk chunk).Should().BeTrue();
            VoxelChunk.ToLocal(p, out int lx, out int ly, out int lz);
            return chunk.GetSkyLight(lx, ly, lz);
        }
    }
}
