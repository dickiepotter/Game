namespace RP.Game.Rendering
{
    using RP.Math;

    /// <summary>
    /// One vertex of the 2D HUD overlay: a position already in normalised device coordinates (x,y ∈ [-1,1])
    /// and an RGB colour. The HUD is drawn as a list of coloured line segments over the final image, so the
    /// game builds an array of these (two per segment) each frame and hands it to the renderer.
    /// </summary>
    public readonly struct HudVertex
    {
        public readonly Vector2 Position; // NDC
        public readonly Vector3 Color;

        public HudVertex(Vector2 position, Vector3 color)
        {
            Position = position;
            Color = color;
        }
    }
}
