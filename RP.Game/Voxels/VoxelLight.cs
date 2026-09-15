namespace RP.Game.Voxels
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Propagates light through a voxel world: daylight falling from the sky and light emitted by blocks,
    /// each stored per voxel so the mesher can bake it into vertex colours.
    /// </summary>
    /// <remarks>
    /// <para><b>Why light is stored, not computed.</b> Real-time lighting for a world of millions of blocks
    /// is not affordable per frame, and it is not what a voxel world wants anyway. Instead each voxel keeps
    /// two small numbers — how much sky reaches it and how much block-light reaches it — updated only when
    /// the world changes. The renderer then reads them for free. The whole system is a flood fill, and its
    /// cost is proportional to the volume actually affected by an edit, not to the size of the world.</para>
    ///
    /// <para><b>Two channels, not one.</b> Sky light and block light are propagated separately and combined
    /// only at display time, because they behave differently: sky light is scaled by the time of day, so a
    /// cave lit by a torch stays lit at midnight while a sunlit field goes dark. Merging them into one
    /// number at propagation time makes that impossible, and every attempt to bolt it back on afterwards
    /// produces torches that dim at night.</para>
    ///
    /// <para><b>The vertical shortcut.</b> Sky light does not attenuate going straight down through open
    /// air — direct sunlight reaches the ground at full strength however far it falls. Only once it must
    /// spread sideways, under an overhang or into a cave mouth, does it lose a level per step. That
    /// asymmetry is why a deep vertical shaft stays bright all the way down while a horizontal tunnel of
    /// the same length fades to black, and it is what makes caves feel like caves.</para>
    ///
    /// <para><b>Removal is not addition run backwards.</b> Taking a light source away is the harder half:
    /// every voxel it lit must be found and re-darkened, but only where <i>that</i> source was the reason
    /// it was lit. The two-pass algorithm below handles it — first a removal fill that clears the affected
    /// region and collects the surviving lights on its border, then a normal fill re-propagating from
    /// those. Skipping the second pass leaves a permanent dark scar where the light used to be.</para>
    /// </remarks>
    public static class VoxelLight
    {
        /// <summary>The brightest a voxel can be. Four bits per channel, so 15.</summary>
        public const byte MaxLevel = 15;

        /// <summary>One voxel being considered by a flood fill.</summary>
        private readonly struct Node
        {
            public readonly BlockPos Position;
            public readonly byte Level;

            public Node(BlockPos position, byte level)
            {
                Position = position;
                Level = level;
            }
        }

        /// <summary>
        /// Recomputes both light channels for a box of the world from scratch.
        /// </summary>
        /// <remarks>
        /// The bulk entry point, for a region that has just been generated. Working a whole box at once is
        /// much cheaper than lighting chunk by chunk, because light crosses chunk boundaries constantly and
        /// a per-chunk pass would have to revisit each boundary repeatedly as its neighbours changed.
        /// </remarks>
        /// <param name="world">The world to light.</param>
        /// <param name="min">Inclusive minimum corner.</param>
        /// <param name="max">Inclusive maximum corner.</param>
        /// <param name="skyTop">The Y above which the sky is unobstructed, so daylight enters at full
        /// strength. Put this above the tallest terrain in the region.</param>
        public static void RelightBox(VoxelVolume world, BlockPos min, BlockPos max, int skyTop)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));

            IVoxelPalette palette = world.Palette;

            // Clear what is there, so a relight cannot inherit stale values from a previous topology.
            for (int y = min.Y; y <= max.Y; y++)
            {
                for (int z = min.Z; z <= max.Z; z++)
                {
                    for (int x = min.X; x <= max.X; x++)
                    {
                        SetSky(world, new BlockPos(x, y, z), 0);
                        SetBlockLight(world, new BlockPos(x, y, z), 0);
                    }
                }
            }

            var skyQueue = new Queue<Node>();
            var blockQueue = new Queue<Node>();

            // Sky: drop a full-strength column down every (x, z) until something opaque stops it. Each
            // voxel the column reaches is a seed for the sideways spread that follows.
            for (int z = min.Z; z <= max.Z; z++)
            {
                for (int x = min.X; x <= max.X; x++)
                {
                    byte level = MaxLevel;
                    int top = System.Math.Min(skyTop, max.Y);

                    for (int y = top; y >= min.Y; y--)
                    {
                        var p = new BlockPos(x, y, z);
                        ushort block = world.GetBlock(p);

                        if (palette.IsOpaque(block))
                        {
                            level = 0;
                            continue;
                        }

                        if (level == MaxLevel)
                        {
                            // Still in direct sun: translucent blocks such as water dim the column, but
                            // open air does not.
                            byte attenuation = palette.IsAir(block) ? (byte)0 : (byte)(palette.LightAttenuation(block) - 1);
                            level = attenuation >= level ? (byte)0 : (byte)(level - attenuation);
                        }

                        if (level > 0)
                        {
                            SetSky(world, p, level);
                            skyQueue.Enqueue(new Node(p, level));
                        }
                    }
                }
            }

            // Block light: every emitter in the box seeds the fill at its own brightness.
            for (int y = min.Y; y <= max.Y; y++)
            {
                for (int z = min.Z; z <= max.Z; z++)
                {
                    for (int x = min.X; x <= max.X; x++)
                    {
                        var p = new BlockPos(x, y, z);
                        byte emission = palette.LightEmission(world.GetBlock(p));
                        if (emission == 0) continue;

                        SetBlockLight(world, p, emission);
                        blockQueue.Enqueue(new Node(p, emission));
                    }
                }
            }

            Spread(world, skyQueue, min, max, sky: true);
            Spread(world, blockQueue, min, max, sky: false);

            MarkRegionDirty(world, min, max);
        }

        /// <summary>
        /// Updates lighting after a single block changed, touching only the volume the change can reach.
        /// </summary>
        /// <remarks>
        /// The interactive entry point, and the reason the whole system is affordable: placing a torch or
        /// breaking a wall re-lights at most a sphere of radius <see cref="MaxLevel"/>, a few thousand
        /// voxels, rather than the region. Both channels are handled, because a single edit can change
        /// both — walling over a window removes sky light, and the wall may itself glow.
        /// </remarks>
        public static void UpdateAfterEdit(VoxelVolume world, BlockPos position, ushort previousBlock, int skyTop)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));

            IVoxelPalette palette = world.Palette;
            ushort nowBlock = world.GetBlock(position);

            // The affected volume: light travels at most MaxLevel steps, so nothing beyond that can change.
            // Sky light is the exception — it can fall an unbounded distance straight down — so the box
            // extends to the top of the sky and far enough below to catch a newly-opened shaft.
            var min = new BlockPos(position.X - MaxLevel, position.Y - MaxLevel, position.Z - MaxLevel);
            var max = new BlockPos(position.X + MaxLevel, System.Math.Max(position.Y + MaxLevel, skyTop), position.Z + MaxLevel);

            bool wasOpaque = palette.IsOpaque(previousBlock);
            bool isOpaque = palette.IsOpaque(nowBlock);
            byte wasEmission = palette.LightEmission(previousBlock);
            byte nowEmission = palette.LightEmission(nowBlock);

            if (wasOpaque == isOpaque && wasEmission == nowEmission) return;

            // A local relight of the affected box is simpler than a targeted removal fill and, at this
            // scale, comparable in cost: the box is a few thousand voxels either way, and correctness is
            // not in question. The targeted path below matters only for very large emitters.
            RelightBox(world, min, max, skyTop);
        }

        /// <summary>
        /// Removes a light source and re-darkens exactly what it lit, then re-propagates the neighbours
        /// that survive — the two-pass removal described in the type remarks.
        /// </summary>
        /// <remarks>
        /// Kept as a public operation in its own right because removal is the half people implement wrongly,
        /// and a game with large or moving emitters wants it directly rather than through a box relight.
        /// </remarks>
        public static void RemoveBlockLight(VoxelVolume world, BlockPos position)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));

            byte existing = GetBlockLight(world, position);
            if (existing == 0) return;

            IVoxelPalette palette = world.Palette;
            var removal = new Queue<Node>();
            var refill = new Queue<Node>();

            SetBlockLight(world, position, 0);
            removal.Enqueue(new Node(position, existing));

            while (removal.Count > 0)
            {
                Node node = removal.Dequeue();

                for (int f = 0; f < VoxelFaces.Count; f++)
                {
                    BlockPos next = node.Position.Neighbour((BlockFace)f);
                    byte level = GetBlockLight(world, next);
                    if (level == 0) continue;

                    if (level < node.Level)
                    {
                        // This voxel was lit *by* the removed source: darken it and keep going.
                        SetBlockLight(world, next, 0);
                        removal.Enqueue(new Node(next, level));
                    }
                    else
                    {
                        // Brighter than the source was at this distance, so it has a light of its own.
                        // It becomes a seed for the refill that repairs the hole.
                        refill.Enqueue(new Node(next, level));
                    }
                }
            }

            // Any emitter caught inside the cleared region must be re-seeded too.
            ushort here = world.GetBlock(position);
            byte emission = palette.LightEmission(here);
            if (emission > 0)
            {
                SetBlockLight(world, position, emission);
                refill.Enqueue(new Node(position, emission));
            }

            var unbounded = new BlockPos(int.MinValue / 4, int.MinValue / 4, int.MinValue / 4);
            var unboundedMax = new BlockPos(int.MaxValue / 4, int.MaxValue / 4, int.MaxValue / 4);
            Spread(world, refill, unbounded, unboundedMax, sky: false);
        }

        /// <summary>
        /// The breadth-first flood that both channels share: take the brightest voxels first, push light
        /// into every neighbour that is darker than what this voxel can give it, and stop where the level
        /// reaches zero.
        /// </summary>
        /// <remarks>
        /// Breadth-first rather than depth-first is what makes this linear. Because the queue is drained in
        /// order of decreasing brightness, each voxel is written at most once at its final value; a
        /// depth-first walk would reach many voxels by a dim path first and then have to revisit them when
        /// a brighter path arrived.
        /// </remarks>
        private static void Spread(VoxelVolume world, Queue<Node> queue, BlockPos min, BlockPos max, bool sky)
        {
            IVoxelPalette palette = world.Palette;

            while (queue.Count > 0)
            {
                Node node = queue.Dequeue();
                if (node.Level <= 1) continue;

                for (int f = 0; f < VoxelFaces.Count; f++)
                {
                    var face = (BlockFace)f;
                    BlockPos next = node.Position.Neighbour(face);

                    if (next.X < min.X || next.X > max.X ||
                        next.Y < min.Y || next.Y > max.Y ||
                        next.Z < min.Z || next.Z > max.Z)
                    {
                        continue;
                    }

                    ushort block = world.GetBlock(next);
                    if (palette.IsOpaque(block)) continue;

                    byte cost = palette.IsAir(block) ? (byte)1 : palette.LightAttenuation(block);
                    if (cost < 1) cost = 1;
                    if (cost >= node.Level) continue;

                    var level = (byte)(node.Level - cost);
                    byte current = sky ? GetSky(world, next) : GetBlockLight(world, next);
                    if (current >= level) continue;

                    if (sky) SetSky(world, next, level);
                    else SetBlockLight(world, next, level);

                    queue.Enqueue(new Node(next, level));
                }
            }
        }

        /// <summary>The combined brightness a renderer should use, given the time of day.</summary>
        /// <param name="sky">Sky-light level at the voxel.</param>
        /// <param name="block">Block-light level at the voxel.</param>
        /// <param name="daylight">How much the sky contributes right now, <c>0</c> at night to <c>1</c> at
        /// noon.</param>
        /// <returns>Brightness in <c>[0, 1]</c>.</returns>
        /// <remarks>
        /// The maximum, not the sum. Two level-8 sources do not make daylight, and adding them would make a
        /// torch-lit room brighten as the sun rises outside a wall it cannot see through. Taking the
        /// stronger of the two keeps each channel meaning what it says.
        /// </remarks>
        public static double Combine(byte sky, byte block, double daylight)
        {
            double skyPart = sky / (double)MaxLevel * daylight;
            double blockPart = block / (double)MaxLevel;
            return skyPart > blockPart ? skyPart : blockPart;
        }

        private static byte GetSky(VoxelVolume world, BlockPos p)
        {
            if (!world.TryGetChunk(ChunkPos.FromBlock(p), out VoxelChunk chunk)) return 0;
            VoxelChunk.ToLocal(p, out int lx, out int ly, out int lz);
            return chunk.GetSkyLight(lx, ly, lz);
        }

        private static byte GetBlockLight(VoxelVolume world, BlockPos p)
        {
            if (!world.TryGetChunk(ChunkPos.FromBlock(p), out VoxelChunk chunk)) return 0;
            VoxelChunk.ToLocal(p, out int lx, out int ly, out int lz);
            return chunk.GetBlockLight(lx, ly, lz);
        }

        private static void SetSky(VoxelVolume world, BlockPos p, byte level)
        {
            ChunkPos cp = ChunkPos.FromBlock(p);
            VoxelChunk? chunk = world.GetChunk(cp);
            if (chunk == null) return;
            VoxelChunk.ToLocal(p, out int lx, out int ly, out int lz);
            chunk.SetSkyLight(lx, ly, lz, level);
        }

        private static void SetBlockLight(VoxelVolume world, BlockPos p, byte level)
        {
            ChunkPos cp = ChunkPos.FromBlock(p);
            VoxelChunk? chunk = world.GetChunk(cp);
            if (chunk == null) return;
            VoxelChunk.ToLocal(p, out int lx, out int ly, out int lz);
            chunk.SetBlockLight(lx, ly, lz, level);
        }

        private static void MarkRegionDirty(VoxelVolume world, BlockPos min, BlockPos max)
        {
            int cx0 = min.X >> VoxelChunk.SizeShift, cx1 = max.X >> VoxelChunk.SizeShift;
            int cy0 = min.Y >> VoxelChunk.SizeShift, cy1 = max.Y >> VoxelChunk.SizeShift;
            int cz0 = min.Z >> VoxelChunk.SizeShift, cz1 = max.Z >> VoxelChunk.SizeShift;

            for (int cy = cy0; cy <= cy1; cy++)
            {
                for (int cz = cz0; cz <= cz1; cz++)
                {
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        world.MarkDirty(new ChunkPos(cx, cy, cz));
                    }
                }
            }
        }
    }
}
