namespace RP.Game.Voxels
{
    using System;
    using System.Collections.Generic;
    using RP.Math;

    /// <summary>
    /// Keeps the world meshed around a moving focus: decides which chunks should be drawable, builds them
    /// nearest-first within a per-frame budget, and drops the ones that fall out of range.
    /// </summary>
    /// <remarks>
    /// <para><b>The budget is the whole design.</b> Generating and meshing a chunk costs single-digit
    /// milliseconds. A player walking forward crosses a chunk boundary every few seconds and needs a whole
    /// new column of them, and doing that work the moment it is discovered produces exactly the stutter
    /// every voxel game is known for. Capping the work per frame turns a 200 ms hitch into forty frames
    /// that each do a little — the world fills in slightly behind the player instead of the game stopping
    /// while it catches up.</para>
    ///
    /// <para><b>Nearest first.</b> The pending set is drained in order of distance from the focus, so the
    /// budget is always spent on what the player is closest to. Without it a queue in insertion order will
    /// cheerfully spend a frame meshing something behind the player while the ground ahead is still missing.</para>
    ///
    /// <para><b>Hysteresis on unload.</b> A chunk loads inside the load radius but is only dropped once it
    /// passes a larger unload radius. A player loitering exactly on a boundary would otherwise page the same
    /// chunk in and out every frame, which is the most expensive thing they could possibly be doing.</para>
    ///
    /// <para><b>Vertical is its own radius.</b> Voxel worlds are far wider than they are tall, and the
    /// interesting vertical extent around a player is small — a few chunks up for the sky and a few down for
    /// caves. Using one radius for all three axes wastes most of the budget on chunks of solid rock and
    /// empty air that mesh to nothing.</para>
    ///
    /// <para><b>Single-threaded, by choice for now.</b> Meshing is pure and takes an <see cref="IVoxelRead"/>,
    /// so moving it to a worker pool is a contained change; the budget exists so that the single-threaded
    /// version is already playable, and so that the threaded version has something to compare against.</para>
    /// </remarks>
    public sealed class VoxelStreamer
    {
        private readonly VoxelVolume _world;
        private readonly VoxelMesher _mesher = new VoxelMesher();
        private readonly VoxelMeshData _scratch = new VoxelMeshData();

        // What is currently drawable, and what still needs building.
        private readonly HashSet<ChunkPos> _resident = new HashSet<ChunkPos>();
        private readonly HashSet<ChunkPos> _pending = new HashSet<ChunkPos>();
        private readonly List<ChunkPos> _sortQueue = new List<ChunkPos>();
        private readonly List<ChunkPos> _toDrop = new List<ChunkPos>();

        private ChunkPos _lastFocus;
        private bool _hasFocus;
        private int _horizontalRadius;
        private int _verticalRadius;

        /// <summary>Creates a streamer over a world.</summary>
        /// <param name="world">The world to stream. Its generator fills chunks on first touch.</param>
        /// <param name="horizontalRadius">How many chunks out, sideways, to keep drawable.</param>
        /// <param name="verticalRadius">How many chunks up and down.</param>
        public VoxelStreamer(VoxelVolume world, int horizontalRadius = 8, int verticalRadius = 4)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _horizontalRadius = horizontalRadius;
            _verticalRadius = verticalRadius;
        }

        /// <summary>How many chunks out, sideways, are kept drawable.</summary>
        /// <remarks>
        /// Changing a radius invalidates the cached focus, so the next <see cref="Update"/> recomputes what
        /// should be resident. Without that, the desired set is only rebuilt when the player crosses a chunk
        /// boundary — and a game that primes a small radius at start-up and then opens it out would sit at
        /// the small one until the player happened to walk far enough to trigger a refresh.
        /// </remarks>
        public int HorizontalRadius
        {
            get => _horizontalRadius;
            set
            {
                if (_horizontalRadius == value) return;
                _horizontalRadius = value;
                _hasFocus = false;
            }
        }

        /// <summary>How many chunks up and down are kept drawable.</summary>
        /// <remarks>Changing it invalidates the cached focus; see <see cref="HorizontalRadius"/>.</remarks>
        public int VerticalRadius
        {
            get => _verticalRadius;
            set
            {
                if (_verticalRadius == value) return;
                _verticalRadius = value;
                _hasFocus = false;
            }
        }

        /// <summary>How many chunks may be built per call to <see cref="Update"/>.</summary>
        public int BuildBudget { get; set; } = 4;

        /// <summary>How far past the load radius a chunk must go before it is dropped, in chunks.</summary>
        public int UnloadMargin { get; set; } = 2;

        /// <summary>How many chunks are currently drawable.</summary>
        public int ResidentCount => _resident.Count;

        /// <summary>How many chunks are known to need building.</summary>
        public int PendingCount => _pending.Count;

        /// <summary>How many chunks were built on the last update.</summary>
        public int LastBuiltCount { get; private set; }

