namespace RP.Game.Rendering
{
    using System;
    using RP.Math;

    /// <summary>
    /// Trauma-based camera shake — the standard "juice" model: events add <b>trauma</b> in [0, 1], trauma
    /// decays linearly, and the offset amplitude follows trauma² — so big hits slam while the tail settles
    /// smoothly instead of buzzing at constant strength until it stops. The offset is built from
    /// incommensurate sinusoids (cheap, smooth, non-repeating noise) and returned in an abstract local
    /// frame; the caller maps it onto whatever axes shake means for it — a cockpit camera's right/up/forward,
    /// a 2D screen's x/y (ignore Z), or a UI element's layout offset.
    /// </summary>
    /// <remarks>
    /// The trauma² response is why this model beats a plain decaying offset: doubling the input more than
    /// doubles the visible shake, so small events register as a tremor while big ones read as a slam, and
    /// stacking several small events never accumulates into absurdity because trauma clamps at 1.
    /// </remarks>
    public sealed class CameraShake
    {
        private double _trauma;
        private double _time;

        /// <summary>Maximum positional offset at full trauma, in the caller's units (metres for a world
        /// camera, pixels for a screen).</summary>
        public double Amplitude { get; set; } = 1.4;

        /// <summary>Trauma lost per second. Higher = snappier recovery.</summary>
        public double Decay { get; set; } = 1.6;

        /// <summary>Adds trauma, clamped to [0, 1]. Guide: ~0.2 a nearby impact, ~0.5 taking a hit, ~0.9 a
        /// collision or point-blank detonation.</summary>
        public void AddTrauma(double amount) => _trauma = Math.Clamp(_trauma + amount, 0, 1);

        /// <summary>Current trauma level — usable to drive secondary flinch (HUD jitter, controller rumble).</summary>
        public double Trauma => _trauma;

        /// <summary>Advances decay and returns this frame's offset in the local (x = right, y = up,
        /// z = forward) frame. Zero once trauma has fully decayed.</summary>
        public Vector3d Update(double dt)
        {
            _time += dt;
            _trauma = Math.Max(0, _trauma - Decay * dt);
            if (_trauma <= 0) return Vector3d.Zero;

            double shake = _trauma * _trauma * Amplitude;
            return new Vector3d(
                Math.Sin(_time * 71.3) * 0.6 + Math.Sin(_time * 37.9) * 0.4,
                Math.Sin(_time * 83.7 + 1.3) * 0.6 + Math.Sin(_time * 43.1 + 0.7) * 0.4,
                Math.Sin(_time * 59.3 + 2.1) * 0.3) * shake;
        }
    }
}
