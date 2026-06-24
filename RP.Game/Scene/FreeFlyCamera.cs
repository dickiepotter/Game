namespace RP.Game.Scene
{
    using System;
    using RP.Game.Rendering;
    using RP.Math;
    using Silk.NET.Input;

    /// <summary>
    /// A debug "free-fly" camera controller: WASD to move in the look plane, Q/E down/up, hold the right
    /// mouse button to look around, left Shift to boost. It drives a <see cref="Camera"/> directly — the
    /// fastest way to fly around a scene and inspect it while building the engine.
    /// </summary>
    /// <remarks>
    /// Orientation is stored as <b>yaw</b> (turn about world up) and <b>pitch</b> (look up/down), which is
    /// the natural pair for a first-person camera and avoids roll creeping in. At yaw = pitch = 0 the
    /// camera looks down −Z, matching the engine's right-handed, Y-up convention. Movement is scaled by the
    /// frame's real elapsed time, so fly speed is the same on any machine.
    /// </remarks>
    public sealed class FreeFlyCamera
    {
        /// <summary>Base movement speed, world units per second.</summary>
        public double MoveSpeed { get; set; } = 14.0;

        /// <summary>Multiplier applied while the boost key (left Shift) is held.</summary>
        public double BoostMultiplier { get; set; } = 4.0;

        /// <summary>Mouse-look sensitivity, radians of turn per pixel of movement.</summary>
        public double LookSensitivity { get; set; } = 0.0025;

        private double _yaw;
        private double _pitch;
        private System.Numerics.Vector2 _lastMousePosition;
        private bool _hasLastMouse;

        /// <summary>Sets yaw/pitch so the camera initially looks from <paramref name="position"/> toward
        /// <paramref name="target"/>, so control begins from the current view rather than snapping.</summary>
        public void AimAt(Vector3d position, Vector3d target)
        {
            Vector3d dir = (target - position).NormalizeOrDefault();
            if (dir.IsZero()) return;
            _pitch = Math.Asin(Math.Clamp(dir.Y, -1.0, 1.0));
            _yaw = Math.Atan2(dir.X, -dir.Z);
        }

        /// <summary>Advances the camera from one frame of input.</summary>
        /// <param name="camera">The camera to move/aim.</param>
        /// <param name="keyboard">Keyboard state.</param>
        /// <param name="mouse">Mouse state.</param>
        /// <param name="deltaSeconds">Real elapsed time this frame, for frame-rate-independent speed.</param>
        public void Update(Camera camera, IKeyboard keyboard, IMouse mouse, double deltaSeconds)
        {
            ApplyMouseLook(mouse);

            // Build the look basis from yaw/pitch. Forward is −Z at (yaw, pitch) = 0.
            var forward = new Vector3d(
                Math.Cos(_pitch) * Math.Sin(_yaw),
                Math.Sin(_pitch),
                -Math.Cos(_pitch) * Math.Cos(_yaw));
            Vector3d right = forward.CrossProduct(Vector3d.YAxis).NormalizeOrDefault();
            Vector3d up = right.CrossProduct(forward).NormalizeOrDefault();

            Vector3d move = Vector3d.Origin;
            if (keyboard.IsKeyPressed(Key.W)) move += forward;
            if (keyboard.IsKeyPressed(Key.S)) move -= forward;
            if (keyboard.IsKeyPressed(Key.D)) move += right;
            if (keyboard.IsKeyPressed(Key.A)) move -= right;
            if (keyboard.IsKeyPressed(Key.E)) move += up;
            if (keyboard.IsKeyPressed(Key.Q)) move -= up;

            double speed = MoveSpeed * (keyboard.IsKeyPressed(Key.ShiftLeft) ? BoostMultiplier : 1.0) * deltaSeconds;
            if (!move.IsZero())
            {
                camera.Position = camera.Position + move.NormalizeOrDefault() * speed;
            }

            camera.Target = camera.Position + forward;
        }

        private void ApplyMouseLook(IMouse mouse)
        {
            System.Numerics.Vector2 position = mouse.Position;
            bool looking = mouse.IsButtonPressed(MouseButton.Right);

            if (looking && _hasLastMouse)
            {
                double dx = position.X - _lastMousePosition.X;
                double dy = position.Y - _lastMousePosition.Y;
                _yaw -= dx * LookSensitivity;   // moving the mouse right turns the view right
                _pitch -= dy * LookSensitivity; // moving the mouse up looks up

                // Stop just short of straight up/down so the basis never degenerates (gimbal flip).
                double limit = Math.PI / 2.0 - 0.01;
                _pitch = Math.Clamp(_pitch, -limit, limit);
            }

            _lastMousePosition = position;
            _hasLastMouse = true;
        }
    }
}
