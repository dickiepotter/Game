namespace RP.Game.Voxels
{
    using System;
    using System.Collections.Generic;
    using RP.Math;

    /// <summary>One block on its way down.</summary>
    public sealed class FallingBlock
    {
        /// <summary>The block that is falling.</summary>
        public ushort Block { get; internal set; }

        /// <summary>Where it started, which is where it goes back if it cannot land.</summary>
        public BlockPos Origin { get; internal set; }

        /// <summary>Its position now. The x and z stay on the column it left.</summary>
        public Vector3d Position { get; internal set; }

        /// <summary>How fast it is falling, in blocks per second. Positive is downward.</summary>
        public double Speed { get; internal set; }

        /// <summary>How long until it lets go, in seconds. Counts down before anything moves.</summary>
        public double Delay { get; internal set; }

        /// <summary>How long it has been falling, for anything that wants to animate it.</summary>
        public double Age { get; internal set; }

        /// <summary>Whether it is still waiting to let go.</summary>
        public bool Waiting => Delay > 0.0;
    }

    /// <summary>What happened to a block that finished falling.</summary>
    public readonly struct LandedBlock
    {
        /// <summary>The block.</summary>
        public readonly ushort Block;

        /// <summary>Where it came from.</summary>
        public readonly BlockPos From;

        /// <summary>Where it ended up. Only meaningful when <see cref="Placed"/> is true.</summary>
        public readonly BlockPos To;

        /// <summary>
        /// Whether it became part of the world again, or could not and should be given to the player as an
        /// item instead.
        /// </summary>
        public readonly bool Placed;

        internal LandedBlock(ushort block, BlockPos from, BlockPos to, bool placed)
        {
            Block = block;
            From = from;
            To = to;
            Placed = placed;
        }
    }

    /// <summary>
    /// Blocks that have lost their support and are falling, moved over time rather than teleported.
    /// </summary>
    /// <remarks>
    /// <para><b>Why not just move them.</b> The structural solver can work out where every unsupported
    /// block ends up in a fraction of a millisecond, and putting them there immediately is correct and
    /// completely unreadable. A player who undermines a cliff sees the cliff become a different cliff
    /// between one frame and the next, with no way to tell what happened, whether they caused it, or
    /// whether it is still happening. The information a collapse carries is in the watching.</para>
    ///
    /// <para><b>Slow enough to read, fast enough to matter.</b> A falling block accelerates under a gravity
    /// well below the player's own, so a two-storey collapse takes something close to a second rather than
    /// three frames. That is long enough to see the shape of what is falling and to get out from under it,
    /// which is the only reason a collapse is interesting rather than merely fatal.</para>
    ///
    /// <para><b>Collapses cascade in order.</b> Each released block carries a delay proportional to its
    /// height above the lowest one, so a tower comes apart from the bottom upward rather than dissolving
    /// all at once. It costs one multiply per block and it is most of the difference between "the structure
    /// fell" and "the structure vanished".</para>
    ///
    /// <para><b>Nothing lands inside anything.</b> A block that finds its resting place occupied -- because
    /// something else landed there first, or the player filled it in while it was in the air -- is reported
    /// rather than placed, so the game can hand it over as an item. Overwriting would silently delete
    /// whatever was there.</para>
    /// </remarks>
    public sealed class VoxelFallingBlocks
    {
        private readonly List<FallingBlock> _falling = new List<FallingBlock>();
        private readonly List<FallingBlock> _finished = new List<FallingBlock>();
        private readonly List<LandedBlock> _landings = new List<LandedBlock>();
        private readonly VoxelVolume _world;

        /// <summary>Creates the system over a world.</summary>
        public VoxelFallingBlocks(VoxelVolume world)
            => _world = world ?? throw new ArgumentNullException(nameof(world));

        /// <summary>
        /// How fast a falling block accelerates, in blocks per second squared.
        /// </summary>
        /// <remarks>
        /// A third of the player's gravity, deliberately. Real debris falls at the same rate as everything
        /// else and is a blur; this is the speed at which a collapse can be watched and understood, which
        /// is the thing the feature is for.
        /// </remarks>
        public double Gravity { get; set; } = 8.0;

        /// <summary>The fastest a block falls, so a long drop stays legible all the way down.</summary>
        public double TerminalSpeed { get; set; } = 12.0;

        /// <summary>
        /// How much later each block lets go per block of height above the lowest in the same collapse.
        /// </summary>
        public double CascadeDelayPerBlock { get; set; } = 0.07;

        /// <summary>The longest a block will wait before letting go, however tall the structure.</summary>
        public double MaxCascadeDelay { get; set; } = 1.2;

        /// <summary>How many may be in the air at once, so undermining a mountain cannot stall the game.</summary>
        public int MaxFalling { get; set; } = 2048;

        /// <summary>How far a block may fall before it is given up on.</summary>
        public int MaxFallDistance { get; set; } = 256;

        /// <summary>Everything currently falling or waiting to.</summary>
        public IReadOnlyList<FallingBlock> Falling => _falling;

        /// <summary>What landed during the last <see cref="Tick"/>.</summary>
        public IReadOnlyList<LandedBlock> Landings => _landings;

        /// <summary>How many blocks are in the air.</summary>
        public int Count => _falling.Count;

        /// <summary>Whether everything has come to rest.</summary>
        public bool IsSettled => _falling.Count == 0;

        /// <summary>
        /// Takes a set of unsupported blocks out of the world and starts them falling.
        /// </summary>
        /// <remarks>
        /// The blocks are removed here rather than when they land, so the world is never momentarily both
        /// holding a block up and dropping it. The delay is assigned from the bottom of the set upward, so
        /// the bottom of a collapse goes first and what was resting on it follows.
        /// </remarks>
        /// <returns>How many blocks were released.</returns>
        public int Release(IReadOnlyList<BlockPos> positions, IVoxelPalette palette)
        {
            if (positions == null) throw new ArgumentNullException(nameof(positions));
            if (positions.Count == 0) return 0;

            int lowest = int.MaxValue;
            for (int i = 0; i < positions.Count; i++)
            {
                if (positions[i].Y < lowest) lowest = positions[i].Y;
            }

            int released = 0;
            for (int i = 0; i < positions.Count; i++)
            {
                if (_falling.Count >= MaxFalling) break;

                BlockPos position = positions[i];
                ushort block = _world.GetBlock(position);
                if (palette != null && palette.IsAir(block)) continue;

                _world.SetBlock(position, 0);

                _falling.Add(new FallingBlock
                {
                    Block = block,
                    Origin = position,
                    Position = new Vector3d(position.X, position.Y, position.Z),
                    Delay = System.Math.Min(MaxCascadeDelay, (position.Y - lowest) * CascadeDelayPerBlock),
                });

                released++;
            }

            return released;
        }

        /// <summary>Starts a single block falling, for anything that knows it should drop without asking
        /// the structural solver — sand poured into a hole, a block placed over a void.</summary>
        public bool Release(BlockPos position, IVoxelPalette palette, double delay = 0.0)
        {
            ushort block = _world.GetBlock(position);
            if (palette != null && palette.IsAir(block)) return false;
            if (_falling.Count >= MaxFalling) return false;

            _world.SetBlock(position, 0);
            _falling.Add(new FallingBlock
            {
                Block = block,
                Origin = position,
                Position = new Vector3d(position.X, position.Y, position.Z),
                Delay = delay,
            });

            return true;
        }

        /// <summary>Advances every falling block. Landings are reported in <see cref="Landings"/>.</summary>
        /// <returns>How many blocks landed.</returns>
        public int Tick(double dt)
        {
            _landings.Clear();
            if (dt <= 0.0 || _falling.Count == 0) return 0;

            IVoxelPalette palette = _world.Palette;
            _finished.Clear();

            for (int i = 0; i < _falling.Count; i++)
            {
                FallingBlock item = _falling[i];

                if (item.Delay > 0.0)
                {
                    item.Delay -= dt;
                    continue;
                }

                item.Age += dt;
                item.Speed = System.Math.Min(TerminalSpeed, item.Speed + (Gravity * dt));

                double nextY = item.Position.Y - (item.Speed * dt);

                // The cell it would occupy once it has moved. Testing the destination rather than the cell
                // it is leaving is what stops a fast block passing through a one-block floor.
                int restingY = (int)System.Math.Floor(nextY);
                int currentY = (int)System.Math.Floor(item.Position.Y);

                bool landed = false;
                for (int y = currentY - 1; y >= restingY; y--)
                {
                    var below = new BlockPos(item.Origin.X, y, item.Origin.Z);
                    if (!palette.IsSolid(_world.GetBlock(below))) continue;

                    // Rest on top of whatever stopped it.
                    Land(item, new BlockPos(item.Origin.X, y + 1, item.Origin.Z), palette);
                    _finished.Add(item);
                    landed = true;
                    break;
                }

                if (landed) continue;

                item.Position = new Vector3d(item.Position.X, nextY, item.Position.Z);

                if (item.Origin.Y - nextY > MaxFallDistance)
                {
                    // Fallen out of the world. Reported unplaced so it becomes an item rather than
                    // silently ceasing to exist.
                    _landings.Add(new LandedBlock(item.Block, item.Origin, default, placed: false));
                    _finished.Add(item);
                }
            }

            for (int i = 0; i < _finished.Count; i++) _falling.Remove(_finished[i]);
            return _landings.Count;
        }

        /// <summary>Runs until nothing is in the air — for tests, and for settling a region offscreen.</summary>
        /// <returns>How many ticks were needed.</returns>
        public int RunToSettle(double dt = 1.0 / 60.0, int maxTicks = 4096)
        {
            for (int i = 1; i <= maxTicks; i++)
            {
                if (IsSettled) return i - 1;
                Tick(dt);
            }

            return maxTicks;
        }

        /// <summary>Drops everything back where it came from, for a world reload.</summary>
        public void Clear() => _falling.Clear();

        private void Land(FallingBlock item, BlockPos at, IVoxelPalette palette)
        {
            ushort occupant = _world.GetBlock(at);

            // Air and fluid both give way; anything solid does not. A block landing in water displaces it,
            // which is the behaviour of a boulder rolled into a pond and the only one that does not leave
            // a suspiciously dry hole.
            if (palette.IsSolid(occupant))
            {
                _landings.Add(new LandedBlock(item.Block, item.Origin, at, placed: false));
                return;
            }

            _world.SetBlock(at, item.Block);
            _landings.Add(new LandedBlock(item.Block, item.Origin, at, placed: true));
        }
    }
}
