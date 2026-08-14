namespace RP.Game.Core
{
    using System;

    /// <summary>
    /// A read-only snapshot of the simulation's sense of time, handed to update code each step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A game has two different clocks and it is vital not to confuse them:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><b>Real (wall-clock) time</b> — how long the last frame actually took to
    ///   draw. It varies constantly with load and refresh rate.</description></item>
    ///   <item><description><b>Simulation time</b> — the steady, fixed-size ticks the physics and game
    ///   logic advance by. Every tick is exactly <see cref="FixedDeltaSeconds"/> long, no matter how
    ///   fast or slow the machine is rendering.</description></item>
    /// </list>
    /// <para>
    /// Keeping simulation time fixed is what makes the game behave identically on a slow laptop and a
    /// fast desktop (build brief S5: "physics must be frame-rate independent"). This struct carries the
    /// simulation clock; <see cref="FixedTimestepAccumulator"/> is what keeps it in step with real time.
    /// </para>
    /// </remarks>
    public readonly struct GameTime : IEquatable<GameTime>
    {
        /// <summary>The fixed length of a single simulation step, in seconds (e.g. 1/60 s).</summary>
        public double FixedDeltaSeconds { get; }

        /// <summary>Total simulation time elapsed, in seconds — the sum of every fixed step so far.</summary>
        public double TotalSeconds { get; }

        /// <summary>How many fixed steps have been simulated since the game started.</summary>
        public long StepCount { get; }

        /// <summary>Builds a time snapshot. Normally only the loop constructs these.</summary>
        public GameTime(double fixedDeltaSeconds, double totalSeconds, long stepCount)
        {
            FixedDeltaSeconds = fixedDeltaSeconds;
            TotalSeconds = totalSeconds;
            StepCount = stepCount;
        }

        /// <summary>
        /// Returns the snapshot for the step that follows this one: one more step, one more
        /// <see cref="FixedDeltaSeconds"/> of total time. Pure — it returns a new value rather than
        /// mutating, matching RP.Math's immutable-value style.
        /// </summary>
        public GameTime Advanced() =>
            new GameTime(FixedDeltaSeconds, TotalSeconds + FixedDeltaSeconds, StepCount + 1);

        public bool Equals(GameTime other) =>
            FixedDeltaSeconds.Equals(other.FixedDeltaSeconds)
            && TotalSeconds.Equals(other.TotalSeconds)
            && StepCount == other.StepCount;

        public override bool Equals(object? obj) => obj is GameTime other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(FixedDeltaSeconds, TotalSeconds, StepCount);

        public override string ToString() =>
            $"GameTime(step {StepCount}, t={TotalSeconds:0.###}s, dt={FixedDeltaSeconds:0.#####}s)";
    }
}
