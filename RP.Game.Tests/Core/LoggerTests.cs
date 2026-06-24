namespace RP.Game.Tests.Core
{
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Core.Logging;

    [TestClass]
    public sealed class LoggerTests
    {
        [TestMethod]
        public void Log_BelowMinimumLevel_IsDropped()
        {
            var sink = new CollectingLogSink();
            var log = new Logger(sink) { MinimumLevel = LogLevel.Warning };

            log.Info("Vulkan", "device created");   // below Warning → dropped
            log.Warning("Vulkan", "deprecated call"); // kept
            log.Error("Vulkan", "validation error");  // kept

            sink.Count.Should().Be(2);
            sink.Entries[0].Level.Should().Be(LogLevel.Warning);
            sink.Entries[1].Level.Should().Be(LogLevel.Error);
        }

        [TestMethod]
        public void Log_AtOrAboveMinimumLevel_ReachesSink()
        {
            var sink = new CollectingLogSink();
            var log = new Logger(sink) { MinimumLevel = LogLevel.Trace };

            log.Trace("A", "t");
            log.Debug("A", "d");
            log.Info("A", "i");
            log.Warning("A", "w");
            log.Error("A", "e");

            sink.Count.Should().Be(5);
        }

        [TestMethod]
        public void Log_FansOutToEverySink()
        {
            var a = new CollectingLogSink();
            var b = new CollectingLogSink();
            var log = new Logger(a, b) { MinimumLevel = LogLevel.Info };

            log.Info("Assets", "loaded mesh");

            a.Count.Should().Be(1);
            b.Count.Should().Be(1);
            a.Entries[0].Message.Should().Be("loaded mesh");
            b.Entries[0].Category.Should().Be("Assets");
        }

        [TestMethod]
        public void Log_PassesLevelCategoryAndMessageThrough()
        {
            var sink = new CollectingLogSink();
            var log = new Logger(sink) { MinimumLevel = LogLevel.Trace };

            log.Log(LogLevel.Warning, "Physics", "penetration depth high");

            var e = sink.Entries[0];
            e.Level.Should().Be(LogLevel.Warning);
            e.Category.Should().Be("Physics");
            e.Message.Should().Be("penetration depth high");
        }

        [TestMethod]
        public void Log_NormalisesNullCategoryAndMessage()
        {
            var sink = new CollectingLogSink();
            var log = new Logger(sink) { MinimumLevel = LogLevel.Trace };

            log.Log(LogLevel.Info, null!, null!);

            sink.Entries[0].Category.Should().Be("General");
            sink.Entries[0].Message.Should().Be(string.Empty);
        }

        [TestMethod]
        public void AddAndRemoveSink_TakeEffect()
        {
            var sink = new CollectingLogSink();
            var log = new Logger { MinimumLevel = LogLevel.Info };

            log.Info("X", "before"); // no sinks yet → goes nowhere
            log.AddSink(sink);
            log.Info("X", "after");
            log.RemoveSink(sink).Should().BeTrue();
            log.Info("X", "gone");

            sink.Count.Should().Be(1);
            sink.Entries[0].Message.Should().Be("after");
        }

        [TestMethod]
        public void AddSink_IgnoresNull()
        {
            var log = new Logger();
            log.AddSink(null!);            // must not throw
            log.RemoveSink(null!).Should().BeFalse();
        }

        [TestMethod]
        public void HasAtLeast_DetectsErrorsForHeadlessRunChecks()
        {
            var sink = new CollectingLogSink();
            var log = new Logger(sink) { MinimumLevel = LogLevel.Trace };

            log.Info("Run", "all good");
            sink.HasAtLeast(LogLevel.Error).Should().BeFalse();

            log.Error("Run", "NaN transform detected");
            sink.HasAtLeast(LogLevel.Error).Should().BeTrue();
            sink.HasAtLeast(LogLevel.Warning).Should().BeTrue();
        }

        [TestMethod]
        public void ChangingMinimumLevelAtRuntime_AppliesToSubsequentCalls()
        {
            var sink = new CollectingLogSink();
            var log = new Logger(sink) { MinimumLevel = LogLevel.Error };

            log.Warning("X", "dropped");
            log.MinimumLevel = LogLevel.Debug;
            log.Warning("X", "kept");

            sink.Count.Should().Be(1);
            sink.Entries[0].Message.Should().Be("kept");
        }
    }
}
