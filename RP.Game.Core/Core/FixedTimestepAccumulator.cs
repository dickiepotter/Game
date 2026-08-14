namespace RP.Game.Core
{
    using System;

    /// <summary>
    /// Decouples a variable-rate render loop from a fixed-rate simulation, so the game updates at a
    /// constant tick rate (e.g. 60 Hz) regardless of how fast the screen is refreshing.
    /// </summary>
    /// <remarks>
    /// <para><b>The problem this solves.</b> The naïve game loop advances physics by however long the
    /// last frame took: <c>position += velocity * frameTime</c>. That is <i>frame-rate dependent</i> —
    /// the same game runs differently at 30, 60, and 144 fps, and a single long frame (a stutter) takes
    /// one enormous integration step that can fling objects through walls. The build brief is explicit
    /// (S5): physics must be frame-rate independent.</para>
    ///
    /// <para><b>The fix (Glenn Fiedler's "Fix Your Timestep").</b> Keep a running store of unspent real
    /// time — the <i>accumulator</i>. Each frame, add the real elapsed time to it, then spend it in
    /// fixed-size lumps of exactly <see cref="FixedDeltaSeconds"/>, running one simulation step per lump.
    /// Whatever real time is left over (always less than one step) stays in the accumulator for next
    /// frame. The simulation therefore only ever sees identical, constant-size steps.</para>
    ///
    /// <para><b>Why <see cref="Alpha"/> exists (interpolated rendering).</b> Because a little unspent
    /// time is usually left over, the renderer is "between" two simulation steps. If it drew the latest
    /// step's positions directly, motion would visibly judder. Instead the renderer blends the previous
    /// and current states by <c>Alpha = leftover / step</c> (a value in <c>[0, 1)</c>):
    /// <c>renderState = Lerp(previous, current, Alpha)</c>. This is why the loop must keep <i>two</i>
    /// copies of moving state — the one before the last step and the one after.</para>
    ///
    /// <para><b>The "spiral of death" and why we clamp.</b> If a single frame takes longer than the time
    /// it costs to simulate it (e.g. the game hitches, or a debugger pauses it), the accumulator grows
    /// faster than we can drain it, so each frame runs even more steps, which makes the next frame slower
    /// still — a runaway collapse. <see cref="MaxFrameSeconds"/> caps how much real time any one frame
    /// may contribute, trading a brief slow-motion wobble for staying alive. Simulation time falls behind
    /// real time in that moment, which is the correct, survivable choice.</para>
    ///
    /// <para>This type is the pure, headless heart of the loop — no clock, no rendering — so it can be
    /// unit-tested exhaustively (build brief S20). A real clock is wired in by the owning game loop.</para>
    /// </remarks>
    public sealed class FixedTimestepAccumulator
    {
        private double _accumulator;

        /// <summary>The fixed length of one simulation step, in seconds. Every step is exactly this long.</summary>
        public double FixedDeltaSeconds { get; }

        /// <summary>
        /// The most real time (seconds) a single <see cref="Advance"/> call may contribute. Anything
        /// beyond this is discarded to prevent the spiral of death (see remarks on the type).
        /// </summary>
        public double MaxFrameSeconds { get; }

        /// <summary>Unspent real time carried over, in seconds. Always in <c>[0, FixedDeltaSeconds)</c>.</summary>
        public double Accumulator => _accumulator;

        /// <summary>
        /// How far rendering sits between the previous and current simulation step, in <c>[0, 1)</c>.
        /// Use it to interpolate render state: <c>Lerp(previous, current, Alpha)</c>.
        /// </summary>
        public double Alpha => _accumulator / FixedDeltaSeconds;

        /// <summary>
        /// Creates an accumulator.
        /// </summary>
        /// <param name="fixedDeltaSeconds">Length of one simulation step. Defaults to 1/60 s (60 Hz),
        /// the brief's suggested physics rate. Must be finite and &gt; 0.</param>
        /// <param name="maxFrameSeconds">Spiral-of-death clamp. Defaults to 0.25 s (≈15 steps at 60 Hz).
        /// Must be finite and &gt;= <paramref name="fixedDeltaSeconds"/> so at least one step can run.</param>
        /// <exception cref="ArgumentOutOfRangeException">If an argument is non-finite, non-positive, or
        /// the clamp is smaller than a single step.</exception>
        public FixedTimestepAccumulator(double fixedDeltaSeconds = 1.0 / 60.0, double maxFrameSeconds = 0.25)
        {
            if (!IsFinite(fixedDeltaSeconds) || fixedDeltaSeconds <= 0.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fixedDeltaSeconds), fixedDeltaSeconds,
                    "The fixed step must be a finite, positive number of seconds.");
            }

            if (!IsFinite(maxFrameSeconds) || maxFrameSeconds < fixedDeltaSeconds)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxFrameSeconds), maxFrameSeconds,
                    "The frame clamp must be finite and at least one fixed step, or no step could ever run.");
            }

            FixedDeltaSeconds = fixedDeltaSeconds;
            MaxFrameSeconds = maxFrameSeconds;
            _accumulator = 0.0;
        }

        /// <summary>
        /// Folds one frame's real elapsed time into the accumulator and reports how many fixed
        /// simulation steps are now due. The caller runs exactly that many steps, then renders using
        /// <see cref="Alpha"/>.
        /// </summary>
        /// <param name="frameSeconds">Real time since the last call, in seconds (≥ 0). It is clamped to
        /// <see cref="MaxFrameSeconds"/> before use.</param>
        /// <returns>The number of fixed steps to run this frame (0 or more).</returns>
        /// <exception cref="ArgumentOutOfRangeException">If <paramref name="frameSeconds"/> is negative
        /// or non-finite — time cannot run backwards, and a NaN would silently poison the accumulator.</exception>
        public int Advance(double frameSeconds)
        {
            if (!IsFinite(frameSeconds) || frameSeconds < 0.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frameSeconds), frameSeconds,
                    "A frame's elapsed time must be a finite, non-negative number of seconds.");
            }

            // Clamp first: this is the spiral-of-death guard. A 10-second hitch contributes at most
            // MaxFrameSeconds, so we never try to "catch up" by running hundreds of steps at once.
            if (frameSeconds > MaxFrameSeconds)
            {
                frameSeconds = MaxFrameSeconds;
            }

            _accumulator += frameSeconds;

            // Spend the accumulator in whole fixed-size steps. Integer arithmetic on the count keeps the
            // leftover exact and avoids drift that a repeated "while (acc >= dt) acc -= dt" can accrue.
            int steps = (int)(_accumulator / FixedDeltaSeconds);
            _accumulator -= steps * FixedDeltaSeconds;

            return steps;
        }

        /// <summary>
        /// Empties the accumulator (leftover time drops to zero) without running any steps. Call this
        /// after a deliberate long stall the game should <i>not</i> try to simulate through — loading a
        /// level, or resuming from a pause — so the first live frame starts cleanly from zero.
        /// </summary>
        public void Reset() => _accumulator = 0.0;

        // System.Double.IsFinite exists on net8.0, but spelling it out keeps the intent obvious and the
        // type trivially portable: "not NaN and not ±Infinity".
        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
