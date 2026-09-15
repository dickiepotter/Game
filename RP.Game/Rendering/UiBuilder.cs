namespace RP.Game.Rendering
{
    using System;
    using System.Collections.Generic;
    using RP.Math;

    /// <summary>A rectangle in overlay space, in pixels, measured from the top-left of the screen.</summary>
    /// <remarks>
    /// Pixels, not normalised device coordinates. Interfaces are laid out in pixels because that is the unit
    /// everything about them is actually expressed in — a border is one pixel, a row is twenty, a margin is
    /// eight — and doing that arithmetic in a space that runs -1 to 1 and has a different scale on each axis
    /// is how interfaces end up subtly stretched on any aspect ratio but the author's.
    /// <see cref="UiBuilder"/> converts once, at the point of emission.
    /// </remarks>
    public readonly struct UiRect
    {
        /// <summary>Left edge.</summary>
        public readonly float X;

        /// <summary>Top edge.</summary>
        public readonly float Y;

        /// <summary>Width.</summary>
        public readonly float Width;

        /// <summary>Height.</summary>
        public readonly float Height;

        /// <summary>Creates a rectangle.</summary>
        public UiRect(float x, float y, float width, float height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        /// <summary>Right edge.</summary>
        public float Right => X + Width;

        /// <summary>Bottom edge.</summary>
        public float Bottom => Y + Height;

        /// <summary>Horizontal centre.</summary>
        public float CenterX => X + (Width * 0.5f);

        /// <summary>Vertical centre.</summary>
        public float CenterY => Y + (Height * 0.5f);

        /// <summary>The same rectangle, shrunk on every side.</summary>
        public UiRect Inset(float amount)
            => new UiRect(X + amount, Y + amount, Width - (amount * 2), Height - (amount * 2));

        /// <summary>The same rectangle, moved.</summary>
        public UiRect Offset(float dx, float dy) => new UiRect(X + dx, Y + dy, Width, Height);

        /// <summary>Whether a point is inside — the whole of hit testing for a mouse-driven interface.</summary>
        public bool Contains(float px, float py) => px >= X && px < Right && py >= Y && py < Bottom;

        /// <summary>A rectangle of the given size, centred on a point.</summary>
        public static UiRect Centered(float cx, float cy, float width, float height)
            => new UiRect(cx - (width * 0.5f), cy - (height * 0.5f), width, height);
    }

    /// <summary>
    /// Builds the two vertex arrays the overlay is drawn from: filled triangles and lines.
    /// </summary>
    /// <remarks>
    /// <para><b>Immediate mode, deliberately.</b> There is no widget tree, no layout pass and no retained
    /// state. A frame's interface is described by running code that calls <see cref="Panel"/> and
    /// <see cref="Text"/> in order, and the result is two arrays. For an interface that changes every frame
    /// anyway — a hotbar, a health bar, a crafting list that scrolls — a retained tree costs a diffing pass
    /// to discover what everyone already knew, and it makes conditional layout awkward for no benefit.</para>
    ///
    /// <para><b>Pixels in, NDC out.</b> The caller works entirely in pixels from the top-left, and the
    /// conversion happens once per vertex at emission. That is what keeps an interface the same shape on
    /// every aspect ratio.</para>
    ///
    /// <para><b>Text is drawn with lines, not glyphs.</b> <see cref="GlyphFont"/> is a vector font, so text
    /// costs a few segments per character and needs no atlas, no sampler and no descriptor set. It also
    /// scales to any size without becoming blurry, which for an interface that has to be legible at 720p
    /// and at 4K is worth more than typographic beauty.</para>
    ///
    /// <para><b>Budgets are enforced, not hoped for.</b> Both arrays have a fixed capacity, and emission
    /// past it is dropped rather than resized. A frame that overruns loses its last few elements, which is
    /// visible and recoverable; an unbounded interface buffer is neither.</para>
    /// </remarks>
    public sealed class UiBuilder
    {
        private readonly List<HudVertex> _fills = new List<HudVertex>();
        private readonly List<HudVertex> _lines = new List<HudVertex>();

        private int _fillCapacity;
        private int _lineCapacity;

        /// <summary>Creates a builder for a screen of a given size.</summary>
        /// <param name="fillCapacity">How many filled vertices may be emitted per frame.</param>
        /// <param name="lineCapacity">How many line vertices may be emitted per frame.</param>
        public UiBuilder(int fillCapacity = 24576, int lineCapacity = 8192)
        {
            _fillCapacity = fillCapacity;
            _lineCapacity = lineCapacity;
        }

        /// <summary>The screen width in pixels, set by <see cref="Begin"/>.</summary>
        public float ScreenWidth { get; private set; } = 1280f;

        /// <summary>The screen height in pixels, set by <see cref="Begin"/>.</summary>
        public float ScreenHeight { get; private set; } = 720f;

        /// <summary>The filled triangles built this frame.</summary>
        public IReadOnlyList<HudVertex> Fills => _fills;

        /// <summary>The line segments built this frame.</summary>
        public IReadOnlyList<HudVertex> Lines => _lines;

        /// <summary>The filled triangles as a span, for handing straight to a renderer with no copy.</summary>
        public ReadOnlySpan<HudVertex> FillSpan => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_fills);

        /// <summary>The line segments as a span, for handing straight to a renderer with no copy.</summary>
        public ReadOnlySpan<HudVertex> LineSpan => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_lines);

        /// <summary>Whether either buffer overflowed this frame.</summary>
        public bool Overflowed { get; private set; }

        /// <summary>The whole screen, as a rectangle.</summary>
        public UiRect Screen => new UiRect(0, 0, ScreenWidth, ScreenHeight);

        /// <summary>Clears both buffers and sets the screen size for this frame.</summary>
        public void Begin(float screenWidth, float screenHeight)
        {
            ScreenWidth = screenWidth < 1 ? 1 : screenWidth;
            ScreenHeight = screenHeight < 1 ? 1 : screenHeight;
            _fills.Clear();
            _lines.Clear();
            Overflowed = false;
        }

        // ---- Primitives ---------------------------------------------------------------------------------

        /// <summary>Fills a rectangle.</summary>
        public void Fill(UiRect rect, Vector3 color, float alpha = 1f)
        {
            if (_fills.Count + 6 > _fillCapacity)
            {
                Overflowed = true;
                return;
            }

            Vector2 topLeft = ToNdc(rect.X, rect.Y);
            Vector2 topRight = ToNdc(rect.Right, rect.Y);
            Vector2 bottomRight = ToNdc(rect.Right, rect.Bottom);
            Vector2 bottomLeft = ToNdc(rect.X, rect.Bottom);

            _fills.Add(new HudVertex(topLeft, color, alpha));
            _fills.Add(new HudVertex(topRight, color, alpha));
            _fills.Add(new HudVertex(bottomRight, color, alpha));

            _fills.Add(new HudVertex(topLeft, color, alpha));
            _fills.Add(new HudVertex(bottomRight, color, alpha));
            _fills.Add(new HudVertex(bottomLeft, color, alpha));
        }

        /// <summary>
        /// Fills an arbitrary quadrilateral, given its corners in order around the perimeter.
        /// </summary>
        /// <remarks>
        /// Needed because not everything worth drawing is axis-aligned. The obvious case is an isometric
        /// cube: three parallelograms, none of them a rectangle, and drawing an item icon as a flat square
        /// instead loses the one piece of information a player most wants from it -- whether the thing is a
        /// block they can place or a material they cannot.
        /// </remarks>
        public void FillQuad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Vector3 color, float alpha = 1f)
        {
            if (_fills.Count + 6 > _fillCapacity)
            {
                Overflowed = true;
                return;
            }

            Vector2 na = ToNdc(a.X, a.Y);
            Vector2 nb = ToNdc(b.X, b.Y);
            Vector2 nc = ToNdc(c.X, c.Y);
            Vector2 nd = ToNdc(d.X, d.Y);

            _fills.Add(new HudVertex(na, color, alpha));
            _fills.Add(new HudVertex(nb, color, alpha));
            _fills.Add(new HudVertex(nc, color, alpha));

            _fills.Add(new HudVertex(na, color, alpha));
            _fills.Add(new HudVertex(nc, color, alpha));
            _fills.Add(new HudVertex(nd, color, alpha));
        }

        /// <summary>Fills a triangle.</summary>
        public void FillTriangle(Vector2 a, Vector2 b, Vector2 c, Vector3 color, float alpha = 1f)
        {
            if (_fills.Count + 3 > _fillCapacity)
            {
                Overflowed = true;
                return;
            }

            _fills.Add(new HudVertex(ToNdc(a.X, a.Y), color, alpha));
            _fills.Add(new HudVertex(ToNdc(b.X, b.Y), color, alpha));
            _fills.Add(new HudVertex(ToNdc(c.X, c.Y), color, alpha));
        }

        /// <summary>
        /// Draws an isometric cube: a top face, a left face and a right face, shaded as if lit from above.
        /// </summary>
        /// <remarks>
        /// <para>The three faces take the same base colour at different brightness, which is all it takes
        /// to read as a solid object rather than a hexagon. The ratios are the same ones the voxel shader
        /// uses for a surface facing up, sideways and away, so an icon and the block it represents agree
        /// about what they look like.</para>
        /// <para>Proportions are the standard two-to-one isometric: a face's vertical rise is half its
        /// horizontal run, which is what makes the three faces meet cleanly at the centre.</para>
        /// </remarks>
        /// <param name="centre">Where the cube's middle sits.</param>
        /// <param name="size">The cube's overall width.</param>
        /// <param name="color">The block's base colour.</param>
        /// <param name="alpha">Opacity.</param>
        public void Cube(Vector2 centre, float size, Vector3 color, float alpha = 1f)
        {
            float hw = size * 0.5f;          // half width
            float qh = size * 0.25f;         // quarter height: the two-to-one isometric rise
            float vh = size * 0.32f;         // how tall the side faces are

            // Six silhouette points, clockwise from the top.
            var top = new Vector2(centre.X, centre.Y - qh - (vh * 0.5f));
            var right = new Vector2(centre.X + hw, centre.Y - (vh * 0.5f));
            var rightLow = new Vector2(centre.X + hw, centre.Y + (vh * 0.5f));
            var bottom = new Vector2(centre.X, centre.Y + qh + (vh * 0.5f));
            var leftLow = new Vector2(centre.X - hw, centre.Y + (vh * 0.5f));
            var left = new Vector2(centre.X - hw, centre.Y - (vh * 0.5f));
            var middle = new Vector2(centre.X, centre.Y + qh - (vh * 0.5f));

            // Top brightest, left mid, right darkest -- the same ordering a surface lit from above takes.
            FillQuad(top, right, middle, left, color * 1.15f, alpha);
            FillQuad(left, middle, bottom, leftLow, color * 0.72f, alpha);
            FillQuad(middle, right, rightLow, bottom, color * 0.52f, alpha);

            // A thin edge where the three faces meet, which is what stops a dark block reading as a blob.
            Vector3 edge = color * 1.5f;
            Line(top.X, top.Y, left.X, left.Y, edge, alpha * 0.7f);
            Line(top.X, top.Y, right.X, right.Y, edge, alpha * 0.7f);
            Line(middle.X, middle.Y, bottom.X, bottom.Y, edge, alpha * 0.5f);
        }

        /// <summary>
        /// Draws a flat, canted lozenge for things that are not blocks -- ingots, tools, materials.
        /// </summary>
        /// <remarks>
        /// Deliberately a different silhouette from <see cref="Cube"/>. The shape alone tells the player
        /// whether something can be placed in the world, which is the question they ask most often and the
        /// one a square swatch cannot answer.
        /// </remarks>
        public void Lozenge(Vector2 centre, float size, Vector3 color, float alpha = 1f)
        {
            float hw = size * 0.46f;
            float hh = size * 0.26f;

            var a = new Vector2(centre.X - hw, centre.Y + (hh * 0.4f));
            var b = new Vector2(centre.X - (hw * 0.45f), centre.Y - hh);
            var c = new Vector2(centre.X + hw, centre.Y - (hh * 0.4f));
            var d = new Vector2(centre.X + (hw * 0.45f), centre.Y + hh);

            FillQuad(a, b, c, d, color, alpha);
            Line(a.X, a.Y, b.X, b.Y, color * 1.5f, alpha * 0.8f);
            Line(b.X, b.Y, c.X, c.Y, color * 1.5f, alpha * 0.8f);
        }

        /// <summary>Draws a line between two points.</summary>
        public void Line(float x0, float y0, float x1, float y1, Vector3 color, float alpha = 1f)
        {
            if (_lines.Count + 2 > _lineCapacity)
            {
                Overflowed = true;
                return;
            }

            _lines.Add(new HudVertex(ToNdc(x0, y0), color, alpha));
            _lines.Add(new HudVertex(ToNdc(x1, y1), color, alpha));
        }

        /// <summary>Outlines a rectangle.</summary>
        public void Outline(UiRect rect, Vector3 color, float alpha = 1f)
        {
            Line(rect.X, rect.Y, rect.Right, rect.Y, color, alpha);
            Line(rect.Right, rect.Y, rect.Right, rect.Bottom, color, alpha);
            Line(rect.Right, rect.Bottom, rect.X, rect.Bottom, color, alpha);
            Line(rect.X, rect.Bottom, rect.X, rect.Y, color, alpha);
        }

        /// <summary>
        /// A filled rectangle with an outline: the substrate of every readable interface.
        /// </summary>
        /// <remarks>
        /// The fill exists to darken the world behind it, and the outline to give the panel an edge the eye
        /// can find. A panel without an outline dissolves into a busy scene however dark it is, which is
        /// why the two are one call rather than two — forgetting the outline is easy and the result looks
        /// broken rather than plain.
        /// </remarks>
        public void Panel(UiRect rect, Vector3 fill, Vector3 border, float fillAlpha = 0.82f, float borderAlpha = 0.9f)
        {
            Fill(rect, fill, fillAlpha);
            Outline(rect, border, borderAlpha);
        }

        /// <summary>
        /// A horizontal bar showing a fraction: health, breath, durability, a furnace's progress.
        /// </summary>
        /// <param name="rect">Where the bar sits.</param>
        /// <param name="fraction">How full, in <c>[0, 1]</c>.</param>
        /// <param name="fill">The colour of the filled portion.</param>
        /// <param name="background">The colour of the empty portion.</param>
        public void Bar(UiRect rect, double fraction, Vector3 fill, Vector3 background)
        {
            double clamped = fraction < 0 ? 0 : (fraction > 1 ? 1 : fraction);

            Fill(rect, background, 0.7f);
            if (clamped > 0)
            {
                Fill(new UiRect(rect.X, rect.Y, (float)(rect.Width * clamped), rect.Height), fill);
            }

            Outline(rect, background * 1.6f, 0.9f);
        }

        // ---- Text ---------------------------------------------------------------------------------------

        /// <summary>
        /// Draws text with its <b>top-left</b> at a point, and returns the width it occupied.
        /// </summary>
        /// <remarks>
        /// Top-left, not the baseline. <see cref="GlyphFont"/> positions text on its baseline, which is the
        /// right choice for a font and the wrong one for laying out rows of an interface: every caller ends
        /// up adding the line height back on, and the one that forgets gets a row that overlaps the one
        /// above it. The adjustment happens once, here.
        /// </remarks>
        public float Text(string text, float x, float y, float height, Vector3 color, float alpha = 1f)
        {
            if (string.IsNullOrEmpty(text)) return 0f;

            // GlyphFont takes an aspect correction for use in normalised space; this builder works in
            // pixels, where the axes already share a scale, so it is 1 and all the arithmetic stays here.
            GlyphFont.Draw(
                text,
                new Vector2(x, y + height),
                height,
                1f,
                (a, b) => Line(a.X, a.Y, b.X, b.Y, color, alpha));

            return MeasureText(text, height);
        }

        /// <summary>Draws text centred on a point, and returns the width it occupied.</summary>
        public float TextCentered(string text, float cx, float y, float height, Vector3 color, float alpha = 1f)
        {
            float width = GlyphFont.Measure(text, height, 1f);
            Text(text, cx - (width * 0.5f), y, height, color, alpha);
            return width;
        }

        /// <summary>Draws text with its right edge at a point.</summary>
        public float TextRight(string text, float right, float y, float height, Vector3 color, float alpha = 1f)
        {
            float width = GlyphFont.Measure(text, height, 1f);
            Text(text, right - width, y, height, color, alpha);
            return width;
        }

        /// <summary>How wide some text would be.</summary>
        public static float MeasureText(string text, float height) => GlyphFont.Measure(text, height, 1f);

        // ---- Conversion ---------------------------------------------------------------------------------

        /// <summary>Converts a pixel position, measured from the top-left, into normalised device coordinates.</summary>
        /// <remarks>
        /// <para>There is <b>no Y flip</b>, and that is the part worth stating, because the instinct is to
        /// add one. It is the right instinct for OpenGL, whose clip space has +Y upward; Vulkan's has +Y
        /// <i>downward</i>, which is the same direction pixels grow. Negating here as well flips the
        /// interface upside down — the hotbar draws along the top edge and every glyph stands on its head.
        /// It is obvious the moment anyone looks at the screen and invisible to anything that does not,
        /// which is precisely how it survives a test suite.</para>
        /// <para>This is also the convention <see cref="GlyphFont"/> documents and assumes, and the one
        /// <see cref="TryProjectWorld"/> produces, so all three agree.</para>
        /// </remarks>
        public Vector2 ToNdc(float x, float y)
            => new Vector2(((x / ScreenWidth) * 2f) - 1f, ((y / ScreenHeight) * 2f) - 1f);

        /// <summary>
        /// Projects a world position into screen pixels, for drawing overlay marks on things in the world.
        /// </summary>
        /// <param name="viewProjection">The camera's Vulkan-corrected view-projection.</param>
        /// <param name="world">The point, in the same space the camera is in.</param>
        /// <param name="x">Screen x in pixels.</param>
        /// <param name="y">Screen y in pixels.</param>
        /// <returns>False if the point is behind the camera, where a projection is meaningless.</returns>
        /// <remarks>
        /// The behind-the-camera test is why this does the divide itself rather than using the matrix's own
        /// transform: a point behind the eye has a negative homogeneous w, and dividing by it mirrors the
        /// point through the origin. The result lands somewhere plausible on screen and is completely
        /// wrong, so an outline drawn around a block the player has walked past appears in front of them.
        /// </remarks>
        public bool TryProjectWorld(Matrix viewProjection, Vector3d world, out float x, out float y)
        {
            double cx = (viewProjection[0, 0] * world.X) + (viewProjection[0, 1] * world.Y) + (viewProjection[0, 2] * world.Z) + viewProjection[0, 3];
            double cy = (viewProjection[1, 0] * world.X) + (viewProjection[1, 1] * world.Y) + (viewProjection[1, 2] * world.Z) + viewProjection[1, 3];
            double cw = (viewProjection[3, 0] * world.X) + (viewProjection[3, 1] * world.Y) + (viewProjection[3, 2] * world.Z) + viewProjection[3, 3];

            x = 0;
            y = 0;
            if (cw <= 1e-6) return false;

            double ndcX = cx / cw;
            double ndcY = cy / cw;

            x = (float)((ndcX + 1.0) * 0.5 * ScreenWidth);
            y = (float)((ndcY + 1.0) * 0.5 * ScreenHeight);
            return true;
        }
    }
}
