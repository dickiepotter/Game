namespace RP.Game.Physics
{
    using System;
    using RP.Math;

    /// <summary>
    /// Detects collisions between sphere-bounded bodies and resolves them with an impulse, returning the
    /// energy of the impact so the game can turn it into crash damage. This is the generic physics
    /// machinery (build brief S4.1); the <i>consequences</i> of an impact (how much hull it costs) are the
    /// game's to decide.
    /// </summary>
    /// <remarks>
    /// <para><b>The collision impulse (build brief S6).</b> When two bodies touch, we compute their
    /// relative velocity along the contact normal. If they are separating, nothing happens. If they are
    /// approaching, we apply an equal-and-opposite impulse (Newton's third law) that reverses that closing
    /// speed by a factor of <c>(1 + restitution)</c> — restitution 0 is a dead stop, 1 a perfect bounce.
    /// The impulse is shared in inverse proportion to mass, so a fighter clipping a capital ship is flung
    /// away while the capital barely twitches.</para>
    /// <para><b>Reduced mass.</b> The "effective" mass of a two-body collision is
    /// <c>(mA·mB)/(mA+mB)</c> — dominated by the <i>lighter</i> body. The kinetic energy available to do
    /// damage is <c>½·reducedMass·closingSpeed²</c>, which is why a light, fast thing hitting a heavy one
    /// still hurts (S7).</para>
    /// </remarks>
    public static class CollisionResolver
    {
        /// <summary>The reduced mass of a two-body system: <c>(mA·mB)/(mA+mB)</c>.</summary>
        public static double ReducedMass(double massA, double massB)
        {
            double sum = massA + massB;
            return sum <= 0 ? 0 : (massA * massB) / sum;
        }

        /// <summary>
        /// Tests two spheres for overlap. On a hit, <paramref name="normal"/> is the unit contact normal
        /// pointing from A to B and <paramref name="penetration"/> is how deeply they overlap.
        /// </summary>
        public static bool SpheresOverlap(
            Vector3d centerA, double radiusA, Vector3d centerB, double radiusB,
            out Vector3d normal, out double penetration)
        {
            Vector3d delta = centerB - centerA;
            double distance = delta.Magnitude;
            double touchDistance = radiusA + radiusB;

            if (distance >= touchDistance)
            {
                normal = new Vector3d(0, 0, 1);
                penetration = 0;
                return false;
            }

            // Coincident centres: pick an arbitrary normal so resolution still has a direction.
            normal = distance > 1e-9 ? delta / distance : new Vector3d(0, 0, 1);
            penetration = touchDistance - distance;
            return true;
        }

        /// <summary>
        /// Tests a sphere against an axis-aligned box. On contact, <paramref name="normal"/> is the unit
        /// direction pushing the sphere <b>out</b> of the box and <paramref name="penetration"/> the depth
        /// to move along it to separate. Handles all three regimes correctly: sphere outside grazing a
        /// face/edge/corner (clamped-point test), and sphere centre fully <i>inside</i> the box — where the
        /// clamped point degenerates onto the centre itself and naive implementations report "no contact" —
        /// by exiting through the nearest face. Axis-aligned by design: for an oriented box, transform the
        /// sphere centre into the box's local frame first (one rotation) rather than rotating eight corners.
        /// </summary>
        public static bool SphereVsAabb(
            Vector3d centre, double radius, Vector3d boxLo, Vector3d boxHi,
            out Vector3d normal, out double penetration)
        {
            var clamped = new Vector3d(
                Math.Clamp(centre.X, boxLo.X, boxHi.X),
                Math.Clamp(centre.Y, boxLo.Y, boxHi.Y),
                Math.Clamp(centre.Z, boxLo.Z, boxHi.Z));
            Vector3d delta = centre - clamped;
            double distSq = delta.MagnitudeSquared;

            if (distSq >= radius * radius)
            {
                normal = default;
                penetration = 0;
                return false;
            }

            if (distSq < 1e-12)
            {
                // Centre inside the box: exit through the nearest face.
                double dxLo = centre.X - boxLo.X, dxHi = boxHi.X - centre.X;
                double dyLo = centre.Y - boxLo.Y, dyHi = boxHi.Y - centre.Y;
                double dzLo = centre.Z - boxLo.Z, dzHi = boxHi.Z - centre.Z;
                double min = Math.Min(Math.Min(Math.Min(dxLo, dxHi), Math.Min(dyLo, dyHi)), Math.Min(dzLo, dzHi));
                normal =
                    min == dxLo ? new Vector3d(-1, 0, 0) :
                    min == dxHi ? new Vector3d(1, 0, 0) :
                    min == dyLo ? new Vector3d(0, -1, 0) :
                    min == dyHi ? new Vector3d(0, 1, 0) :
                    min == dzLo ? new Vector3d(0, 0, -1) : new Vector3d(0, 0, 1);
                penetration = min + radius;
                return true;
            }

            double dist = Math.Sqrt(distSq);
            normal = delta / dist;
            penetration = radius - dist;
            return true;
        }

        /// <summary>
        /// Swept segment-vs-sphere test — the tunnelling-proof hit test for anything fast and small
        /// (projectiles, raycast probes). True when segment [<paramref name="a"/>, <paramref name="b"/>]
        /// passes within <paramref name="radius"/> of <paramref name="centre"/>. Outputs both the
        /// closest-approach parameter <paramref name="t"/> in [0, 1] (for ordering multiple candidate hits
        /// along a trajectory) and <paramref name="entryPoint"/> — where the segment <b>first pierces</b>
        /// the sphere surface, which is where an impact visually belongs. A dead-centre shot still reports
        /// its entry on the surface, not the centre; a segment starting inside the sphere reports the
        /// surface point radially outward from its start.
        /// </summary>
        public static bool SegmentIntersectsSphere(
            Vector3d a, Vector3d b, Vector3d centre, double radius, out double t, out Vector3d entryPoint)
        {
            Vector3d d = b - a;
            double len2 = d.MagnitudeSquared;
            if (len2 < 1e-18)
            {
                t = 0;
                entryPoint = a;
                return (a - centre).Magnitude <= radius;
            }

            t = Math.Clamp((centre - a).DotProduct(d) / len2, 0, 1);
            Vector3d closest = a + d * t;
            double missSq = (closest - centre).MagnitudeSquared;
            if (missSq > radius * radius)
            {
                entryPoint = closest;
                return false;
            }

            // Back up from the closest approach to the first surface crossing.
            double back = Math.Sqrt(Math.Max(0, radius * radius - missSq)) / Math.Sqrt(len2);
            Vector3d entry = a + d * Math.Max(0, t - back);
            Vector3d radial = entry - centre;
            double radialMag = radial.Magnitude;
            entryPoint = radialMag > 1e-12 ? centre + radial / radialMag * radius : entry;
            return true;
        }

        /// <summary>
        /// The kinetic energy of an impact (joules-ish): <c>½·reducedMass·closingSpeed²</c>, using the full
        /// relative speed between the two bodies (build brief S7/S16).
        /// </summary>
        public static double ImpactEnergy(RigidBody a, RigidBody b)
        {
            double closingSpeed = (a.Velocity - b.Velocity).Magnitude;
            return 0.5 * ReducedMass(a.Mass, b.Mass) * closingSpeed * closingSpeed;
        }

        /// <summary>
        /// Resolves a collision between two bodies along <paramref name="normal"/> (pointing A→B), applying
        /// an equal-and-opposite impulse that conserves momentum. Returns the impact energy (0 if the
        /// bodies were already separating, so no impulse was applied).
        /// </summary>
        /// <param name="restitution">Bounciness in [0, 1]: 0 = inelastic (stick), 1 = elastic (bounce).</param>
        public static double Resolve(RigidBody a, RigidBody b, Vector3d normal, double restitution = 0.2)
        {
            double energy = ImpactEnergy(a, b);

            // Closing speed along the normal (positive when approaching, since normal points A→B and we
            // measure A's velocity relative to B).
            Vector3d relativeVelocity = a.Velocity - b.Velocity;
            double closing = relativeVelocity.DotProduct(normal);
            if (closing <= 0)
            {
                return 0; // separating (or sliding) — no impulse
            }

            double inverseMassA = a.Mass > 0 ? 1.0 / a.Mass : 0;
            double inverseMassB = b.Mass > 0 ? 1.0 / b.Mass : 0;
            double inverseMassSum = inverseMassA + inverseMassB;
            if (inverseMassSum <= 0) return energy; // both immovable

            double impulseMagnitude = (1.0 + restitution) * closing / inverseMassSum;
            Vector3d impulse = normal * impulseMagnitude;

            // Equal and opposite: A is pushed back along −normal, B forward along +normal.
            a.Velocity -= impulse * inverseMassA;
            b.Velocity += impulse * inverseMassB;

            return energy;
        }
    }
}
