namespace RP.Game.Voxels
{
    using System;
    using RP.Math;

    /// <summary>
    /// Collision between an axis-aligned box and a voxel world: how anything with a body — player, creature,
    /// cart, dropped item — moves without walking through walls.
    /// </summary>
    /// <remarks>
    /// <para><b>Axis-separated sweeping.</b> The move is applied one axis at a time — all of X, then all of
    /// Y, then all of Z — and each is resolved against the blocks it overlaps before the next begins. This
    /// looks naive next to a true swept-volume test and is, for a grid, both simpler and better behaved.
    /// The reason is that a diagonal move into a corner has no single correct "time of impact": resolving
    /// the whole displacement at once has to pick an axis to push back along, and whichever it picks is
    /// wrong half the time, producing the classic symptom of a player sticking on the seam between two
    /// flush blocks. Resolving axes independently cannot produce that, because each axis only ever asks a
    /// question with one answer.</para>
    ///
    /// <para><b>Why the order is X, Y, Z and not something else.</b> Y is resolved in the middle so that
    /// landing happens before the horizontal move is finalised, which is what lets a body walk off a ledge
    /// and begin falling in the same step rather than hanging in the air for one frame.</para>
    ///
    /// <para><b>The epsilon.</b> Resolved positions are pulled back by a hair rather than placed exactly
    /// flush. Floating-point rounding at an exact block boundary can put the body a fraction <i>inside</i>
    /// the block, and the next step then finds it already overlapping and pushes it out in an arbitrary
    /// direction — which the player sees as being flung sideways when they walk into a wall.</para>
    /// </remarks>
    public static class VoxelCollision
    {
        /// <summary>
        /// How far a resolved surface is held back from an exact block boundary. Small enough to be
        /// invisible, large enough to survive the rounding of a world coordinate in the thousands.
        /// </summary>
        public const double SkinWidth = 1e-4;

        /// <summary>What a move collided with, per axis.</summary>
        public readonly struct MoveResult
        {
            /// <summary>Whether the move was stopped along X.</summary>
            public readonly bool HitX;

            /// <summary>Whether the move was stopped along Y.</summary>
            public readonly bool HitY;

            /// <summary>Whether the move was stopped along Z.</summary>
            public readonly bool HitZ;

            /// <summary>Whether the body landed on something — stopped while moving downward.</summary>
            public readonly bool Landed;

            /// <summary>Whether the body struck something above it — stopped while moving upward.</summary>
            public readonly bool HitCeiling;

            internal MoveResult(bool hitX, bool hitY, bool hitZ, bool landed, bool hitCeiling)
            {
                HitX = hitX;
                HitY = hitY;
                HitZ = hitZ;
                Landed = landed;
                HitCeiling = hitCeiling;
            }

            /// <summary>Whether anything was struck at all.</summary>
            public bool HitAnything => HitX || HitY || HitZ;
        }

