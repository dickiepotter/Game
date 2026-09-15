namespace RP.Game.Tests.Rendering
{
    using System.Linq;
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Rendering;
    using RP.Math;

    /// <summary>
    /// The overlay builder: where pixels land in clip space, and what the icon primitives actually emit.
    /// </summary>
    /// <remarks>
    /// The orientation tests here exist because an interface drawn entirely upside down once survived a
    /// fully green suite. Nothing else in the codebase asserts on a screen coordinate, so a sign error in
    /// one conversion is invisible to every other test and to every frame counter, and is only caught by
    /// someone looking at the screen. These put that one conversion under test.
    /// </remarks>
    [TestClass]
    public sealed class UiBuilderTests
    {
        private static UiBuilder NewBuilder(float width = 1280f, float height = 720f)
        {
            var ui = new UiBuilder();
            ui.Begin(width, height);
            return ui;
        }

        // ---- Pixels in, clip space out -----------------------------------------------------------------

        [TestMethod]
        public void TheTopOfTheScreenIsTheTopOfClipSpace()
        {
            // Vulkan clip space is +Y down, unlike OpenGL. Flipping here -- the correct thing to do under
            // GL, and the habit anyone arriving from GL brings with them -- silently turns every panel,
            // every bar and every glyph upside down while leaving the geometry perfectly valid.
            UiBuilder ui = NewBuilder();
            ui.Fill(new UiRect(0, 0, 1280, 360), Vector3.One);

            float topMost = ui.Fills.Min(v => v.Position.Y);
            float bottomMost = ui.Fills.Max(v => v.Position.Y);

            topMost.Should().BeApproximately(-1f, 1e-5f, "pixel row 0 is the top edge of clip space");
            bottomMost.Should().BeApproximately(0f, 1e-5f, "half way down the screen is the middle");
        }

        [TestMethod]
        public void TheLeftOfTheScreenIsTheLeftOfClipSpace()
        {
            UiBuilder ui = NewBuilder();
            ui.Fill(new UiRect(0, 0, 640, 720), Vector3.One);

            ui.Fills.Min(v => v.Position.X).Should().BeApproximately(-1f, 1e-5f);
            ui.Fills.Max(v => v.Position.X).Should().BeApproximately(0f, 1e-5f);
        }

        [TestMethod]
        public void TheWholeScreenFillsTheWholeOfClipSpace()
        {
            UiBuilder ui = NewBuilder(1920, 1080);
            ui.Fill(ui.Screen, Vector3.One);

            ui.Fills.Min(v => v.Position.X).Should().BeApproximately(-1f, 1e-5f);
            ui.Fills.Max(v => v.Position.X).Should().BeApproximately(1f, 1e-5f);
            ui.Fills.Min(v => v.Position.Y).Should().BeApproximately(-1f, 1e-5f);
            ui.Fills.Max(v => v.Position.Y).Should().BeApproximately(1f, 1e-5f);
        }

        [TestMethod]
        public void TheSameLayoutLandsInTheSamePlaceAtAnyResolution()
        {
            // What makes working in pixels safe: the conversion is the only thing that knows the screen
            // size, so an interface laid out for 720p is the same shape at 4K.
            UiBuilder small = NewBuilder(1280, 720);
            UiBuilder large = NewBuilder(2560, 1440);

            small.Fill(new UiRect(0, 0, 640, 360), Vector3.One);
            large.Fill(new UiRect(0, 0, 1280, 720), Vector3.One);

            for (int i = 0; i < small.Fills.Count; i++)
            {
                large.Fills[i].Position.X.Should().BeApproximately(small.Fills[i].Position.X, 1e-5f);
                large.Fills[i].Position.Y.Should().BeApproximately(small.Fills[i].Position.Y, 1e-5f);
            }
        }

        // ---- Icons ---------------------------------------------------------------------------------------

        [TestMethod]
        public void ACubeIsThreeFacesAtThreeBrightnesses()
        {
            // The three faces are the whole point: a flat swatch and a cube are the same colour, and only
            // the shading tells the player they are looking at something solid.
            UiBuilder ui = NewBuilder();
            ui.Cube(new Vector2(200, 200), 40f, new Vector3(0.5f, 0.5f, 0.5f));

            ui.Fills.Count.Should().Be(18, "three quads of two triangles");

            float[] brightness = ui.Fills
                .Select(v => v.Color.X)
                .Distinct()
                .OrderByDescending(b => b)
                .ToArray();

            brightness.Should().HaveCount(3, "top, left and right must not be the same shade");
            brightness[0].Should().BeGreaterThan(brightness[1]);
            brightness[1].Should().BeGreaterThan(brightness[2]);
        }

        [TestMethod]
        public void ACubeIsLitFromAbove()
        {
            // The brightest face has to be the top one, or the cube reads as lit from below and stops
            // looking like a block at all.
            UiBuilder ui = NewBuilder();
            var centre = new Vector2(200, 200);
            ui.Cube(centre, 40f, new Vector3(0.5f, 0.5f, 0.5f));

            float brightest = ui.Fills.Max(v => v.Color.X);
            Vector2 centreNdc = ToNdc(ui, centre);

            float averageY = ui.Fills.Where(v => v.Color.X == brightest).Average(v => v.Position.Y);
            averageY.Should().BeLessThan(centreNdc.Y, "the brightest face sits above the middle");
        }

        [TestMethod]
        public void ACubeStaysInsideTheSpaceItIsGiven()
        {
            // Icons are drawn into slots. One that overruns its box paints over its neighbour, and in a
            // hotbar that reads as a rendering fault rather than as a large item.
            UiBuilder ui = NewBuilder();
            var centre = new Vector2(400, 300);
            const float Size = 48f;

            ui.Cube(centre, Size, Vector3.One);

            Vector2 min = ToNdc(ui, new Vector2(centre.X - (Size * 0.5f), centre.Y - (Size * 0.5f)));
            Vector2 max = ToNdc(ui, new Vector2(centre.X + (Size * 0.5f), centre.Y + (Size * 0.5f)));

            ui.Fills.Should().OnlyContain(v =>
                v.Position.X >= min.X - 1e-5f && v.Position.X <= max.X + 1e-5f &&
                v.Position.Y >= min.Y - 1e-5f && v.Position.Y <= max.Y + 1e-5f);
        }

        [TestMethod]
        public void ACubeAndALozengeAreTellableApart()
        {
            // The silhouette is the information. If the two shapes covered the same area a player would be
            // back to reading labels, which is what the icons exist to avoid.
            UiBuilder cube = NewBuilder();
            UiBuilder lozenge = NewBuilder();

            cube.Cube(new Vector2(200, 200), 40f, Vector3.One);
            lozenge.Lozenge(new Vector2(200, 200), 40f, Vector3.One);

            float CubeHeight(UiBuilder ui) => ui.Fills.Max(v => v.Position.Y) - ui.Fills.Min(v => v.Position.Y);

            CubeHeight(cube).Should().BeGreaterThan(CubeHeight(lozenge) * 1.4f, "a cube is visibly the taller");
            lozenge.Fills.Count.Should().Be(6, "a lozenge is one quad");
        }

        [TestMethod]
        public void AnIconCarriesItsAlphaOntoEveryVertex()
        {
            // Unaffordable recipes are drawn faded. An icon that ignored its alpha would show a greyed-out
            // row with a full-strength picture in it.
            UiBuilder ui = NewBuilder();
            ui.Cube(new Vector2(200, 200), 40f, Vector3.One, 0.35f);

            ui.Fills.Should().OnlyContain(v => v.Alpha == 0.35f);
            ui.Lines.Should().OnlyContain(v => v.Alpha < 0.35f, "the edging is fainter still");
        }

        // ---- Budgets ---------------------------------------------------------------------------------------

        [TestMethod]
        public void EmissionPastTheBudgetIsDroppedAndReported()
        {
            // Dropped rather than resized, and flagged rather than silent: a frame that overruns loses its
            // last few elements, which is visible and recoverable.
            var ui = new UiBuilder(fillCapacity: 12, lineCapacity: 12);
            ui.Begin(1280, 720);

            for (int i = 0; i < 20; i++) ui.Fill(new UiRect(i, 0, 5, 5), Vector3.One);

            ui.Overflowed.Should().BeTrue();
            ui.Fills.Count.Should().BeLessThanOrEqualTo(12);
        }

        [TestMethod]
        public void BeginClearsTheFrameIncludingTheOverflowFlag()
        {
            var ui = new UiBuilder(fillCapacity: 6, lineCapacity: 6);
            ui.Begin(1280, 720);
            ui.Fill(new UiRect(0, 0, 5, 5), Vector3.One);
            ui.Fill(new UiRect(0, 0, 5, 5), Vector3.One);
            ui.Overflowed.Should().BeTrue();

            ui.Begin(1280, 720);

            ui.Fills.Should().BeEmpty();
            ui.Lines.Should().BeEmpty();
            ui.Overflowed.Should().BeFalse("one bad frame must not mark every frame after it");
        }

        /// <summary>The same pixels-to-clip-space conversion the builder uses, for checking against.</summary>
        private static Vector2 ToNdc(UiBuilder ui, Vector2 pixels)
            => new Vector2(((pixels.X / ui.ScreenWidth) * 2f) - 1f, ((pixels.Y / ui.ScreenHeight) * 2f) - 1f);
    }
}
