namespace RP.Game.Rendering
{
    using System;
    using System.Collections.Generic;
    using RP.Math;

    /// <summary>
    /// A line-drawn <b>16-segment display font</b> for the HUD: every glyph is a subset of sixteen fixed
    /// segments in a cell, so text renders through the same line overlay as the rest of the HUD (no texture
    /// atlas, no glyph pipeline) and carries a deliberately instrument-panel look. Uppercase letters, digits
    /// and a few symbols; lowercase is folded to uppercase. Generic engine utility — the game supplies the
    /// line emitter and colour.
    /// </summary>
    public static class GlyphFont
    {
        // Segment bit positions (classic 16-seg naming).
        private const int A1 = 1 << 0, A2 = 1 << 1, B = 1 << 2, C = 1 << 3, D1 = 1 << 4, D2 = 1 << 5,
            E = 1 << 6, F = 1 << 7, G1 = 1 << 8, G2 = 1 << 9, H = 1 << 10, J = 1 << 11, K = 1 << 12,
            L = 1 << 13, M = 1 << 14, N = 1 << 15;

        // Each segment as a line in cell space (x in 0..2, y in 0..4, origin bottom-left).
        private static readonly (float ax, float ay, float bx, float by)[] Segments =
        {
            (0, 4, 1, 4), // A1
            (1, 4, 2, 4), // A2
            (2, 4, 2, 2), // B
            (2, 2, 2, 0), // C
            (0, 0, 1, 0), // D1
            (1, 0, 2, 0), // D2
            (0, 2, 0, 0), // E
            (0, 4, 0, 2), // F
            (0, 2, 1, 2), // G1
            (1, 2, 2, 2), // G2
            (0, 4, 1, 2), // H
            (1, 4, 1, 2), // J
            (2, 4, 1, 2), // K
            (0, 0, 1, 2), // L
            (1, 0, 1, 2), // M
            (2, 0, 1, 2), // N
        };

        private static readonly Dictionary<char, int> Glyphs = new()
        {
            [' '] = 0,
            ['0'] = A1 | A2 | B | C | D1 | D2 | E | F,
            ['1'] = B | C | J,
            ['2'] = A1 | A2 | B | G1 | G2 | E | D1 | D2,
            ['3'] = A1 | A2 | B | C | D1 | D2 | G2,
            ['4'] = F | G1 | G2 | B | C,
            ['5'] = A1 | A2 | F | G1 | G2 | C | D1 | D2,
            ['6'] = A1 | A2 | F | E | D1 | D2 | C | G1 | G2,
            ['7'] = A1 | A2 | B | C,
            ['8'] = A1 | A2 | B | C | D1 | D2 | E | F | G1 | G2,
            ['9'] = A1 | A2 | B | C | F | G1 | G2 | D1 | D2,
            ['A'] = A1 | A2 | B | C | E | F | G1 | G2,
            ['B'] = A1 | A2 | B | C | D1 | D2 | G2 | J | M,
            ['C'] = A1 | A2 | F | E | D1 | D2,
            ['D'] = A1 | A2 | B | C | D1 | D2 | J | M,
            ['E'] = A1 | A2 | F | E | G1 | G2 | D1 | D2,
            ['F'] = A1 | A2 | F | E | G1 | G2,
            ['G'] = A1 | A2 | F | E | D1 | D2 | C | G2,
            ['H'] = F | E | B | C | G1 | G2,
            ['I'] = A1 | A2 | D1 | D2 | J | M,
            ['J'] = B | C | D1 | D2 | E,
            ['K'] = F | E | G1 | K | N,
            ['L'] = F | E | D1 | D2,
            ['M'] = F | E | B | C | H | K,
            ['N'] = F | E | B | C | H | N,
            ['O'] = A1 | A2 | B | C | D1 | D2 | E | F,
            ['P'] = A1 | A2 | B | F | E | G1 | G2,
            ['Q'] = A1 | A2 | B | C | D1 | D2 | E | F | N,
            ['R'] = A1 | A2 | B | F | E | G1 | G2 | N,
            ['S'] = A1 | A2 | F | G1 | G2 | C | D1 | D2,
            ['T'] = A1 | A2 | J | M,
            ['U'] = F | E | B | C | D1 | D2,
            ['V'] = F | E | L | K,
            ['W'] = F | E | B | C | L | N,
            ['X'] = H | K | L | N,
            ['Y'] = H | K | M,
            ['Z'] = A1 | A2 | K | L | D1 | D2,
            ['-'] = G1 | G2,
            ['+'] = G1 | G2 | J | M,
            ['.'] = D1,
            ['/'] = K | L,
            ['%'] = K | L | A1 | D2,
            [':'] = J | M,
            ['('] = A2 | F | E | D1,
            [')'] = A1 | B | C | D2,
        };

        // A glyph cell is half as wide as tall (x 0..2 vs y 0..4); glyphs advance with a small gap between.
        private const float CellAspect = 0.5f;
        private const float Advance = 0.66f; // fraction of height, per character

        /// <summary>Width (in NDC-x) a string will occupy at the given cell <paramref name="height"/>.</summary>
        public static float Measure(string text, float height, float aspectX) =>
            (text?.Length ?? 0) * Advance * height * aspectX;

        /// <summary>
        /// Draws <paramref name="text"/> with its lower-left at <paramref name="origin"/>, each glyph
        /// <paramref name="height"/> tall in NDC-y. <paramref name="aspectX"/> (typically 1/aspect) keeps the
        /// glyphs square. The caller's <paramref name="emit"/> draws one line in its own space/colour. Returns
        /// the pen's end x.
        /// </summary>
        public static float Draw(string text, Vector2 origin, float height, float aspectX, Action<Vector2, Vector2> emit)
        {
            if (string.IsNullOrEmpty(text)) return origin.X;

            float cellW = CellAspect * height * aspectX; // width of the 0..2 cell in NDC-x
            float step = Advance * height * aspectX;
            float penX = origin.X;

            foreach (char raw in text)
            {
                char c = char.ToUpperInvariant(raw);
                if (Glyphs.TryGetValue(c, out int mask) && mask != 0)
                {
                    for (int s = 0; s < Segments.Length; s++)
                    {
                        if ((mask & (1 << s)) == 0) continue;
                        var seg = Segments[s];
                        // The HUD overlay is +Y-down (Vulkan clip space), so subtract the cell's upward y to
                        // keep glyphs upright; origin is the text's baseline (its bottom-left).
                        var a = new Vector2(penX + seg.ax * 0.5f * cellW, origin.Y - seg.ay * 0.25f * height);
                        var b = new Vector2(penX + seg.bx * 0.5f * cellW, origin.Y - seg.by * 0.25f * height);
                        emit(a, b);
                    }
                }

                penX += step;
            }

            return penX;
        }
    }
}
