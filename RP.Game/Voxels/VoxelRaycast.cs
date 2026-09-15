namespace RP.Game.Voxels
{
    using System;
    using RP.Math;

    /// <summary>What a ray found when it was cast through a voxel world.</summary>
    public readonly struct VoxelHit
    {
        /// <summary>Whether the ray hit anything before running out of range.</summary>
        public readonly bool Hit;

        /// <summary>The block that was struck.</summary>
        public readonly BlockPos Block;

        /// <summary>The face of that block the ray entered through.</summary>
        public readonly BlockFace Face;

        /// <summary>The block id at <see cref="Block"/>.</summary>
        public readonly ushort Value;

        /// <summary>How far along the ray the hit was, in world units.</summary>
        public readonly double Distance;

        internal VoxelHit(bool hit, BlockPos block, BlockFace face, ushort value, double distance)
        {
            Hit = hit;
            Block = block;
            Face = face;
            Value = value;
            Distance = distance;
        }

        /// <summary>A miss.</summary>
        public static readonly VoxelHit Miss = default;

        /// <summary>
        /// The empty block immediately outside the face that was struck — where a placed block goes.
        /// </summary>
        /// <remarks>
        /// Deriving this from the hit face rather than from the ray's position is what makes placement feel
        /// right. Rounding the ray's own coordinate puts the new block on whichever side the floating-point
        /// arithmetic happened to land, so aiming at the top of a block sometimes buries the new one inside
        /// the old. The entry face is unambiguous: you are looking at the top, so it goes on top.
        /// </remarks>
        public BlockPos PlacementPosition => Block.Neighbour(Face);

        /// <summary>The exact world-space point where the ray crossed into the block.</summary>
        public Vector3d PointOn(Vector3d origin, Vector3d direction) => origin + (direction * Distance);
    }

    /// <summary>
    /// Walks a ray through a voxel grid, visiting every block it passes through in order, and stops at the
    /// first one that matters. This is how a player mines, places, and is told what they are looking at.
    /// </summary>
    /// <remarks>
    /// <para><b>The algorithm.</b> Amanatides and Woo's grid traversal (1987). The naive alternative —
    /// stepping along the ray in small increments and sampling — is both slower and wrong: too large a step
    /// walks straight through a block corner and misses it, and too small a step visits the same block
    /// dozens of times and still misses at grazing angles. The traversal instead tracks, for each axis, the
    /// distance to the next grid plane crossing on that axis, and always steps whichever axis is nearest.
    /// Every block the ray genuinely touches is visited, exactly once, in strict order, with no tuning
    /// parameter to get wrong.</para>
    ///
    /// <para><b>Why it also reports the face.</b> The axis that was stepped <i>is</i> the face the ray
    /// entered through, for free, as a by-product of the loop. That single fact is what block placement,
    /// face highlighting and directional mining all hang off.</para>
    ///
    /// <para><b>Cost.</b> The work is proportional to the number of blocks crossed, not to the range in
    /// world units and not to the size of the world. A 5-unit reach costs a handful of iterations, so this
    /// can run every frame for the cursor highlight without a thought.</para>
    /// </remarks>
    public static class VoxelRaycast
    {
        /// <summary>
        /// The traversal state: which block we are in, which way each axis steps, and how far along the ray
        /// the next crossing of each axis lies.
        /// </summary>
        /// <remarks>
        /// Extracted into a struct so that both <see cref="Cast"/> and <see cref="Traverse"/> drive exactly
        /// the same walk. The alternative - implementing one on top of the other - looks tidier and is a
        /// trap: a traversal built by repeatedly re-casting from a nudged origin re-reports the block it
        /// restarts inside, so every block is visited twice and the walk crawls. Being a struct it costs no
        /// allocation, which matters because the cursor cast runs every frame.
        /// </remarks>
        private struct GridWalker
        {
            public int X, Y, Z;
            public double Travelled;
            public BlockFace Face;

            private int _stepX, _stepY, _stepZ;
            private double _deltaX, _deltaY, _deltaZ;
            private double _maxX, _maxY, _maxZ;

            public BlockPos Block => new BlockPos(X, Y, Z);

            public bool Begin(Vector3d origin, Vector3d direction)
            {
                double dx = direction.X, dy = direction.Y, dz = direction.Z;
                if ((dx * dx) + (dy * dy) + (dz * dz) <= 0.0) return false;

                X = BlockPos.Floor(origin.X);
                Y = BlockPos.Floor(origin.Y);
                Z = BlockPos.Floor(origin.Z);

                _stepX = dx > 0 ? 1 : (dx < 0 ? -1 : 0);
                _stepY = dy > 0 ? 1 : (dy < 0 ? -1 : 0);
                _stepZ = dz > 0 ? 1 : (dz < 0 ? -1 : 0);

                // How far along the ray one whole block of travel is, per axis. A zero component gives
                // infinity, which correctly means "this axis never triggers a step".
                _deltaX = dx == 0.0 ? double.PositiveInfinity : System.Math.Abs(1.0 / dx);
                _deltaY = dy == 0.0 ? double.PositiveInfinity : System.Math.Abs(1.0 / dy);
                _deltaZ = dz == 0.0 ? double.PositiveInfinity : System.Math.Abs(1.0 / dz);

                _maxX = FirstCrossing(origin.X, X, _stepX, _deltaX);
                _maxY = FirstCrossing(origin.Y, Y, _stepY, _deltaY);
                _maxZ = FirstCrossing(origin.Z, Z, _stepZ, _deltaZ);

                Travelled = 0.0;

                // No face was crossed to arrive at the starting block, so report the one facing back along
                // the ray - the face a block would be placed against if the player acted from inside.
                Face = DominantFace(-dx, -dy, -dz);
                return true;
            }

            /// <summary>Advances into the next block the ray enters. Always succeeds; the caller stops on
            /// distance.</summary>
            public void Step()
            {
                // Step whichever axis reaches its next grid plane first.
                if (_maxX < _maxY && _maxX < _maxZ)
                {
                    X += _stepX;
                    Travelled = _maxX;
                    _maxX += _deltaX;

                    // We stepped +X, so we came in through the block's -X face, and vice versa.
                    Face = _stepX > 0 ? BlockFace.NegativeX : BlockFace.PositiveX;
                }
                else if (_maxY < _maxZ)
                {
                    Y += _stepY;
                    Travelled = _maxY;
                    _maxY += _deltaY;
                    Face = _stepY > 0 ? BlockFace.NegativeY : BlockFace.PositiveY;
                }
                else
                {
                    Z += _stepZ;
                    Travelled = _maxZ;
                    _maxZ += _deltaZ;
                    Face = _stepZ > 0 ? BlockFace.NegativeZ : BlockFace.PositiveZ;
                }
            }
        }

        /// <summary>
        /// Casts a ray and returns the first block for which <paramref name="predicate"/> is true.
        /// </summary>
        /// <param name="world">The world to walk.</param>
        /// <param name="origin">Where the ray starts, in world space.</param>
        /// <param name="direction">Which way it points. Need not be unit length, but
        /// <see cref="VoxelHit.Distance"/> is then measured in units of this vector's length.</param>
        /// <param name="maxDistance">How far to walk before giving up.</param>
        /// <param name="predicate">What counts as a hit. Null means "anything that is not air".</param>
        public static VoxelHit Cast(
            IVoxelRead world,
            Vector3d origin,
            Vector3d direction,
            double maxDistance,
            Func<ushort, bool>? predicate = null)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (maxDistance <= 0.0) return VoxelHit.Miss;

            var walker = default(GridWalker);
            if (!walker.Begin(origin, direction)) return VoxelHit.Miss;

            IVoxelPalette palette = world.Palette;

            // The starting block counts: a player standing inside a block should be told so.
            ushort startBlock = world.GetBlock(walker.Block);
            if (predicate == null ? !palette.IsAir(startBlock) : predicate(startBlock))
            {
                return new VoxelHit(true, walker.Block, walker.Face, startBlock, 0.0);
            }

            // A generous but finite iteration cap. The loop terminates on distance anyway; this only guards
            // against a denormal direction driving the deltas to zero and the walk never advancing.
            int guard = (int)(maxDistance * 3.0) + 32;
            while (guard-- > 0)
            {
                walker.Step();
                if (walker.Travelled > maxDistance) return VoxelHit.Miss;

                BlockPos here = walker.Block;
                ushort block = world.GetBlock(here);
                if (predicate == null ? !palette.IsAir(block) : predicate(block))
                {
                    return new VoxelHit(true, here, walker.Face, block, walker.Travelled);
                }
            }

            return VoxelHit.Miss;
        }

        /// <summary>
        /// Casts a ray and reports the first block that stops a body, ignoring blocks a body passes through
        /// such as grass and water. The cast a projectile or a movement check wants.
        /// </summary>
        public static VoxelHit CastSolid(IVoxelRead world, Vector3d origin, Vector3d direction, double maxDistance)
        {
            IVoxelPalette palette = world.Palette;
            return Cast(world, origin, direction, maxDistance, palette.IsSolid);
        }

        /// <summary>
        /// Casts a ray and reports the first block that blocks sight. An important distinction from
        /// <see cref="CastSolid"/>: glass stops a body but not a sightline, and a thicket of foliage stops a
        /// sightline but not a body.
        /// </summary>
        public static VoxelHit CastOpaque(IVoxelRead world, Vector3d origin, Vector3d direction, double maxDistance)
        {
            IVoxelPalette palette = world.Palette;
            return Cast(world, origin, direction, maxDistance, palette.IsOpaque);
        }

        /// <summary>
        /// Walks the ray and hands every block it crosses to <paramref name="visitor"/>, in order, stopping
        /// when the visitor returns false or the range runs out.
        /// </summary>
        /// <remarks>
        /// The general form. Useful where the first hit is not the answer: a piercing shot that damages
        /// everything along its path, an explosion tracing occlusion and losing energy through each block,
        /// or a survey tool that reports the ore it detects through intervening stone.
        /// </remarks>
        public static void Traverse(
            IVoxelRead world,
            Vector3d origin,
            Vector3d direction,
            double maxDistance,
            Func<BlockPos, ushort, BlockFace, double, bool> visitor)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (visitor == null) throw new ArgumentNullException(nameof(visitor));
            if (maxDistance <= 0.0) return;

            var walker = default(GridWalker);
            if (!walker.Begin(origin, direction)) return;

            if (!visitor(walker.Block, world.GetBlock(walker.Block), walker.Face, 0.0)) return;

            int guard = (int)(maxDistance * 3.0) + 32;
            while (guard-- > 0)
            {
                walker.Step();
                if (walker.Travelled > maxDistance) return;

                BlockPos here = walker.Block;
                if (!visitor(here, world.GetBlock(here), walker.Face, walker.Travelled)) return;
            }
        }

        /// <summary>
        /// The distance along the ray to the first grid plane crossing on one axis.
        /// </summary>
        /// <remarks>
        /// The asymmetry is the easy thing to get wrong. Travelling in the positive direction, the next
        /// plane is the block's far edge, so the distance is measured to <c>block + 1</c>; travelling
        /// negative, the next plane is the block's own edge, at <c>block</c>. Using one formula for both
        /// puts every backward-travelling ray a block out of step, which presents as mining the wrong block
        /// only when facing west or down.
        /// </remarks>
        private static double FirstCrossing(double position, int block, int step, double delta)
        {
            if (step == 0) return double.PositiveInfinity;
            double boundary = step > 0 ? block + 1 : block;
            return System.Math.Abs(boundary - position) * delta;
        }

        /// <summary>The face whose normal best matches a direction — used when there is no crossing to read
        /// the face from, as when the ray begins inside a block.</summary>
        private static BlockFace DominantFace(double dx, double dy, double dz)
        {
            double ax = System.Math.Abs(dx), ay = System.Math.Abs(dy), az = System.Math.Abs(dz);
            if (ax >= ay && ax >= az) return dx > 0 ? BlockFace.PositiveX : BlockFace.NegativeX;
            if (ay >= az) return dy > 0 ? BlockFace.PositiveY : BlockFace.NegativeY;
            return dz > 0 ? BlockFace.PositiveZ : BlockFace.NegativeZ;
        }
    }
}
