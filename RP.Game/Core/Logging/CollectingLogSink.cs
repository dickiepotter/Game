namespace RP.Game.Core.Logging
{
    using System.Collections.Generic;

    /// <summary>
    /// A <see cref="ILogSink"/> that keeps every message it receives in a list.
    /// </summary>
    /// <remarks>
    /// Two uses: <b>tests</b> assert on what the engine logged (e.g. "a Vulkan validation error was
    /// reported"), and a <b>headless run</b> (build brief S20) can collect the log and scan it for errors
    /// afterwards when no console is being watched. It is intentionally simple and thread-safe so it can
    /// receive messages from the Vulkan debug messenger's thread.
    /// </remarks>
    public sealed class CollectingLogSink : ILogSink
    {
        /// <summary>One captured log message.</summary>
        public readonly struct Entry
        {
            public LogLevel Level { get; }
            public string Category { get; }
            public string Message { get; }

            public Entry(LogLevel level, string category, string message)
            {
                Level = level;
                Category = category;
                Message = message;
            }

            public override string ToString() => $"[{Level}] {Category}: {Message}";
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly object _gate = new object();

        /// <summary>A snapshot copy of everything captured so far, in order.</summary>
        public IReadOnlyList<Entry> Entries
        {
            get { lock (_gate) { return _entries.ToArray(); } }
        }

        /// <summary>How many messages have been captured.</summary>
        public int Count { get { lock (_gate) { return _entries.Count; } } }

        /// <summary>True if any captured message is at <paramref name="level"/> or above — the headless
        /// run's "did anything go wrong?" check.</summary>
        public bool HasAtLeast(LogLevel level)
        {
            lock (_gate)
            {
                foreach (var e in _entries)
                {
                    if (e.Level >= level) return true;
                }

                return false;
            }
        }

        /// <summary>Forgets all captured messages.</summary>
        public void Clear() { lock (_gate) { _entries.Clear(); } }

        /// <inheritdoc />
        public void Write(LogLevel level, string category, string message)
        {
            lock (_gate) { _entries.Add(new Entry(level, category, message)); }
        }
    }
}
