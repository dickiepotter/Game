namespace RP.Game.Core.Logging
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The engine's logging façade: producers call <see cref="Log"/> (or the level helpers); the logger
    /// applies a minimum-level filter and fans surviving messages out to every attached <see cref="ILogSink"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why a class and not just <c>Console.WriteLine</c>? Three reasons the build needs from day one:
    /// (1) a single choke-point where Vulkan validation output is collected when no human is watching the
    /// console (build brief S4.2); (2) a level filter so release builds stay quiet; (3) multiple sinks, so
    /// the same message can hit the console <i>and</i> an in-memory buffer a test can assert on.
    /// </para>
    /// <para>
    /// <b>Thread-safety.</b> Vulkan's debug messenger can fire from a driver thread, and asset streaming
    /// runs off the main thread, so log calls can race. A single lock around the sink list and the fan-out
    /// keeps it correct without asking callers to think about it. Logging is not on the hot path, so the
    /// lock's cost is irrelevant.
    /// </para>
    /// </remarks>
    public sealed class Logger
    {
        private readonly List<ILogSink> _sinks = new List<ILogSink>();
        private readonly object _gate = new object();

        /// <summary>
        /// Messages below this level are dropped before reaching any sink. Defaults to
        /// <see cref="LogLevel.Info"/>. Lower it to <see cref="LogLevel.Debug"/>/<see cref="LogLevel.Trace"/>
        /// while diagnosing; raise it for release.
        /// </summary>
        public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

        /// <summary>Creates a logger with no sinks. Attach at least one with <see cref="AddSink"/>.</summary>
        public Logger() { }

        /// <summary>Creates a logger pre-wired with the given sinks (a convenience for startup code).</summary>
        public Logger(params ILogSink[] sinks)
        {
            if (sinks != null)
            {
                foreach (var sink in sinks)
                {
                    if (sink != null) _sinks.Add(sink);
                }
            }
        }

        /// <summary>Attaches a destination. Ignores a null sink rather than failing a startup wire-up.</summary>
        public void AddSink(ILogSink sink)
        {
            if (sink == null) return;
            lock (_gate) { _sinks.Add(sink); }
        }

        /// <summary>Detaches a destination. Returns true if it was present.</summary>
        public bool RemoveSink(ILogSink sink)
        {
            if (sink == null) return false;
            lock (_gate) { return _sinks.Remove(sink); }
        }

        /// <summary>
        /// The core entry point: log a message at a level under a category. Below
        /// <see cref="MinimumLevel"/> it returns immediately, having touched no sink.
        /// </summary>
        /// <param name="level">Severity of the message.</param>
        /// <param name="category">Short source tag; a null/empty value is normalised to <c>"General"</c>.</param>
        /// <param name="message">Message text; a null value is normalised to an empty string so a sink
        /// never has to defend against null.</param>
        public void Log(LogLevel level, string category, string message)
        {
            // The filter is the first thing we do: dropped messages must cost almost nothing, because in a
            // shipping build the vast majority of Trace/Debug calls are dropped here.
            if (level < MinimumLevel) return;

            if (string.IsNullOrEmpty(category)) category = "General";
            if (message == null) message = string.Empty;

            // Copy the sink references under the lock, then write outside it. This keeps a slow or
            // re-entrant sink from holding the lock (and lets a sink log without deadlocking).
            ILogSink[] snapshot;
            lock (_gate) { snapshot = _sinks.ToArray(); }

            foreach (var sink in snapshot)
            {
                sink.Write(level, category, message);
            }
        }

        /// <summary>Logs at <see cref="LogLevel.Trace"/>.</summary>
        public void Trace(string category, string message) => Log(LogLevel.Trace, category, message);

        /// <summary>Logs at <see cref="LogLevel.Debug"/>.</summary>
        public void Debug(string category, string message) => Log(LogLevel.Debug, category, message);

        /// <summary>Logs at <see cref="LogLevel.Info"/>.</summary>
        public void Info(string category, string message) => Log(LogLevel.Info, category, message);

        /// <summary>Logs at <see cref="LogLevel.Warning"/>.</summary>
        public void Warning(string category, string message) => Log(LogLevel.Warning, category, message);

        /// <summary>Logs at <see cref="LogLevel.Error"/>.</summary>
        public void Error(string category, string message) => Log(LogLevel.Error, category, message);
    }
}
