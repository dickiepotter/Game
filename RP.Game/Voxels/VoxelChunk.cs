namespace RP.Game.Voxels
{
    using System;

    /// <summary>
    /// One cubic block of the world: a <see cref="Size"/>-cubed array of block ids, plus the light levels
    /// computed for them. Chunks are the unit of generation, meshing, streaming, saving and network
    /// transfer — everything the world does at scale, it does one chunk at a time.
    /// </summary>
    /// <remarks>
    /// <para><b>Why 32.</b> The chunk edge is a compromise between four pressures. Too small and the
    /// per-chunk overheads dominate: every chunk is a draw call, a mesh buffer, a dictionary entry and a
    /// streaming decision. Too large and every edit re-meshes a wasteful volume, the streaming radius
    /// quantises coarsely, and a single chunk stops fitting in cache. At 32 a chunk is 32,768 voxels — 64 KB
    /// of block ids and 32 KB of light — which is a comfortable working set, and the edge is a power of two
    /// so the block-to-chunk conversions are shifts and masks rather than divisions. It also happens to
    /// match the width of the greedy mesher's bitmask row, which keeps that inner loop branch-free.</para>
    ///
    /// <para><b>The uniform fast path.</b> Most chunks in a real world contain exactly one kind of block:
    /// sky is all air, deep rock is all stone. Storing 32,768 copies of the same number for those is the
    /// single largest waste in a naive voxel engine, and there are far more of them than there are
    /// interesting chunks. A uniform chunk therefore holds no array at all — just the one id — and
    /// allocates only when something is written that differs. A view distance that would cost gigabytes
    /// dense costs a small fraction of that, and the saving grows with view distance rather than shrinking.</para>
    ///
    /// <para><b>Revision, not dirty flags.</b> Edits bump <see cref="Revision"/> rather than setting a
    /// boolean. A monotone counter lets several independent consumers — the mesher, the save system, the
    /// network replicator — each remember the revision they last saw and work out for themselves whether
    /// they are behind, without coordinating over who gets to clear the flag. With a shared boolean,
    /// whichever consumer runs first clears it and the others silently miss the edit.</para>
    ///
    /// <para><b>Threading.</b> A chunk is not internally synchronised. The intended discipline is that a
    /// chunk is written by one thread at a time and read freely once published; the volume above it owns
    /// that policy.</para>
    /// </remarks>
    public sealed class VoxelChunk
    {
        /// <summary>The power of two that <see cref="Size"/> is. Block-to-chunk conversions shift by this.</summary>
        public const int SizeShift = 5;

        /// <summary>The edge length of a chunk, in blocks.</summary>
        public const int Size = 1 << SizeShift;

        /// <summary>Mask that extracts a block's coordinate within its chunk.</summary>
        public const int SizeMask = Size - 1;

        /// <summary>The number of blocks in a chunk.</summary>
        public const int Volume = Size * Size * Size;

        // Null while the chunk is uniform; allocated on the first write that differs from _uniformBlock.
        private ushort[]? _blocks;
        private ushort _uniformBlock;

        // Light, one byte per voxel: sky level in the high nibble, block level in the low. Packing them
        // halves the memory and, more usefully, halves the cache traffic in the mesher, which reads both
        // for every vertex it emits. Also null until first needed.
        private byte[]? _light;

        /// <summary>Creates a chunk uniformly filled with one block type.</summary>
        public VoxelChunk(ChunkPos position, ushort fill = 0)
        {
            Position = position;
            _uniformBlock = fill;
        }

        /// <summary>Where this chunk sits in the world grid.</summary>
        public ChunkPos Position { get; }

        /// <summary>
        /// Increments on every change to the chunk's contents. Consumers compare it against the value they
        /// last processed to discover they are stale; see the type remarks for why this is not a flag.
        /// </summary>
        public int Revision { get; private set; }

        /// <summary>Whether the chunk is still a single repeated block and holds no array.</summary>
        public bool IsUniform => _blocks == null;

        /// <summary>The block filling a uniform chunk. Meaningless unless <see cref="IsUniform"/>.</summary>
        public ushort UniformBlock => _uniformBlock;

        /// <summary>Whether light levels have been computed and stored for this chunk.</summary>
        public bool HasLight => _light != null;

        /// <summary>Converts a within-chunk coordinate to an index into the flat arrays.</summary>
        /// <remarks>
        /// X varies fastest, then Z, then Y. That ordering is chosen for the two hottest loops: the mesher
        /// sweeps horizontal slices, and the greedy pass walks rows of constant Y and Z, so consecutive
        /// voxels in those loops are consecutive in memory. A Y-fastest layout would make every one of
        /// those reads a cache miss.
        /// </remarks>
        public static int Index(int lx, int ly, int lz) => (((ly << SizeShift) | lz) << SizeShift) | lx;

        /// <summary>Whether a within-chunk coordinate is in range.</summary>
        public static bool InBounds(int lx, int ly, int lz)
            => (uint)lx < Size && (uint)ly < Size && (uint)lz < Size;

        /// <summary>Extracts the within-chunk coordinate of a world block address.</summary>
        public static void ToLocal(BlockPos world, out int lx, out int ly, out int lz)
        {
            lx = world.X & SizeMask;
            ly = world.Y & SizeMask;
            lz = world.Z & SizeMask;
        }

        /// <summary>Reads a block by within-chunk coordinate. Out-of-range coordinates return 0 (air).</summary>
        public ushort GetBlock(int lx, int ly, int lz)
        {
            if (!InBounds(lx, ly, lz)) return 0;
            ushort[]? blocks = _blocks;
            return blocks == null ? _uniformBlock : blocks[Index(lx, ly, lz)];
        }

        /// <summary>Reads a block by flat index, with no bounds check. For hot loops that have already
        /// validated the coordinate.</summary>
        public ushort GetBlockAt(int index)
        {
            ushort[]? blocks = _blocks;
            return blocks == null ? _uniformBlock : blocks[index];
        }

        /// <summary>
        /// Writes a block by within-chunk coordinate, materialising the dense array if the chunk was
        /// uniform and the value differs. Returns whether anything actually changed.
        /// </summary>
        /// <remarks>
        /// The "did anything change" return is not a convenience. Placing a block where that block already
        /// is happens constantly — a player holding the button down, a machine re-asserting its output, a
        /// network update that arrived twice — and treating those as edits would bump the revision, force a
        /// re-mesh of the chunk and its neighbours, and dirty it for saving, all for no visible difference.
        /// </remarks>
        public bool SetBlock(int lx, int ly, int lz, ushort block)
        {
            if (!InBounds(lx, ly, lz)) return false;

            if (_blocks == null)
            {
                if (block == _uniformBlock) return false;
                Materialise();
            }

            int i = Index(lx, ly, lz);
            if (_blocks![i] == block) return false;

            _blocks[i] = block;
            Revision++;
            return true;
        }

        /// <summary>Refills the whole chunk with one block, discarding any dense array.</summary>
        public void Fill(ushort block)
        {
            _blocks = null;
            _uniformBlock = block;
            Revision++;
        }

        /// <summary>
        /// Collapses a dense chunk back to uniform if every voxel now holds the same block.
        /// </summary>
        /// <remarks>
        /// Worth doing after bulk edits — a player flattening a hillside, a machine mining out a shaft,
        /// terrain generation finishing a chunk that turned out to be solid stone after all. Without it a
        /// chunk that was touched once keeps its 64 KB array forever, and a long-running world slowly grows
        /// into the memory profile the uniform path existed to avoid.
        /// </remarks>
        public bool TryCompact()
        {
            ushort[]? blocks = _blocks;
            if (blocks == null) return true;

            ushort first = blocks[0];
            for (int i = 1; i < Volume; i++)
            {
                if (blocks[i] != first) return false;
            }

            _blocks = null;
            _uniformBlock = first;
            return true;
        }

        /// <summary>The sky-light level at a within-chunk coordinate, <c>0</c> to <see cref="VoxelLight.MaxLevel"/>.</summary>
        public byte GetSkyLight(int lx, int ly, int lz)
        {
            byte[]? light = _light;
            if (light == null || !InBounds(lx, ly, lz)) return 0;
            return (byte)(light[Index(lx, ly, lz)] >> 4);
        }

        /// <summary>The block-light level at a within-chunk coordinate.</summary>
        public byte GetBlockLight(int lx, int ly, int lz)
        {
            byte[]? light = _light;
            if (light == null || !InBounds(lx, ly, lz)) return 0;
            return (byte)(light[Index(lx, ly, lz)] & 0x0F);
        }

        /// <summary>Both light levels at once, which is what the mesher wants and saves a second index.</summary>
        public void GetLight(int lx, int ly, int lz, out byte sky, out byte block)
        {
            byte[]? light = _light;
            if (light == null || !InBounds(lx, ly, lz))
            {
                sky = 0;
                block = 0;
                return;
            }

            byte packed = light[Index(lx, ly, lz)];
            sky = (byte)(packed >> 4);
            block = (byte)(packed & 0x0F);
        }

        /// <summary>Sets the sky-light level, allocating the light array on first use.</summary>
        public void SetSkyLight(int lx, int ly, int lz, byte level)
        {
            if (!InBounds(lx, ly, lz)) return;
            byte[] light = EnsureLight();
            int i = Index(lx, ly, lz);
            light[i] = (byte)((light[i] & 0x0F) | ((level & 0x0F) << 4));
        }

        /// <summary>Sets the block-light level, allocating the light array on first use.</summary>
        public void SetBlockLight(int lx, int ly, int lz, byte level)
        {
            if (!InBounds(lx, ly, lz)) return;
            byte[] light = EnsureLight();
            int i = Index(lx, ly, lz);
            light[i] = (byte)((light[i] & 0xF0) | (level & 0x0F));
        }

        /// <summary>Sets every voxel's sky light to one level — the fast path for a chunk entirely above
        /// ground (all full daylight) or entirely buried (all dark).</summary>
        public void FillSkyLight(byte level)
        {
            byte[] light = EnsureLight();
            byte high = (byte)((level & 0x0F) << 4);
            for (int i = 0; i < Volume; i++)
            {
                light[i] = (byte)((light[i] & 0x0F) | high);
            }
        }

        /// <summary>Drops the computed light, so it will be recomputed on next use.</summary>
        public void ClearLight() => _light = null;

        /// <summary>
        /// Exposes the dense block array for bulk operations such as generation, save and network transfer.
        /// Materialises the chunk if it was uniform.
        /// </summary>
        /// <remarks>
        /// Deliberately explicit rather than a property: handing out the backing store bypasses the
        /// change tracking, so a caller that writes through it must bump <see cref="Revision"/> itself via
        /// <see cref="MarkChanged"/>. Worth the sharp edge — terrain generation fills 32,768 voxels per
        /// chunk and going through <see cref="SetBlock"/> for each would cost a bounds check, a uniformity
        /// check and a revision bump per voxel.
        /// </remarks>
        public ushort[] GetWritableBlocks()
        {
            if (_blocks == null) Materialise();
            return _blocks!;
        }

        /// <summary>Records that the chunk's contents changed, for callers writing through
        /// <see cref="GetWritableBlocks"/>.</summary>
        public void MarkChanged() => Revision++;

        private void Materialise()
        {
            var blocks = new ushort[Volume];
            if (_uniformBlock != 0)
            {
                for (int i = 0; i < Volume; i++) blocks[i] = _uniformBlock;
            }

            _blocks = blocks;
        }

        private byte[] EnsureLight() => _light ??= new byte[Volume];
    }
}
