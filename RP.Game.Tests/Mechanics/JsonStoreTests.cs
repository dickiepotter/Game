namespace RP.Game.Tests.Mechanics
{
    using System.IO;
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Mechanics;

    [TestClass]
    public sealed class JsonStoreTests
    {
        public sealed class Sample
        {
            public int Version { get; set; } = 1;
            public string Name { get; set; } = "";
            public double[] Numbers { get; set; } = System.Array.Empty<double>();
        }

        private string _dir = "";

        [TestInitialize]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "RP.Game.Tests.JsonStore", TestContext!.TestName!);
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
            Directory.CreateDirectory(_dir);
        }

        [TestCleanup]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }

        public TestContext? TestContext { get; set; }

        [TestMethod]
        public void Save_ThenLoad_RoundTrips()
        {
            string path = Path.Combine(_dir, "a.json");
            var original = new Sample { Version = 3, Name = "Spectre", Numbers = new[] { 1.5, -2.0, 3.25 } };

            JsonStore.Save(path, original);
            JsonStore.TryLoad(path, out Sample? loaded).Should().BeTrue();

            loaded!.Version.Should().Be(3);
            loaded.Name.Should().Be("Spectre");
            loaded.Numbers.Should().Equal(1.5, -2.0, 3.25);
        }

        [TestMethod]
        public void TryLoad_MissingFile_ReturnsFalse()
        {
            JsonStore.TryLoad(Path.Combine(_dir, "nope.json"), out Sample? loaded).Should().BeFalse();
            loaded.Should().BeNull();
        }

        [TestMethod]
        public void TryLoad_CorruptFile_ReturnsFalseAndDoesNotThrow()
        {
            string path = Path.Combine(_dir, "broken.json");
            File.WriteAllText(path, "{ this is not valid json ]");

            JsonStore.TryLoad(path, out Sample? loaded).Should().BeFalse();
            loaded.Should().BeNull();
        }

        [TestMethod]
        public void Save_OverwritesExistingAtomically()
        {
            string path = Path.Combine(_dir, "slot.json");
            JsonStore.Save(path, new Sample { Version = 1 });
            JsonStore.Save(path, new Sample { Version = 2 });

            JsonStore.TryLoad(path, out Sample? loaded).Should().BeTrue();
            loaded!.Version.Should().Be(2);
            File.Exists(path + ".tmp").Should().BeFalse(); // temp cleaned up by the replace
        }
    }
}
