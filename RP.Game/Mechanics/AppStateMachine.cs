namespace RP.Game.Mechanics
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The application state machine: it holds the current <see cref="AppState"/> and enforces which
    /// transitions are legal, firing an event when the state changes. Keeping the legal transitions in one
    /// table (rather than scattered <c>if</c>s) makes the flow auditable and stops nonsense like jumping
    /// from <see cref="AppState.Boot"/> straight into <see cref="AppState.Paused"/>.
    /// </summary>
    /// <remarks>
    /// This is generic machinery (no Spectre specifics) — any game has the same Boot → Menu → Playing ⇄
    /// Paused shape, so it lives in Game (build brief S21.1).
    /// </remarks>
    public sealed class AppStateMachine
    {
        // For each state, the set of states it is allowed to move to.
        private static readonly IReadOnlyDictionary<AppState, AppState[]> Allowed =
            new Dictionary<AppState, AppState[]>
            {
                [AppState.Boot] = new[] { AppState.MainMenu, AppState.Exiting },
                [AppState.MainMenu] = new[] { AppState.Playing, AppState.Exiting },
                [AppState.Playing] = new[] { AppState.Paused, AppState.MainMenu, AppState.Exiting },
                [AppState.Paused] = new[] { AppState.Playing, AppState.MainMenu, AppState.Exiting },
                [AppState.Exiting] = Array.Empty<AppState>(),
            };

        /// <summary>The current state.</summary>
        public AppState Current { get; private set; }

        /// <summary>Raised after a successful transition, with (from, to).</summary>
        public event Action<AppState, AppState>? StateChanged;

        public AppStateMachine(AppState initial = AppState.Boot)
        {
            Current = initial;
        }

        /// <summary>True if a move from the current state to <paramref name="target"/> is legal.</summary>
        public bool CanTransitionTo(AppState target) =>
            Array.IndexOf(Allowed[Current], target) >= 0;

        /// <summary>
        /// Attempts to move to <paramref name="target"/>. Returns false (and does nothing) if the
        /// transition is not allowed, so callers can guard UI without exceptions.
        /// </summary>
        public bool TryTransitionTo(AppState target)
        {
            if (!CanTransitionTo(target)) return false;

            AppState from = Current;
            Current = target;
            StateChanged?.Invoke(from, target);
            return true;
        }

        /// <summary>Convenience: is the simulation running (Playing, not Paused/menu)?</summary>
        public bool IsSimulating => Current == AppState.Playing;
    }
}
