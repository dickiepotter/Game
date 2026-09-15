namespace RP.Game.Voxels
{
    using System;
    using System.Collections.Generic;
    using RP.Math;

    /// <summary>
    /// One vertex of a chunk mesh. Position and normal describe the geometry; the rest is what the shader
    /// needs to light and texture it without a second lookup.
    /// </summary>
    /// <remarks>
    /// Blittable by construction — a flat block of floats and one integer — so an array of these copies
    /// straight into a GPU vertex buffer with no marshalling. Keep the field order in lockstep with the
    /// backend's vertex attribute descriptions and the shader's <c>layout(location = ...)</c> inputs.
    /// </remarks>
    public readonly struct VoxelVertex
    {
        /// <summary>Position, relative to the chunk's origin.</summary>
        public readonly Vector3 Position;

        /// <summary>Outward surface normal (unit length, axis-aligned).</summary>
        public readonly Vector3 Normal;

        /// <summary>Texture coordinate within the face, in <c>[0, width] x [0, height]</c> of the merged
        /// quad — so a merged quad tiles its material rather than stretching it.</summary>
        public readonly Vector2 TexCoord;

        /// <summary>The material token from <see cref="IVoxelPalette.FaceAppearance"/>.</summary>
        public readonly uint Material;

        /// <summary>Ambient occlusion in <c>[0, 1]</c>, where 1 is fully open and 0 is deeply tucked into a
        /// corner.</summary>
        public readonly float AmbientOcclusion;

        /// <summary>Sky-light level in <c>[0, 1]</c>, to be scaled by the time of day.</summary>
        public readonly float SkyLight;

        /// <summary>Block-light level in <c>[0, 1]</c>, independent of the time of day.</summary>
        public readonly float BlockLight;

        /// <summary>Creates a vertex.</summary>
        public VoxelVertex(
            Vector3 position, Vector3 normal, Vector2 texCoord, uint material,
            float ambientOcclusion, float skyLight, float blockLight)
        {
            Position = position;
            Normal = normal;
            TexCoord = texCoord;
            Material = material;
            AmbientOcclusion = ambientOcclusion;
            SkyLight = skyLight;
            BlockLight = blockLight;
        }
    }

    /// <summary>The triangles produced for one chunk, ready to upload.</summary>
    public sealed class VoxelMeshData
    {
        /// <summary>The mesh vertices.</summary>
        public List<VoxelVertex> Vertices { get; } = new List<VoxelVertex>();

        /// <summary>Triangle indices into <see cref="Vertices"/>.</summary>
        public List<uint> Indices { get; } = new List<uint>();

        /// <summary>Whether anything at all was produced — a chunk of solid rock or empty air meshes to
        /// nothing, and there are far more of those than of interesting chunks.</summary>
        public bool IsEmpty => Indices.Count == 0;

        /// <summary>Empties the buffers so the instance can be reused for the next chunk, which avoids
        /// re-allocating two large lists for every re-mesh.</summary>
        public void Clear()
        {
            Vertices.Clear();
            Indices.Clear();
        }
    }

    /// <summary>
    /// Turns a chunk of voxels into triangles, merging coplanar faces of the same appearance into the
    /// largest rectangles it can, and baking ambient occlusion and light into the vertices as it goes.
    /// </summary>
    /// <remarks>
    /// <para><b>Why not one cube per block.</b> A naive mesher emits six quads per solid voxel. A chunk of
    /// 32,768 voxels would be 196,608 quads, almost all of them buried inside the rock where nobody can see
    /// them. The first and largest saving is to emit a face only where the neighbouring block does not hide
    /// it, which reduces a solid chunk to its shell. The second is <b>greedy meshing</b>: a flat stone
    /// floor 32 blocks square is 1,024 identical quads that could be one, and merging them cuts the vertex
    /// count, the index count and — because a merged quad is rasterised once instead of a thousand times
    /// with a thousand state-identical draws — the GPU's work too.</para>
    ///
    /// <para><b>How the merge works.</b> For each of the six face directions, the chunk is sliced into 32
    /// planes. Each plane is flattened into a 32x32 mask, one entry per potential face, holding everything
    /// that decides how that face is drawn: its material, its four ambient-occlusion values and its four
    /// light values. Then the mask is swept: find an unclaimed entry, extend it as far right as identical
    /// entries continue, then extend that whole row downward as far as identical rows continue, emit one
    /// quad, and clear the rectangle. Two faces merge only when they would be drawn <i>identically</i> —
    /// which is why the lighting has to be part of the key, and why a lit gradient naturally refuses to
    /// merge exactly where the gradient is.</para>
    ///
    /// <para><b>Ambient occlusion.</b> The single cheapest thing that makes a blocky world look solid
    /// rather than like a pile of stickers. Each vertex sits at a corner where three other blocks meet the
    /// face; counting how many of them are opaque gives a four-level darkening that shades inside corners,
    /// grounds objects against the floor, and picks out every edge. It costs three neighbour lookups per
    /// vertex and no GPU time at all.</para>
    ///
    /// <para><b>The diagonal flip.</b> A quad is two triangles, and which diagonal splits them is normally
    /// arbitrary. With per-vertex AO it is not: interpolating across the wrong diagonal makes a corner
    /// shadow bend visibly the wrong way, and because the choice is consistent across the mesh the error
    /// reads as a herringbone pattern over the whole world. Choosing the diagonal that connects the two
    /// <i>most similar</i> corners fixes it, and costs one comparison.</para>
    ///
    /// <para><b>Threading.</b> A mesher instance holds scratch buffers and is therefore not thread-safe,
    /// but it reads the world without writing, so one mesher per worker thread meshes many chunks in
    /// parallel safely.</para>
    /// </remarks>
    public sealed class VoxelMesher
    {
        private const int Size = VoxelChunk.Size;

        // One mask entry per face position in the current slice. Reused across every slice and every
        // direction, so a mesher allocates this once and never again.
        private readonly MaskEntry[] _mask = new MaskEntry[Size * Size];

        // The 32x32x32 chunk plus a one-voxel skin on all sides, so the inner loops can read a neighbour
        // without a bounds test or a chunk lookup. Fetching the skin once up front turns what would be
        // hundreds of thousands of dictionary probes into 34^3 reads.
        private const int PaddedSize = Size + 2;
        private readonly ushort[] _blocks = new ushort[PaddedSize * PaddedSize * PaddedSize];
        private readonly byte[] _light = new byte[PaddedSize * PaddedSize * PaddedSize];
        private readonly bool[] _opaque = new bool[PaddedSize * PaddedSize * PaddedSize];

        /// <summary>Everything that decides how one face is drawn. Two faces merge only if these match.</summary>
        private readonly struct MaskEntry : IEquatable<MaskEntry>
        {
            public readonly uint Material;
            public readonly byte Ao0, Ao1, Ao2, Ao3;
            public readonly byte Sky0, Sky1, Sky2, Sky3;
            public readonly byte Block0, Block1, Block2, Block3;
            public readonly bool Present;

            public MaskEntry(
                uint material,
                byte ao0, byte ao1, byte ao2, byte ao3,
                byte sky0, byte sky1, byte sky2, byte sky3,
                byte block0, byte block1, byte block2, byte block3)
            {
                Material = material;
                Ao0 = ao0; Ao1 = ao1; Ao2 = ao2; Ao3 = ao3;
                Sky0 = sky0; Sky1 = sky1; Sky2 = sky2; Sky3 = sky3;
                Block0 = block0; Block1 = block1; Block2 = block2; Block3 = block3;
                Present = true;
            }

            public bool Equals(MaskEntry other)
                => Present == other.Present
                   && Material == other.Material
                   && Ao0 == other.Ao0 && Ao1 == other.Ao1 && Ao2 == other.Ao2 && Ao3 == other.Ao3
                   && Sky0 == other.Sky0 && Sky1 == other.Sky1 && Sky2 == other.Sky2 && Sky3 == other.Sky3
                   && Block0 == other.Block0 && Block1 == other.Block1 && Block2 == other.Block2 && Block3 == other.Block3;

            public override bool Equals(object? obj) => obj is MaskEntry other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(Material, Ao0, Ao1, Ao2, Ao3, Sky0, Block0);
        }

        /// <summary>
        /// Whether to merge coplanar faces. Leave it on; the switch exists so the mesher can be compared
        /// against its own unmerged output in tests, which is how the merge is proved to preserve the
        /// surface rather than quietly dropping or duplicating faces.
        /// </summary>
        public bool GreedyMerge { get; set; } = true;

        /// <summary>
        /// Whether to darken vertices tucked into corners. Also a test switch, and a legitimate quality
        /// setting for very low-end hardware — though it costs nothing on the GPU, only in mesh build time.
        /// </summary>
        public bool AmbientOcclusion { get; set; } = true;

        /// <summary>
        /// Builds the mesh for one chunk. Returns an empty mesh for a chunk with no visible surface.
        /// </summary>
        /// <param name="world">The world, read for the chunk and its immediate neighbours.</param>
        /// <param name="position">Which chunk to mesh.</param>
        /// <param name="into">A mesh to fill. Cleared first; pass the same instance back to avoid
        /// re-allocating.</param>
        public void Mesh(IVoxelRead world, ChunkPos position, VoxelMeshData into)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (into == null) throw new ArgumentNullException(nameof(into));

            into.Clear();
            if (!Gather(world, position)) return;

            for (int f = 0; f < VoxelFaces.Count; f++)
            {
                MeshDirection(world, (BlockFace)f, into);
            }
        }

        // The 27 chunks covering the padded region, fetched once. Index is (dy+1)*9 + (dz+1)*3 + (dx+1).
        private readonly VoxelChunk?[] _neighbourhood = new VoxelChunk?[27];

        /// <summary>
        /// Copies the chunk and its one-voxel skin into flat scratch arrays.
        /// </summary>
        /// <remarks>
        /// <para><b>The neighbourhood is fetched once, not per voxel.</b> The obvious implementation asks
        /// the world for each of the 34-cubed padded positions in turn, and each of those asks costs a
        /// coordinate decomposition, a dictionary probe for the owning chunk, and a second probe for its
        /// light — call it eighty thousand hash lookups to mesh one chunk. Measured, that was 129 ms per
        /// chunk, six times the cost of *generating* it, and it is entirely overhead: the answers all come
        /// from the same twenty-seven chunks.</para>
        /// <para>Fetching those twenty-seven up front and indexing into them directly turns the per-voxel
        /// cost into arithmetic. The interior of the region — 32,768 of the 39,304 voxels — is one chunk
        /// and is copied without any neighbourhood lookup at all.</para>
        /// </remarks>
        /// <returns>False if the chunk is entirely air, in which case there is nothing to mesh.</returns>
        private bool Gather(IVoxelRead world, ChunkPos position)
        {
            IVoxelPalette palette = world.Palette;
            bool anySolid = false;

            // The fast path that most of a world takes: a uniform chunk of air has no surface of its own.
            // Its *neighbours* draw the faces that border it, so there is genuinely nothing to do here.
            if (world.TryGetChunk(position, out VoxelChunk centre) && centre.IsUniform && palette.IsAir(centre.UniformBlock))
            {
                return false;
            }

            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        ChunkPos at = position.Offset(dx, dy, dz);
                        _neighbourhood[((dy + 1) * 9) + ((dz + 1) * 3) + (dx + 1)] =
                            world.TryGetChunk(at, out VoxelChunk found) ? found : null;
                    }
                }
            }

            for (int y = -1; y <= Size; y++)
            {
                // Which chunk row, and which voxel row inside it. Computed once per plane rather than per
                // voxel, because -1 and Size are the only values that leave the centre chunk.
                int cy = y < 0 ? -1 : (y >= Size ? 1 : 0);
                int ly = y & VoxelChunk.SizeMask;

                for (int z = -1; z <= Size; z++)
                {
                    int cz = z < 0 ? -1 : (z >= Size ? 1 : 0);
                    int lz = z & VoxelChunk.SizeMask;

                    for (int x = -1; x <= Size; x++)
                    {
                        int cx = x < 0 ? -1 : (x >= Size ? 1 : 0);
                        int lx = x & VoxelChunk.SizeMask;

                        VoxelChunk? source = _neighbourhood[((cy + 1) * 9) + ((cz + 1) * 3) + (cx + 1)];

                        ushort block = 0;
                        byte light = 0;
                        if (source is not null)
                        {
                            block = source.GetBlock(lx, ly, lz);
                            source.GetLight(lx, ly, lz, out byte sky, out byte blockLight);
                            light = (byte)((sky << 4) | blockLight);
                        }

                        int i = PaddedIndex(x, y, z);
                        _blocks[i] = block;
                        _opaque[i] = palette.IsOpaque(block);
                        _light[i] = light;

                        if (!palette.IsAir(block)) anySolid = true;
                    }
                }
            }

            return anySolid;
        }

        /// <summary>Index into the padded scratch arrays, where coordinates run from -1 to Size.</summary>
        private static int PaddedIndex(int x, int y, int z)
            => (((y + 1) * PaddedSize) + (z + 1)) * PaddedSize + (x + 1);

        /// <summary>
        /// Meshes every face pointing one direction, slice by slice.
        /// </summary>
        private void MeshDirection(IVoxelRead world, BlockFace face, VoxelMeshData into)
        {
            IVoxelPalette palette = world.Palette;
            int axis = VoxelFaces.Axis(face);
            BlockPos step = VoxelFaces.Offsets[(int)face];

            // The two axes that span the slice. Choosing them in a fixed order keeps the mask's u and v
            // consistent with the corner table, which is what makes the winding come out right.
            int uAxis = axis == 0 ? 2 : 0;          // X faces sweep Z then Y; Y and Z faces sweep X then ...
            int vAxis = axis == 1 ? 2 : 1;          // ... Z for Y faces, Y for X and Z faces.

            for (int slice = 0; slice < Size; slice++)
            {
                BuildMask(palette, face, axis, uAxis, vAxis, slice, step);
                EmitMask(face, axis, uAxis, vAxis, slice, into);
            }
        }

        /// <summary>
        /// Fills <see cref="_mask"/> for one slice: for every position in the plane, decide whether a face
        /// is visible there and, if so, everything about how it should look.
        /// </summary>
        private void BuildMask(
            IVoxelPalette palette, BlockFace face, int axis, int uAxis, int vAxis, int slice, BlockPos step)
        {
            Array.Clear(_mask, 0, _mask.Length);

            for (int v = 0; v < Size; v++)
            {
                for (int u = 0; u < Size; u++)
                {
                    Scatter(axis, uAxis, vAxis, slice, u, v, out int x, out int y, out int z);

                    int here = PaddedIndex(x, y, z);
                    ushort block = _blocks[here];
                    if (palette.IsAir(block)) continue;

                    int nx = x + step.X, ny = y + step.Y, nz = z + step.Z;
                    ushort neighbour = _blocks[PaddedIndex(nx, ny, nz)];

                    // The visibility rule. An opaque neighbour hides this face entirely. A neighbour of the
                    // same type hides it too unless the block asks otherwise — that is what stops the inside
                    // of a body of water being drawn as a lattice of surfaces.
                    if (_opaque[PaddedIndex(nx, ny, nz)]) continue;
                    if (neighbour == block && !palette.DrawsAgainstSelf(block)) continue;

                    uint material = palette.FaceAppearance(block, face);

                    // AO and light are both sampled from the *air* side of the face: what matters is how
                    // much of the world can see this face, not what is behind it.
                    ComputeCorners(
                        nx, ny, nz, axis, uAxis, vAxis,
                        out byte a0, out byte a1, out byte a2, out byte a3,
                        out byte s0, out byte s1, out byte s2, out byte s3,
                        out byte b0, out byte b1, out byte b2, out byte b3);

                    _mask[(v * Size) + u] = new MaskEntry(material, a0, a1, a2, a3, s0, s1, s2, s3, b0, b1, b2, b3);
                }
            }
        }

        /// <summary>
        /// Sweeps the mask, merging identical entries into the largest rectangles it can and emitting a quad
        /// for each.
        /// </summary>
        private void EmitMask(BlockFace face, int axis, int uAxis, int vAxis, int slice, VoxelMeshData into)
        {
            for (int v = 0; v < Size; v++)
            {
                for (int u = 0; u < Size;)
                {
                    MaskEntry entry = _mask[(v * Size) + u];
                    if (!entry.Present)
                    {
                        u++;
                        continue;
                    }

                    // Grow right while the entries are identical.
                    int width = 1;
                    if (GreedyMerge)
                    {
                        while (u + width < Size && _mask[(v * Size) + u + width].Equals(entry)) width++;
                    }

                    // Grow down while every entry in the next row across that width is identical.
                    int height = 1;
                    if (GreedyMerge)
                    {
                        while (v + height < Size)
                        {
                            bool rowMatches = true;
                            for (int k = 0; k < width; k++)
                            {
                                if (!_mask[((v + height) * Size) + u + k].Equals(entry))
                                {
                                    rowMatches = false;
                                    break;
                                }
                            }

                            if (!rowMatches) break;
                            height++;
                        }
                    }

                    EmitQuad(face, axis, uAxis, vAxis, slice, u, v, width, height, entry, into);

                    // Claim the rectangle so it is not emitted again.
                    for (int dv = 0; dv < height; dv++)
                    {
                        for (int du = 0; du < width; du++)
                        {
                            _mask[((v + dv) * Size) + u + du] = default;
                        }
                    }

                    u += width;
                }
            }
        }

        /// <summary>Writes one merged quad as two triangles.</summary>
        private void EmitQuad(
            BlockFace face, int axis, int uAxis, int vAxis, int slice,
            int u, int v, int width, int height, MaskEntry entry, VoxelMeshData into)
        {
            Vector3 normal = VoxelFaces.Normals[(int)face];
            Vector3[] unitCorners = VoxelFaces.Corners[(int)face];

            // The unit cube's corners for this face, scaled up to the merged rectangle. Corner k of the
            // unit face sits at some (0 or 1) along u and v; multiplying those by width and height stretches
            // the quad while leaving the fixed axis alone.
            var positions = new Vector3[4];
            for (int k = 0; k < 4; k++)
            {
                Vector3 c = unitCorners[k];
                Scatter(axis, uAxis, vAxis, slice, u, v, out int bx, out int by, out int bz);

                double cu = Component(c, uAxis);
                double cv = Component(c, vAxis);
                double cFixed = Component(c, axis);

                double px = 0, py = 0, pz = 0;
                Place(ref px, ref py, ref pz, axis, cFixed);
                Place(ref px, ref py, ref pz, uAxis, cu * width);
                Place(ref px, ref py, ref pz, vAxis, cv * height);

                positions[k] = new Vector3((float)(bx + px), (float)(by + py), (float)(bz + pz));
            }

            // Corner attributes, in the same order as the corner table.
            byte[] ao = { entry.Ao0, entry.Ao1, entry.Ao2, entry.Ao3 };
            byte[] sky = { entry.Sky0, entry.Sky1, entry.Sky2, entry.Sky3 };
            byte[] blk = { entry.Block0, entry.Block1, entry.Block2, entry.Block3 };

            // Texture coordinates span the merged rectangle, so the material tiles across it instead of
            // being stretched over the whole thing. Without this, greedy meshing would visibly smear the
            // texture of every large flat surface, which is why some engines abandon merging altogether.
            var uvs = new Vector2[4];
            for (int k = 0; k < 4; k++)
            {
                Vector3 c = unitCorners[k];
                uvs[k] = new Vector2((float)(Component(c, uAxis) * width), (float)(Component(c, vAxis) * height));
            }

            uint baseIndex = (uint)into.Vertices.Count;
            for (int k = 0; k < 4; k++)
            {
                into.Vertices.Add(new VoxelVertex(
                    positions[k],
                    normal,
                    uvs[k],
                    entry.Material,
                    AmbientOcclusion ? ao[k] / 3f : 1f,
                    sky[k] / (float)VoxelLight.MaxLevel,
                    blk[k] / (float)VoxelLight.MaxLevel));
            }

            // The diagonal flip. Split along whichever diagonal joins the two corners whose occlusion is
            // most alike, so the interpolated shadow bends the way the geometry does. See the type remarks.
            bool flip = ao[0] + ao[2] > ao[1] + ao[3];

            if (flip)
            {
                into.Indices.Add(baseIndex + 1);
                into.Indices.Add(baseIndex + 2);
                into.Indices.Add(baseIndex + 3);
                into.Indices.Add(baseIndex + 1);
                into.Indices.Add(baseIndex + 3);
                into.Indices.Add(baseIndex + 0);
            }
            else
            {
                into.Indices.Add(baseIndex + 0);
                into.Indices.Add(baseIndex + 1);
                into.Indices.Add(baseIndex + 2);
                into.Indices.Add(baseIndex + 0);
                into.Indices.Add(baseIndex + 2);
                into.Indices.Add(baseIndex + 3);
            }
        }

        /// <summary>
        /// Computes the four corners' ambient occlusion and light for a face, sampling from the air block in
        /// front of it.
        /// </summary>
        /// <remarks>
        /// The AO rule is the standard one and worth stating plainly: at each corner, look at the two
        /// blocks flanking it along the face's two in-plane axes and the one diagonally between them. If
        /// both flanking blocks are opaque the corner is fully dark regardless of the diagonal — because
        /// the corner is then inside a crease no light can reach round. Otherwise the darkness is simply
        /// how many of the three are opaque.
        /// </remarks>
        private void ComputeCorners(
            int ax, int ay, int az, int axis, int uAxis, int vAxis,
            out byte a0, out byte a1, out byte a2, out byte a3,
            out byte s0, out byte s1, out byte s2, out byte s3,
            out byte b0, out byte b1, out byte b2, out byte b3)
        {
            // The unit face's corners in (u, v) order, matching VoxelFaces.Corners.
            Span<int> cu = stackalloc int[4];
            Span<int> cv = stackalloc int[4];
            CornerOffsets(axis, uAxis, vAxis, cu, cv);

            Span<byte> ao = stackalloc byte[4];
            Span<byte> sky = stackalloc byte[4];
            Span<byte> blk = stackalloc byte[4];

            for (int k = 0; k < 4; k++)
            {
                // Step to either side of the corner along the two in-plane axes. A unit corner sits at 0 or
                // 1; convert that to a step of -1 or +1 from the face's own cell.
                int du = cu[k] == 0 ? -1 : 1;
                int dv = cv[k] == 0 ? -1 : 1;

                bool sideU = OpaqueAtOffset(ax, ay, az, uAxis, du);
                bool sideV = OpaqueAtOffset(ax, ay, az, vAxis, dv);
                bool corner = OpaqueAtOffset(ax, ay, az, uAxis, du, vAxis, dv);

                ao[k] = (byte)(sideU && sideV ? 0 : 3 - ((sideU ? 1 : 0) + (sideV ? 1 : 0) + (corner ? 1 : 0)));

                // Smooth lighting: average the light of the four cells touching this corner, skipping those
                // that are opaque (they have no light to give and would drag the average to black).
                AverageLight(ax, ay, az, uAxis, du, vAxis, dv, out sky[k], out blk[k]);
            }

            a0 = ao[0]; a1 = ao[1]; a2 = ao[2]; a3 = ao[3];
            s0 = sky[0]; s1 = sky[1]; s2 = sky[2]; s3 = sky[3];
            b0 = blk[0]; b1 = blk[1]; b2 = blk[2]; b3 = blk[3];
        }

        /// <summary>The (u, v) position of each of a face's four corners on the unit square, read off the
        /// shared corner table so the two can never disagree.</summary>
        private static void CornerOffsets(int axis, int uAxis, int vAxis, Span<int> cu, Span<int> cv)
        {
            // Every face's corner table uses the same u/v pairing for a given axis, so corner k of face
            // NegativeX has the same (u, v) as corner k of PositiveX. Reading from NegativeX is enough.
            Vector3[] corners = VoxelFaces.Corners[axis * 2];
            for (int k = 0; k < 4; k++)
            {
                cu[k] = (int)Component(corners[k], uAxis);
                cv[k] = (int)Component(corners[k], vAxis);
            }
        }

        private bool OpaqueAtOffset(int x, int y, int z, int axisA, int deltaA)
        {
            Place(ref x, ref y, ref z, axisA, deltaA);
            return _opaque[PaddedIndex(x, y, z)];
        }

        private bool OpaqueAtOffset(int x, int y, int z, int axisA, int deltaA, int axisB, int deltaB)
        {
            Place(ref x, ref y, ref z, axisA, deltaA);
            Place(ref x, ref y, ref z, axisB, deltaB);
            return _opaque[PaddedIndex(x, y, z)];
        }

        private void AverageLight(
            int x, int y, int z, int axisA, int deltaA, int axisB, int deltaB, out byte sky, out byte block)
        {
            int skySum = 0, blockSum = 0, count = 0;

            for (int i = 0; i < 4; i++)
            {
                int sx = x, sy = y, sz = z;
                if ((i & 1) != 0) Place(ref sx, ref sy, ref sz, axisA, deltaA);
                if ((i & 2) != 0) Place(ref sx, ref sy, ref sz, axisB, deltaB);

                int idx = PaddedIndex(sx, sy, sz);
                if (_opaque[idx]) continue;

                byte packed = _light[idx];
                skySum += packed >> 4;
                blockSum += packed & 0x0F;
                count++;
            }

            if (count == 0)
            {
                sky = 0;
                block = 0;
                return;
            }

            sky = (byte)(skySum / count);
            block = (byte)(blockSum / count);
        }

        /// <summary>Maps a slice position back to a chunk-local coordinate.</summary>
        private static void Scatter(int axis, int uAxis, int vAxis, int slice, int u, int v, out int x, out int y, out int z)
        {
            x = 0;
            y = 0;
            z = 0;
            Place(ref x, ref y, ref z, axis, slice);
            Place(ref x, ref y, ref z, uAxis, u);
            Place(ref x, ref y, ref z, vAxis, v);
        }

        private static void Place(ref int x, ref int y, ref int z, int axis, int value)
        {
            if (axis == 0) x += value;
            else if (axis == 1) y += value;
            else z += value;
        }

        private static void Place(ref double x, ref double y, ref double z, int axis, double value)
        {
            if (axis == 0) x += value;
            else if (axis == 1) y += value;
            else z += value;
        }

        private static double Component(Vector3 v, int axis) => axis == 0 ? v.X : (axis == 1 ? v.Y : v.Z);
    }
}
