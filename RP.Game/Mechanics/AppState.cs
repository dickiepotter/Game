namespace RP.Game.Mechanics
{
    /// <summary>
    /// The top-level states the application can be in. A game is not "always playing" — it boots, sits in
    /// menus, plays, pauses — and each state routes input and decides what is simulated and drawn
    /// differently (build brief S21.1).
    /// </summary>
    public enum AppState
    {
        /// <summary>Starting up: loading settings and assets before anything is shown.</summary>
        Boot,

        /// <summary>The main menu (New Game, Continue, Settings, Quit). Nothing is simulated.</summary>
        MainMenu,

        /// <summary>In the game world, simulating and rendering normally.</summary>
        Playing,

        /// <summary>In-game but paused: the fixed-timestep simulation is frozen; a menu overlays the world.</summary>
        Paused,

        /// <summary>Shutting down.</summary>
        Exiting,
    }
}
