namespace RP.Game.Rendering
{
    using RP.Math;

    /// <summary>
    /// One mesh vertex: a position and a colour, both single-precision. This is the CPU-side layout that
    /// gets copied verbatim into a GPU vertex buffer, so its field order and types <i>are</i> the memory
    /// layout the shader reads — keep them in lockstep with the shader's <c>layout(location = …)</c> inputs
    /// and with the Vulkan attribute descriptions in the backend.
    /// </summary>
    /// <remarks>
    /// The struct is deliberately a flat block of floats (via <see cref="Vector3"/>, itself three floats),
    /// so it is "unmanaged" and blittable: the engine can take its address and memcpy an array of them
    /// straight to the GPU with no marshalling. A normal is added when lighting arrives.
    /// </remarks>
    public readonly struct Vertex
    {
        /// <summary>Object-space position.</summary>
        public readonly Vector3 Position;

        /// <summary>Vertex colour (RGB, linear).</summary>
        public readonly Vector3 Color;

        public Vertex(Vector3 position, Vector3 color)
        {
            Position = position;
            Color = color;
        }
    }
}
