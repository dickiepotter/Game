namespace RP.Game.Voxels
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The fluid properties of a block, asked of the game by <see cref="VoxelFluidSimulation"/>.
    /// </summary>
    /// <remarks>
    /// A third optional palette, alongside <see cref="IVoxelPalette"/> and <see cref="IVoxelStructure"/>.
    /// A game with no fluids implements none of this and pays nothing for it.
    /// </remarks>
    public interface IVoxelFluid
    {
        /// <summary>
        /// Which fluid this block is, or 0 for anything that is not a fluid. Distinct kinds never mix —
        /// they react instead, via <see cref="Reaction"/>.
        /// </summary>
        int FluidKind(ushort block);

        /// <summary>
        /// How full this block is, from 1 (a thin film about to evaporate) to <see cref="VoxelFluidSimulation.MaxLevel"/>
        /// (brim full). Meaningless for non-fluids.
        /// </summary>
        int FluidLevel(ushort block);

        /// <summary>
        /// Whether this block produces fluid indefinitely — a spring, a lava vent, the open sea. Sources are
        /// never drained by the simulation; everything else is.
        /// </summary>
        bool IsFluidSource(ushort block);

        /// <summary>The block id for a given fluid kind at a given level.</summary>
        ushort FluidBlock(int kind, int level, bool source);

        /// <summary>
        /// How many simulation ticks pass between updates of this fluid. The viscosity dial: water at 1
        /// races across a floor, lava at 6 creeps, and tar at 20 barely moves within a player's attention
        /// span — which is what makes each of them feel like a different substance rather than a recolour.
        /// </summary>
        int FluidTickDelay(int kind);

        /// <summary>
        /// How far this fluid runs horizontally from its source before it thins to nothing. Water at 7
        /// floods a room; lava at 3 makes a pool you can walk around; tar at 2 stays where it is spilled.
        /// </summary>
        int FluidMaxSpread(int kind);

        /// <summary>
        /// Mass per block relative to water, which is 1. Drives buoyancy: anything less than 1 floats,
        /// anything more sinks. Oil at 0.85 floats on water; tar at 1.2 sinks under it.
        /// </summary>
        double FluidDensity(int kind);

        /// <summary>
        /// What is produced where two different fluids meet, or 0 for "nothing, they simply refuse to mix".
        /// Water meeting lava makes rock; that single rule is the oldest emergent puzzle in the genre.
        /// </summary>
        ushort Reaction(int kindA, int kindB);

        /// <summary>
        /// Whether flowing fluid can wash this block away. True for tall grass, torches, saplings and
        /// crops — things a flood should destroy rather than politely flow around.
        /// </summary>
        bool IsWashedAway(ushort block);

        /// <summary>
        /// What this block becomes when fluid of a given kind reaches it, or the block unchanged.
        /// </summary>
        /// <remarks>
        /// The hook for "materials behave differently in air and in water": quicklime slakes, ash becomes
        /// mud, fire goes out, certain ores oxidise, a sponge saturates. It is asked for every block a
        /// fluid newly touches, so a game can build chemistry on it without the engine knowing any.
        /// </remarks>
        ushort TransformOnContact(ushort block, int fluidKind);
    }

    /// <summary>
    /// A cellular fluid simulation: water, lava, oil and tar that fall, spread, pool, drain and react.
    /// </summary>
    /// <remarks>
    /// <para><b>Levels, not volume.</b> Each fluid block carries a fullness from 1 to
    /// <see cref="MaxLevel"/>. A source is always full; everything else takes its level from the best of its
    /// neighbours, minus one. That single rule produces the whole behaviour: fluid runs downhill, thins as
    /// it spreads, pools in hollows, and drains away when its source is cut — with no volume to conserve
    /// and no per-cell state beyond the block id already stored.</para>
    ///
    /// <para><b>Why not conserve volume.</b> True volume conservation is the intuitive design and it is a
    /// trap. It needs floating-point state per cell, it makes every edit a redistribution problem, and it
    /// produces the failure everyone recognises: a puddle that never quite settles, flickering between
    /// levels forever because the arithmetic never lands exactly. Levels are integers, they converge in a
    /// bounded number of ticks, and the result is stable and predictable — which for a building game
    /// matters far more than physical accuracy.</para>
    ///
    /// <para><b>Falling is not spreading.</b> A fluid with somewhere to fall pours straight down at full
    /// strength and does <i>not</i> spread sideways on the way. That is what makes a waterfall a column
    /// rather than a cone, and it is why a one-block hole drains a flooded room completely instead of
    /// leaving a film behind.</para>
    ///
    /// <para><b>Scheduling.</b> Nothing is simulated unless something disturbed it. Cells enter the queue
    /// when a block near them changes, and leave it once they stop changing, so a world with a million
    /// blocks of ocean in it costs nothing until someone digs into the seabed. The per-tick budget bounds
    /// the worst case, so a player opening a dam cannot stall the frame — the flood simply takes a few more
    /// ticks to arrive, which is also what it should look like.</para>
    /// </remarks>
    public sealed class VoxelFluidSimulation
    {
        /// <summary>The level of a full block of fluid. Levels run 1..8.</summary>
        public const int MaxLevel = 8;

        private readonly VoxelVolume _world;
        private readonly IVoxelFluid _fluids;
        private readonly IVoxelPalette _palette;

        // Scheduled work. The set keeps the queue free of duplicates, which matters because a single edit
        // can schedule the same cell from several directions and an unbounded queue would grow without
        // limit under a steady flow.
        private readonly Queue<BlockPos> _pending = new Queue<BlockPos>();
        private readonly HashSet<BlockPos> _scheduled = new HashSet<BlockPos>();

        private long _tick;

        /// <summary>Creates a simulation over a world.</summary>
        public VoxelFluidSimulation(VoxelVolume world, IVoxelFluid fluids)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _fluids = fluids ?? throw new ArgumentNullException(nameof(fluids));
            _palette = world.Palette;
        }

        /// <summary>How many cells may be updated per tick, so a large flood cannot stall a frame.</summary>
        public int UpdateBudget { get; set; } = 2048;

        /// <summary>How many cells are waiting to be simulated.</summary>
        public int PendingCount => _pending.Count;

        /// <summary>How many cells were updated on the last tick.</summary>
        public int LastUpdateCount { get; private set; }

        /// <summary>Whether anything at all is still moving.</summary>
        public bool IsSettled => _pending.Count == 0;

        /// <summary>Queues a cell for simulation.</summary>
        public void Schedule(BlockPos position)
        {
            if (_scheduled.Add(position)) _pending.Enqueue(position);
        }

        /// <summary>
        /// Queues a cell and everything around it — what to call after any block change, since removing a
        /// wall lets the fluid behind it move and placing one may cut a flow off.
        /// </summary>
        public void ScheduleNeighbourhood(BlockPos position)
        {
            Schedule(position);
            for (int f = 0; f < VoxelFaces.Count; f++) Schedule(position.Neighbour((BlockFace)f));

            // The cell above too: it may now have nothing holding it up.
            Schedule(position.Neighbour(BlockFace.PositiveY).Neighbour(BlockFace.PositiveY));
        }

        /// <summary>Advances the simulation by one tick.</summary>
        /// <returns>How many cells changed.</returns>
        public int Tick()
        {
            _tick++;
            LastUpdateCount = 0;

            int budget = UpdateBudget;
            int deferred = 0;
            int changes = 0;

            // Snapshot the queue length so cells scheduled *by* this tick wait for the next one. Without
            // that, a fast fluid re-queues itself and the loop runs until the budget is exhausted every
            // tick, which turns "water flows quickly" into "water pegs a core".
            int todo = _pending.Count;

            for (int i = 0; i < todo && budget > 0; i++)
            {
                BlockPos position = _pending.Dequeue();
                _scheduled.Remove(position);

                int kind = _fluids.FluidKind(_world.GetBlock(position));

                // Viscosity: a cell whose fluid is not due this tick goes back in the queue untouched.
                if (kind != 0)
                {
                    int delay = System.Math.Max(1, _fluids.FluidTickDelay(kind));
                    if (_tick % delay != 0)
                    {
                        _pending.Enqueue(position);
                        _scheduled.Add(position);
                        deferred++;
                        continue;
                    }
                }

                budget--;
                if (Update(position)) changes++;
            }

            LastUpdateCount = changes;
            return changes;
        }

        /// <summary>Runs until nothing is moving or the tick limit is reached — for tests and for settling a
        /// freshly generated region before a player ever sees it.</summary>
        /// <returns>How many ticks were needed; the limit if it never settled.</returns>
        public int RunToSettle(int maxTicks = 512)
        {
            for (int i = 1; i <= maxTicks; i++)
            {
                if (IsSettled) return i - 1;
                Tick();
            }

            return maxTicks;
        }

        /// <summary>Applies the rules to one cell.</summary>
        private bool Update(BlockPos position)
        {
            ushort current = _world.GetBlock(position);
            int kind = _fluids.FluidKind(current);

            if (kind == 0)
            {
                // Not fluid, and nothing to do. Flow is deliberately *push-only*: fluid cells move fluid
                // into their neighbours, and an empty cell never pulls it in on its own initiative.
                //
                // The pull path is the obvious alternative and it silently defeats viscosity. The tick
                // gate keys off the fluid occupying the cell being updated, and an empty cell has none --
                // so every air cell beside a pool flooded on the very next tick regardless of whether it
                // was water or tar, and the viscosity dial did nothing at all except on the first block.
                return false;
            }

            // A source never drains and never changes; it only ever pushes outward.
            if (_fluids.IsFluidSource(current))
            {
                SpreadFrom(position, kind, MaxLevel);
                return false;
            }

            int level = _fluids.FluidLevel(current);

            // ---- React with any different fluid touching this one ----
            if (TryReact(position, kind)) return true;

            // ---- Let neighbouring blocks change on contact ----
            // Asked of every neighbour, not just the ones the fluid can flow into. A solid block is
            // precisely the interesting case -- ash slaking to mud, quicklime setting, a fire going out --
            // and it is exactly the one the flow path never reaches, because the fluid cannot enter it.
            TouchNeighbours(position, kind);

            // ---- Recompute this cell's level from its supply ----
            int supplied = SupplyLevel(position, kind);

            if (supplied <= 0)
            {
                // Cut off: the fluid drains away rather than sitting there forever.
                _world.SetBlock(position, 0);
                ScheduleNeighbourhood(position);
                return true;
            }

            bool changed = supplied != level;
            if (changed)
            {
                _world.SetBlock(position, _fluids.FluidBlock(kind, supplied, false));
                ScheduleNeighbourhood(position);
                level = supplied;
            }

            SpreadFrom(position, kind, level);
            return changed;
        }

        /// <summary>
        /// The level this cell should hold, given what is feeding it.
        /// </summary>
        /// <remarks>
        /// Fluid directly above supplies a full block — a falling column does not thin with height, which is
        /// why a waterfall is as deep at the bottom as at the top. Sideways neighbours supply one less than
        /// themselves, which is what makes a spreading pool taper.
        /// </remarks>
        private int SupplyLevel(BlockPos position, int kind)
        {
            ushort above = _world.GetBlock(position.Neighbour(BlockFace.PositiveY));
            if (_fluids.FluidKind(above) == kind) return MaxLevel;

            int best = 0;
            for (int f = 0; f < VoxelFaces.Count; f++)
            {
                var face = (BlockFace)f;
                if (face == BlockFace.PositiveY || face == BlockFace.NegativeY) continue;

                ushort neighbour = _world.GetBlock(position.Neighbour(face));
                if (_fluids.FluidKind(neighbour) != kind) continue;

                int level = _fluids.IsFluidSource(neighbour) ? MaxLevel : _fluids.FluidLevel(neighbour);
                if (level - 1 > best) best = level - 1;
            }

            // The thinnest this fluid gets before it stops existing. A fluid whose reach is R survives down
            // to level MaxLevel - R, which places its last wet block exactly R steps from the source --
            // comparing against the level *below* that would stop it one block short of its stated reach.
            int minLevel = MaxLevel - _fluids.FluidMaxSpread(kind);
            return best < minLevel ? 0 : best;
        }

        /// <summary>Pushes fluid out of a cell: down first, and only sideways if it cannot fall.</summary>
        private void SpreadFrom(BlockPos position, int kind, int level)
        {
            BlockPos below = position.Neighbour(BlockFace.NegativeY);
            if (CanEnter(below, kind))
            {
                // Falling fluid arrives full, whatever the level of the cell it left. This is what makes a
                // trickle at the top of a cliff a full-strength pour at the bottom.
                Place(below, kind, MaxLevel);
                return;
            }

            // Already draining downward into its own kind: still falling, so still not spreading. Without
            // this the whole height of a waterfall spreads sideways at every level, which both looks wrong
            // -- a cone of spray rather than a column -- and inflates the simulated volume enormously,
            // because each of those sideways cells then spreads and falls in turn.
            if (_fluids.FluidKind(_world.GetBlock(below)) == kind) return;

            // Standing on something. Spread outward, thinning by one.
            if (level <= 1) return;

            for (int f = 0; f < VoxelFaces.Count; f++)
            {
                var face = (BlockFace)f;
                if (face == BlockFace.PositiveY || face == BlockFace.NegativeY) continue;

                BlockPos side = position.Neighbour(face);
                if (!CanEnter(side, kind)) continue;

                int minLevel = MaxLevel - _fluids.FluidMaxSpread(kind);
                if (level - 1 < minLevel) continue;

                Place(side, kind, level - 1);
            }
        }

        /// <summary>
        /// Offers every neighbouring block the chance to change because this fluid is touching it.
        /// </summary>
        private void TouchNeighbours(BlockPos position, int kind)
        {
            for (int f = 0; f < VoxelFaces.Count; f++)
            {
                BlockPos side = position.Neighbour((BlockFace)f);
                ushort existing = _world.GetBlock(side);
                if (_palette.IsAir(existing) || _fluids.FluidKind(existing) != 0) continue;

                ushort transformed = _fluids.TransformOnContact(existing, kind);
                if (transformed == existing) continue;

                _world.SetBlock(side, transformed);
                ScheduleNeighbourhood(side);
            }
        }

        /// <summary>Whether fluid of a kind may move into a cell.</summary>
        private bool CanEnter(BlockPos position, int kind)
        {
            ushort block = _world.GetBlock(position);

            if (_palette.IsSolid(block)) return false;
            if (_palette.IsAir(block)) return true;
            if (_fluids.IsWashedAway(block)) return true;

            // Fluid of the same kind: only worth entering if we would raise its level, or the simulation
            // would rewrite the same cell forever.
            int other = _fluids.FluidKind(block);
            if (other == kind) return false;

            // A different fluid: entering means reacting, which the reaction pass handles.
            return other == 0;
        }

        /// <summary>Writes fluid into a cell, transforming whatever was there if the game says so.</summary>
        private void Place(BlockPos position, int kind, int level)
        {
            ushort existing = _world.GetBlock(position);

            // The "materials behave differently in water" hook: ask the game what this block becomes on
            // contact before drowning it.
            ushort transformed = _fluids.TransformOnContact(existing, kind);
            if (transformed != existing)
            {
                _world.SetBlock(position, transformed);
                ScheduleNeighbourhood(position);
                return;
            }

            ushort fluid = _fluids.FluidBlock(kind, level, false);
            if (existing == fluid) return;

            _world.SetBlock(position, fluid);
            ScheduleNeighbourhood(position);
        }

        /// <summary>Turns this cell to rock (or whatever the game says) if a rival fluid touches it.</summary>
        private bool TryReact(BlockPos position, int kind)
        {
            for (int f = 0; f < VoxelFaces.Count; f++)
            {
                ushort neighbour = _world.GetBlock(position.Neighbour((BlockFace)f));
                int otherKind = _fluids.FluidKind(neighbour);
                if (otherKind == 0 || otherKind == kind) continue;

                ushort product = _fluids.Reaction(kind, otherKind);
                if (product == 0) continue;

                _world.SetBlock(position, product);
                ScheduleNeighbourhood(position);
                return true;
            }

            return false;
        }
    }
}
