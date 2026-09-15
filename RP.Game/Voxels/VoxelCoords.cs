namespace RP.Game.Voxels
{
    using System;
    using RP.Math;

    /// <summary>
    /// The six faces of a cube, in the fixed order every voxel routine in this namespace relies on.
    /// </summary>
    /// <remarks>
    /// The order is load-bearing, not cosmetic: <see cref="VoxelFaces"/> indexes parallel tables of
    /// normals, neighbour offsets and quad corners by the integer value of this enum, and the mesher,
    /// the lighting flood-fill and the raycaster all step through faces by index. Opposite faces are
    /// deliberately adjacent and ordered negative-then-positive on each axis, so <c>face ^ 1</c> is the
    /// opposite face — which is exactly what a flood-fill needs when it steps into a neighbour and must
    /// know which way it came in.
    /// </remarks>
    public enum BlockFace
    {
        /// <summary>The face toward -X (west).</summary>
        NegativeX = 0,

        /// <summary>The face toward +X (east).</summary>
        PositiveX = 1,

        /// <summary>The face toward -Y (down).</summary>
        NegativeY = 2,

        /// <summary>The face toward +Y (up).</summary>
        PositiveY = 3,

        /// <summary>The face toward -Z (north).</summary>
        NegativeZ = 4,

        /// <summary>The face toward +Z (south).</summary>
        PositiveZ = 5,
    }

    /// <summary>
    /// The lookup tables that turn a <see cref="BlockFace"/> into geometry: which way it points, which
    /// neighbour lies beyond it, and where its four corners sit on a unit cube.
    /// </summary>
    /// <remarks>
    /// These live in one place because getting them subtly inconsistent is the most common voxel bug there
    /// is, and the symptom is miserable to diagnose: faces that light from the wrong direction, quads wound
    /// backwards so they vanish under back-face culling, or ambient occlusion sampled from the wrong
    /// neighbour so shadows appear on the opposite side of every block. One table, used by everything.
    /// </remarks>
    public static class VoxelFaces
    {
        /// <summary>The number of faces on a cube. Loop bounds read better as <c>VoxelFaces.Count</c>.</summary>
        public const int Count = 6;

        /// <summary>The outward normal of each face, indexed by <see cref="BlockFace"/>.</summary>
        public static readonly Vector3[] Normals =
        {
            new Vector3(-1, 0, 0),
            new Vector3(1, 0, 0),
            new Vector3(0, -1, 0),
            new Vector3(0, 1, 0),
            new Vector3(0, 0, -1),
            new Vector3(0, 0, 1),
        };

        /// <summary>The integer step to the neighbouring block across each face.</summary>
        public static readonly BlockPos[] Offsets =
        {
            new BlockPos(-1, 0, 0),
            new BlockPos(1, 0, 0),
            new BlockPos(0, -1, 0),
            new BlockPos(0, 1, 0),
            new BlockPos(0, 0, -1),
            new BlockPos(0, 0, 1),
        };

        /// <summary>
        /// The four corners of each face on the unit cube spanning <c>(0,0,0)</c> to <c>(1,1,1)</c>, wound
        /// counter-clockwise when seen from <i>outside</i> the cube.
        /// </summary>
        /// <remarks>
        /// Winding decides which side of a triangle the GPU treats as the front. Get it wrong on a subset of
        /// faces and those faces disappear when back-face culling is on — the classic "my world has holes in
        /// it only from certain angles" symptom. Fixed here once, verified by test against the normals.
        /// </remarks>
        public static readonly Vector3[][] Corners =
        {
            // -X: looking toward +X, so wind around the YZ plane at x = 0.
            new[] { new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(0, 1, 1), new Vector3(0, 1, 0) },

            // +X
            new[] { new Vector3(1, 0, 1), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(1, 1, 1) },

            // -Y (the underside)
            new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1) },

            // +Y (the top)
            new[] { new Vector3(0, 1, 1), new Vector3(1, 1, 1), new Vector3(1, 1, 0), new Vector3(0, 1, 0) },

            // -Z
            new[] { new Vector3(1, 0, 0), new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0) },

            // +Z
            new[] { new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 1) },
        };

        /// <summary>The face pointing the opposite way. Implemented as <c>face ^ 1</c>, which is why the
        /// enum pairs opposites on adjacent values.</summary>
        public static BlockFace Opposite(BlockFace face) => (BlockFace)((int)face ^ 1);

        /// <summary>The axis a face lies on: 0 for X, 1 for Y, 2 for Z.</summary>
        public static int Axis(BlockFace face) => (int)face >> 1;

        /// <summary>Whether a face points along the positive direction of its axis.</summary>
        public static bool IsPositive(BlockFace face) => ((int)face & 1) == 1;
    }

    /// <summary>
    /// The integer coordinate of a single block in the world. Distinct from a <see cref="Vector3"/> on
    /// purpose: a block address is exact and countable, and mixing it with the continuous position of an
    /// entity standing on that block is the source of a whole family of off-by-one bugs.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a dedicated type.</b> Blocks are addressed constantly — by the mesher, the lighting
    /// pass, the raycaster, the physics sweep, every network edit and every save record. Giving the
    /// address a type means the compiler catches "you passed a world position where a block address was
    /// wanted", and it gives one obvious home for the conversions between world space, block space and
    /// chunk space, which is precisely where flooring mistakes otherwise breed.</para>
    /// <para><b>Reach.</b> 32-bit components give a world about four billion blocks on a side. That is far
    /// beyond anything reachable, so the practical limit is the streaming budget, not the coordinate.</para>
    /// </remarks>
    public readonly struct BlockPos : IEquatable<BlockPos>
    {
        /// <summary>The block at the world origin.</summary>
        public static readonly BlockPos Zero = new BlockPos(0, 0, 0);

        /// <summary>Creates a block address.</summary>
        public BlockPos(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>The block's X coordinate.</summary>
        public int X { get; }

        /// <summary>The block's Y coordinate — height, with +Y up.</summary>
        public int Y { get; }

        /// <summary>The block's Z coordinate.</summary>
        public int Z { get; }

        /// <summary>The block address containing a continuous world position.</summary>
        /// <remarks>
        /// Uses floor, not truncation. The block containing <c>x = -0.3</c> is block <c>-1</c>, because
        /// block <c>-1</c> spans <c>[-1, 0)</c>; truncating would report block <c>0</c> and make the two
        /// blocks either side of every axis overlap. This is the same trap as
        /// <c>RP.Math.Noise.PerlinNoise.FastFloor</c>, and it bites hardest here, where it shows up as a
        /// player who can walk into the wall on one side of the world and not the other.
        /// </remarks>
        public static BlockPos FromWorld(Vector3d world) => new BlockPos(Floor(world.X), Floor(world.Y), Floor(world.Z));

        /// <summary>The block address containing a continuous world position.</summary>
        public static BlockPos FromWorld(double x, double y, double z) => new BlockPos(Floor(x), Floor(y), Floor(z));

        /// <summary>Floor to <see cref="int"/>, correct for negative values.</summary>
        public static int Floor(double v)
        {
            int i = (int)v;
            return v < i ? i - 1 : i;
        }

        /// <summary>The world-space position of this block's minimum corner.</summary>
        public Vector3d ToWorld() => new Vector3d(X, Y, Z);

        /// <summary>The world-space position of this block's centre.</summary>
        public Vector3d Center() => new Vector3d(X + 0.5, Y + 0.5, Z + 0.5);

        /// <summary>The neighbouring block across a face.</summary>
        public BlockPos Neighbour(BlockFace face)
        {
            BlockPos d = VoxelFaces.Offsets[(int)face];
            return new BlockPos(X + d.X, Y + d.Y, Z + d.Z);
        }

        /// <summary>Component-wise addition.</summary>
        public static BlockPos operator +(BlockPos a, BlockPos b) => new BlockPos(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        /// <summary>Component-wise subtraction.</summary>
        public static BlockPos operator -(BlockPos a, BlockPos b) => new BlockPos(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        /// <summary>The Chebyshev (chessboard) distance to another block — the number of steps if diagonal
        /// moves are free. The right measure for "is this block within N of that one" over a cubic region.</summary>
        public int ChebyshevDistance(BlockPos other)
        {
            int dx = System.Math.Abs(X - other.X);
            int dy = System.Math.Abs(Y - other.Y);
            int dz = System.Math.Abs(Z - other.Z);
            int m = dx > dy ? dx : dy;
            return m > dz ? m : dz;
        }

        /// <summary>The Manhattan distance to another block — the number of face-to-face steps between them.
        /// This is the metric a light flood-fill propagates along.</summary>
        public int ManhattanDistance(BlockPos other)
            => System.Math.Abs(X - other.X) + System.Math.Abs(Y - other.Y) + System.Math.Abs(Z - other.Z);

        /// <inheritdoc />
        public bool Equals(BlockPos other) => X == other.X && Y == other.Y && Z == other.Z;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is BlockPos other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);

        /// <summary>Equality.</summary>
        public static bool operator ==(BlockPos a, BlockPos b) => a.Equals(b);

        /// <summary>Inequality.</summary>
        public static bool operator !=(BlockPos a, BlockPos b) => !a.Equals(b);

        /// <inheritdoc />
        public override string ToString() => $"[{X},{Y},{Z}]";
    }

    /// <summary>
    /// The integer address of one chunk in the voxel grid — a block address divided by the chunk size.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="BlockPos"/> because confusing the two is catastrophic and silent: a chunk
    /// address used as a block address points at a block 32 times too close to the origin, which generates
    /// a world that looks almost right. Different type, compiler catches it.
    /// </remarks>
    public readonly struct ChunkPos : IEquatable<ChunkPos>
    {
        /// <summary>Creates a chunk address.</summary>
        public ChunkPos(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>The chunk's X index.</summary>
        public int X { get; }

        /// <summary>The chunk's Y index.</summary>
        public int Y { get; }

        /// <summary>The chunk's Z index.</summary>
        public int Z { get; }

        /// <summary>The chunk containing a block.</summary>
        /// <remarks>
        /// An arithmetic shift rather than a division: <c>-1 / 32</c> is <c>0</c> in C# (division truncates
        /// toward zero), but block <c>-1</c> belongs to chunk <c>-1</c>. <c>-1 &gt;&gt; 5</c> is <c>-1</c>,
        /// which is the floor, which is correct. The same trap as <see cref="BlockPos.FromWorld(Vector3d)"/>,
        /// one level up.
        /// </remarks>
        public static ChunkPos FromBlock(BlockPos block)
            => new ChunkPos(block.X >> VoxelChunk.SizeShift, block.Y >> VoxelChunk.SizeShift, block.Z >> VoxelChunk.SizeShift);

        /// <summary>The chunk containing a continuous world position.</summary>
        public static ChunkPos FromWorld(Vector3d world) => FromBlock(BlockPos.FromWorld(world));

        /// <summary>The block address of this chunk's minimum corner.</summary>
        public BlockPos Origin()
            => new BlockPos(X << VoxelChunk.SizeShift, Y << VoxelChunk.SizeShift, Z << VoxelChunk.SizeShift);

        /// <summary>The world-space position of this chunk's centre.</summary>
        public Vector3d Center()
        {
            const double Half = VoxelChunk.Size * 0.5;
            BlockPos o = Origin();
            return new Vector3d(o.X + Half, o.Y + Half, o.Z + Half);
        }

        /// <summary>The neighbouring chunk across a face.</summary>
        public ChunkPos Neighbour(BlockFace face)
        {
            BlockPos d = VoxelFaces.Offsets[(int)face];
            return new ChunkPos(X + d.X, Y + d.Y, Z + d.Z);
        }

        /// <summary>Component-wise offset.</summary>
        public ChunkPos Offset(int dx, int dy, int dz) => new ChunkPos(X + dx, Y + dy, Z + dz);

        /// <inheritdoc />
        public bool Equals(ChunkPos other) => X == other.X && Y == other.Y && Z == other.Z;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is ChunkPos other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);

        /// <summary>Equality.</summary>
        public static bool operator ==(ChunkPos a, ChunkPos b) => a.Equals(b);

        /// <summary>Inequality.</summary>
        public static bool operator !=(ChunkPos a, ChunkPos b) => !a.Equals(b);

        /// <inheritdoc />
        public override string ToString() => $"C({X},{Y},{Z})";
    }
}