        /// <summary>
        /// Moves an axis-aligned box through the world by a displacement, stopping at whatever it runs into.
        /// </summary>
        /// <param name="world">The world to collide against.</param>
        /// <param name="min">The box's minimum corner. Updated in place to the resolved position.</param>
        /// <param name="size">The box's extent along each axis. Unchanged.</param>
        /// <param name="delta">How far to try to move.</param>
        /// <returns>What was struck, per axis.</returns>
        public static MoveResult Move(IVoxelRead world, ref Vector3d min, Vector3d size, Vector3d delta)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));

            bool hitX = MoveAxis(world, ref min, size, 0, delta.X);
            bool hitY = MoveAxis(world, ref min, size, 1, delta.Y);
            bool hitZ = MoveAxis(world, ref min, size, 2, delta.Z);

            bool landed = hitY && delta.Y < 0.0;
            bool ceiling = hitY && delta.Y > 0.0;
            return new MoveResult(hitX, hitY, hitZ, landed, ceiling);
        }

        /// <summary>
        /// Moves the box along one axis, stopping flush against the first solid block in the way.
        /// </summary>
        /// <remarks>
        /// Rather than stepping in increments, this asks directly which blocks the box would sweep through
        /// on this axis and takes the nearest blocking plane. That makes the cost independent of speed — a
        /// body moving at forty blocks a second is checked as cheaply as one moving at one, and neither can
        /// tunnel through a wall, which an increment-based sweep only avoids by being slow.
        /// </remarks>
        private static bool MoveAxis(IVoxelRead world, ref Vector3d min, Vector3d size, int axis, double distance)
        {
            if (distance == 0.0) return false;

            IVoxelPalette palette = world.Palette;
            Vector3d max = min + size;

            // The slab of blocks the box sweeps through on this axis.
            double from = Component(min, axis);
            double to = from + distance;

            bool forward = distance > 0.0;

            // The leading face of the box on this axis, and where it ends up.
            double leadStart = forward ? Component(max, axis) : from;
            double leadEnd = leadStart + distance;

            int first = BlockPos.Floor(forward ? leadStart : leadEnd);
            int last = BlockPos.Floor(forward ? leadEnd : leadStart);

            // The extent of the box on the other two axes decides which columns to test.
            int axisA = (axis + 1) % 3;
            int axisB = (axis + 2) % 3;

            // Pull the bounds in by the skin so a box resting exactly flush against a wall does not count
            // that wall as overlapping on the perpendicular axes, which would wedge it in place.
            int a0 = BlockPos.Floor(Component(min, axisA) + SkinWidth);
            int a1 = BlockPos.Floor(Component(max, axisA) - SkinWidth);
            int b0 = BlockPos.Floor(Component(min, axisB) + SkinWidth);
            int b1 = BlockPos.Floor(Component(max, axisB) - SkinWidth);

            int step = forward ? 1 : -1;
            int scanStart = forward ? first : last;
            int scanEnd = forward ? last : first;

            for (int plane = scanStart; forward ? plane <= scanEnd : plane >= scanEnd; plane += step)
            {
                for (int a = a0; a <= a1; a++)
                {
                    for (int b = b0; b <= b1; b++)
                    {
                        BlockPos position = Compose(axis, plane, axisA, a, axisB, b);
                        if (!palette.IsSolid(world.GetBlock(position))) continue;

                        // Stop flush against this block's near face, held back by the skin.
                        double blocking = forward ? plane - SkinWidth : plane + 1.0 + SkinWidth;
                        double resolved = forward ? blocking - Size(size, axis) : blocking;

                        // Only actually a collision if it stops us short of where we wanted to go.
                        if (forward ? resolved < to : resolved > to)
                        {
                            min = With(min, axis, resolved);
                            return true;
                        }
                    }
                }
            }

            min = With(min, axis, to);
            return false;
        }

        /// <summary>Whether an axis-aligned box overlaps any solid block.</summary>
        /// <remarks>
        /// Used to test whether a position is legal at all — before teleporting something into it, before
        /// spawning a creature, and by the step-up logic below to check that the raised position is clear.
        /// </remarks>
        public static bool Overlaps(IVoxelRead world, Vector3d min, Vector3d size)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));

            IVoxelPalette palette = world.Palette;
            Vector3d max = min + size;

            int x0 = BlockPos.Floor(min.X + SkinWidth), x1 = BlockPos.Floor(max.X - SkinWidth);
            int y0 = BlockPos.Floor(min.Y + SkinWidth), y1 = BlockPos.Floor(max.Y - SkinWidth);
            int z0 = BlockPos.Floor(min.Z + SkinWidth), z1 = BlockPos.Floor(max.Z - SkinWidth);

            for (int y = y0; y <= y1; y++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        if (palette.IsSolid(world.GetBlock(new BlockPos(x, y, z)))) return true;
                    }
                }
            }

            return false;
        }

        /// <summary>Whether a box is resting on solid ground — probed a hair below its underside.</summary>
        public static bool IsSupported(IVoxelRead world, Vector3d min, Vector3d size)
        {
            var probe = new Vector3d(min.X, min.Y - (SkinWidth * 4.0), min.Z);
            var thin = new Vector3d(size.X, SkinWidth * 2.0, size.Z);
            return Overlaps(world, probe, thin);
        }

        private static double Component(Vector3d v, int axis) => axis == 0 ? v.X : (axis == 1 ? v.Y : v.Z);

        private static double Size(Vector3d v, int axis) => Component(v, axis);

        private static Vector3d With(Vector3d v, int axis, double value)
            => axis == 0 ? new Vector3d(value, v.Y, v.Z)
             : axis == 1 ? new Vector3d(v.X, value, v.Z)
                         : new Vector3d(v.X, v.Y, value);

        private static BlockPos Compose(int axis, int value, int axisA, int a, int axisB, int b)
        {
            int x = 0, y = 0, z = 0;
            Assign(ref x, ref y, ref z, axis, value);
            Assign(ref x, ref y, ref z, axisA, a);
            Assign(ref x, ref y, ref z, axisB, b);
            return new BlockPos(x, y, z);
        }

        private static void Assign(ref int x, ref int y, ref int z, int axis, int value)
        {
            if (axis == 0) x = value;
            else if (axis == 1) y = value;
            else z = value;
        }
    }

    /// <summary>
    /// A body that walks: gravity, jumping, ground detection, step-up, and the acceleration model that makes
    /// movement feel like a game rather than like a physics demonstration.
    /// </summary>
    /// <remarks>
    /// <para><b>Why not the rigid-body integrator.</b> <see cref="RP.Game.Physics.RigidBody"/> is the right
    /// tool for something being thrown, pushed or flown. A character on foot is not simulated, it is
    /// <i>driven</i>: the player expects to reach full speed almost instantly, to stop almost instantly, to
    /// turn on the spot, and to have precise control in mid-air that no real body possesses. Modelling that
    /// with forces and friction means fighting the simulation to get responsiveness back. Driving velocity
    /// directly toward a target, with separate ground and air rates, gets the feel right in a few lines and
    /// leaves the rigid body for things that genuinely tumble.</para>
    ///
    /// <para><b>Step-up is not a detail.</b> Without it, a one-block rise stops a player dead and they must
    /// jump to cross a doorway sill or a cobbled path. With it, terrain becomes walkable and the world feels
    /// continuous. It is implemented as "try the move at the raised height, and only keep it if the raised
    /// position is clear and lands on something" — which correctly refuses to lift a player into a
    /// one-block gap they should have to crouch through.</para>
    ///
    /// <para><b>Coyote time and jump buffering.</b> Two small forgivenesses that players never notice when
    /// present and always feel when absent: a jump still works for a moment after walking off an edge, and a
    /// jump pressed just before landing fires on landing rather than being swallowed. Both exist because
    /// human reaction time is not frame-accurate, and both are the difference between a controller that
    /// feels tight and one that feels unresponsive.</para>
    /// </remarks>
    public sealed class VoxelCharacter
    {
        private double _coyoteTimer;
        private double _jumpBufferTimer;

        /// <summary>The position of the centre of the body's base — where it stands.</summary>
        public Vector3d Position { get; set; }

        /// <summary>Current velocity in blocks per second.</summary>
        public Vector3d Velocity { get; set; }

        /// <summary>How wide the body is, in blocks. Slightly under 1 so it fits through a 1-block gap.</summary>
        public double Width { get; set; } = 0.6;

        /// <summary>How tall the body is, in blocks.</summary>
        public double Height { get; set; } = 1.8;

        /// <summary>How high above <see cref="Position"/> the eyes sit.</summary>
        public double EyeHeight { get; set; } = 1.62;

        /// <summary>The tallest rise the body walks up without jumping.</summary>
        public double StepHeight { get; set; } = 1.05;

        /// <summary>Downward acceleration, in blocks per second squared.</summary>
        /// <remarks>
        /// Far stronger than Earth's 9.81. Real gravity at this scale gives a floaty, drifting jump that
        /// players consistently describe as sluggish — the arc is simply too long when a "metre" is a block
        /// you can see the edges of. Roughly 2.5g is the value that reads as natural here.
        /// </remarks>
        public double Gravity { get; set; } = 25.0;

        /// <summary>Terminal downward speed, so a long fall does not reach tunnelling velocity.</summary>
        public double TerminalVelocity { get; set; } = 60.0;

        /// <summary>Walking speed in blocks per second.</summary>
        public double WalkSpeed { get; set; } = 4.5;

        /// <summary>Sprinting speed in blocks per second.</summary>
        public double SprintSpeed { get; set; } = 10.0;

        /// <summary>How fast horizontal velocity converges on the target while on the ground.</summary>
        public double GroundAcceleration { get; set; } = 60.0;

        /// <summary>How fast it converges while airborne — much lower, so a jump commits.</summary>
        public double AirAcceleration { get; set; } = 12.0;

        /// <summary>
        /// How high a jump from dry ground reaches, in blocks.
        /// </summary>
        /// <remarks>
        /// Stated as a height rather than as an impulse because the height is the thing that matters and
        /// the impulse is an implementation detail of the gravity constant. A world built in one-block
        /// multiples is navigated by asking "can I get onto that?", and the answer has to be a number
        /// someone can reason about without solving a quadratic. Two blocks and a little over, so a
        /// two-high step is reliably clearable rather than something you land on if the frame timing
        /// happens to be kind.
        /// </remarks>
        public double JumpHeight { get; set; } = 2.08;

        /// <summary>
        /// How high a jump reaches when standing in a thin fluid such as water, in blocks.
        /// </summary>
        /// <remarks>
        /// Water costs you half a block. It is enough to notice immediately -- a ledge you were hopping
        /// onto a moment ago is suddenly out of reach -- without making a shallow stream a wall.
        /// </remarks>
        public double WaterJumpHeight { get; set; } = 1.55;

        /// <summary>
        /// How high a jump reaches when standing in a thick fluid such as lava, oil or tar, in blocks.
        /// </summary>
        /// <remarks>
        /// One block, and no more. Wading into tar should feel like a decision: you can still climb out of
        /// a one-deep channel, and you cannot bound out of a pit you walked into.
        /// </remarks>
        public double ViscousJumpHeight { get; set; } = 1.02;

        /// <summary>
        /// The upward speed a jump of a given height needs, given the gravity in force.
        /// </summary>
        /// <remarks>
        /// Straight from <c>v = sqrt(2gh)</c>. Keeping it as a function rather than a stored speed means
        /// changing gravity cannot silently change how high anything jumps, which is the sort of coupling
        /// that turns one tuning pass into three.
        /// </remarks>
        public double SpeedForHeight(double height) => System.Math.Sqrt(2.0 * Gravity * System.Math.Max(0.0, height));

        /// <summary>Upward speed imparted by a jump from dry ground.</summary>
        public double JumpSpeed => SpeedForHeight(JumpHeight);

        /// <summary>How long after leaving the ground a jump still works.</summary>
        public double CoyoteTime { get; set; } = 0.12;

        /// <summary>How long before landing a jump press is remembered.</summary>
        public double JumpBufferTime { get; set; } = 0.14;

        /// <summary>Whether the body is currently standing on something.</summary>
        public bool OnGround { get; private set; }

        /// <summary>Whether the body is inside a fluid, which changes how it moves.</summary>
        public bool InFluid { get; private set; }

        /// <summary>
        /// The fluid properties of the world, if the game has them. Optional: without it the body still
        /// swims, but every fluid behaves like water and nothing drowns.
        /// </summary>
        public IVoxelFluid? Fluids { get; set; }

        /// <summary>Which fluid the body is in, or 0 for none.</summary>
        public int FluidKind { get; private set; }

        /// <summary>How dense that fluid is relative to water. Above 1 the body sinks in it.</summary>
        public double FluidDensity { get; private set; } = 1.0;

        /// <summary>
        /// How thick the fluid the body is in is: 1 for water, higher for oil, lava and tar.
        /// </summary>
        /// <remarks>
        /// Taken from the fluid's own tick delay, which is already the game's measure of how sluggishly a
        /// fluid moves. Deriving it rather than declaring a second number means a fluid cannot be defined
        /// as creeping across the ground while behaving like water around your knees.
        /// </remarks>
        public double FluidViscosity { get; private set; } = 1.0;

        /// <summary>
        /// How high the body can jump from where it is standing, in blocks.
        /// </summary>
        /// <remarks>
        /// Thin fluids cost half a block, thick ones a whole one, and the crossover is gradual rather than
        /// a cliff so that a fluid added later lands somewhere sensible without being special-cased here.
        /// </remarks>
        public double CurrentJumpHeight
        {
            get
            {
                if (!InFluid) return JumpHeight;
                if (FluidViscosity <= 1.0) return WaterJumpHeight;

                // Fully thick by the time a fluid is three times water's sluggishness, which is where lava
                // and everything slower than it sits.
                double thickness = System.Math.Min(1.0, (FluidViscosity - 1.0) / 2.0);
                return WaterJumpHeight + ((ViscousJumpHeight - WaterJumpHeight) * thickness);
            }
        }

        /// <summary>Whether the body's head is under the surface — the condition for drowning.</summary>
        /// <remarks>
        /// Tested at eye height rather than at the body's centre, because what a player understands is
        /// "my head went under". Wading chest-deep must not start the drowning timer, and a body standing
        /// in a one-deep stream must not either.
        /// </remarks>
        public bool IsHeadSubmerged { get; private set; }

        /// <summary>How much of the body is below the surface, in <c>[0, 1]</c>. Drives buoyancy.</summary>
        public double Submersion { get; private set; }

        /// <summary>
        /// Whether the body is on the way up from a jump it made itself.
        /// </summary>
        /// <remarks>
        /// Distinguishes "rising because I jumped" from "rising because I am buoyant", which the vertical
        /// velocity alone cannot. Only the first should be ballistic.
        /// </remarks>
        private bool _jumpAscent;

        private double _maxBreath = 15.0;
        private double _breath = 15.0;

        /// <summary>
        /// How long the body can hold its breath, in seconds. Lowering it also lowers a fuller
        /// <see cref="Breath"/> to match, so a creature configured for a short breath does not start life
        /// holding more air than its lungs can contain.
        /// </summary>
        public double MaxBreath
        {
            get => _maxBreath;
            set
            {
                _maxBreath = value < 0.0 ? 0.0 : value;
                if (_breath > _maxBreath) _breath = _maxBreath;
            }
        }

        /// <summary>How much breath is left, in seconds. Refills quickly in air.</summary>
        public double Breath
        {
            get => _breath;
            private set => _breath = value;
        }

        /// <summary>
        /// Whether this body needs to breathe air at all. False for fish, the drowned, and anything wearing
        /// a rebreather — the flag a game flips rather than special-casing the controller.
        /// </summary>
        public bool NeedsAir { get; set; } = true;

        /// <summary>How fast breath comes back once the head is clear, as a multiple of real time.</summary>
        public double BreathRecoveryRate { get; set; } = 4.0;

        /// <summary>
        /// How long the body has been out of breath, in seconds. Reset the moment it surfaces. The game
        /// reads this to apply drowning damage on whatever schedule it likes; the engine does not know what
        /// damage is.
        /// </summary>
        public double DrowningTime { get; private set; }

        /// <summary>Whether the body is out of breath and taking on water.</summary>
        public bool IsDrowning => DrowningTime > 0.0;

        /// <summary>When true, gravity and collision are skipped entirely — the creative/spectator mode.</summary>
        public bool NoClip { get; set; }

        /// <summary>The world-space point the camera should sit at.</summary>
        public Vector3d EyePosition => new Vector3d(Position.X, Position.Y + EyeHeight, Position.Z);

        /// <summary>The body's minimum corner, for collision.</summary>
        public Vector3d BoxMin => new Vector3d(Position.X - (Width * 0.5), Position.Y, Position.Z - (Width * 0.5));

        /// <summary>The body's extent, for collision.</summary>
        public Vector3d BoxSize => new Vector3d(Width, Height, Width);

        /// <summary>
        /// Advances the body one fixed timestep.
        /// </summary>
        /// <param name="world">The world to collide against.</param>
        /// <param name="dt">The timestep, in seconds.</param>
        /// <param name="wishDirection">Desired horizontal direction, in world space. Need not be normalised;
        /// longer than unit is clamped, so a diagonal input cannot outrun a straight one.</param>
        /// <param name="jump">Whether the jump control is held.</param>
        /// <param name="sprint">Whether the sprint control is held.</param>
        public void Step(IVoxelRead world, double dt, Vector3d wishDirection, bool jump, bool sprint)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (dt <= 0.0) return;

            if (NoClip)
            {
                StepNoClip(dt, wishDirection, jump, sprint);
                return;
            }

            IVoxelPalette palette = world.Palette;
            SampleFluid(world, palette);
            UpdateBreath(dt);

            // ---- Horizontal: drive velocity toward the target rather than integrating forces ----
            double wishLength = System.Math.Sqrt((wishDirection.X * wishDirection.X) + (wishDirection.Z * wishDirection.Z));
            double targetSpeed = sprint ? SprintSpeed : WalkSpeed;
            if (InFluid) targetSpeed *= 0.55;

            double targetX = 0.0, targetZ = 0.0;
            if (wishLength > 1e-9)
            {
                // Clamp rather than normalise: a half-pressed analogue stick should walk, not run.
                double scale = targetSpeed / System.Math.Max(wishLength, 1.0);
                targetX = wishDirection.X * scale;
                targetZ = wishDirection.Z * scale;
            }

            double acceleration = OnGround ? GroundAcceleration : AirAcceleration;
            if (InFluid) acceleration *= 0.6;

            double vx = Approach(Velocity.X, targetX, acceleration * dt);
            double vz = Approach(Velocity.Z, targetZ, acceleration * dt);

            // ---- Vertical ----
            double vy = Velocity.Y;

            _coyoteTimer = OnGround ? CoyoteTime : System.Math.Max(0.0, _coyoteTimer - dt);
            _jumpBufferTimer = jump ? JumpBufferTime : System.Math.Max(0.0, _jumpBufferTimer - dt);

            if (InFluid)
            {
                // Buoyancy is the difference between the fluid's density and the body's, scaled by how
                // much of the body is actually under. A body floats in water, bobs on oil, and sinks
                // steadily in tar -- all from one number the game supplies per fluid, with no special cases
                // in here.
                // Standing on the bottom is standing on the ground, and you can jump off it. Wading
                // through a stream and finding the jump button does nothing is the wrong answer: it reads
                // as the controls having broken rather than as the water being heavy. What a fluid takes
                // is height, not the jump itself.
                if (_jumpBufferTimer > 0.0 && _coyoteTimer > 0.0)
                {
                    vy = SpeedForHeight(CurrentJumpHeight);
                    _coyoteTimer = 0.0;
                    _jumpBufferTimer = 0.0;
                    _jumpAscent = true;
                }
                else if (jump && !_jumpAscent)
                {
                    // Swimming up, for a body already off the bottom. Weaker than a jump: you cannot leap
                    // out of deep water, you climb out.
                    vy = System.Math.Min(vy + (Gravity * 0.55 * dt), 4.2);
                }

                if (_jumpAscent)
                {
                    // The ascent of a jump is ballistic even in fluid, so that CurrentJumpHeight is a
                    // promise rather than an optimistic ceiling. Solving drag and buoyancy backwards for
                    // an impulse that happens to land on the right height would be arithmetic nobody could
                    // check, and it would drift the moment either constant moved.
                    vy -= Gravity * dt;
                }
                else
                {
                    // Buoyancy is the difference between the fluid's density and the body's, scaled by how
                    // much of the body is actually under. A body floats in water, bobs on oil, and sinks
                    // steadily in tar -- all from one number the game supplies per fluid, with no special
                    // cases in here.
                    double displaced = FluidDensity * Submersion;
                    double buoyancy = (displaced - BodyDensity) * Gravity * BuoyancyStrength;

                    vy += buoyancy * dt;
                    vy -= Gravity * dt * (1.0 - Submersion);   // the part still in air falls normally

                    // Drag, which is what makes a fluid feel thick. Scales with density, so tar is
                    // treacle, and with submersion, because a body with its ankles wet is not swimming.
                    // Without that second factor a shallow stream damps a jump as hard as a deep lake and
                    // wading across a brook feels like wading through the sea.
                    double drag = 2.4 * FluidDensity * System.Math.Max(0.15, Submersion);
                    vy *= 1.0 - System.Math.Min(drag * dt, 0.9);
                }
            }
            else
            {
                if (_jumpBufferTimer > 0.0 && _coyoteTimer > 0.0)
                {
                    // No gravity on the frame the impulse lands. Taking a frame's worth off immediately
                    // costs a fixed slice of the jump and makes the height depend on the timestep, so the
                    // same jump clears a ledge at 144 frames a second and does not at 30.
                    vy = JumpSpeed;
                    _coyoteTimer = 0.0;
                    _jumpBufferTimer = 0.0;
                    _jumpAscent = true;
                }
                else
                {
                    vy -= Gravity * dt;
                    if (vy < -TerminalVelocity) vy = -TerminalVelocity;
                }
            }

            Velocity = new Vector3d(vx, vy, vz);

            // ---- Move and resolve ----
            Vector3d size = BoxSize;
            Vector3d min = BoxMin;
            var delta = new Vector3d(vx * dt, vy * dt, vz * dt);

            Vector3d beforeMove = min;
            VoxelCollision.MoveResult result = VoxelCollision.Move(world, ref min, size, delta);

            // Step-up: if the horizontal move was blocked while on the ground, try again from a raised
            // start and keep it only if the body ends up higher, clear, and still supported.
            if ((result.HitX || result.HitZ) && (OnGround || _coyoteTimer > 0.0) && delta.Y <= 0.0)
            {
                Vector3d raised = beforeMove;
                raised = new Vector3d(raised.X, raised.Y + StepHeight, raised.Z);

                if (!VoxelCollision.Overlaps(world, raised, size))
                {
                    Vector3d stepped = raised;
                    VoxelCollision.Move(world, ref stepped, size, new Vector3d(delta.X, 0.0, delta.Z));

                    // Settle back down onto whatever is under the new position.
                    Vector3d settled = stepped;
                    VoxelCollision.Move(world, ref settled, size, new Vector3d(0.0, -StepHeight, 0.0));

                    bool gainedGround = settled.Y >= beforeMove.Y - VoxelCollision.SkinWidth;
                    double horizontalGain = System.Math.Abs(settled.X - beforeMove.X) + System.Math.Abs(settled.Z - beforeMove.Z);
                    double originalGain = System.Math.Abs(min.X - beforeMove.X) + System.Math.Abs(min.Z - beforeMove.Z);

                    if (gainedGround && horizontalGain > originalGain + 1e-6 &&
                        VoxelCollision.IsSupported(world, settled, size))
                    {
                        min = settled;
                        vy = 0.0;
                    }
                }
            }

            // Stopping against a surface kills the velocity into it; leaving it intact means the body keeps
            // accelerating into a wall and shoots away the moment it clears.
            if (result.HitX) vx = 0.0;
            if (result.HitZ) vz = 0.0;
            if (result.HitY) vy = 0.0;

            Velocity = new Vector3d(vx, vy, vz);
            Position = new Vector3d(min.X + (Width * 0.5), min.Y, min.Z + (Width * 0.5));

            OnGround = result.Landed || VoxelCollision.IsSupported(world, min, size);
            if (OnGround) _coyoteTimer = CoyoteTime;

            // The ascent ends at the top of the arc, when the ceiling stops it, or when the feet are back
            // on something. After that the body is subject to whatever it is standing or floating in
            // again, which is what lets a jump in water turn into a float rather than a stone's descent.
            if (_jumpAscent && (vy <= 0.0 || OnGround)) _jumpAscent = false;
        }

        /// <summary>Free flight: no gravity, no collision, and the up/down controls move vertically.</summary>
        private void StepNoClip(double dt, Vector3d wishDirection, bool up, bool fast)
        {
            double speed = (fast ? SprintSpeed * 2.5 : SprintSpeed) ;
            var target = new Vector3d(wishDirection.X, up ? 1.0 : wishDirection.Y, wishDirection.Z);

            double length = target.Magnitude;
            if (length > 1e-9) target = target * (speed / System.Math.Max(length, 1.0));

            Velocity = new Vector3d(
                Approach(Velocity.X, target.X, GroundAcceleration * dt),
                Approach(Velocity.Y, target.Y, GroundAcceleration * dt),
                Approach(Velocity.Z, target.Z, GroundAcceleration * dt));

            Position += Velocity * dt;
            OnGround = false;
            InFluid = false;
            _jumpAscent = false;
        }

        /// <summary>
        /// How dense the body is relative to water. Just under 1, so a still body floats with its head out
        /// -- which is what lets a player who falls in a lake survive without swimming.
        /// </summary>
        public double BodyDensity { get; set; } = 0.8;

        /// <summary>
        /// How hard buoyancy pushes, as a multiple of the physically correct force.
        /// </summary>
        /// <remarks>
        /// Deliberately above 1, and this is a game-feel decision rather than an error. A body that is
        /// nearly neutrally buoyant really does rise at a few tens of centimetres a second, and simulating
        /// that faithfully means a player who falls into a lake spends the better part of a minute drifting
        /// up while their breath runs out. Multiplying the force gets them to the surface in a second or
        /// two, which is what everyone expects "swim up" to mean, without changing the sign of anything --
        /// dense fluids still sink a body, light ones still float it.
        /// </remarks>
        public double BuoyancyStrength { get; set; } = 3.0;

        /// <summary>
        /// Measures what the body is in: which fluid, where its surface is, how much of the body is under,
        /// and whether the head is.
        /// </summary>
        /// <remarks>
        /// <para>Submersion is computed from the <i>actual surface height</i> of the fluid in this column,
        /// not from a handful of point samples up the body. The first implementation sampled at the feet,
        /// the chest and the head and took the fraction that came back wet, which quantises submersion to
        /// quarters — and a body floats where buoyancy balances weight, which is almost never exactly a
        /// quarter. The result was a body that could not find equilibrium and bobbed between two states
        /// forever, its head ducking under and clearing several times a second. Reading the surface gives a
        /// continuous value and the body settles.</para>
        /// <para>The topmost fluid block's own level is folded in, so a half-full block gives a surface
        /// half a block up rather than a whole one. That is what stops a body floating visibly proud of a
        /// shallow puddle.</para>
        /// </remarks>
        private void SampleFluid(IVoxelRead world, IVoxelPalette palette)
        {
            int bottom = BlockPos.Floor(Position.Y);
            int top = BlockPos.Floor(Position.Y + Height);

            int surfaceBlockY = int.MinValue;
            int kind = 0;

            // Walk down from the head: the first fluid found is the surface, and that is the fluid the body
            // is in. Starting at the head rather than the feet is what lets a body wading out of a lake
            // report the lake rather than the puddle at its ankles.
            for (int y = top; y >= bottom; y--)
            {
                ushort block = world.GetBlock(new BlockPos(BlockPos.Floor(Position.X), y, BlockPos.Floor(Position.Z)));
                int here = FluidKindOf(block, palette);
                if (here == 0) continue;

                surfaceBlockY = y;
                kind = here;
                break;
            }

            FluidKind = kind;
            InFluid = kind != 0;

            if (!InFluid)
            {
                Submersion = 0.0;
                FluidDensity = 1.0;
                FluidViscosity = 1.0;
                FluidSurfaceHeight = double.NegativeInfinity;
                IsHeadSubmerged = false;
                return;
            }

            // The fluid's own fullness sets where within its block the surface sits.
            double fill = 1.0;
            if (Fluids != null)
            {
                ushort surfaceBlock = world.GetBlock(
                    new BlockPos(BlockPos.Floor(Position.X), surfaceBlockY, BlockPos.Floor(Position.Z)));
                if (!Fluids.IsFluidSource(surfaceBlock))
                {
                    int level = Fluids.FluidLevel(surfaceBlock);
                    if (level > 0) fill = level / (double)VoxelFluidSimulation.MaxLevel;
                }
            }

            FluidSurfaceHeight = surfaceBlockY + fill;

            double under = FluidSurfaceHeight - Position.Y;
            Submersion = under <= 0.0 ? 0.0 : (under >= Height ? 1.0 : under / Height);

            IsHeadSubmerged = Position.Y + EyeHeight < FluidSurfaceHeight;
            FluidDensity = Fluids != null ? Fluids.FluidDensity(kind) : 1.0;
            FluidViscosity = Fluids != null ? System.Math.Max(1, Fluids.FluidTickDelay(kind)) : 1.0;
        }

        /// <summary>The world-space height of the fluid surface in this column, or negative infinity in air.</summary>
        public double FluidSurfaceHeight { get; private set; } = double.NegativeInfinity;

        /// <summary>Which fluid a block is, falling back to "anything neither air nor solid" without a
        /// fluid palette, so a game with no fluid system still gets swimming rather than nothing.</summary>
        private int FluidKindOf(ushort block, IVoxelPalette palette)
        {
            if (Fluids != null) return Fluids.FluidKind(block);
            return !palette.IsAir(block) && !palette.IsSolid(block) ? 1 : 0;
        }

        /// <summary>Runs the breath clock.</summary>
        private void UpdateBreath(double dt)
        {
            if (!NeedsAir)
            {
                Breath = MaxBreath;
                DrowningTime = 0.0;
                return;
            }

            if (IsHeadSubmerged)
            {
                Breath -= dt;
                if (Breath <= 0.0)
                {
                    Breath = 0.0;
                    DrowningTime += dt;
                }

                return;
            }

            // Surfacing clears the drowning state at once -- the punishment for going under should end when
            // you get out, not linger and kill you on dry land.
            DrowningTime = 0.0;
            Breath = System.Math.Min(MaxBreath, Breath + (dt * BreathRecoveryRate));
        }

        /// <summary>Moves <paramref name="current"/> toward <paramref name="target"/> by at most
        /// <paramref name="maxDelta"/> — the drive that replaces force-and-friction.</summary>
        private static double Approach(double current, double target, double maxDelta)
        {
            double difference = target - current;
            if (difference > maxDelta) return current + maxDelta;
            if (difference < -maxDelta) return current - maxDelta;
            return target;
        }
    }
}
