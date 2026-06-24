namespace RP.Game.Rendering
{
    using RP.Math;

    /// <summary>
    /// Per-instance data for instanced rendering: where this copy of the mesh sits, and what colour to
    /// tint it. One <see cref="InstanceData"/> per object, packed into a second vertex buffer that the GPU
    /// steps through once <i>per instance</i> rather than once per vertex.
    /// </summary>
    /// <remarks>
    /// <para><b>Why instancing.</b> Drawing 8,000 cubes as 8,000 separate draw calls would drown the CPU.
    /// Instancing issues <i>one</i> draw that says "render this mesh N times"; the GPU pulls a fresh
    /// <see cref="InstanceData"/> for each copy and the vertex shader places it. This is what makes the
    /// brief's "thousands of ships, projectiles and debris" affordable (build brief S2, S5).</para>
    /// <para>Kept tiny (two <see cref="Vector3"/>) so the whole instance buffer stays small and cache
    /// friendly. A full per-instance transform (rotation/scale) would be a matrix here; a shared spin is
    /// pushed as a constant instead for now.</para>
    /// </remarks>
    public readonly struct InstanceData
    {
        /// <summary>World-space offset added to the mesh's vertices for this instance.</summary>
        public readonly Vector3 Offset;

        /// <summary>Per-instance colour tint.</summary>
        public readonly Vector3 Color;

        public InstanceData(Vector3 offset, Vector3 color)
        {
            Offset = offset;
            Color = color;
        }
    }
}
