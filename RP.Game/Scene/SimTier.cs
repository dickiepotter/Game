namespace RP.Game.Scene
{
    /// <summary>
    /// The simulation level-of-detail an entity is run at, by distance from the player (build brief S5).
    /// This is what makes "huge battles" affordable: only the handful of things near the player get the
    /// full treatment, while distant thousands are cheap cosmetics.
    /// </summary>
    public enum SimTier
    {
        /// <summary>Full physics, full AI, full collision (~50–150 entities).</summary>
        Near,

        /// <summary>Integrated motion, simplified collision, coarse AI (hundreds).</summary>
        Mid,

        /// <summary>Billboards/impostors, no collision, scripted drift (thousands).</summary>
        Far,
    }
}
