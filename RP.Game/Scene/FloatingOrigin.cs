namespace RP.Game.Scene
{
    using RP.Math;

    /// <summary>
    /// The floating-origin (camera-relative rebasing) system that keeps a kilometre-scale world inside
    /// 32-bit float precision. The simulation stores true positions in double; this converts them to the
    /// float positions the GPU and near-field physics use, relative to a movable <see cref="Origin"/> that
    /// is snapped back toward the player whenever they wander too far.
    /// </summary>
    /// <remarks>
    /// <para><b>The problem (build brief S5).</b> A 32-bit float has only ~7 significant digits. At 50,000
    /// metres from the origin the smallest representable step is several millimetres, so positions visibly
    /// jitter and physics goes unstable. Doubles fix the storage, but the GPU still wants floats.</para>
    /// <para><b>The fix.</b> Keep the world's true positions in double, but render and do near physics in a
    /// space centred on a floating <see cref="Origin"/> that follows the player. As long as the player
    /// stays within a few kilometres of <see cref="Origin"/>, every rendered coordinate is small and
    /// precise. When they cross <see cref="RebaseThreshold"/>, <see cref="MaybeRebase"/> snaps the origin
    /// to them — a large, instantaneous shift of the whole world that is invisible because everything moves
    /// together and the player's own local position returns to near zero.</para>
    /// </remarks>
    public sealed class FloatingOrigin
    {
        /// <summary>The current world-space point that maps to local (render) zero.</summary>
        public Vector3d Origin { get; private set; }

        /// <summary>How far the focus may drift from <see cref="Origin"/> before a rebase (metres).</summary>
        public double RebaseThreshold { get; set; }

        public FloatingOrigin(double rebaseThreshold = 4096.0)
        {
            RebaseThreshold = rebaseThreshold;
            Origin = Vector3d.Origin;
        }

        /// <summary>
        /// If <paramref name="focus"/> (normally the player's true position) has drifted past
        /// <see cref="RebaseThreshold"/> from <see cref="Origin"/>, snaps the origin to it and returns
        /// true. Callers re-derive any cached local positions after a true return.
        /// </summary>
        public bool MaybeRebase(Vector3d focus)
        {
            if ((focus - Origin).Magnitude > RebaseThreshold)
            {
                Origin = focus;
                return true;
            }

            return false;
        }

        /// <summary>Converts a true (double) world position into the local float space for rendering.</summary>
        public Vector3 ToRenderSpace(Vector3d truePosition) => (Vector3)(truePosition - Origin);

        /// <summary>Converts a local float position back to a true (double) world position.</summary>
        public Vector3d FromRenderSpace(Vector3 localPosition) => Origin + (Vector3d)localPosition;
    }
}
