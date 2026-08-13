namespace RP.Game.Rendering
{
    using System;
    using RP.Math;

    /// <summary>
    /// Pilot-head inertia: the camera is not bolted to the airframe. Under acceleration a head lags behind
    /// (thrust forward presses you back into the seat; a hard brake throws you toward the canopy), then a
    /// neck-muscle spring recentres it. Modelled as a critically-damped spring driving an offset toward
    /// <c>-acceleration × Compliance</c>, clamped to a physical travel limit. Feed it the craft's proper
    /// acceleration in the craft's own local frame; add the returned offset to the camera position in that
    /// same frame.
    /// </summary>
    /// <remarks>
    /// <para><b>Why critical damping.</b> Underdamped, the head visibly oscillates after every input
    /// (nauseating); overdamped, it smears and feels like lag. Critical damping (damping = 2·√stiffness for
    /// unit mass) recentres as fast as possible with no overshoot, which is what real vestibular
    /// stabilisation feels like from inside.</para>
    /// <para>Works for any vehicle camera — cockpit, chase (feed world-frame acceleration and add in world
    /// axes), even a walking head-bob rig if you feed stride accelerations. Compose with
    /// <see cref="CameraShake"/>: shake is impulsive noise, this is sustained-load response; together they
    /// cover both feel regimes.</para>
    /// </remarks>
    public sealed class HeadMotion
    {
        private Vector3d _offset;
        private Vector3d _velocity;

        /// <summary>Metres of head travel per m/s² of sustained acceleration (before the clamp). The
        /// default gives ~0.3 m at a hard 45 m/s² burn — pronounced but not sickening.</summary>
        public double Compliance { get; set; } = 0.007;

        /// <summary>Maximum head travel from centre (metres) — the harness/canopy limit.</summary>
        public double MaxTravel { get; set; } = 0.45;

        /// <summary>Spring stiffness (1/s²). Higher = a stiffer neck that recentres faster.</summary>
        public double Stiffness { get; set; } = 40.0;

        /// <summary>Current head offset, expressed in the same local frame the acceleration was fed in —
        /// rotate it by the craft's orientation to get the world-space camera offset.</summary>
        public Vector3d Offset => _offset;

        /// <summary>
        /// Advances the spring one step. <paramref name="localAcceleration"/> is the craft's proper
        /// acceleration expressed in its own local frame (m/s²); dt is the frame time.
        /// </summary>
        public Vector3d Update(Vector3d localAcceleration, double dt)
        {
            if (dt <= 0) return _offset;

            // The head wants to sit opposite the acceleration, within its travel limit.
            Vector3d target = (localAcceleration * -Compliance).ClampMagnitude(MaxTravel);

            // Critically damped spring: a = k(target - x) - 2√k · v.
            double damping = 2.0 * Math.Sqrt(Stiffness);
            Vector3d accel = (target - _offset) * Stiffness - _velocity * damping;
            _velocity += accel * dt;
            _offset = (_offset + _velocity * dt).ClampMagnitude(MaxTravel);
            return _offset;
        }

        /// <summary>Snaps the head back to centre (e.g. on respawn or camera cut).</summary>
        public void Reset()
        {
            _offset = Vector3d.Zero;
            _velocity = Vector3d.Zero;
        }
    }
}
