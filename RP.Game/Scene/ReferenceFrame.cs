namespace RP.Game.Scene
{
    using RP.Math;

    /// <summary>
    /// A moving, rotating local coordinate frame — a world authored in its own space and then dropped into
    /// the galaxy at a position/orientation that can drift and tumble. The motivating case (build brief S13)
    /// is the wreck of the dreadnought <i>Tantalus</i>: its interior is built once in a clean local frame
    /// where the bow is +Z and the keel is −Y, while the hulk itself slowly tumbles through space. Everything
    /// inside it (geometry, the player, debris) is expressed in local coordinates and converted to world only
    /// when it must interact with the outside.
    /// </summary>
    /// <remarks>
    /// <para>A point fixed in the frame is not stationary in the world: it inherits the frame's linear
    /// <see cref="Velocity"/> plus the tangential velocity of the tumble, <c>ω × r</c>. <see cref="PointVelocity"/>
    /// gives that, which is what a ship must match to hold station against (or settle onto) the tumbling hulk —
    /// the difference between "fly alongside the wreck" and "be flung off it".</para>
    /// <para>This is pure double-precision maths and composes with <see cref="FloatingOrigin"/>: transform
    /// local→world here, then world→render there. It deliberately knows nothing about rendering or streaming;
    /// <see cref="ChunkStreamer"/> handles deciding which parts of a large local world are resident.</para>
    /// </remarks>
    public sealed class ReferenceFrame
    {
        /// <summary>The frame origin in world space (metres).</summary>
        public Vector3d Position { get; set; }

        /// <summary>Linear velocity of the whole frame (metres/second).</summary>
        public Vector3d Velocity { get; set; }

        /// <summary>Orientation of the frame's axes in world space.</summary>
        public Quaternion Orientation { get; set; } = Quaternion.Identity;

        /// <summary>The tumble: angular velocity in world space (radians/second).</summary>
        public Vector3d AngularVelocity { get; set; }

        /// <summary>Maps a point expressed in frame-local coordinates to world space.</summary>
        public Vector3d ToWorld(Vector3d local) => Position + Orientation.Rotate(local);

        /// <summary>Maps a world-space point into this frame's local coordinates.</summary>
        public Vector3d ToLocal(Vector3d world) => Orientation.Conjugate().Rotate(world - Position);

        /// <summary>Rotates a direction (no translation) from local into world space.</summary>
        public Vector3d DirectionToWorld(Vector3d localDirection) => Orientation.Rotate(localDirection);

        /// <summary>Rotates a direction (no translation) from world into local space.</summary>
        public Vector3d DirectionToLocal(Vector3d worldDirection) => Orientation.Conjugate().Rotate(worldDirection);

        /// <summary>
        /// The world-space velocity of a point that is fixed in this frame, given that point in <i>local</i>
        /// coordinates: the frame's own drift plus the tangential velocity of the tumble (<c>v + ω × r</c>,
        /// where <c>r</c> is the point's world offset from the frame origin). A ship matches this to ride the
        /// hulk; the mismatch is the velocity that would throw it off.
        /// </summary>
        public Vector3d PointVelocity(Vector3d local)
        {
            Vector3d r = Orientation.Rotate(local); // offset from the frame origin, in world space
            return Velocity + AngularVelocity.CrossProduct(r);
        }

        /// <summary>
        /// Advances the frame by <paramref name="dt"/> seconds: drifts the origin along <see cref="Velocity"/>
        /// and tumbles the orientation about <see cref="AngularVelocity"/>. The angular step is taken as an
        /// axis-angle rotation (the same scheme <c>RigidBody</c> uses) so the orientation stays a valid
        /// rotation rather than drifting off the unit sphere.
        /// </summary>
        public void Advance(double dt)
        {
            Position += Velocity * dt;

            double angularSpeed = AngularVelocity.Magnitude;
            if (angularSpeed > 1e-12)
            {
                Vector3d axis = AngularVelocity / angularSpeed;
                Quaternion delta = Quaternion.FromAxisAngle(axis, new Angle(angularSpeed * dt));
                Orientation = (delta * Orientation).NormalizeOrDefault();
            }
        }
    }
}
