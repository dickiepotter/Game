namespace RP.Game.Scene
{
    using System;
    using RP.Game.Rendering;
    using RP.Math;

    /// <summary>
    /// The frustum-culling <i>system</i>: it decides which instances are worth drawing. The frustum
    /// <i>maths</i> lives in <see cref="RP.Math"/> (the <see cref="Frustum"/> type and its intersection
    /// tests); this is the engine machinery that applies it to a batch of instances — exactly the
    /// "intersection maths in Math, culling system in Game" split the brief asks for (S4.1).
    /// </summary>
    /// <remarks>
    /// Culling is the difference between "draw the 10% of the battlefield on screen" and "draw all of it":
    /// for a kilometre-scale world most objects are off-screen most of the time, and rejecting them with a
    /// handful of plane tests is far cheaper than asking the GPU to transform and discard them.
    /// </remarks>
    public static class FrustumCuller
    {
        /// <summary>
        /// Copies the visible instances from <paramref name="source"/> into <paramref name="destination"/>,
        /// keeping each whose bounding sphere (centre = instance offset, radius = <paramref name="boundingRadius"/>)
        /// intersects <paramref name="frustum"/>. Returns the number kept.
        /// </summary>
        /// <param name="destination">Must be at least as long as <paramref name="source"/>.</param>
        public static int Cull(
            Frustum frustum,
            ReadOnlySpan<InstanceData> source,
            float boundingRadius,
            Span<InstanceData> destination)
        {
            if (destination.Length < source.Length)
            {
                throw new ArgumentException("Destination must be at least as large as source.", nameof(destination));
            }

            int kept = 0;
            for (int i = 0; i < source.Length; i++)
            {
                // Vector3 (float) widens implicitly to the double Vector the sphere wants.
                var sphere = new BoundingSphere(source[i].Offset, boundingRadius);
                if (frustum.Intersects(sphere))
                {
                    destination[kept++] = source[i];
                }
            }

            return kept;
        }
    }
}
