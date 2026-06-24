namespace RP.Game.Rendering
{
    using System;
    using RP.Math;

    /// <summary>
    /// A perspective camera: it produces the <b>view</b> and <b>projection</b> matrices that turn world
    /// positions into the clip-space coordinates the GPU rasterises. All the matrix maths comes from
    /// <see cref="RP.Math"/>; this type only holds the camera's placement and lens, and stitches in the
    /// one correction Vulkan needs.
    /// </summary>
    /// <remarks>
    /// <para><b>The two matrices.</b> The <see cref="View"/> matrix moves the world so the camera sits at
    /// the origin looking down −Z (a rigid move: rotate + translate). The <see cref="Projection"/> matrix
    /// then applies perspective — distant things shrink — squashing the visible frustum into a cube.
    /// Multiplied together (<see cref="ViewProjection"/>) and then by an object's model matrix, they give
    /// the full world→clip transform.</para>
    ///
    /// <para><b>Why a clip correction.</b> RP.Math's projection follows the OpenGL convention: +Y is up in
    /// clip space and depth maps to <c>[-1, 1]</c>. <b>Vulkan</b> differs on both counts — its clip-space
    /// +Y points <i>down</i>, and depth maps to <c>[0, 1]</c>. Rather than fork the maths library, we
    /// post-multiply a tiny constant matrix (<see cref="VulkanClipCorrection"/>) that flips Y and remaps Z.
    /// This is the single place those API quirks live; everything above sees clean right-handed, Y-up maths
    /// (build brief S4.4 keeps Vulkan specifics at the backend boundary).</para>
    /// </remarks>
    public sealed class Camera
    {
        /// <summary>
        /// Flips clip-space Y and remaps depth from OpenGL's [-1, 1] to Vulkan's [0, 1]. Applied as
        /// <c>correction · projection</c>. The Z row computes <c>z' = 0.5·z + 0.5·w</c>.
        /// </summary>
        public static readonly Matrix VulkanClipCorrection = new Matrix(
            1, 0, 0, 0,
            0, -1, 0, 0,
            0, 0, 0.5, 0.5,
            0, 0, 0, 1);

        /// <summary>World-space eye position.</summary>
        public Vector3d Position { get; set; } = new Vector3d(0, 0, 5);

        /// <summary>World-space point the camera looks at.</summary>
        public Vector3d Target { get; set; } = Vector3d.Origin;

        /// <summary>The "up" hint (need not be exactly perpendicular to the view direction).</summary>
        public Vector3d Up { get; set; } = Vector3d.YAxis;

        /// <summary>Vertical field of view.</summary>
        public Angle FieldOfView { get; set; } = new Angle(60, AngleUnits.DEG);

        /// <summary>Width ÷ height of the render target. Keep this updated on resize, or the image stretches.</summary>
        public double AspectRatio { get; set; } = 16.0 / 9.0;

        /// <summary>Near clip distance (must be &gt; 0).</summary>
        public double NearPlane { get; set; } = 0.1;

        /// <summary>Far clip distance.</summary>
        public double FarPlane { get; set; } = 1000.0;

        /// <summary>The view matrix (world → camera space).</summary>
        public Matrix View => Matrix.LookAt(Position, Target, Up);

        /// <summary>The projection matrix, already corrected for Vulkan's clip space.</summary>
        public Matrix Projection =>
            VulkanClipCorrection * Matrix.PerspectiveFieldOfView(FieldOfView, AspectRatio, NearPlane, FarPlane);

        /// <summary>The combined world→clip transform for a static object at the origin.</summary>
        public Matrix ViewProjection => Projection * View;

        /// <summary>
        /// Flattens a 4×4 RP.Math matrix into 16 floats in <b>column-major</b> order — the layout GLSL's
        /// <c>mat4</c> expects. RP.Math exposes elements as <c>m[row, col]</c> with the column-vector
        /// convention (<c>v' = M·v</c>), which matches GLSL's convention; only the storage order differs,
        /// so we transpose the indexing here (<c>dst[col*4 + row] = m[row, col]</c>).
        /// </summary>
        public static void ToColumnMajorFloats(Matrix m, Span<float> destination16)
        {
            if (destination16.Length < 16) throw new ArgumentException("Need room for 16 floats.", nameof(destination16));
            for (int col = 0; col < 4; col++)
            {
                for (int row = 0; row < 4; row++)
                {
                    destination16[col * 4 + row] = (float)m[row, col];
                }
            }
        }
    }
}
