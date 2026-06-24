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
