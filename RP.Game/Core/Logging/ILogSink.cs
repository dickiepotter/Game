namespace RP.Game.Core.Logging
{
    /// <summary>
    /// A destination for log messages — a console, a file, an in-memory buffer, an on-screen overlay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole point of this interface is <b>decoupling</b>: code that <i>produces</i> log messages
    /// (the Vulkan debug messenger, the asset loader, the physics step) should not know or care <i>where</i>
    /// they go. It talks to a <see cref="Logger"/>; the logger fans each message out to one or more sinks.
    /// Swapping "print to console" for "also write to a file" becomes a wiring change at startup, with no
    /// edit to any producer. This is the same separation the renderer uses to keep Vulkan out of gameplay.
    /// </para>
    /// <para>
    /// A sink receives only messages that already passed the logger's level filter, and receives the
    /// <i>already-formatted</i> message string, so a sink's job is purely "put these characters somewhere".
    /// Implementations should be cheap and must not throw for routine input.
    /// </para>
    /// </remarks>
    public interface ILogSink
    {
        /// <summary>
        /// Writes one message to this destination.
        /// </summary>
        /// <param name="level">Severity (already passed the logger's minimum-level filter).</param>
        /// <param name="category">A short source tag, e.g. <c>"Vulkan"</c>, <c>"Assets"</c>, <c>"Physics"</c>.</param>
        /// <param name="message">The human-readable message text.</param>
        void Write(LogLevel level, string category, string message);
    }
}
