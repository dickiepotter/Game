namespace RP.Game.Voxels
{
    using System;
    using System.Collections.Generic;

    /// <summary>How a block participates in holding the world up.</summary>
    public enum StructuralClass : byte
    {
        /// <summary>Has no weight and gives no support — air, fluids, torches, tall grass. Ignored entirely
        /// by the support solver, so a sea does not hold up a cliff and a torch does not brace a ceiling.</summary>
        Weightless = 0,

        /// <summary>Holds itself up unconditionally and anchors everything resting on it. Bedrock, the deep
        /// rock, and the shell of anything that must never be undermined.</summary>
        Anchored = 1,

        /// <summary>Ordinary material: needs a path back to anchored ground, and can cantilever out from one
        /// as far as its cohesion allows.</summary>
        Supported = 2,

        /// <summary>Loose granular material — sand, gravel, snow, ash. Needs something <i>directly</i>
        /// beneath it; it has no cohesion at all and will not span a gap of even one block.</summary>
        Loose = 3,
    }

    /// <summary>
    /// The structural properties of a block, asked of the game by the support solver.
    /// </summary>
    /// <remarks>
    /// A second, optional palette rather than more methods on <see cref="IVoxelPalette"/>. The core palette
    /// is the five questions every voxel routine needs; structural integrity is a system a game may not want
    /// at all, and a game that does not want it should not have to answer questions about it.
    /// </remarks>
    public interface IVoxelStructure
    {
        /// <summary>How this block participates in support.</summary>
        StructuralClass StructuralClassOf(ushort block);

        /// <summary>
        /// How far this block may sit from a vertical column that reaches the ground, in blocks.
        /// </summary>
        /// <remarks>
        /// <para>This is the "horizontal stickiness" dial, and it is the number that decides what a world
        /// lets people build. Stone at 6 gives an arch or a balcony but not a floating plaza. Wood at 8
        /// gives a tree branches of believable length. Steel at 16 lets a player build a bridge that reads
        /// as engineering. Zero means the block must be directly on top of something.</para>
        /// <para>Note that the cost is only charged for <i>sideways</i> travel. Stacking straight up is
        /// free, so a cohesion of 6 still permits a tower of any height — which is what stops a
        /// well-intentioned integrity system from making the game unplayable.</para>
        /// </remarks>
        int Cohesion(ushort block);
    }

    /// <summary>
    /// Decides which blocks are still held up after the world changes, and which come down.
    /// </summary>
    /// <remarks>
    /// <para><b>The model.</b> Support is a cost, measured in sideways steps from a column that reaches the
    /// ground. Stacking vertically costs nothing — a tower is as stable as its base — while every sideways
    /// step, and every step downward onto something hanging, costs one. A block stands if that cost is
    /// within its own cohesion. Everything the system does falls out of those two rules.</para>
    ///
    /// <para><b>Worked through a tree</b>, since that is the case people test first:</para>
    /// <list type="bullet">
    ///   <item><description>The trunk rests on the ground, so it costs 0, and every trunk block stacked
    ///   above it also costs 0. A tree of any height stands.</description></item>
    ///   <item><description>A branch runs sideways from the trunk: its blocks cost 1, 2, 3 … outward. With
    ///   wood at cohesion 8, branches reach eight blocks and no further — which is a believable tree rather
    ///   than an arbitrary limit.</description></item>
    ///   <item><description><b>Cut one block out of a four-wide trunk</b> and the block above it has no
    ///   support beneath — but it is touching three neighbours that cost 0, so it costs 1, and everything
    ///   stacked above it inherits that 1 for free. Well within cohesion: the tree does not notice.</description></item>
    ///   <item><description><b>Cut the whole horizontal slice</b> and every block above is now cut off:
    ///   nothing beneath, and no sideways neighbour with a path down either, because they are all in the
    ///   same predicament. The entire crown comes down.</description></item>
    ///   <item><description>Chop a branch at its base and only the branch falls, for the same reason.</description></item>
    /// </list>
    ///
    /// <para><b>Why 0-1 BFS rather than Dijkstra.</b> Every step costs either 0 (upward) or 1 (sideways or
    /// hanging), and a shortest-path search over 0/1 weights does not need a priority queue: pushing
    /// zero-cost moves to the front of a deque and unit-cost moves to the back visits nodes in
    /// non-decreasing cost order anyway. Linear instead of n log n, which matters because this runs every
    /// time a player breaks a block.</para>
    ///
    /// <para><b>Bounded by construction.</b> The search covers a box around the disturbance and stops at a
    /// visit cap. Anything solid on the box's lower face is treated as grounded — it is held up by the
    /// world outside the search, which is exactly what it means to be standing on a mountain. That
    /// assumption is what keeps the cost constant regardless of how much world is loaded.</para>
    /// </remarks>
    public static class VoxelStructure
    {
        /// <summary>Cost meaning "no path to the ground was found at all".</summary>
        private const int Unreachable = int.MaxValue;

        /// <summary>The region a support search covers, and the cap that keeps its cost constant.</summary>
        public readonly struct SearchBounds
        {
            /// <summary>How far sideways to search.</summary>
            public readonly int HorizontalRadius;

            /// <summary>How far above the disturbance to search — tall enough to catch whatever is standing
            /// on it.</summary>
            public readonly int HeightAbove;

            /// <summary>How far below to search before assuming the world beneath holds things up.</summary>
            public readonly int DepthBelow;

            /// <summary>The visit cap. A search that hits it concludes the mass is landscape and nothing
            /// falls, which is the safe answer.</summary>
            public readonly int MaxVisits;

            /// <summary>Creates a search region.</summary>
            public SearchBounds(int horizontalRadius, int heightAbove, int depthBelow, int maxVisits)
            {
                HorizontalRadius = horizontalRadius;
                HeightAbove = heightAbove;
                DepthBelow = depthBelow;
                MaxVisits = maxVisits;
            }

            /// <summary>
            /// Creates the default search region: wide enough for a building, tall enough for a tree.
            /// </summary>
            /// <remarks>
            /// Written as an explicit parameterless constructor rather than by giving the constructor above
            /// default arguments, because on a <see langword="struct"/> those are not the same thing.
            /// <c>new SearchBounds()</c> binds to the implicit all-zeros constructor and silently ignores a
            /// constructor whose parameters are all optional — so a search built that way ran with a radius
            /// of zero and a visit cap of zero, concluded it had exhausted its budget immediately, and
            /// reported that nothing anywhere had lost support. It fails silently and in the safe direction,
            /// which is exactly why it survived until a test asked whether a floating block falls.
            /// </remarks>
            public SearchBounds()
                : this(horizontalRadius: 20, heightAbove: 48, depthBelow: 8, maxVisits: 262144)
            {
            }

            /// <summary>The default region: wide enough for a building, tall enough for a tree.</summary>
            public static SearchBounds Default => new SearchBounds(20, 48, 8, 262144);
        }

        /// <summary>
        /// Computes the support cost of every load-bearing block in a box around a disturbance.
        /// </summary>
        /// <remarks>
        /// The single implementation of the model; both <see cref="FindUnsupported"/> and
        /// <see cref="SupportCost"/> read their answers out of the map it returns, so the two can never
        /// disagree about what is holding what up.
        /// </remarks>
        /// <returns>The cost map, or null if the visit cap was hit — meaning the mass is too large to judge
        /// and should be left alone.</returns>
        public static Dictionary<BlockPos, int>? ComputeSupportCosts(
            IVoxelRead world, IVoxelStructure structure, BlockPos centre, SearchBounds bounds)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (structure == null) throw new ArgumentNullException(nameof(structure));

            var min = new BlockPos(
                centre.X - bounds.HorizontalRadius, centre.Y - bounds.DepthBelow, centre.Z - bounds.HorizontalRadius);
            var max = new BlockPos(
                centre.X + bounds.HorizontalRadius, centre.Y + bounds.HeightAbove, centre.Z + bounds.HorizontalRadius);

            var cost = new Dictionary<BlockPos, int>();
            var queue = new LinkedList<BlockPos>();

            // ---- Seed: everything grounded for free ----
            // The bottom face of the box. Anything solid there is held up by world we deliberately did not
            // look at, which is exactly what standing on a mountain means.
            for (int z = min.Z; z <= max.Z; z++)
            {
                for (int x = min.X; x <= max.X; x++)
                {
                    var p = new BlockPos(x, min.Y, z);
                    if (!Carries(structure, world.GetBlock(p))) continue;
                    cost[p] = 0;
                    queue.AddLast(p);
                }
            }

            // Anchored blocks anywhere in the box are seeds too.
            for (int y = min.Y; y <= max.Y; y++)
            {
                for (int z = min.Z; z <= max.Z; z++)
                {
                    for (int x = min.X; x <= max.X; x++)
                    {
                        var p = new BlockPos(x, y, z);
                        if (structure.StructuralClassOf(world.GetBlock(p)) != StructuralClass.Anchored) continue;
                        if (cost.ContainsKey(p)) continue;
                        cost[p] = 0;
                        queue.AddLast(p);
                    }
                }
            }

            // ---- 0-1 BFS outward from the grounded seeds ----
            int visits = 0;
            while (queue.Count > 0)
            {
                if (++visits > bounds.MaxVisits) return null;

                BlockPos current = queue.First!.Value;
                queue.RemoveFirst();
                int here = cost[current];

                for (int f = 0; f < VoxelFaces.Count; f++)
                {
                    var face = (BlockFace)f;
                    BlockPos next = current.Neighbour(face);

                    if (next.X < min.X || next.X > max.X ||
                        next.Y < min.Y || next.Y > max.Y ||
                        next.Z < min.Z || next.Z > max.Z)
                    {
                        continue;
                    }

                    ushort block = world.GetBlock(next);
                    if (!Carries(structure, block)) continue;

                    // Stacking straight up is free; sideways and hanging below both cost one. This single
                    // asymmetry is the whole model: towers stand at any height, cantilevers do not.
                    int step = face == BlockFace.PositiveY ? 0 : 1;

                    // Loose material has no cohesion at all: it inherits only the free vertical step, so a
                    // grain of sand one block sideways from support is already falling.
                    if (step != 0 && structure.StructuralClassOf(block) == StructuralClass.Loose) continue;

                    int candidate = here + step;
                    if (cost.TryGetValue(next, out int existing) && existing <= candidate) continue;

                    cost[next] = candidate;

                    // The 0-1 trick: free moves to the front, unit moves to the back, so the deque stays in
                    // non-decreasing cost order without a priority queue.
                    if (step == 0) queue.AddFirst(next);
                    else queue.AddLast(next);
                }
            }

            return cost;
        }

        /// <summary>
        /// Finds every block that loses its support when <paramref name="removed"/> is taken away.
        /// </summary>
        /// <param name="world">The world, already reflecting the removal.</param>
        /// <param name="structure">The structural properties of the game's blocks.</param>
        /// <param name="removed">Where the block was taken from.</param>
        /// <param name="into">Receives the unsupported blocks, ordered top-down so a caller applying them in
        /// order never drops a block onto one that is about to move.</param>
        /// <param name="bounds">How far to search. The default suits a building or a tree.</param>
        /// <returns>True if anything at all lost support.</returns>
        public static bool FindUnsupported(
            IVoxelRead world,
            IVoxelStructure structure,
            BlockPos removed,
            List<BlockPos> into,
            SearchBounds bounds = default)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            if (bounds.MaxVisits == 0) bounds = SearchBounds.Default;

            into.Clear();

            Dictionary<BlockPos, int>? cost = ComputeSupportCosts(world, structure, removed, bounds);
            if (cost == null) return false; // too large to judge: treat the mass as landscape

            var min = new BlockPos(
                removed.X - bounds.HorizontalRadius, removed.Y - bounds.DepthBelow, removed.Z - bounds.HorizontalRadius);
            var max = new BlockPos(
                removed.X + bounds.HorizontalRadius, removed.Y + bounds.HeightAbove, removed.Z + bounds.HorizontalRadius);

            for (int y = min.Y; y <= max.Y; y++)
            {
                for (int z = min.Z; z <= max.Z; z++)
                {
                    for (int x = min.X; x <= max.X; x++)
                    {
                        var p = new BlockPos(x, y, z);
                        ushort block = world.GetBlock(p);
                        if (!Carries(structure, block)) continue;
                        if (structure.StructuralClassOf(block) == StructuralClass.Anchored) continue;

                        int reach = cost.TryGetValue(p, out int c) ? c : Unreachable;
                        if (reach <= structure.Cohesion(block)) continue;

                        into.Add(p);
                    }
                }
            }

            if (into.Count == 0) return false;

            // Top down, so a caller applying the collapse never drops a block onto one still to move.
            into.Sort((a, b) => b.Y.CompareTo(a.Y));
            return true;
        }

        /// <summary>
        /// How many sideways steps a block sits from ground: 0 is resting on the ground or stacked directly
        /// above something that is, and <see cref="int.MaxValue"/> means it is not held up at all.
        /// </summary>
        /// <remarks>
        /// Exposed because it is useful well beyond collapse. A building tool can tint a block by how close
        /// to falling it is, and showing a player exactly why the bridge they are extending is about to end
        /// turns an opaque rule into a mechanic they can plan around.
        /// </remarks>
        public static int SupportCost(
            IVoxelRead world, IVoxelStructure structure, BlockPos position, SearchBounds bounds = default)
        {
            if (bounds.MaxVisits == 0) bounds = SearchBounds.Default;

            Dictionary<BlockPos, int>? cost = ComputeSupportCosts(world, structure, position, bounds);
            if (cost == null) return 0; // part of the landscape

            return cost.TryGetValue(position, out int c) ? c : Unreachable;
        }

        /// <summary>One block that came down, and where it ended up.</summary>
        public readonly struct FallEvent
        {
            /// <summary>Where the block was.</summary>
            public readonly BlockPos From;

            /// <summary>Where it came to rest.</summary>
            public readonly BlockPos To;

            /// <summary>Which block moved.</summary>
            public readonly ushort Block;

            internal FallEvent(BlockPos from, BlockPos to, ushort block)
            {
                From = from;
                To = to;
                Block = block;
            }

            /// <summary>How far it fell, in blocks.</summary>
            public int Distance => From.Y - To.Y;

            /// <summary>Whether it fell out of the world rather than landing on anything.</summary>
            public bool Destroyed => To.Y == int.MinValue;
        }

        /// <summary>
        /// Drops every block that has lost its support to wherever it comes to rest.
        /// </summary>
        /// <remarks>
        /// <para>Blocks settle straight down and are applied top-down, so a collapsing column lands as a
        /// column rather than the top block overtaking the one beneath it. A block with nothing below it
        /// within <paramref name="maxFall"/> is destroyed instead of falling forever, which is what stops a
        /// tree felled over a chasm hanging in the queue for the rest of the session.</para>
        /// <para>The fall events are reported rather than merely applied, so a game can spawn a visible
        /// falling block, play the right impact sound for the distance, and damage whatever it landed on —
        /// none of which the engine should be deciding.</para>
        /// </remarks>
        /// <returns>How many blocks moved.</returns>
        public static int Collapse(
            VoxelVolume world,
            IVoxelStructure structure,
            IReadOnlyList<BlockPos> unsupported,
            List<FallEvent> into,
            int maxFall = 256)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (structure == null) throw new ArgumentNullException(nameof(structure));
            if (unsupported == null) throw new ArgumentNullException(nameof(unsupported));

            into?.Clear();
            IVoxelPalette palette = world.Palette;
            int moved = 0;

            // FindUnsupported already ordered these top-down; re-sorting here keeps Collapse correct even
            // when handed a list from somewhere else.
            var ordered = new List<BlockPos>(unsupported);
            ordered.Sort((a, b) => b.Y.CompareTo(a.Y));

            foreach (BlockPos from in ordered)
            {
                ushort block = world.GetBlock(from);
                if (palette.IsAir(block)) continue;

                // Find the resting place: the last empty cell before something solid.
                int restY = from.Y;
                for (int step = 1; step <= maxFall; step++)
                {
                    var candidate = new BlockPos(from.X, from.Y - step, from.Z);
                    ushort under = world.GetBlock(candidate);

                    // Fluid does not hold a falling block up -- a boulder sinks through a pond. Solid does.
                    if (palette.IsSolid(under)) break;
                    restY = candidate.Y;
                }

                if (restY == from.Y) continue; // already resting on something

                world.SetBlock(from, 0);

                if (from.Y - restY >= maxFall)
                {
                    into?.Add(new FallEvent(from, new BlockPos(from.X, int.MinValue, from.Z), block));
                    moved++;
                    continue;
                }

                var to = new BlockPos(from.X, restY, from.Z);
                world.SetBlock(to, block);
                into?.Add(new FallEvent(from, to, block));
                moved++;
            }

            return moved;
        }

        /// <summary>
        /// The whole response to a block being removed: work out what lost support, drop it, and repeat
        /// until the world stops moving.
        /// </summary>
        /// <remarks>
        /// Iterated because one collapse causes the next: the crown of a felled tree lands on a shed roof,
        /// the roof gives way, and what it was holding follows. Bounded by <paramref name="maxRounds"/> so a
        /// pathological structure cannot collapse forever — and if it hits the bound, what remains is simply
        /// left standing, which is far better than a hang.
        /// </remarks>
        /// <returns>How many blocks moved in total.</returns>
        public static int SettleAfterRemoval(
            VoxelVolume world,
            IVoxelStructure structure,
            BlockPos removed,
            List<FallEvent>? into = null,
            SearchBounds bounds = default,
            int maxRounds = 8)
        {
            var unsupported = new List<BlockPos>();
            var round = new List<FallEvent>();
            int total = 0;

            into?.Clear();

            BlockPos focus = removed;
            for (int i = 0; i < maxRounds; i++)
            {
                if (!FindUnsupported(world, structure, focus, unsupported, bounds)) break;

                int moved = Collapse(world, structure, unsupported, round);
                if (moved == 0) break;

                total += moved;
                if (into != null) into.AddRange(round);

                // Re-examine around where the debris landed, not where it started.
                int lowest = int.MaxValue;
                foreach (FallEvent fall in round)
                {
                    if (!fall.Destroyed && fall.To.Y < lowest) lowest = fall.To.Y;
                }

                if (lowest == int.MaxValue) break;
                focus = new BlockPos(focus.X, lowest, focus.Z);
            }

            return total;
        }

        /// <summary>Whether a block has weight and passes load — the blocks the solver reasons about.</summary>
        private static bool Carries(IVoxelStructure structure, ushort block)
        {
            StructuralClass kind = structure.StructuralClassOf(block);
            return kind != StructuralClass.Weightless;
        }
    }
}
