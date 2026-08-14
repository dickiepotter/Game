namespace RP.Game.Physics
{
    using RP.Math;

    /// <summary>
    /// A Newtonian rigid body: the physical state of one object (ship, debris, projectile) and the rule
    /// for advancing it. It carries linear state (position, velocity) and angular state (orientation,
    /// angular velocity), accumulates forces and torques during a step, then integrates them.
    /// </summary>
    /// <remarks>
    /// <para><b>Why double-precision position.</b> The world is tens of kilometres across, far enough that
    /// 32-bit floats jitter (build brief S5). The simulation's "truth" position is therefore a
    /// <see cref="Vector3d"/>; rendering rebases it to float near the camera (the floating-origin scheme).
    /// Velocity is modest in magnitude but kept double too, so a long burn never accumulates float drift.</para>
    ///
    /// <para><b>Space is Newtonian (build brief S6).</b> There is no drag: forces change velocity, and with
    /// no force a body drifts forever. Translation uses semi-implicit Euler (stable, energy-friendly).
    /// Rotation integrates the angular velocity into the orientation quaternion via an axis-angle delta,
    /// which stays a valid rotation without the drift a naive component update would introduce. Rotational
    /// inertia is a single coarse scalar here — enough to make capitals turn like fortresses and fighters
    /// like darts — rather than a full inertia tensor.</para>
    /// </remarks>
    public sealed class RigidBody
    {
        /// <summary>Galaxy-space position, double precision (metres).</summary>
        public Vector3d Position { get; set; }

        /// <summary>Linear velocity (metres/second). Persists — momentum is conserved.</summary>
        public Vector3d Velocity { get; set; }

        /// <summary>Orientation as a unit quaternion.</summary>
        public Quaternion Orientation { get; set; } = Quaternion.Identity;

        /// <summary>Angular velocity in world space (radians/second). Persists.</summary>
        public Vector3d AngularVelocity { get; set; }

        /// <summary>Mass (kg-ish). Drives acceleration and collision impulse. Must be &gt; 0.</summary>
        public double Mass { get; set; } = 1.0;

        /// <summary>Coarse rotational inertia: higher = slower to spin up. Must be &gt; 0.</summary>
        public double InertiaScalar { get; set; } = 1.0;

        private Vector3d _forceAccumulator;
        private Vector3d _torqueAccumulator;

        /// <summary>The body's local forward axis (−Z) expressed in world space.</summary>
        public Vector3d Forward => Orientation.Rotate(new Vector3d(0, 0, -1));

        /// <summary>The body's local right axis (+X) expressed in world space.</summary>
        public Vector3d Right => Orientation.Rotate(new Vector3d(1, 0, 0));

        /// <summary>The body's local up axis (+Y) expressed in world space.</summary>
        public Vector3d Up => Orientation.Rotate(new Vector3d(0, 1, 0));

        /// <summary>Adds a world-space force for this step (cleared after <see cref="Integrate"/>).</summary>
        public void ApplyForce(Vector3d worldForce) => _forceAccumulator += worldForce;

        /// <summary>Adds a force expressed in the body's own frame (e.g. "thrust forward").</summary>
        public void ApplyForceLocal(Vector3d localForce) => ApplyForce(Orientation.Rotate(localForce));

        /// <summary>Adds a world-space torque for this step (cleared after <see cref="Integrate"/>).</summary>
        public void ApplyTorque(Vector3d worldTorque) => _torqueAccumulator += worldTorque;

        /// <summary>Adds a torque expressed in the body's own frame (e.g. "yaw left").</summary>
        public void ApplyTorqueLocal(Vector3d localTorque) => ApplyTorque(Orientation.Rotate(localTorque));

        /// <summary>
        /// Advances the body by <paramref name="dt"/> seconds: integrate linear motion from the
        /// accumulated force, integrate angular motion from the accumulated torque, then clear both
        /// accumulators ready for the next step.
        /// </summary>
        public void Integrate(double dt)
        {
            // Linear: a = F/m, then a stable semi-implicit Euler step.
            Vector3d acceleration = _forceAccumulator / Mass;
            (Position, Velocity) = Integrators.SemiImplicitEuler(Position, Velocity, acceleration, dt);

            // Angular: angular acceleration = torque / inertia, integrate the angular velocity, then fold
            // it into the orientation as a small rotation about the angular-velocity axis.
            Vector3d angularAcceleration = _torqueAccumulator / InertiaScalar;
            AngularVelocity += angularAcceleration * dt;

            double angularSpeed = AngularVelocity.Magnitude;
            if (angularSpeed > 1e-12)
            {
                Vector3d axis = AngularVelocity / angularSpeed;
                Quaternion delta = Quaternion.FromAxisAngle(axis, new Angle(angularSpeed * dt));
                Orientation = (delta * Orientation).NormalizeOrDefault();
            }

            _forceAccumulator = Vector3d.Zero;
            _torqueAccumulator = Vector3d.Zero;
        }
    }
}
