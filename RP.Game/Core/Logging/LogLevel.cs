namespace RP.Game.Core.Logging
{
    /// <summary>
    /// The severity of a log message, ordered from most to least chatty.
    /// </summary>
    /// <remarks>
    /// The numeric order matters: a logger keeps a <i>minimum</i> level and drops anything below it, so
    /// shipping at <see cref="Warning"/> silences <see cref="Trace"/>/<see cref="Debug"/>/<see cref="Info"/>
    /// with a single comparison. Vulkan's validation layers map naturally onto these — verbose/info →
    /// <see cref="Debug"/>/<see cref="Info"/>, warning → <see cref="Warning"/>, error → <see cref="Error"/> —
    /// which is exactly why the engine funnels validation output through this one ladder (build brief S4.2).
    /// </remarks>
    public enum LogLevel
    {
        /// <summary>Fine-grained "what is happening right now" spam, off in normal runs.</summary>
        Trace = 0,

        /// <summary>Developer diagnostics useful while building a feature.</summary>
        Debug = 1,

        /// <summary>Normal lifecycle milestones (device created, swapchain resized, asset loaded).</summary>
        Info = 2,

        /// <summary>Something is wrong but recoverable — API misuse the driver tolerated, a missing asset
        /// that fell back to a placeholder.</summary>
        Warning = 3,

        /// <summary>A genuine failure: a Vulkan validation error, an unrecoverable resource fault.</summary>
        Error = 4,
    }
}
