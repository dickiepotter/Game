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
    /// <para>Kept small (two <see cref="Vector3"/> + a scale) so the whole instance buffer stays cache
    /// friendly. A full per-instance transform (rotation) would be a matrix here; a shared spin is pushed as
    /// a constant instead, and a single uniform <see cref="Scale"/> lets one cube stand in for ships from a
    /// fighter to a capital.</para>
    /// </remarks>
    public readonly struct InstanceData
    {
        /// <summary>World-space offset added to the mesh's vertices for this instance.</summary>
        public readonly Vector3 Offset;

        /// <summary>Per-instance colour tint.</summary>
        public readonly Vector3 Color;

        /// <summary>Uniform scale applied to the unit mesh before the offset.</summary>
        public readonly float Scale;

        /// <summary>
        /// Per-instance orientation as a unit quaternion <c>(x, y, z, w)</c>, applied to the mesh before
        /// scale + offset. The shader rotates each vertex (and its normal) by this, so one hull mesh can
        /// point in any direction — the difference between a frozen formation and a living dogfight. The
        /// identity quaternion <c>(0, 0, 0, 1)</c> leaves the mesh in its authored orientation.
        /// </summary>
        public readonly Vector4 Rotation;

        public InstanceData(Vector3 offset, Vector3 color, float scale = 1f)
            : this(offset, color, scale, Vector4.UnitW) { }

        public InstanceData(Vector3 offset, Vector3 color, float scale, Vector4 rotation)
        {
            Offset = offset;
            Color = color;
            Scale = scale;
            Rotation = rotation;
        }
    }
}
