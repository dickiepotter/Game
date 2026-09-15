namespace RP.Game.Voxels
{
    using System;
    using System.Collections.Generic;
    using RP.Math;

    /// <summary>What a spawn site must satisfy.</summary>
    public readonly struct SpawnRules
    {
        /// <summary>How far from the focus a site must be, at minimum.</summary>
        public readonly double MinDistance;

        /// <summary>How far from the focus a site may be, at most.</summary>
        public readonly double MaxDistance;

        /// <summary>How many blocks of clear headroom the creature needs.</summary>
        public readonly int Height;

        /// <summary>The brightest a site may be. 16 means light is ignored.</summary>
        public readonly int MaxLight;

        /// <summary>The darkest a site may be. 0 means light is ignored.</summary>
        public readonly int MinLight;

        /// <summary>Whether the site must be out of the observer's view.</summary>
        public readonly bool MustBeUnobserved;

        /// <summary>Creates a rule set.</summary>
        public SpawnRules(
            double minDistance, double maxDistance, int height = 2,
            int minLight = 0, int maxLight = 16, bool mustBeUnobserved = true)
        {
            MinDistance = minDistance;
            MaxDistance = maxDistance;
            Height = height < 1 ? 1 : height;
            MinLight = minLight;
            MaxLight = maxLight;
            MustBeUnobserved = mustBeUnobserved;
        }
    }

    /// <summary>
    /// Finds places in the world where something could appear without anyone watching it happen.
    /// </summary>
    /// <remarks>
    /// <para><b>Unobserved is the whole point.</b> A creature that materialises in front of a player
    /// destroys the illusion that the world was already there — and it is the single most common complaint
    /// about spawning systems. Testing against the view frustum means things arrive behind the player, in
    /// the dark, or round a corner, which is where they would have walked from anyway.</para>
    ///
    /// <para><b>Sampling, not scanning.</b> The shell around a player holds millions of positions and
    /// almost all of them are solid rock or open sky. Enumerating them to pick a few is hopeless; throwing
    /// darts and testing where they land costs a fixed number of attempts per spawn whatever the volume,
    /// and the distribution is even enough that nobody can tell the difference.</para>
    ///
    /// <para><b>Light is a range, not a ceiling.</b> Hostile things want darkness and grazing things want
    /// daylight, and both are the same query with different bounds — so the rules carry a minimum as well
    /// as a maximum rather than a single threshold and a flag.</para>
    /// </remarks>
    public static class VoxelSpawner
    {
        /// <summary>
        /// Tries to find one valid spawn position.
        /// </summary>
        /// <param name="world">The world to search.</param>
        /// <param name="focus">Where the observer is.</param>
        /// <param name="viewDirection">Which way the observer is looking. Zero disables the view test.</param>
        /// <param name="fieldOfView">The observer's field of view, for deciding what counts as watched.</param>
        /// <param name="rules">What the site must satisfy.</param>
        /// <param name="random">The source of randomness. Passed in so a server and a client can agree.</param>
        /// <param name="daylight">How bright the sky currently is, for combining the two light channels.</param>
        /// <param name="found">The position, standing on the ground.</param>
        /// <param name="attempts">How many darts to throw before giving up.</param>
        public static bool TryFindSite(
            IVoxelRead world,
            Vector3d focus,
            Vector3d viewDirection,
            Angle fieldOfView,
            in SpawnRules rules,
            Random random,
            double daylight,
            out BlockPos found,
            int attempts = 24)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (random == null) throw new ArgumentNullException(nameof(random));

            found = default;
            IVoxelPalette palette = world.Palette;

            // Half the field of view, widened a little: a site just outside the frustum edge is still one
            // the player may be about to turn toward, and something appearing there is as jarring as one
            // appearing dead ahead.
            double cosLimit = System.Math.Cos((fieldOfView.Rad * 0.5) + 0.35);
            bool testView = rules.MustBeUnobserved && viewDirection.MagnitudeSquared > 1e-9;
            Vector3d look = testView ? viewDirection.Normalize() : Vector3d.Zero;

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                // A dart in the shell: a random direction, a random distance within the band, and a
                // vertical spread narrower than the horizontal one because worlds are wide and shallow.
                double angle = random.NextDouble() * System.Math.PI * 2;
                double distance = rules.MinDistance + (random.NextDouble() * (rules.MaxDistance - rules.MinDistance));
                double height = (random.NextDouble() - 0.5) * (rules.MaxDistance * 0.7);

                var candidate = new Vector3d(
                    focus.X + (System.Math.Cos(angle) * distance),
                    focus.Y + height,
                    focus.Z + (System.Math.Sin(angle) * distance));

                if (testView)
                {
                    Vector3d toCandidate = candidate - focus;
                    double lengthSquared = toCandidate.MagnitudeSquared;
                    if (lengthSquared > 1e-9)
                    {
                        double alignment = toCandidate.Normalize().DotProduct(look);
                        if (alignment > cosLimit) continue;   // in front of the observer: not allowed
                    }
                }

                if (!TryStandOn(world, palette, BlockPos.FromWorld(candidate), rules.Height, out BlockPos standing)) continue;

                // The light test uses the same combination the renderer does, so "dark" means what the
                // player sees rather than what the sky channel happens to hold at midday.
                if (!world.TryGetChunk(ChunkPos.FromBlock(standing), out VoxelChunk chunk)) continue;

                VoxelChunk.ToLocal(standing, out int lx, out int ly, out int lz);
                chunk.GetLight(lx, ly, lz, out byte sky, out byte block);
                int light = (int)System.Math.Round(VoxelLight.Combine(sky, block, daylight) * VoxelLight.MaxLevel);

                if (light > rules.MaxLight || light < rules.MinLight) continue;

                found = standing;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Finds solid footing near a position with enough clear space above it.
        /// </summary>
        /// <remarks>
        /// Searches downward from the candidate rather than requiring an exact hit, because a dart thrown
        /// into a shell almost never lands exactly on a surface — it lands in rock or in air, and the
        /// interesting position is the ground below it either way.
        /// </remarks>
        public static bool TryStandOn(
            IVoxelRead world, IVoxelPalette palette, BlockPos from, int height, out BlockPos standing, int searchDown = 24)
        {
            standing = default;

            for (int drop = 0; drop < searchDown; drop++)
            {
                var feet = new BlockPos(from.X, from.Y - drop, from.Z);
                BlockPos below = feet.Neighbour(BlockFace.NegativeY);

                if (!palette.IsSolid(world.GetBlock(below))) continue;

                // Solid ground: check there is room to stand.
                bool clear = true;
                for (int h = 0; h < height; h++)
                {
                    if (!palette.IsSolid(world.GetBlock(new BlockPos(feet.X, feet.Y + h, feet.Z)))) continue;
                    clear = false;
                    break;
                }

                if (!clear) continue;

                standing = feet;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Decides whether a position is sealed in — the test behind walls actually keeping things where they
    /// are put.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this matters more than it looks.</b> A creature that despawns when the player walks
    /// away makes every pen, cage and trap pointless, and quietly removes an entire category of play. The
    /// rule "anything that cannot get out stays loaded" is what turns a wall from decoration into a
    /// mechanic — and it is also what makes deliberate creature farming work without a special-cased
    /// "farm block" that tells the player what they were supposed to build.</para>
    ///
    /// <para><b>Bounded flood, and the bound is the answer.</b> The search walks reachable space outward
    /// and stops at a cell budget. Running out of budget <i>is</i> the "not enclosed" result: a space that
    /// large is open enough that something can wander off in it, and the alternative — flooding until the
    /// world ends — would cost more than the question is worth.</para>
    /// </remarks>
    public static class VoxelEnclosure
    {
        /// <summary>
        /// Whether a creature standing here is sealed in: no route out within the given budget.
        /// </summary>
        /// <param name="world">The world.</param>
        /// <param name="from">Where the creature is standing.</param>
        /// <param name="height">How tall it is, so it cannot squeeze through a one-block gap it could not walk.</param>
        /// <param name="maxCells">How much space counts as "enclosed". Beyond this it is loose.</param>
        /// <param name="maxRadius">How far from the start to search, whatever the cell budget allows.</param>
        public static bool IsEnclosed(
            IVoxelRead world, BlockPos from, int height = 2, int maxCells = 4096, int maxRadius = 48)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));

            IVoxelPalette palette = world.Palette;

            var seen = new HashSet<BlockPos> { from };
            var queue = new Queue<BlockPos>();
            queue.Enqueue(from);

            while (queue.Count > 0)
            {
                if (seen.Count > maxCells) return false;   // too much room to be a pen

                BlockPos current = queue.Dequeue();

                for (int f = 0; f < VoxelFaces.Count; f++)
                {
                    var face = (BlockFace)f;

                    // Upward is not a route out on its own: a creature cannot fly out of a pit, and
                    // treating it as one would call every open-topped pen unenclosed.
                    if (face == BlockFace.PositiveY) continue;

                    BlockPos next = current.Neighbour(face);
                    if (next.ChebyshevDistance(from) > maxRadius) return false;
                    if (seen.Contains(next)) continue;

                    // Standing room: the creature must fit, and have something under it or be able to fall.
                    bool fits = true;
                    for (int h = 0; h < height; h++)
                    {
                        if (!palette.IsSolid(world.GetBlock(new BlockPos(next.X, next.Y + h, next.Z)))) continue;
                        fits = false;
                        break;
                    }

                    if (!fits) continue;

                    seen.Add(next);
                    queue.Enqueue(next);
                }
            }

            return true;
        }
    }
}
