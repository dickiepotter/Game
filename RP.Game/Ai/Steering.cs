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
        /// <b>Break turn</b> — the defensive manoeuvre against a shooter: steer hard <i>perpendicular</i>
        /// to the threat's line of sight, so every shot needs a fresh lead solution. Straight away from a
        /// gun is the worst move (zero angular rate for the shooter); across it is the best. The
        /// perpendicular is chosen in the plane spanned by the threat line and the current velocity — the
        /// turn that reuses the most existing speed — with <paramref name="phase"/> flipping which way
        /// around, so alternating or per-ship phases produce jinks rather than one predictable arc.
        /// </summary>
        /// <param name="position">The evader's position.</param>
        /// <param name="velocity">The evader's velocity.</param>
        /// <param name="threat">The shooter's position.</param>
        /// <param name="maxSpeed">The evader's speed budget.</param>
        /// <param name="maxForce">Steering-force clamp.</param>
        /// <param name="phase">Sign selector for the turn direction (e.g. +1/−1, or sin(time) sampled).</param>
        public static Vector3d BreakTurn(Vector3d position, Vector3d velocity, Vector3d threat,
            double maxSpeed, double maxForce, double phase = 1.0)
        {
            Vector3d line = (position - threat).NormalizeOrDefault();
            if (line.IsZero()) return Vector3d.Origin;

            // Perpendicular in the (line, velocity) plane; fall back to any perpendicular when flying
            // straight down the threat line (where the plane degenerates).
            Vector3d across = velocity - line * velocity.DotProduct(line);
            if (across.MagnitudeSquared < 1e-6)
            {
                across = line.CrossProduct(new Vector3d(0.31, 0.95, 0.12));
                if (across.IsZero()) across = line.CrossProduct(new Vector3d(1, 0, 0));
            }

            Vector3d desired = across.NormalizeOrDefault() * (phase >= 0 ? maxSpeed : -maxSpeed);
            return (desired - velocity).ClampMagnitude(maxForce);
        }

        /// <summary>
        /// <b>Extend</b> — disengage to reset the fight: steer away from the threat with a slight offset
        /// off the pure flee line (a straight-line extension is a zero-deflection shot for a pursuer; a
        /// few degrees off keeps their nose working). Use it when winded — shields down, hull low — then
        /// re-engage once <paramref name="position"/> is far enough out.
        /// </summary>
        public static Vector3d Extend(Vector3d position, Vector3d velocity, Vector3d threat,
            double maxSpeed, double maxForce)
        {
            Vector3d away = (position - threat).NormalizeOrDefault();
            if (away.IsZero()) return Vector3d.Origin;

            Vector3d skew = away.CrossProduct(new Vector3d(0.31, 0.95, 0.12)).NormalizeOrDefault() * 0.18;
            Vector3d desired = (away + skew).NormalizeOrDefault() * maxSpeed;
            return (desired - velocity).ClampMagnitude(maxForce);
        }

        /// <summary>
        /// The local-frame offset of a formation slot in a <b>finger-four</b>-style stack: slot 0 is the
        /// leader at the origin; wingmen fall in behind, alternating sides, stepped back and slightly
        /// below — the arrangement real flights use because everyone can see the leader and nobody sits in
        /// anyone's wash. Transform by the leader's orientation and add its position for the world-space
        /// slot, then <see cref="Arrive"/> at it. Slots beyond 3 continue the echelon outward, so any
        /// flight size works.
        /// </summary>
        /// <param name="slot">0 = leader, 1+ = wingmen.</param>
        /// <param name="spacing">Lateral spacing between adjacent ships (metres) — a few hull lengths.</param>
        public static Vector3d FormationSlotLocal(int slot, double spacing)
        {
            if (slot <= 0) return Vector3d.Origin;

            int pair = (slot + 1) / 2;                 // 1,1,2,2,3,3…
            double side = slot % 2 == 1 ? 1.0 : -1.0;  // starboard first, then port
            // Echelon: out to the side, stepped back (+Z is aft in the engine's forward = −Z convention),
            // and slightly low so the leader stays in everyone's canopy view.
            return new Vector3d(side * pair * spacing, -0.15 * pair * spacing, 0.8 * pair * spacing);
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
