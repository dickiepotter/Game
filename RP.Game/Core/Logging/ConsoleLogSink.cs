namespace RP.Game.Core.Logging
{
    using System;

    /// <summary>
    /// A <see cref="ILogSink"/> that writes to the console, colouring by severity so warnings and errors
    /// stand out during a build run.
    /// </summary>
    /// <remarks>
    /// This is the default sink for development. It formats each line as
    /// <c>[LEVEL] category: message</c>. Colour is restored after each write so the sink never leaves the
    /// console in a tinted state. It writes errors and warnings to <see cref="Console.Error"/> so they can
    /// be redirected separately from ordinary output.
    /// </remarks>
    public sealed class ConsoleLogSink : ILogSink
    {
        private readonly object _gate = new object();

        /// <inheritdoc />
        public void Write(LogLevel level, string category, string message)
        {
            // Console colour is process-global shared state, so guard it: two threads tinting at once
            // could bleed one level's colour into another's line.
            lock (_gate)
            {
                ConsoleColor previous = Console.ForegroundColor;
                Console.ForegroundColor = ColourFor(level);

                var writer = level >= LogLevel.Warning ? Console.Error : Console.Out;
                writer.WriteLine($"[{level.ToString().ToUpperInvariant()}] {category}: {message}");

                Console.ForegroundColor = previous;
            }
        }

        private static ConsoleColor ColourFor(LogLevel level) => level switch
        {
            LogLevel.Trace => ConsoleColor.DarkGray,
            LogLevel.Debug => ConsoleColor.Gray,
            LogLevel.Info => ConsoleColor.White,
            LogLevel.Warning => ConsoleColor.Yellow,
            LogLevel.Error => ConsoleColor.Red,
            _ => ConsoleColor.White,
        };
    }
}
