namespace RP.Game.Scene
{
    using System;
    using System.Collections.Generic;
    using RP.Math;

    /// <summary>Integer address of one chunk in a uniform 3D grid.</summary>
    public readonly struct ChunkId : IEquatable<ChunkId>
    {
        public ChunkId(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public int X { get; }
        public int Y { get; }
        public int Z { get; }

        public bool Equals(ChunkId other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object? obj) => obj is ChunkId other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        public override string ToString() => $"({X},{Y},{Z})";
    }

    /// <summary>What changed in residency on the last <see cref="ChunkStreamer.Update"/>.</summary>
    public readonly struct ChunkResidencyChange
    {
        public ChunkResidencyChange(IReadOnlyList<ChunkId> loaded, IReadOnlyList<ChunkId> unloaded)
        {
            Loaded = loaded;
            Unloaded = unloaded;
        }

        /// <summary>Chunks that became resident this update.</summary>
        public IReadOnlyList<ChunkId> Loaded { get; }

        /// <summary>Chunks that were dropped this update.</summary>
        public IReadOnlyList<ChunkId> Unloaded { get; }
    }

    /// <summary>
    /// Decides which chunks of a large gridded world should be resident around a moving focus, so only the
    /// neighbourhood the player can actually reach is paged in (build brief S13: streaming the wreck
    /// interior). It is generic — it deals only in <see cref="ChunkId"/>s and a focus point in whatever space
    /// the grid lives in (for the wreck, that is its tumbling local frame, via <see cref="ReferenceFrame"/>).
    /// </summary>
    /// <remarks>
    /// A chunk loads when its centre comes within <see cref="LoadRadius"/> of the focus and is only dropped
    /// once its centre passes <see cref="UnloadRadius"/> — the same <b>hysteresis</b> idea as
    /// <see cref="SimTierManager"/>, so a player loitering on a chunk boundary does not page the same chunk in
    /// and out every frame. The caller does the actual asset load/unload off the returned change set.
    /// </remarks>
    public sealed class ChunkStreamer
    {
        private readonly HashSet<ChunkId> _resident = new();

        /// <summary>Edge length of a cubic chunk (metres).</summary>
        public double ChunkSize { get; }

        /// <summary>A chunk whose centre is within this of the focus is paged in.</summary>
        public double LoadRadius { get; }

        /// <summary>A resident chunk is dropped once its centre passes this (must exceed <see cref="LoadRadius"/>).</summary>
        public double UnloadRadius { get; }

        public ChunkStreamer(double chunkSize, double loadRadius, double unloadRadius)
        {
            if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
            if (unloadRadius < loadRadius) throw new ArgumentException("UnloadRadius must be >= LoadRadius.", nameof(unloadRadius));

            ChunkSize = chunkSize;
            LoadRadius = loadRadius;
            UnloadRadius = unloadRadius;
        }

        /// <summary>The chunks currently resident.</summary>
        public IReadOnlyCollection<ChunkId> Resident => _resident;

        /// <summary>The chunk containing a point in grid space.</summary>
        public ChunkId CellOf(Vector3d point) => new(
            (int)Math.Floor(point.X / ChunkSize),
            (int)Math.Floor(point.Y / ChunkSize),
            (int)Math.Floor(point.Z / ChunkSize));

        /// <summary>World-space centre of a chunk.</summary>
        public Vector3d CentreOf(ChunkId id) => new(
            (id.X + 0.5) * ChunkSize,
            (id.Y + 0.5) * ChunkSize,
            (id.Z + 0.5) * ChunkSize);

        /// <summary>
        /// Recomputes residency for a focus point and returns what changed. Newly in-range chunks are loaded;
        /// resident chunks that have fallen past <see cref="UnloadRadius"/> are dropped; everything between the
        /// two radii is left as it was.
        /// </summary>
        public ChunkResidencyChange Update(Vector3d focus)
        {
            var loaded = new List<ChunkId>();

            // Bring in every chunk whose centre is within the load radius.
            int reach = (int)Math.Ceiling(LoadRadius / ChunkSize) + 1;
            ChunkId centre = CellOf(focus);
            double loadSq = LoadRadius * LoadRadius;

            for (int dx = -reach; dx <= reach; dx++)
            for (int dy = -reach; dy <= reach; dy++)
            for (int dz = -reach; dz <= reach; dz++)
            {
                var id = new ChunkId(centre.X + dx, centre.Y + dy, centre.Z + dz);
                if (_resident.Contains(id)) continue;
                if ((CentreOf(id) - focus).MagnitudeSquared <= loadSq && _resident.Add(id))
                {
                    loaded.Add(id);
                }
            }

            // Drop resident chunks that have drifted past the (larger) unload radius.
            var unloaded = new List<ChunkId>();
            double unloadSq = UnloadRadius * UnloadRadius;
            foreach (ChunkId id in _resident)
            {
                if ((CentreOf(id) - focus).MagnitudeSquared > unloadSq) unloaded.Add(id);
            }

            foreach (ChunkId id in unloaded) _resident.Remove(id);

            return new ChunkResidencyChange(loaded, unloaded);
        }
    }
}
