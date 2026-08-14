namespace RP.Game.Scene
{
    /// <summary>
    /// Assigns entities a <see cref="SimTier"/> from their distance to the player, and promotes/demotes
    /// them as that distance changes — with a <b>hysteresis</b> band so an entity hovering right on a
    /// boundary does not flip tiers every frame (which would thrash AI/physics on and off).
    /// </summary>
    /// <remarks>
    /// Hysteresis works by making each boundary "sticky": to move to a closer (more expensive) tier the
    /// entity must come well inside the radius (<c>radius − band</c>); to drop to a cheaper tier it must
    /// fall well outside (<c>radius + band</c>). Between those, it keeps its current tier.
    /// </remarks>
    public sealed class SimTierManager
    {
        /// <summary>Inside this distance an entity is Near (full sim).</summary>
        public double NearRadius { get; }

        /// <summary>Between <see cref="NearRadius"/> and this it is Mid; beyond it, Far.</summary>
        public double MidRadius { get; }

        /// <summary>The hysteresis half-band width around each boundary.</summary>
        public double Hysteresis { get; }

        public SimTierManager(double nearRadius, double midRadius, double hysteresis = 0)
        {
            NearRadius = nearRadius;
            MidRadius = midRadius;
            Hysteresis = hysteresis;
        }

        /// <summary>The tier for a fresh entity (no hysteresis, since it has no current tier).</summary>
        public SimTier Classify(double distance)
        {
            if (distance <= NearRadius) return SimTier.Near;
            if (distance <= MidRadius) return SimTier.Mid;
            return SimTier.Far;
        }

        /// <summary>
        /// The entity's next tier given its current one, applying hysteresis so it only changes when it has
        /// clearly crossed a boundary.
        /// </summary>
        public SimTier Reclassify(SimTier current, double distance)
        {
            // A boundary the entity is currently inside of is pushed outward by the band (sticky to stay);
            // a boundary it is currently outside of is pulled inward (must clearly enter to promote).
            double nearBoundary = NearRadius + (current == SimTier.Near ? Hysteresis : -Hysteresis);
            if (distance <= nearBoundary) return SimTier.Near;

            bool currentlyAtMostMid = current == SimTier.Near || current == SimTier.Mid;
            double midBoundary = MidRadius + (currentlyAtMostMid ? Hysteresis : -Hysteresis);
            if (distance <= midBoundary) return SimTier.Mid;

            return SimTier.Far;
        }
    }
}
