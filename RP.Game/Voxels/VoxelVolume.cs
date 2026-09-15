namespace RP.Game.Voxels
{
    using System;
    using System.Collections.Generic;
    using RP.Math;

    /// <summary>
    /// Read-only access to a voxel world. Meshing, lighting, raycasting and physics all take this rather
    /// than the concrete <see cref="VoxelVolume"/>, so any of them can be run against a test fixture, a
    /// network-replicated view, or a generated-but-not-yet-resident region.
    /// </summary>
    public interface IVoxelRead
    {
        /// <summary>The block at a world address. Unloaded or out-of-range regions read as air.</summary>
        ushort GetBlock(BlockPos position);

        /// <summary>The chunk at a chunk address, if it is resident.</summary>
        bool TryGetChunk(ChunkPos position, out VoxelChunk chunk);

        /// <summary>How the engine should treat each block id.</summary>
        IVoxelPalette Palette { get; }
    }

    /// <summary>
    /// A sparse, effectively unbounded voxel world: a dictionary of <see cref="VoxelChunk"/> keyed by chunk
    /// address, with the block-level accessors that hide the chunk boundary from everything above.
    /// </summary>
    /// <remarks>
    /// <para><b>Sparse, in all three axes.</b> Chunks exist only where something has generated or edited
    /// them, vertically as well as horizontally. That is what lets the world have deep cave systems and
    /// tall structures without paying for the empty sky between them, and it means there is no hard height
    /// limit baked into the engine — a game can put its build ceiling wherever it likes, or nowhere.</para>
    ///
    /// <para><b>The neighbour-dirtying rule.</b> Editing a block one voxel inside a chunk boundary changes
    /// what the <i>neighbouring</i> chunk's mesh should look like, because the face that was hidden against
    /// this block is now exposed. Forgetting this is the single most common voxel rendering bug, and it
    /// shows as a seam of missing geometry along chunk borders that only appears where the player has dug.
    /// <see cref="SetBlock"/> handles it: an edit within one voxel of any face marks that neighbour dirty
    /// too, and an edit in a corner marks all three.</para>
    ///
    /// <para><b>Generation on demand.</b> Supply a <see cref="Generator"/> and reading an absent chunk
    /// creates it rather than returning air. Leave it null and the world is exactly what has been put in it,
    /// which is what tests and the client half of a networked session want.</para>
    ///
    /// <para><b>Threading.</b> Reads are safe to run concurrently with other reads. Writes are not safe
    /// against anything; a caller running generation or meshing on worker threads should keep edits on one
    /// thread and publish chunks into the volume when they are complete.</para>
    /// </remarks>
    public sealed class VoxelVolume : IVoxelRead
    {
        private readonly Dictionary<ChunkPos, VoxelChunk> _chunks = new Dictionary<ChunkPos, VoxelChunk>();
        private readonly HashSet<ChunkPos> _dirty = new HashSet<ChunkPos>();

        /// <summary>Creates an empty world.</summary>
        /// <param name="palette">How the engine should interpret this world's block ids.</param>
        public VoxelVolume(IVoxelPalette palette)
        {
            Palette = palette ?? throw new ArgumentNullException(nameof(palette));
        }

        /// <inheritdoc />
        public IVoxelPalette Palette { get; }

        /// <summary>
        /// Fills a chunk that is being read for the first time. Null means absent chunks stay absent and
        /// read as air.
        /// </summary>
        public Action<VoxelChunk>? Generator { get; set; }

        /// <summary>How many chunks are currently resident.</summary>
        public int ChunkCount => _chunks.Count;

        /// <summary>Every resident chunk. The order is unspecified and changes as chunks load.</summary>
        public IEnumerable<VoxelChunk> Chunks => _chunks.Values;

        /// <summary>The chunks edited since the last <see cref="DrainDirty"/>, and so needing a re-mesh.</summary>
        public IReadOnlyCollection<ChunkPos> DirtyChunks => _dirty;

        /// <inheritdoc />
        public bool TryGetChunk(ChunkPos position, out VoxelChunk chunk) => _chunks.TryGetValue(position, out chunk!);

        /// <summary>
        /// The chunk at an address, generating it if a <see cref="Generator"/> is set and it is absent.
        /// Returns null when the chunk does not exist and nothing can create it.
        /// </summary>
        public VoxelChunk? GetChunk(ChunkPos position)
        {
            if (_chunks.TryGetValue(position, out VoxelChunk? existing)) return existing;
            if (Generator == null) return null;

            var chunk = new VoxelChunk(position);
            // Publish before generating so a generator that reads neighbouring blocks through this volume
            // cannot recurse into generating this same chunk a second time.
            _chunks[position] = chunk;
            Generator(chunk);
            return chunk;
        }

        /// <summary>The chunk at an address, creating an empty one if absent. Bypasses the generator.</summary>
        public VoxelChunk GetOrCreateChunk(ChunkPos position)
        {
            if (_chunks.TryGetValue(position, out VoxelChunk? existing)) return existing;
            var chunk = new VoxelChunk(position);
            _chunks[position] = chunk;
            return chunk;
        }

        /// <summary>Inserts or replaces a chunk — how a generator thread or a network stream publishes one.</summary>
        public void PutChunk(VoxelChunk chunk)
        {
            if (chunk == null) throw new ArgumentNullException(nameof(chunk));
            _chunks[chunk.Position] = chunk;
            MarkDirtyWithNeighbours(chunk.Position);
        }

        /// <summary>Drops a chunk from residency.</summary>
        public bool RemoveChunk(ChunkPos position)
        {
            if (!_chunks.Remove(position)) return false;
            MarkDirtyWithNeighbours(position);
            return true;
        }

        /// <inheritdoc />
        public ushort GetBlock(BlockPos position)
        {
            ChunkPos cp = ChunkPos.FromBlock(position);
            VoxelChunk? chunk = GetChunk(cp);
            if (chunk == null) return 0;

            VoxelChunk.ToLocal(position, out int lx, out int ly, out int lz);
            return chunk.GetBlock(lx, ly, lz);
        }

        /// <summary>
        /// How much daylight reaches a world position, 0 to 15.
        /// </summary>
        /// <remarks>
        /// Light has been stored per chunk since the lighting was written, and every consumer so far --
        /// the mesher, the spawner -- already had a chunk in hand. Anything reasoning about the world in
        /// world coordinates did not, and was left converting addresses by hand. Crops care whether they
        /// can see the sky; so, eventually, will anything that grows, melts or burns.
        /// </remarks>
        public byte GetSkyLight(BlockPos position)
        {
            VoxelChunk? chunk = GetChunk(ChunkPos.FromBlock(position));
            if (chunk == null) return 0;

            VoxelChunk.ToLocal(position, out int lx, out int ly, out int lz);
            return chunk.GetSkyLight(lx, ly, lz);
        }

        /// <summary>How much light from torches and glowing blocks reaches a world position, 0 to 15.</summary>
        public byte GetBlockLight(BlockPos position)
        {
            VoxelChunk? chunk = GetChunk(ChunkPos.FromBlock(position));
            if (chunk == null) return 0;

            VoxelChunk.ToLocal(position, out int lx, out int ly, out int lz);
            return chunk.GetBlockLight(lx, ly, lz);
        }

        /// <summary>The block at a world address without generating an absent chunk — absent reads as air.</summary>
        public ushort PeekBlock(BlockPos position)
        {
            if (!_chunks.TryGetValue(ChunkPos.FromBlock(position), out VoxelChunk? chunk)) return 0;
            VoxelChunk.ToLocal(position, out int lx, out int ly, out int lz);
            return chunk.GetBlock(lx, ly, lz);
        }

        /// <summary>
        /// Writes a block, creating its chunk if necessary and marking every chunk whose mesh is affected
        /// dirty. Returns whether the world actually changed.
        /// </summary>
        public bool SetBlock(BlockPos position, ushort block)
        {
            ChunkPos cp = ChunkPos.FromBlock(position);
            VoxelChunk chunk = GetChunk(cp) ?? GetOrCreateChunk(cp);

            VoxelChunk.ToLocal(position, out int lx, out int ly, out int lz);
            if (!chunk.SetBlock(lx, ly, lz, block)) return false;

            _dirty.Add(cp);
            MarkTouchedNeighbours(cp, lx, ly, lz);
            return true;
        }

        /// <summary>Marks a chunk as needing a re-mesh without changing it — for a light update, say.</summary>
        public void MarkDirty(ChunkPos position) => _dirty.Add(position);

        /// <summary>Returns the dirty set and clears it, so the caller can re-mesh exactly what changed.</summary>
        public ChunkPos[] DrainDirty()
        {
            if (_dirty.Count == 0) return Array.Empty<ChunkPos>();
            var result = new ChunkPos[_dirty.Count];
            _dirty.CopyTo(result);
            _dirty.Clear();
            return result;
        }

        /// <summary>
        /// The highest block at a column that is not air, searching down from <paramref name="startY"/>.
        /// Returns <see cref="int.MinValue"/> if the column is empty over the searched range.
        /// </summary>
        /// <remarks>
        /// The primitive behind "where does a player spawn", "where does this tree's trunk start" and
        /// "where does rain land". Bounded rather than unbounded because a sparse world has no floor to
        /// stop at, and an unbounded search down an empty column would not terminate.
        /// </remarks>
        public int HighestSolidY(int x, int z, int startY, int minY)
        {
            IVoxelPalette palette = Palette;
            for (int y = startY; y >= minY; y--)
            {
                ushort block = GetBlock(new BlockPos(x, y, z));
                if (!palette.IsAir(block)) return y;
            }

            return int.MinValue;
        }

        /// <summary>
        /// Runs an action over every block in an inclusive box. Chunk lookups are hoisted out of the inner
        /// loops, so a large region costs one dictionary probe per chunk rather than one per voxel.
        /// </summary>
        public void ForEachBlock(BlockPos min, BlockPos max, Action<BlockPos, ushort> body)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));

            int cx0 = min.X >> VoxelChunk.SizeShift, cx1 = max.X >> VoxelChunk.SizeShift;
            int cy0 = min.Y >> VoxelChunk.SizeShift, cy1 = max.Y >> VoxelChunk.SizeShift;
            int cz0 = min.Z >> VoxelChunk.SizeShift, cz1 = max.Z >> VoxelChunk.SizeShift;

            for (int cy = cy0; cy <= cy1; cy++)
            {
                for (int cz = cz0; cz <= cz1; cz++)
                {
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        var cp = new ChunkPos(cx, cy, cz);
                        VoxelChunk? chunk = GetChunk(cp);
                        if (chunk == null) continue;

                        BlockPos origin = cp.Origin();
                        int x0 = System.Math.Max(min.X, origin.X), x1 = System.Math.Min(max.X, origin.X + VoxelChunk.Size - 1);
                        int y0 = System.Math.Max(min.Y, origin.Y), y1 = System.Math.Min(max.Y, origin.Y + VoxelChunk.Size - 1);
                        int z0 = System.Math.Max(min.Z, origin.Z), z1 = System.Math.Min(max.Z, origin.Z + VoxelChunk.Size - 1);

                        for (int y = y0; y <= y1; y++)
                        {
                            for (int z = z0; z <= z1; z++)
                            {
                                for (int x = x0; x <= x1; x++)
                                {
                                    ushort block = chunk.GetBlock(x - origin.X, y - origin.Y, z - origin.Z);
                                    body(new BlockPos(x, y, z), block);
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Fills an inclusive box with one block type, returning how many voxels actually changed.
        /// </summary>
        /// <remarks>
        /// The bulk primitive every large-area tool ends up calling: an excavation charge, a machine
        /// clearing a shaft, a structure stamping itself into the terrain, a player with an upgraded tool
        /// mining a 5x5x5 volume at once. Going through <see cref="SetBlock"/> per voxel would re-derive the
        /// chunk for each and dirty the same neighbours thousands of times.
        /// </remarks>
        public int FillBox(BlockPos min, BlockPos max, ushort block)
        {
            int changed = 0;

            int cx0 = min.X >> VoxelChunk.SizeShift, cx1 = max.X >> VoxelChunk.SizeShift;
            int cy0 = min.Y >> VoxelChunk.SizeShift, cy1 = max.Y >> VoxelChunk.SizeShift;
            int cz0 = min.Z >> VoxelChunk.SizeShift, cz1 = max.Z >> VoxelChunk.SizeShift;

            for (int cy = cy0; cy <= cy1; cy++)
            {
                for (int cz = cz0; cz <= cz1; cz++)
                {
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        var cp = new ChunkPos(cx, cy, cz);
                        VoxelChunk chunk = GetChunk(cp) ?? GetOrCreateChunk(cp);
                        BlockPos origin = cp.Origin();

                        int x0 = System.Math.Max(min.X, origin.X), x1 = System.Math.Min(max.X, origin.X + VoxelChunk.Size - 1);
                        int y0 = System.Math.Max(min.Y, origin.Y), y1 = System.Math.Min(max.Y, origin.Y + VoxelChunk.Size - 1);
                        int z0 = System.Math.Max(min.Z, origin.Z), z1 = System.Math.Min(max.Z, origin.Z + VoxelChunk.Size - 1);

                        bool touched = false;
                        for (int y = y0; y <= y1; y++)
                        {
                            for (int z = z0; z <= z1; z++)
                            {
                                for (int x = x0; x <= x1; x++)
                                {
                                    if (chunk.SetBlock(x - origin.X, y - origin.Y, z - origin.Z, block))
                                    {
                                        changed++;
                                        touched = true;
                                    }
                                }
                            }
                        }

                        if (touched) MarkDirtyWithNeighbours(cp);
                    }
                }
            }

            return changed;
        }

        /// <summary>Marks a chunk and all six of its face neighbours dirty.</summary>
        private void MarkDirtyWithNeighbours(ChunkPos position)
        {
            _dirty.Add(position);
            for (int f = 0; f < VoxelFaces.Count; f++)
            {
                _dirty.Add(position.Neighbour((BlockFace)f));
            }
        }

        /// <summary>
        /// Marks only those neighbouring chunks whose meshes an edit at this local coordinate can actually
        /// affect — the ones it touches the boundary of. An edit in the middle of a chunk affects nobody
        /// else, and dirtying all six every time would triple the re-meshing work for no benefit.
        /// </summary>
        private void MarkTouchedNeighbours(ChunkPos cp, int lx, int ly, int lz)
        {
            if (lx == 0) _dirty.Add(cp.Offset(-1, 0, 0));
            else if (lx == VoxelChunk.SizeMask) _dirty.Add(cp.Offset(1, 0, 0));

            if (ly == 0) _dirty.Add(cp.Offset(0, -1, 0));
            else if (ly == VoxelChunk.SizeMask) _dirty.Add(cp.Offset(0, 1, 0));

            if (lz == 0) _dirty.Add(cp.Offset(0, 0, -1));
            else if (lz == VoxelChunk.SizeMask) _dirty.Add(cp.Offset(0, 0, 1));
        }
    }
}
