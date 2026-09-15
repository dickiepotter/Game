namespace RP.Game.Tests.Voxels
{
    using System.Collections.Generic;
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Voxels;
    using RP.Math;

    /// <summary>
    /// A palette covering the structural and fluid systems as well as the core five questions.
    /// </summary>
    /// <remarks>
    /// Ids are laid out in ranges so a test can say what it means inline: 1 stone, 2 wood, 3 bedrock,
    /// 4 sand, 5 leaves, 6 ash, 7 mud, 20 decoration, and three fluids at 40, 50 and 60, each occupying
    /// eight level ids followed by its source.
    /// </remarks>
    internal sealed class PhysicsPalette : IVoxelPalette, IVoxelStructure, IVoxelFluid
    {
        public const ushort Air = 0;
        public const ushort Stone = 1;
        public const ushort Wood = 2;
        public const ushort Bedrock = 3;
        public const ushort Sand = 4;
        public const ushort Leaves = 5;
        public const ushort Ash = 6;
        public const ushort Mud = 7;
        public const ushort Decoration = 20;

        public const int WaterKind = 1;
        public const int LavaKind = 2;
        public const int TarKind = 3;

        public const ushort WaterBase = 40;   // levels 1..8 at 40..47, source at 48
        public const ushort LavaBase = 50;
        public const ushort TarBase = 60;

        public static ushort Water(int level) => (ushort)(WaterBase + level - 1);

        public static ushort WaterSource => (ushort)(WaterBase + 8);

        public static ushort LavaSource => (ushort)(LavaBase + 8);

        public static ushort TarSource => (ushort)(TarBase + 8);

        // ---- IVoxelPalette ----
        public bool IsAir(ushort block) => block == Air;

        public bool IsOpaque(ushort block) => block is Stone or Wood or Bedrock or Sand or Ash or Mud;

        public bool IsSolid(ushort block) => block is Stone or Wood or Bedrock or Sand or Leaves or Ash or Mud;

        public byte LightEmission(ushort block) => (byte)(FluidKind(block) == LavaKind ? 15 : 0);

        public byte LightAttenuation(ushort block) => (byte)(FluidKind(block) == WaterKind ? 3 : 1);

        public uint FaceAppearance(ushort block, BlockFace face) => block;

        public bool DrawsAgainstSelf(ushort block) => false;

        // ---- IVoxelStructure ----
        public StructuralClass StructuralClassOf(ushort block)
        {
            if (block == Air || block == Decoration || FluidKind(block) != 0) return StructuralClass.Weightless;
            if (block == Bedrock) return StructuralClass.Anchored;
            if (block == Sand) return StructuralClass.Loose;
            return StructuralClass.Supported;
        }

        public int Cohesion(ushort block) => block switch
        {
            Wood => 8,
            Leaves => 2,
            Stone => 6,
            Sand => 0,
            _ => 4,
        };

        // ---- IVoxelFluid ----
        public int FluidKind(ushort block)
        {
            if (block >= WaterBase && block <= WaterBase + 8) return WaterKind;
            if (block >= LavaBase && block <= LavaBase + 8) return LavaKind;
            if (block >= TarBase && block <= TarBase + 8) return TarKind;
            return 0;
        }

        public int FluidLevel(ushort block)
        {
            int kind = FluidKind(block);
            if (kind == 0) return 0;
            int baseId = kind == WaterKind ? WaterBase : (kind == LavaKind ? LavaBase : TarBase);
            int offset = block - baseId;
            return offset >= 8 ? VoxelFluidSimulation.MaxLevel : offset + 1;
        }

        public bool IsFluidSource(ushort block)
        {
            int kind = FluidKind(block);
            if (kind == 0) return false;
            int baseId = kind == WaterKind ? WaterBase : (kind == LavaKind ? LavaBase : TarBase);
            return block - baseId == 8;
        }

        public ushort FluidBlock(int kind, int level, bool source)
        {
            int baseId = kind == WaterKind ? WaterBase : (kind == LavaKind ? LavaBase : TarBase);
            if (source) return (ushort)(baseId + 8);
            if (level < 1) return Air;
            if (level > 8) level = 8;
            return (ushort)(baseId + level - 1);
        }

        public int FluidTickDelay(int kind) => kind switch
        {
            WaterKind => 1,
            LavaKind => 4,
            TarKind => 10,
            _ => 1,
        };

        public int FluidMaxSpread(int kind) => kind switch
        {
            WaterKind => 7,
            LavaKind => 3,
            TarKind => 2,
            _ => 4,
        };

        public double FluidDensity(int kind) => kind switch
        {
            WaterKind => 1.0,
            LavaKind => 3.1,
            TarKind => 1.4,
            _ => 1.0,
        };

        public ushort Reaction(int kindA, int kindB)
        {
            bool water = kindA == WaterKind || kindB == WaterKind;
            bool lava = kindA == LavaKind || kindB == LavaKind;
            return water && lava ? Stone : (ushort)0;
        }

        public bool IsWashedAway(ushort block) => block == Decoration;

        public ushort TransformOnContact(ushort block, int fluidKind)
            => block == Ash && fluidKind == WaterKind ? Mud : block;
    }

    /// <summary>
    /// Collision, the character controller, structural collapse, fluid flow and breathing — the systems
    /// that decide whether a voxel world behaves like a place or like a diagram.
    /// </summary>
    [TestClass]
    public sealed class VoxelWorldPhysicsTests
    {
        private static readonly PhysicsPalette Palette = new PhysicsPalette();

        private static VoxelVolume NewWorld() => new VoxelVolume(Palette);

        /// <summary>
        /// A flat floor of stone at y = 0, on bedrock, spanning a generous area.
        /// </summary>
        /// <remarks>
        /// The bedrock matters. A support search looks a fixed distance below the disturbance and treats
        /// whatever it finds on the bottom face of that box as grounded; a floor floating above the box
        /// bottom with nothing anchored anywhere is, correctly, not held up by anything, and the solver
        /// will say so. Test worlds need a real foundation for the same reason real ones do.
        /// </remarks>
        private static VoxelVolume WorldWithFloor()
        {
            VoxelVolume world = NewWorld();
            world.FillBox(new BlockPos(-32, -16, -32), new BlockPos(32, -1, 32), PhysicsPalette.Bedrock);
            world.FillBox(new BlockPos(-32, 0, -32), new BlockPos(32, 0, 32), PhysicsPalette.Stone);
            return world;
        }

        // =================================================================================================
        // Collision
        // =================================================================================================

        [TestMethod]
        public void Collision_StopsFlushAgainstAWall()
        {
            VoxelVolume world = WorldWithFloor();
            world.FillBox(new BlockPos(5, 1, -5), new BlockPos(5, 4, 5), PhysicsPalette.Stone);

            var min = new Vector3d(0.0, 1.0, 0.0);
            var size = new Vector3d(0.6, 1.8, 0.6);

            VoxelCollision.MoveResult result = VoxelCollision.Move(world, ref min, size, new Vector3d(10, 0, 0));

            result.HitX.Should().BeTrue();
            min.X.Should().BeApproximately(5.0 - 0.6, 1e-3, "the box should rest against the wall face");
            VoxelCollision.Overlaps(world, min, size).Should().BeFalse();
        }

        [TestMethod]
        public void Collision_DoesNotTunnelThroughAWallAtSpeed()
        {
            // An increment-based sweep only avoids tunnelling by being slow. Resolving against the blocks
            // the box would sweep through makes the cost independent of speed and the result exact.
            VoxelVolume world = WorldWithFloor();
            world.FillBox(new BlockPos(5, 1, -5), new BlockPos(5, 4, 5), PhysicsPalette.Stone);

            foreach (double speed in new[] { 1.0, 20.0, 200.0, 5000.0 })
            {
                var min = new Vector3d(0.0, 1.0, 0.0);
                var size = new Vector3d(0.6, 1.8, 0.6);
                VoxelCollision.Move(world, ref min, size, new Vector3d(speed, 0, 0));

                min.X.Should().BeLessThan(5.0, "speed {0} must not pass through the wall", speed);
                VoxelCollision.Overlaps(world, min, size).Should().BeFalse();
            }
        }

        [TestMethod]
        public void Collision_LandsOnTheFloorRatherThanInIt()
        {
            VoxelVolume world = WorldWithFloor();

            var min = new Vector3d(0.0, 20.0, 0.0);
            var size = new Vector3d(0.6, 1.8, 0.6);

            VoxelCollision.MoveResult result = VoxelCollision.Move(world, ref min, size, new Vector3d(0, -40, 0));

            result.Landed.Should().BeTrue();
            min.Y.Should().BeApproximately(1.0, 1e-3);
            VoxelCollision.IsSupported(world, min, size).Should().BeTrue();
        }

        [TestMethod]
        public void Collision_OverlapsIgnoresABoxRestingExactlyFlush()
        {
            // A body sitting exactly on the floor must not read as inside it, or every step would begin by
            // resolving a phantom overlap and shoving the player somewhere arbitrary.
            VoxelVolume world = WorldWithFloor();
            VoxelCollision.Overlaps(world, new Vector3d(0, 1, 0), new Vector3d(0.6, 1.8, 0.6)).Should().BeFalse();
            VoxelCollision.Overlaps(world, new Vector3d(0, 0.9, 0), new Vector3d(0.6, 1.8, 0.6)).Should().BeTrue();
        }

        // =================================================================================================
        // The character controller
        // =================================================================================================

        private static VoxelCharacter NewCharacter(VoxelVolume world, Vector3d position)
            => new VoxelCharacter { Position = position, Fluids = Palette };

        private static void Simulate(VoxelCharacter character, VoxelVolume world, double seconds,
                                     Vector3d wish = default, bool jump = false, bool sprint = false)
        {
            const double Dt = 1.0 / 60.0;
            for (double t = 0; t < seconds; t += Dt) character.Step(world, Dt, wish, jump, sprint);
        }

        [TestMethod]
        public void Character_FallsAndComesToRestOnTheGround()
        {
            VoxelVolume world = WorldWithFloor();
            VoxelCharacter character = NewCharacter(world, new Vector3d(0, 12, 0));

            Simulate(character, world, 4.0);

            character.Position.Y.Should().BeApproximately(1.0, 1e-2);
            character.OnGround.Should().BeTrue();
            character.Velocity.Y.Should().BeApproximately(0.0, 1e-6);
        }

        [TestMethod]
        public void Character_WalksUpASingleBlockStepWithoutJumping()
        {
            // Without step-up a one-block rise stops a player dead and they must jump over a doorway sill.
            // With it, terrain is walkable and the world feels continuous.
            VoxelVolume world = WorldWithFloor();
            world.FillBox(new BlockPos(3, 1, -8), new BlockPos(20, 1, 8), PhysicsPalette.Stone);

            VoxelCharacter character = NewCharacter(world, new Vector3d(0, 1, 0));
            Simulate(character, world, 0.5);   // settle

            Simulate(character, world, 3.0, wish: new Vector3d(1, 0, 0));

            character.Position.X.Should().BeGreaterThan(4.0, "the step must not have blocked the walk");
            character.Position.Y.Should().BeApproximately(2.0, 1e-2, "the body should now be on the upper level");
        }

        [TestMethod]
        public void Character_WillNotStepIntoAGapTooShortToStandIn()
        {
            // Step-up must refuse to lift a body into a one-block slot it could not stand in, or a player
            // walks into a crawlspace and is wedged there.
            VoxelVolume world = WorldWithFloor();
            world.FillBox(new BlockPos(3, 1, -8), new BlockPos(20, 1, 8), PhysicsPalette.Stone); // the step
            world.FillBox(new BlockPos(3, 3, -8), new BlockPos(20, 5, 8), PhysicsPalette.Stone); // a low ceiling over it

            VoxelCharacter character = NewCharacter(world, new Vector3d(0, 1, 0));
            Simulate(character, world, 0.5);
            Simulate(character, world, 3.0, wish: new Vector3d(1, 0, 0));

            character.Position.X.Should().BeLessThan(3.0, "there is nowhere to stand on the step");
        }

        /// <summary>
        /// Jumps once from a standing start and reports how high the body got, in blocks.
        /// </summary>
        private static double JumpHeightOf(VoxelCharacter character, VoxelVolume world, Vector3d wish = default)
        {
            const double Dt = 1.0 / 60.0;
            Simulate(character, world, 1.0, wish: wish);   // settle, and reach walking speed

            double floor = character.Position.Y;
            double peak = floor;

            for (int i = 0; i < 180; i++)
            {
                character.Step(world, Dt, wish, jump: i < 2, sprint: false);
                if (character.Position.Y > peak) peak = character.Position.Y;
                if (i > 4 && character.OnGround) break;
            }

            return peak - floor;
        }

        [TestMethod]
        public void Character_JumpClearsTwoBlocks()
        {
            // The world is built in multiples of one block, so the jump is stated in them. Two, because a
            // player navigating terrain asks "can I get onto that?" and the answer should be yes for a
            // two-high step and no for a three-high one -- a wall you have to build stairs for.
            VoxelVolume world = WorldWithFloor();
            VoxelCharacter character = NewCharacter(world, new Vector3d(0, 1, 0));

            double height = JumpHeightOf(character, world);

            height.Should().BeGreaterThan(2.0, "a jump must clear a two-block ledge");
            height.Should().BeLessThan(3.0, "but not a three-block one");
        }

        [TestMethod]
        public void Character_CanActuallyLandOnATwoBlockLedge()
        {
            // Apex height is necessary and not sufficient: the body also has to travel far enough forward
            // while it is up there. A jump that reaches two blocks straight up and stalls is a jump a
            // player cannot use to get anywhere.
            VoxelVolume world = WorldWithFloor();
            world.FillBox(new BlockPos(3, 1, -8), new BlockPos(20, 2, 8), PhysicsPalette.Stone);

            VoxelCharacter character = NewCharacter(world, new Vector3d(-4, 1, 0));
            const double Dt = 1.0 / 60.0;
            var forward = new Vector3d(1, 0, 0);

            // Walk into the ledge first, the way a player does: two blocks is above the step-up height, so
            // the body comes to a stop against it and the jump starts from there rather than from a
            // standstill four blocks back.
            for (int i = 0; i < 240; i++) character.Step(world, Dt, forward, false, false);
            character.Position.X.Should().BeApproximately(2.7, 0.05, "stopped against the face of the ledge");

            for (int i = 0; i < 180; i++) character.Step(world, Dt, forward, jump: i < 2, sprint: false);

            character.Position.Y.Should().BeApproximately(3.0, 1e-2, "the body should be standing on the ledge");
            character.Position.X.Should().BeGreaterThan(3.0);
        }

        [TestMethod]
        public void Character_CanChangeDirectionInMidAir()
        {
            // "Jump two blocks and move forward or backward." Air control has to be enough to matter and
            // not so much that a jump stops being a commitment.
            VoxelVolume world = WorldWithFloor();

            VoxelCharacter forward = NewCharacter(world, new Vector3d(0, 1, 0));
            VoxelCharacter backward = NewCharacter(world, new Vector3d(0, 1, 8));

            const double Dt = 1.0 / 60.0;
            for (int i = 0; i < 90; i++)
            {
                forward.Step(world, Dt, new Vector3d(1, 0, 0), jump: i < 2, sprint: false);
                backward.Step(world, Dt, new Vector3d(-1, 0, 0), jump: i < 2, sprint: false);
            }

            forward.Position.X.Should().BeGreaterThan(1.5, "a jump carries you forward");
            backward.Position.X.Should().BeLessThan(-1.5, "and backward just as readily");
        }

        [TestMethod]
        public void Character_JumpsLowerStandingInWater()
        {
            // Half a block off the top. Enough to notice at once -- a ledge you were hopping onto a moment
            // ago is out of reach -- without making a shallow stream into a wall.
            VoxelVolume world = WorldWithFloor();
            world.FillBox(new BlockPos(-16, 1, -16), new BlockPos(16, 1, 16), PhysicsPalette.WaterSource);

            VoxelCharacter character = NewCharacter(world, new Vector3d(0, 1, 0));
            double height = JumpHeightOf(character, world);

            height.Should().BeInRange(1.35, 1.8, "about a block and a half");
        }

        [TestMethod]
        public void Character_JumpsLowerStillStandingInSomethingThick()
        {
            // One block, and no more. Wading into tar should feel like a decision: you can climb out of a
            // one-deep channel and you cannot bound out of a pit you walked into.
            VoxelVolume world = WorldWithFloor();
            world.FillBox(new BlockPos(-16, 1, -16), new BlockPos(16, 1, 16), PhysicsPalette.TarSource);

            VoxelCharacter character = NewCharacter(world, new Vector3d(0, 1, 0));
            double height = JumpHeightOf(character, world);

            height.Should().BeInRange(0.85, 1.25, "about one block");
        }

        [TestMethod]
        public void Character_ThickerFluidsNeverJumpHigherThanThinnerOnes()
        {
            // The ordering is the promise; the exact numbers are tuning. A fluid added later has to land
            // somewhere sensible without being special-cased in the controller, and it does because the
            // height comes from the fluid's own tick delay rather than from a table of names.
            double Height(ushort fluid)
            {
                VoxelVolume pool = WorldWithFloor();
                if (fluid != PhysicsPalette.Air) pool.FillBox(new BlockPos(-16, 1, -16), new BlockPos(16, 1, 16), fluid);
                return JumpHeightOf(NewCharacter(pool, new Vector3d(0, 1, 0)), pool);
            }

            double dry = Height(PhysicsPalette.Air);
            double water = Height(PhysicsPalette.WaterSource);
            double tar = Height(PhysicsPalette.TarSource);

            dry.Should().BeGreaterThan(water);
            water.Should().BeGreaterThan(tar);
        }

        [TestMethod]
        public void Character_DoesNotJumpOutOfLavaBecauseItIsFloatingOnIt()
        {
            // Lava is three times the density of water, so a body does not stand in it to push off -- it
            // rides on top. That is not a missing jump, it is the buoyancy model being right, and the
            // ordering test above deliberately leaves lava out for exactly this reason.
            VoxelVolume world = WorldWithFloor();
            world.FillBox(new BlockPos(-16, 1, -16), new BlockPos(16, 4, 16), PhysicsPalette.LavaSource);

            VoxelCharacter character = NewCharacter(world, new Vector3d(0, 1, 0));
            Simulate(character, world, 3.0);

            character.OnGround.Should().BeFalse("nothing is holding it up but the lava");
            character.Position.Y.Should().BeGreaterThan(2.0, "it has risen toward the surface");
        }

        [TestMethod]
        public void Character_CanStillJumpOutOfAShallowStream()
        {
            // The bug this replaced: standing ankle-deep in water, the jump button did nothing at all,
            // because being in fluid at all switched the controller into swimming. That reads as the
            // controls having broken rather than as the water being heavy.
            VoxelVolume world = WorldWithFloor();
            world.FillBox(new BlockPos(-16, 1, -16), new BlockPos(16, 1, 16), PhysicsPalette.WaterSource);
            world.FillBox(new BlockPos(4, 1, -16), new BlockPos(16, 1, 16), PhysicsPalette.Stone);

            VoxelCharacter character = NewCharacter(world, new Vector3d(0, 1, 0));
            Simulate(character, world, 0.5);

            const double Dt = 1.0 / 60.0;
            for (int i = 0; i < 180; i++) character.Step(world, Dt, new Vector3d(1, 0, 0), jump: i < 2, sprint: false);

            character.Position.Y.Should().BeApproximately(2.0, 1e-2, "out of the stream and onto the bank");
        }

        [TestMethod]
        public void Character_ReportsHowFastItWasGoingWhenItLanded()
        {
            // What fall damage is worked out from. Speed rather than distance, because distance stops
            // being a proxy for impact the moment water, drag or a push is involved.
            VoxelVolume world = WorldWithFloor();
            VoxelCharacter character = NewCharacter(world, new Vector3d(0, 40, 0));

            const double Dt = 1.0 / 60.0;
            double reported = 0;

            for (int i = 0; i < 600; i++)
            {
                character.Step(world, Dt, default, false, false);
                if (character.LandingSpeed > 0) { reported = character.LandingSpeed; break; }
            }

            // Fell 39 blocks under gravity 25: sqrt(2 * 25 * 39) is about 44.
            reported.Should().BeInRange(40.0, 48.0);
        }

        [TestMethod]
        public void Character_ReportsNoImpactWhenItIsNotLanding()
        {
            VoxelVolume world = WorldWithFloor();
            VoxelCharacter character = NewCharacter(world, new Vector3d(0, 1, 0));
            Simulate(character, world, 1.0);

            character.LandingSpeed.Should().Be(0.0, "standing still is not an impact");

            Simulate(character, world, 0.2, wish: new Vector3d(1, 0, 0));
            character.LandingSpeed.Should().Be(0.0, "nor is walking along the floor");
        }

        [TestMethod]
        public void Character_ArrivesGentlyWhenItFallsIntoWater()
        {
            // The reason speed is the right measure: the same drop is lethal onto stone and survivable into
            // a lake, and nothing has to say so -- drag and buoyancy are already in the velocity.
            const double Dt = 1.0 / 60.0;

            double Drop(VoxelVolume into)
            {
                VoxelCharacter body = NewCharacter(into, new Vector3d(0, 40, 0));
                for (int i = 0; i < 900; i++)
                {
                    body.Step(into, Dt, default, false, false);
                    if (body.LandingSpeed > 0) return body.LandingSpeed;
                }

                return 0;
            }

            double ontoStone = Drop(WorldWithFloor());
            double intoWater = Drop(PoolWorld(depth: 8));

            intoWater.Should().BeLessThan(ontoStone * 0.6, "the water should take most of the impact out");
        }

        [TestMethod]
        public void Character_CoyoteTime_AllowsAJumpJustAfterLeavingAnEdge()
        {
            VoxelVolume world = NewWorld();
            world.FillBox(new BlockPos(-8, 0, -8), new BlockPos(0, 0, 8), PhysicsPalette.Stone);

            VoxelCharacter character = NewCharacter(world, new Vector3d(-1, 1, 0));
            Simulate(character, world, 0.5);

            const double Dt = 1.0 / 60.0;

            // Walk until the body genuinely leaves the ledge -- the body is 0.6 wide, so it stays supported
            // for a few frames after its centre passes the edge.
            int frames = 0;
            while (character.OnGround && frames++ < 240)
            {
                character.Step(world, Dt, new Vector3d(1, 0, 0), false, false);
            }

            character.OnGround.Should().BeFalse("we have walked off the ledge");
            character.Velocity.Y.Should().BeLessThan(0.0, "and started to fall");

            // Now jump, a frame late. A strict on-ground test would swallow this; coyote time must not.
            character.Step(world, Dt, new Vector3d(1, 0, 0), jump: true, sprint: false);
            character.Velocity.Y.Should().BeGreaterThan(0.0, "coyote time should still allow the jump");
        }

        [TestMethod]
        public void Character_SprintIsFasterThanWalking()
        {
            VoxelVolume world = WorldWithFloor();

            VoxelCharacter walker = NewCharacter(world, new Vector3d(0, 1, 0));
            VoxelCharacter sprinter = NewCharacter(world, new Vector3d(0, 1, 8));

            Simulate(walker, world, 2.0, wish: new Vector3d(1, 0, 0));
            Simulate(sprinter, world, 2.0, wish: new Vector3d(1, 0, 0), sprint: true);

            sprinter.Position.X.Should().BeGreaterThan(walker.Position.X * 1.5);
        }

        [TestMethod]
        public void Character_DiagonalInputIsNoFasterThanStraight()
        {
            // Clamping rather than normalising the wish vector is what stops the oldest movement exploit in
            // the genre: running diagonally at root-two times the intended speed.
            VoxelVolume world = WorldWithFloor();

            VoxelCharacter straight = NewCharacter(world, new Vector3d(0, 1, 0));
            VoxelCharacter diagonal = NewCharacter(world, new Vector3d(0, 1, 10));

            Simulate(straight, world, 2.0, wish: new Vector3d(1, 0, 0));
            Simulate(diagonal, world, 2.0, wish: new Vector3d(1, 0, 1));

            double straightDistance = straight.Position.X;
            double diagonalDistance = System.Math.Sqrt(
                (diagonal.Position.X * diagonal.Position.X) +
                ((diagonal.Position.Z - 10) * (diagonal.Position.Z - 10)));

            diagonalDistance.Should().BeLessThan(straightDistance * 1.02);
        }

        [TestMethod]
        public void Character_NeverEndsInsideASolidBlock()
        {
            // The invariant that matters most: whatever the input, a body must never finish a step embedded
            // in the world.
            VoxelVolume world = WorldWithFloor();
            var rng = new System.Random(1234);
            for (int i = 0; i < 400; i++)
            {
                world.SetBlock(new BlockPos(rng.Next(-10, 10), rng.Next(1, 6), rng.Next(-10, 10)), PhysicsPalette.Stone);
            }

            VoxelCharacter character = NewCharacter(world, new Vector3d(0.5, 8, 0.5));
            const double Dt = 1.0 / 60.0;

            for (int i = 0; i < 1200; i++)
            {
                var wish = new Vector3d(rng.NextDouble() * 2 - 1, 0, rng.NextDouble() * 2 - 1);
                character.Step(world, Dt, wish, rng.NextDouble() < 0.1, rng.NextDouble() < 0.3);

                VoxelCollision.Overlaps(world, character.BoxMin, character.BoxSize)
                    .Should().BeFalse("step {0} left the body inside the world at {1}", i, character.Position);
            }
        }

        // =================================================================================================
        // Structural integrity -- the cases from the brief, worked literally
        // =================================================================================================

        /// <summary>Builds a tree: a four-by-four trunk rising from the ground, with a side branch.</summary>
        private static VoxelVolume TreeWorld(int trunkHeight = 12, int branchLength = 5, int branchY = 8)
        {
            VoxelVolume world = NewWorld();
            world.FillBox(new BlockPos(-24, -2, -24), new BlockPos(24, -1, 24), PhysicsPalette.Bedrock);
            world.FillBox(new BlockPos(-24, 0, -24), new BlockPos(24, 0, 24), PhysicsPalette.Stone);

            // A four-by-four trunk from y = 1 upward.
            world.FillBox(new BlockPos(0, 1, 0), new BlockPos(3, trunkHeight, 3), PhysicsPalette.Wood);

            // A branch running out along +X from the trunk's side.
            world.FillBox(new BlockPos(4, branchY, 1), new BlockPos(3 + branchLength, branchY, 1), PhysicsPalette.Wood);

            return world;
        }

        [TestMethod]
        public void Structure_ATowerOfAnyHeightStands()
        {
            // Vertical stacking is free, which is what stops a well-meaning integrity system making the
            // game unbuildable.
            VoxelVolume world = WorldWithFloor();
            world.FillBox(new BlockPos(0, 1, 0), new BlockPos(0, 120, 0), PhysicsPalette.Stone);

            var unsupported = new List<BlockPos>();
            VoxelStructure.FindUnsupported(
                world, Palette, new BlockPos(0, 60, 0), unsupported,
                new VoxelStructure.SearchBounds(horizontalRadius: 8, heightAbove: 140, depthBelow: 4, maxVisits: 262144));

            unsupported.Should().BeEmpty();
        }

        [TestMethod]
        public void Structure_RemovingOneBlockOfAFourWideTrunkDoesNotFellTheTree()
        {
            // The brief's case, literally: the block above the hole is touching three neighbours that reach
            // the ground, so it costs one sideways step, and everything stacked above inherits that for
            // free. Well inside wood's cohesion of eight.
            VoxelVolume world = TreeWorld();
            var hole = new BlockPos(0, 6, 0);
            world.SetBlock(hole, PhysicsPalette.Air);

            var unsupported = new List<BlockPos>();
            bool anyFell = VoxelStructure.FindUnsupported(world, Palette, hole, unsupported);

            anyFell.Should().BeFalse("three quarters of the trunk still reaches the ground");
        }

        [TestMethod]
        public void Structure_RemovingAWholeHorizontalSliceFellsEverythingAbove()
        {
            // The other half of the brief's case. With all four trunk blocks gone, every block above has
            // nothing beneath it and no sideways neighbour with a path down either -- they are all in the
            // same predicament -- so the crown comes down.
            VoxelVolume world = TreeWorld();
            world.FillBox(new BlockPos(0, 6, 0), new BlockPos(3, 6, 3), PhysicsPalette.Air);

            var unsupported = new List<BlockPos>();
            bool anyFell = VoxelStructure.FindUnsupported(world, Palette, new BlockPos(0, 6, 0), unsupported);

            anyFell.Should().BeTrue();

            // Everything above the cut, and nothing below it.
            foreach (BlockPos p in unsupported) p.Y.Should().BeGreaterThan(6);
            unsupported.Should().Contain(new BlockPos(0, 7, 0));
            unsupported.Should().Contain(new BlockPos(3, 12, 3));
        }

        [TestMethod]
        public void Structure_ChoppingABranchAtItsBaseDropsOnlyTheBranch()
        {
            VoxelVolume world = TreeWorld(branchLength: 5, branchY: 8);
            var cut = new BlockPos(4, 8, 1);
            world.SetBlock(cut, PhysicsPalette.Air);

            var unsupported = new List<BlockPos>();
            VoxelStructure.FindUnsupported(world, Palette, cut, unsupported).Should().BeTrue();

            // Only the rest of the branch comes down.
            foreach (BlockPos p in unsupported)
            {
                p.X.Should().BeGreaterThan(4, "only the severed branch should fall, not the trunk");
                p.Y.Should().Be(8);
            }

            unsupported.Should().Contain(new BlockPos(5, 8, 1));
            unsupported.Should().NotContain(new BlockPos(3, 8, 1), "that block is part of the trunk");
        }

        [TestMethod]
        public void Structure_ABranchCannotReachFurtherThanItsCohesion()
        {
            // Cohesion is a real limit, not decoration: wood at 8 gives branches of believable length and
            // refuses a nine-block cantilever.
            VoxelVolume world = TreeWorld(branchLength: 14, branchY: 8);

            var unsupported = new List<BlockPos>();
            VoxelStructure.FindUnsupported(world, Palette, new BlockPos(4, 8, 1), unsupported).Should().BeTrue();

            // The trunk's outer face is x = 3, so the branch block at x = 3 + n costs n steps.
            foreach (BlockPos p in unsupported) (p.X - 3).Should().BeGreaterThan(8);
            unsupported.Should().NotContain(new BlockPos(11, 8, 1), "eight steps out is exactly at the limit");
            unsupported.Should().Contain(new BlockPos(12, 8, 1), "nine steps out is past it");
        }

        [TestMethod]
        public void Structure_LooseMaterialNeedsSomethingDirectlyBeneathIt()
        {
            // Sand has no cohesion at all: one block sideways from support is already falling.
            VoxelVolume world = WorldWithFloor();
            world.SetBlock(new BlockPos(0, 1, 0), PhysicsPalette.Stone);  // a one-block pillar on the floor
            world.SetBlock(new BlockPos(0, 2, 0), PhysicsPalette.Sand);   // directly above it: held
            world.SetBlock(new BlockPos(1, 2, 0), PhysicsPalette.Sand);   // beside it, over nothing: falls

            var unsupported = new List<BlockPos>();
            VoxelStructure.FindUnsupported(world, Palette, new BlockPos(0, 1, 0), unsupported);

            unsupported.Should().Contain(new BlockPos(1, 2, 0), "sand has no cohesion to span even one block");
            unsupported.Should().NotContain(new BlockPos(0, 2, 0), "but it is happy directly on top of something");
        }

        [TestMethod]
        public void Structure_AnchoredBlocksNeverFall()
        {
            VoxelVolume world = NewWorld();
            world.SetBlock(new BlockPos(0, 40, 0), PhysicsPalette.Bedrock);

            var unsupported = new List<BlockPos>();
            VoxelStructure.FindUnsupported(world, Palette, new BlockPos(0, 40, 0), unsupported);

            unsupported.Should().NotContain(new BlockPos(0, 40, 0));
        }

        [TestMethod]
        public void Structure_FluidsAndDecorationsNeitherFallNorHoldAnythingUp()
        {
            // A sea must not hold up a cliff, and a torch must not brace a ceiling.
            VoxelVolume world = NewWorld();
            world.FillBox(new BlockPos(-8, 0, -8), new BlockPos(8, 0, 8), PhysicsPalette.Bedrock);
            world.FillBox(new BlockPos(-8, 1, -8), new BlockPos(8, 5, 8), PhysicsPalette.WaterSource);
            world.SetBlock(new BlockPos(0, 6, 0), PhysicsPalette.Stone); // floating above the water

            var unsupported = new List<BlockPos>();
            VoxelStructure.FindUnsupported(world, Palette, new BlockPos(0, 6, 0), unsupported);

            unsupported.Should().Contain(new BlockPos(0, 6, 0), "water gives no support");
            foreach (BlockPos p in unsupported)
            {
                Palette.FluidKind(world.GetBlock(p)).Should().Be(0, "fluids are weightless and never fall as blocks");
            }
        }

        [TestMethod]
        public void Structure_Collapse_DropsBlocksOntoWhatIsBeneath()
        {
            VoxelVolume world = WorldWithFloor();
            world.SetBlock(new BlockPos(0, 9, 0), PhysicsPalette.Stone);

            var falls = new List<VoxelStructure.FallEvent>();
            int moved = VoxelStructure.Collapse(world, Palette, new[] { new BlockPos(0, 9, 0) }, falls);

            moved.Should().Be(1);
            world.GetBlock(new BlockPos(0, 9, 0)).Should().Be(PhysicsPalette.Air);
            world.GetBlock(new BlockPos(0, 1, 0)).Should().Be(PhysicsPalette.Stone);
            falls[0].Distance.Should().Be(8);
        }

        [TestMethod]
        public void Structure_SettleAfterRemoval_BringsTheCrownAllTheWayDown()
        {
            VoxelVolume world = TreeWorld(trunkHeight: 10, branchLength: 3, branchY: 7);
            world.FillBox(new BlockPos(0, 5, 0), new BlockPos(3, 5, 3), PhysicsPalette.Air);

            var falls = new List<VoxelStructure.FallEvent>();
            int moved = VoxelStructure.SettleAfterRemoval(world, Palette, new BlockPos(0, 5, 0), falls);

            moved.Should().BeGreaterThan(0);

            // The crown did not vanish: it settled back onto the stump, so the cut is filled and the
            // column is now one block shorter than it was.
            world.GetBlock(new BlockPos(0, 5, 0)).Should().Be(PhysicsPalette.Wood, "the cut should be filled by what fell into it");
            world.GetBlock(new BlockPos(0, 10, 0)).Should().Be(PhysicsPalette.Air, "and the top of the trunk should now be empty");
        }

        // =================================================================================================
        // Fluids
        // =================================================================================================

        private static VoxelFluidSimulation NewFluidSim(VoxelVolume world) => new VoxelFluidSimulation(world, Palette);

        [TestMethod]
        public void Fluid_PoursStraightDownAShaftWithoutThinning()
        {
            // Falling fluid arrives full however far it drops: a trickle at the top of a cliff is a
            // full-strength pour at the bottom, which is what makes a waterfall a column and not a cone.
            VoxelVolume world = NewWorld();
            world.FillBox(new BlockPos(-2, -1, -2), new BlockPos(2, -1, 2), PhysicsPalette.Bedrock);
            world.SetBlock(new BlockPos(0, 20, 0), PhysicsPalette.WaterSource);

            VoxelFluidSimulation sim = NewFluidSim(world);
            sim.ScheduleNeighbourhood(new BlockPos(0, 20, 0));
            sim.RunToSettle();

            for (int y = 0; y < 20; y++)
            {
                ushort block = world.GetBlock(new BlockPos(0, y, 0));
                Palette.FluidKind(block).Should().Be(PhysicsPalette.WaterKind, "the column should be wet at y={0}", y);
                Palette.FluidLevel(block).Should().Be(VoxelFluidSimulation.MaxLevel, "falling water is full at y={0}", y);
            }
        }

        [TestMethod]
        public void Fluid_SpreadsSidewaysExactlyAsFarAsItsReach()
        {
            VoxelVolume world = NewWorld();
            world.FillBox(new BlockPos(-24, 0, -24), new BlockPos(24, 0, 24), PhysicsPalette.Bedrock);
            world.SetBlock(new BlockPos(0, 1, 0), PhysicsPalette.WaterSource);

            VoxelFluidSimulation sim = NewFluidSim(world);
            sim.ScheduleNeighbourhood(new BlockPos(0, 1, 0));
            sim.RunToSettle();

            // Water reaches 7, so the seventh block along is the last wet one.
            Palette.FluidKind(world.GetBlock(new BlockPos(7, 1, 0))).Should().Be(PhysicsPalette.WaterKind);
            Palette.FluidKind(world.GetBlock(new BlockPos(8, 1, 0))).Should().Be(0, "beyond its reach");
        }

        [TestMethod]
        public void Fluid_LavaSpreadsMuchLessFarThanWater()
        {
            VoxelVolume world = NewWorld();
            world.FillBox(new BlockPos(-24, 0, -24), new BlockPos(24, 0, 24), PhysicsPalette.Bedrock);
            world.SetBlock(new BlockPos(0, 1, 0), PhysicsPalette.LavaSource);

            VoxelFluidSimulation sim = NewFluidSim(world);
            sim.ScheduleNeighbourhood(new BlockPos(0, 1, 0));
            sim.RunToSettle();

            Palette.FluidKind(world.GetBlock(new BlockPos(3, 1, 0))).Should().Be(PhysicsPalette.LavaKind);
            Palette.FluidKind(world.GetBlock(new BlockPos(4, 1, 0))).Should().Be(0, "lava makes a pool, not a flood");
        }

        [TestMethod]
        public void Fluid_DrainsAwayWhenItsSourceIsRemoved()
        {
            VoxelVolume world = NewWorld();
            world.FillBox(new BlockPos(-24, 0, -24), new BlockPos(24, 0, 24), PhysicsPalette.Bedrock);
            var source = new BlockPos(0, 1, 0);
            world.SetBlock(source, PhysicsPalette.WaterSource);

            VoxelFluidSimulation sim = NewFluidSim(world);
            sim.ScheduleNeighbourhood(source);
            sim.RunToSettle();
            Palette.FluidKind(world.GetBlock(new BlockPos(4, 1, 0))).Should().Be(PhysicsPalette.WaterKind);

            world.SetBlock(source, PhysicsPalette.Air);
            sim.ScheduleNeighbourhood(source);
            sim.RunToSettle();

            for (int x = -7; x <= 7; x++)
            {
                Palette.FluidKind(world.GetBlock(new BlockPos(x, 1, 0)))
                       .Should().Be(0, "everything should have drained at x={0}", x);
            }
        }

        [TestMethod]
        public void Fluid_WaterMeetingLavaMakesRock()
        {
            VoxelVolume world = NewWorld();
            world.FillBox(new BlockPos(-8, 0, -8), new BlockPos(8, 0, 8), PhysicsPalette.Bedrock);
            world.SetBlock(new BlockPos(-1, 1, 0), PhysicsPalette.WaterSource);
            world.SetBlock(new BlockPos(1, 1, 0), PhysicsPalette.LavaSource);

            VoxelFluidSimulation sim = NewFluidSim(world);
            sim.ScheduleNeighbourhood(new BlockPos(-1, 1, 0));
            sim.ScheduleNeighbourhood(new BlockPos(1, 1, 0));
            sim.RunToSettle();

            world.GetBlock(new BlockPos(0, 1, 0)).Should().Be(PhysicsPalette.Stone, "the two should have met and set");
        }

        [TestMethod]
        public void Fluid_WashesAwayLooseDecorationButNotStone()
        {
            VoxelVolume world = NewWorld();
            world.FillBox(new BlockPos(-8, 0, -8), new BlockPos(8, 0, 8), PhysicsPalette.Bedrock);
            world.SetBlock(new BlockPos(2, 1, 0), PhysicsPalette.Decoration);
            world.SetBlock(new BlockPos(3, 1, 0), PhysicsPalette.Stone);
            world.SetBlock(new BlockPos(0, 1, 0), PhysicsPalette.WaterSource);

            VoxelFluidSimulation sim = NewFluidSim(world);
            sim.ScheduleNeighbourhood(new BlockPos(0, 1, 0));
            sim.RunToSettle();

            Palette.FluidKind(world.GetBlock(new BlockPos(2, 1, 0)))
                   .Should().Be(PhysicsPalette.WaterKind, "the flood should have taken the grass");
            world.GetBlock(new BlockPos(3, 1, 0)).Should().Be(PhysicsPalette.Stone, "but not the stone");
        }

        [TestMethod]
        public void Fluid_TransformsMaterialsThatBehaveDifferentlyWhenWet()
        {
            // The hook that lets a game build chemistry on the fluid system without the engine knowing any:
            // ash becomes mud the moment water reaches it.
            VoxelVolume world = NewWorld();
            world.FillBox(new BlockPos(-8, 0, -8), new BlockPos(8, 0, 8), PhysicsPalette.Bedrock);
            world.SetBlock(new BlockPos(2, 1, 0), PhysicsPalette.Ash);
            world.SetBlock(new BlockPos(0, 1, 0), PhysicsPalette.WaterSource);

            VoxelFluidSimulation sim = NewFluidSim(world);
            sim.ScheduleNeighbourhood(new BlockPos(0, 1, 0));
            sim.RunToSettle();

            world.GetBlock(new BlockPos(2, 1, 0)).Should().Be(PhysicsPalette.Mud);
        }

        [TestMethod]
        public void Fluid_ViscosityMakesTarSlowerThanWater()
        {
            // Same spill, same geometry: only the tick delay differs, and that alone should make one of
            // them take several times as long to settle.
            int waterTicks = TicksToSettle(PhysicsPalette.WaterSource);
            int tarTicks = TicksToSettle(PhysicsPalette.TarSource);

            tarTicks.Should().BeGreaterThan(waterTicks * 3);

            static int TicksToSettle(ushort source)
            {
                VoxelVolume world = NewWorld();
                world.FillBox(new BlockPos(-16, 0, -16), new BlockPos(16, 0, 16), PhysicsPalette.Bedrock);
                world.SetBlock(new BlockPos(0, 1, 0), source);

                VoxelFluidSimulation sim = NewFluidSim(world);
                sim.ScheduleNeighbourhood(new BlockPos(0, 1, 0));
                return sim.RunToSettle(4096);
            }
        }

        [TestMethod]
        public void Fluid_SettlesRatherThanFlickeringForever()
        {
            // The failure mode that volume-conserving fluids fall into: a puddle that never quite lands,
            // oscillating between levels because the arithmetic never resolves. Integer levels converge.
            VoxelVolume world = NewWorld();
            world.FillBox(new BlockPos(-16, 0, -16), new BlockPos(16, 0, 16), PhysicsPalette.Bedrock);
            world.FillBox(new BlockPos(-4, 1, -4), new BlockPos(-4, 6, 4), PhysicsPalette.Stone);
            world.SetBlock(new BlockPos(0, 6, 0), PhysicsPalette.WaterSource);

            VoxelFluidSimulation sim = NewFluidSim(world);
            sim.ScheduleNeighbourhood(new BlockPos(0, 6, 0));

            int ticks = sim.RunToSettle(2048);
            ticks.Should().BeLessThan(2048, "the simulation must reach a stable state");
            sim.IsSettled.Should().BeTrue();
        }

        [TestMethod]
        public void Fluid_RespectsItsPerTickBudget()
        {
            VoxelVolume world = NewWorld();
            world.FillBox(new BlockPos(-40, 0, -40), new BlockPos(40, 0, 40), PhysicsPalette.Bedrock);
            world.SetBlock(new BlockPos(0, 1, 0), PhysicsPalette.WaterSource);

            var sim = new VoxelFluidSimulation(world, Palette) { UpdateBudget = 8 };
            sim.ScheduleNeighbourhood(new BlockPos(0, 1, 0));

            for (int i = 0; i < 20; i++)
            {
                sim.Tick();
                sim.LastUpdateCount.Should().BeLessThanOrEqualTo(8);
            }
        }

        // =================================================================================================
        // Breathing and buoyancy
        // =================================================================================================

        /// <summary>
        /// A flooded chamber with a solid roof, so a body cannot simply float up out of it.
        /// </summary>
        /// <remarks>
        /// Needed because buoyancy works: a body dropped into open water surfaces within a second or two,
        /// which is correct and makes it useless for testing what happens when you cannot. Drowning needs
        /// somewhere you cannot surface from -- which is also the only situation in which it happens in
        /// play.
        /// </remarks>
        private static VoxelVolume FloodedChamberWorld()
        {
            VoxelVolume world = NewWorld();
            world.FillBox(new BlockPos(-16, -8, -16), new BlockPos(16, 0, 16), PhysicsPalette.Stone);
            world.FillBox(new BlockPos(-16, 1, -16), new BlockPos(16, 8, 16), PhysicsPalette.WaterSource);
            world.FillBox(new BlockPos(-16, 9, -16), new BlockPos(16, 12, 16), PhysicsPalette.Stone);
            return world;
        }

        /// <summary>A pool of water deep enough to swim in, over a stone floor.</summary>
        private static VoxelVolume PoolWorld(int depth = 10)
        {
            VoxelVolume world = NewWorld();
            world.FillBox(new BlockPos(-16, -1, -16), new BlockPos(16, 0, 16), PhysicsPalette.Stone);
            world.FillBox(new BlockPos(-16, 1, -16), new BlockPos(16, depth, 16), PhysicsPalette.WaterSource);
            return world;
        }

        [TestMethod]
        public void Breathing_DepletesOnlyWhileTheHeadIsUnder()
        {
            // Wading chest-deep must not start the timer. What a player understands is "my head went under".
            VoxelVolume world = PoolWorld(depth: 1);   // one block deep: wading
            VoxelCharacter wader = NewCharacter(world, new Vector3d(0, 1, 0));
            Simulate(wader, world, 5.0);

            wader.IsHeadSubmerged.Should().BeFalse();
            wader.Breath.Should().Be(wader.MaxBreath);

            // Held under by a roof, because an unroofed body simply floats up and breathes -- which is the
            // behaviour the buoyancy test asserts, and the reason this one cannot use open water.
            VoxelVolume deep = FloodedChamberWorld();
            VoxelCharacter diver = NewCharacter(deep, new Vector3d(0, 3, 0));
            Simulate(diver, deep, 5.0);

            diver.IsHeadSubmerged.Should().BeTrue();
            diver.Breath.Should().BeLessThan(diver.MaxBreath);
        }

        [TestMethod]
        public void Breathing_RunsOutAndThenStartsDrowning()
        {
            VoxelVolume world = FloodedChamberWorld();
            VoxelCharacter diver = NewCharacter(world, new Vector3d(0, 3, 0));
            diver.MaxBreath = 2.0;

            Simulate(diver, world, 1.0);
            diver.IsHeadSubmerged.Should().BeTrue("the chamber has a roof; there is nowhere to surface");
            diver.Breath.Should().BeGreaterThan(0.0);
            diver.IsDrowning.Should().BeFalse();

            Simulate(diver, world, 3.0);
            diver.Breath.Should().Be(0.0);
            diver.IsDrowning.Should().BeTrue();
            diver.DrowningTime.Should().BeGreaterThan(0.5);
        }

        [TestMethod]
        public void Breathing_RecoversTheMomentTheHeadClearsTheSurface()
        {
            // Surfacing must end the punishment at once, not leave a player dying on dry land.
            VoxelVolume world = FloodedChamberWorld();
            VoxelCharacter diver = NewCharacter(world, new Vector3d(0, 3, 0));
            diver.MaxBreath = 2.0;

            Simulate(diver, world, 4.0);
            diver.IsDrowning.Should().BeTrue();

            // Lift them out onto dry land.
            VoxelVolume dry = WorldWithFloor();
            diver.Position = new Vector3d(0, 1, 0);
            Simulate(diver, dry, 0.2);

            diver.IsDrowning.Should().BeFalse("drowning stops on surfacing");
            diver.DrowningTime.Should().Be(0.0);

            Simulate(diver, dry, 2.0);
            diver.Breath.Should().Be(diver.MaxBreath, "breath comes back quickly");
        }

        [TestMethod]
        public void Breathing_CreaturesThatDoNotNeedAirAreUnaffected()
        {
            VoxelVolume world = FloodedChamberWorld();
            VoxelCharacter fish = NewCharacter(world, new Vector3d(0, 3, 0));
            fish.NeedsAir = false;

            Simulate(fish, world, 30.0);

            fish.Breath.Should().Be(fish.MaxBreath);
            fish.IsDrowning.Should().BeFalse();
        }

        [TestMethod]
        public void Buoyancy_ABodyFloatsInWaterInsteadOfSinkingToTheBottom()
        {
            // Body density just under water's, so a player who falls in a lake bobs back up rather than
            // walking along the bottom until they drown.
            VoxelVolume world = PoolWorld(depth: 14);
            VoxelCharacter swimmer = NewCharacter(world, new Vector3d(0, 3, 0));

            Simulate(swimmer, world, 12.0);

            swimmer.Position.Y.Should().BeGreaterThan(12.0, "buoyancy should have carried them to the surface");
            swimmer.IsHeadSubmerged.Should().BeFalse("and their head should be clear of it");
            swimmer.Submersion.Should().BeInRange(0.6, 0.95, "a floating body sits mostly, but not wholly, under");
        }

        [TestMethod]
        public void Buoyancy_DenseFluidsHoldABodyHigherThanWaterDoes()
        {
            // Same body, same depth, different fluid: the only thing that differs is the density the game
            // reports, and that alone should change how it floats.
            VoxelVolume water = PoolWorld(depth: 14);

            VoxelVolume tar = NewWorld();
            tar.FillBox(new BlockPos(-16, -1, -16), new BlockPos(16, 0, 16), PhysicsPalette.Stone);
            tar.FillBox(new BlockPos(-16, 1, -16), new BlockPos(16, 14, 16), PhysicsPalette.TarSource);

            VoxelCharacter inWater = NewCharacter(water, new Vector3d(0, 3, 0));
            VoxelCharacter inTar = NewCharacter(tar, new Vector3d(0, 3, 0));

            Simulate(inWater, water, 10.0);
            Simulate(inTar, tar, 10.0);

            inTar.FluidDensity.Should().BeGreaterThan(inWater.FluidDensity);
            inTar.Position.Y.Should().BeGreaterThan(inWater.Position.Y - 0.01,
                "a denser fluid should support the body at least as high");
        }

        [TestMethod]
        public void Buoyancy_SwimmingIsSlowerThanWalking()
        {
            VoxelVolume pool = PoolWorld(depth: 14);
            VoxelVolume dry = WorldWithFloor();

            VoxelCharacter swimmer = NewCharacter(pool, new Vector3d(0, 6, 0));
            VoxelCharacter walker = NewCharacter(dry, new Vector3d(0, 1, 0));

            Simulate(swimmer, pool, 3.0, wish: new Vector3d(1, 0, 0));
            Simulate(walker, dry, 3.0, wish: new Vector3d(1, 0, 0));

            swimmer.Position.X.Should().BeLessThan(walker.Position.X);
        }
    }
}