        /// <summary>Whether everything in range has been built.</summary>
        public bool IsComplete => _pending.Count == 0;

        /// <summary>
        /// Brings the streamed set up to date around a focus point.
        /// </summary>
        /// <param name="focus">Where the player is, in world space.</param>
        /// <param name="upload">Called with each freshly built mesh. The mesh is reused between calls, so
        /// the callback must copy anything it needs to keep.</param>
        /// <param name="drop">Called for each chunk that has left the view.</param>
        public void Update(Vector3d focus, Action<ChunkPos, VoxelMeshData> upload, Action<ChunkPos> drop)
        {
            if (upload == null) throw new ArgumentNullException(nameof(upload));
            if (drop == null) throw new ArgumentNullException(nameof(drop));

            ChunkPos centre = ChunkPos.FromWorld(focus);

            // Only recompute the desired set when the player actually changes chunk. Between crossings the
            // set cannot have changed, and rebuilding it every frame is thousands of hash lookups for
            // nothing.
            if (!_hasFocus || !centre.Equals(_lastFocus))
            {
                _lastFocus = centre;
                _hasFocus = true;
                RefreshDesiredSet(centre, drop);
            }

            // Edits dirty chunks directly; fold those into the pending set wherever they are still in range.
            foreach (ChunkPos dirty in _world.DrainDirty())
            {
                if (InRange(dirty, centre, HorizontalRadius, VerticalRadius)) _pending.Add(dirty);
            }

            BuildSome(centre, upload);
        }

        /// <summary>Forgets everything, so the next update rebuilds from scratch — for a teleport or a
        /// world reload.</summary>
        public void Reset()
        {
            _resident.Clear();
            _pending.Clear();
            _hasFocus = false;
        }

        /// <summary>Works out what should be drawable and what has left the view.</summary>
        private void RefreshDesiredSet(ChunkPos centre, Action<ChunkPos> drop)
        {
            for (int dy = -VerticalRadius; dy <= VerticalRadius; dy++)
            {
                for (int dz = -HorizontalRadius; dz <= HorizontalRadius; dz++)
                {
                    for (int dx = -HorizontalRadius; dx <= HorizontalRadius; dx++)
                    {
                        ChunkPos position = centre.Offset(dx, dy, dz);
                        if (_resident.Contains(position)) continue;
                        _pending.Add(position);
                    }
                }
            }

            // Hysteresis: only drop once a chunk is well outside, not the instant it leaves.
            _toDrop.Clear();
            foreach (ChunkPos position in _resident)
            {
                if (!InRange(position, centre, HorizontalRadius + UnloadMargin, VerticalRadius + UnloadMargin))
                {
                    _toDrop.Add(position);
                }
            }

            foreach (ChunkPos position in _toDrop)
            {
                _resident.Remove(position);
                drop(position);
            }

            _toDrop.Clear();
            foreach (ChunkPos position in _pending)
            {
                if (!InRange(position, centre, HorizontalRadius + UnloadMargin, VerticalRadius + UnloadMargin))
                {
                    _toDrop.Add(position);
                }
            }

            foreach (ChunkPos position in _toDrop) _pending.Remove(position);
        }

        /// <summary>Builds up to the budget, nearest to the focus first.</summary>
        private void BuildSome(ChunkPos centre, Action<ChunkPos, VoxelMeshData> upload)
        {
            LastBuiltCount = 0;
            if (_pending.Count == 0) return;

            _sortQueue.Clear();
            _sortQueue.AddRange(_pending);

            // Squared chunk distance is enough to order by, and avoids a square root per comparison.
            _sortQueue.Sort((a, b) => DistanceSquared(a, centre).CompareTo(DistanceSquared(b, centre)));

            int budget = BuildBudget;
            for (int i = 0; i < _sortQueue.Count && budget > 0; i++)
            {
                ChunkPos position = _sortQueue[i];
                _pending.Remove(position);

                // Touch the chunk so the generator fills it, then mesh it. A chunk that meshes to nothing --
                // solid rock, open sky -- is still resident: it has been considered, and re-considering it
                // every frame would spend the whole budget on the inside of mountains.
                _world.GetChunk(position);
                _mesher.Mesh(_world, position, _scratch);

                upload(position, _scratch);
                _resident.Add(position);

                budget--;
                LastBuiltCount++;
            }
        }

        private static bool InRange(ChunkPos position, ChunkPos centre, int horizontal, int vertical)
            => System.Math.Abs(position.X - centre.X) <= horizontal
               && System.Math.Abs(position.Z - centre.Z) <= horizontal
               && System.Math.Abs(position.Y - centre.Y) <= vertical;

        private static long DistanceSquared(ChunkPos a, ChunkPos b)
        {
            long dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;

            // Vertical distance is weighted up, so the budget is spent on the ring around the player before
            // the column above and below them. A player notices missing ground long before missing sky.
            return (dx * dx) + (4 * dy * dy) + (dz * dz);
        }
    }
}
