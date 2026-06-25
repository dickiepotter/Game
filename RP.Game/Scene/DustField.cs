namespace RP.Game.Scene
{
    using System;
    using RP.Math;

    /// <summary>
    /// A local field of motes that conveys <b>translation speed</b>. The backdrop starfield sits at infinity,
    /// so it only parallaxes when the camera turns — flying in a straight line through it feels static. This
    /// field instead fills a cube that follows the viewer: each frame, any mote that has fallen more than a
    /// half-extent behind (in any axis, relative to the viewer) wraps to the opposite face, so there is always
    /// near geometry streaming past. The faster you move, the faster it flows — the cheapest, oldest trick for
    /// selling speed in space (build brief S5/S13). Pure positions; the game draws them as faint instances.
    /// </summary>
    public sealed class DustField
    {
        private readonly Vector3d[] _points;
        private readonly double _halfExtent;
        private readonly double _size;

        /// <summary>
        /// Scatters <paramref name="count"/> motes uniformly through a cube of side 2·<paramref name="halfExtent"/>
        /// metres, centred on the origin until the first <see cref="Update"/> recentres it on the viewer.
        /// </summary>
        public DustField(int count, double halfExtent = 700.0, int seed = 1)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            _halfExtent = halfExtent;
            _size = halfExtent * 2.0;
            _points = new Vector3d[count];

            var rng = new Random(seed);
            for (int i = 0; i < count; i++)
            {
                _points[i] = new Vector3d(
                    (rng.NextDouble() - 0.5) * _size,
                    (rng.NextDouble() - 0.5) * _size,
                    (rng.NextDouble() - 0.5) * _size);
            }
        }

        /// <summary>The current mote positions in true world space.</summary>
        public ReadOnlySpan<Vector3d> Points => _points;

        /// <summary>The cube's half-extent (metres) — motes live within ±this of the viewer.</summary>
        public double HalfExtent => _halfExtent;

        /// <summary>
        /// Recentres the field on <paramref name="viewer"/>: every mote more than a half-extent away on any
        /// axis is wrapped back to the opposite side, so the cube of dust always surrounds the viewer and the
        /// motes appear to stream past as it moves.
        /// </summary>
        public void Update(Vector3d viewer)
        {
            for (int i = 0; i < _points.Length; i++)
            {
                Vector3d d = _points[i] - viewer;
                _points[i] = viewer + new Vector3d(Wrap(d.X), Wrap(d.Y), Wrap(d.Z));
            }
        }

        // Fold a coordinate into [-halfExtent, +halfExtent).
        private double Wrap(double v)
        {
            v = (v + _halfExtent) % _size;
            if (v < 0) v += _size;
            return v - _halfExtent;
        }
    }
}
