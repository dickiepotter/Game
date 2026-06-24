namespace RP.Game.Ai
{
    using System.Collections.Generic;
    using RP.Math;

    /// <summary>
    /// Classic Reynolds <b>steering behaviours</b>: small, composable rules that turn "where do I want to
    /// be" into a steering force, the building blocks of believable ship AI (build brief S12). Each returns
    /// a force (an acceleration intent) clamped to <c>maxForce</c>; the AI sums the behaviours it wants and
    /// feeds the result to the ship as thrust. These are generic (no Spectre specifics), so they live in
    /// Game and any game can reuse them.
    /// </summary>
    /// <remarks>
    /// The shared trick: compute a <i>desired</i> velocity, then steer = desired − current velocity. A ship
    /// already moving the right way needs little force; one moving the wrong way gets turned hard.
    /// </remarks>
    public static class Steering
    {
        /// <summary>Steer to chase a fixed point at full speed.</summary>
        public static Vector3d Seek(Vector3d position, Vector3d velocity, Vector3d target, double maxSpeed, double maxForce)
        {
            Vector3d desired = (target - position).NormalizeOrDefault() * maxSpeed;
            return (desired - velocity).ClampMagnitude(maxForce);
        }

        /// <summary>Steer directly away from a point (the opposite of <see cref="Seek"/>).</summary>
        public static Vector3d Flee(Vector3d position, Vector3d velocity, Vector3d threat, double maxSpeed, double maxForce)
        {
            Vector3d desired = (position - threat).NormalizeOrDefault() * maxSpeed;
            return (desired - velocity).ClampMagnitude(maxForce);
        }

        /// <summary>
        /// Like <see cref="Seek"/> but eases to a stop: within <paramref name="slowRadius"/> the desired
        /// speed ramps down to zero, so the ship arrives instead of overshooting.
        /// </summary>
        public static Vector3d Arrive(Vector3d position, Vector3d velocity, Vector3d target, double maxSpeed, double maxForce, double slowRadius)
        {
            Vector3d toTarget = target - position;
            double distance = toTarget.Magnitude;
            if (distance < 1e-9) return (velocity * -1).ClampMagnitude(maxForce); // at target: brake

            double speed = distance < slowRadius ? maxSpeed * (distance / slowRadius) : maxSpeed;
            Vector3d desired = toTarget / distance * speed;
            return (desired - velocity).ClampMagnitude(maxForce);
        }

        /// <summary>
        /// Chase a <i>moving</i> target by seeking where it will be, estimated from how long the gap takes
        /// to close at <paramref name="maxSpeed"/>. This is what makes a pursuer cut the corner.
        /// </summary>
        public static Vector3d Pursue(Vector3d position, Vector3d velocity, Vector3d targetPosition, Vector3d targetVelocity, double maxSpeed, double maxForce)
        {
            double distance = (targetPosition - position).Magnitude;
            double leadTime = maxSpeed > 0 ? distance / maxSpeed : 0;
            Vector3d future = targetPosition + targetVelocity * leadTime;
            return Seek(position, velocity, future, maxSpeed, maxForce);
        }

        /// <summary>
        /// <b>Separation</b> (the anti-collision rule of flocking): steer away from neighbours that are too
        /// close, weighted so the nearest push hardest. Keeps a pack of Wasps from piling into one point.
        /// </summary>
        public static Vector3d Separation(Vector3d position, IReadOnlyList<Vector3d> neighbours, double radius, double maxForce)
        {
            Vector3d push = Vector3d.Origin;
            int count = 0;
            foreach (Vector3d other in neighbours)
            {
                Vector3d away = position - other;
                double distance = away.Magnitude;
                if (distance > 1e-9 && distance < radius)
                {
                    push += away / (distance * distance); // closer neighbours weigh more
                    count++;
                }
            }

            if (count == 0) return Vector3d.Origin;
            return push.ClampMagnitude(maxForce);
        }
    }
}
