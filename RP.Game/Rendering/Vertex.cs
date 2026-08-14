namespace RP.Game.Rendering
{
    using RP.Math;

    /// <summary>
    /// One mesh vertex: position, normal, and colour — all single-precision. This is the CPU-side layout
    /// copied verbatim into a GPU vertex buffer, so its field order and types <i>are</i> the memory layout
    /// the shader reads. Keep it in lockstep with the shader's <c>layout(location = …)</c> inputs and with
    /// the Vulkan attribute descriptions in the backend.
    /// </summary>
    /// <remarks>
    /// A flat block of floats (each <see cref="Vector3"/> is three), so the struct is "unmanaged" and
    /// blittable: an array of them memcpys straight to the GPU. The <see cref="Normal"/> drives lighting —
    /// it is the surface's outward direction, which the shader dots against the light direction.
    /// </remarks>
    public readonly struct Vertex
    {
        /// <summary>Object-space position.</summary>
        public readonly Vector3 Position;

        /// <summary>Object-space outward surface normal (unit length).</summary>
        public readonly Vector3 Normal;

        /// <summary>Vertex colour (RGB, linear).</summary>
        public readonly Vector3 Color;

        public Vertex(Vector3 position, Vector3 normal, Vector3 color)
        {
            Position = position;
            Normal = normal;
            Color = color;
        }
    }
}
