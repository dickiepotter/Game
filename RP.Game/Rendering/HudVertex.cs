namespace RP.Game.Rendering
{
    using RP.Math;

    /// <summary>
    /// One vertex of the 2D overlay: a position already in normalised device coordinates
    /// (x, y in <c>[-1, 1]</c>), an RGB colour, and an opacity.
    /// </summary>
    /// <remarks>
    /// <para>The overlay is drawn in two passes from arrays of these: filled triangles first, then lines on
    /// top. A game builds both arrays each frame and hands them over — there is no retained scene, because
    /// a heads-up display changes every frame anyway and diffing it would cost more than rebuilding it.</para>
    ///
    /// <para><b>Why alpha is a separate field rather than folded into the colour.</b> The two passes blend
    /// differently and deliberately so. Filled panels use ordinary source-alpha blending, because a panel's
    /// job is to <i>darken</i> what is behind it so text on top of it can be read. Lines blend additively,
    /// which makes them glow over the scene — right for a reticle or a targeting bracket, and exactly wrong
    /// for a panel, which would turn into a bright fog. Keeping opacity explicit lets one vertex type feed
    /// both.</para>
    ///
    /// <para>The two-argument constructor leaves the vertex fully opaque, so existing callers that predate
    /// the alpha channel behave exactly as they did.</para>
    /// </remarks>
    public readonly struct HudVertex
    {
        /// <summary>Position in normalised device coordinates.</summary>
        public readonly Vector2 Position;

        /// <summary>Colour (RGB, linear).</summary>
        public readonly Vector3 Color;

        /// <summary>Opacity in <c>[0, 1]</c>.</summary>
        public readonly float Alpha;

        /// <summary>Creates a fully opaque vertex.</summary>
        public HudVertex(Vector2 position, Vector3 color)
        {
            Position = position;
            Color = color;
            Alpha = 1f;
        }

        /// <summary>Creates a vertex with an explicit opacity.</summary>
        public HudVertex(Vector2 position, Vector3 color, float alpha)
        {
            Position = position;
            Color = color;
            Alpha = alpha;
        }
    }
}
