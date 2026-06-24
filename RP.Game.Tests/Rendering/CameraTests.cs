namespace RP.Game.Tests.Rendering
{
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Rendering;
    using RP.Math;

    [TestClass]
    public sealed class CameraTests
    {
        [TestMethod]
        public void ToColumnMajorFloats_TransposesRowIndexedStorageToColumnMajor()
        {
            // Row-major construction: m[row, col] == row*4 + col.
            var m = new Matrix(
                0, 1, 2, 3,
                4, 5, 6, 7,
                8, 9, 10, 11,
                12, 13, 14, 15);

            var dst = new float[16];
            Camera.ToColumnMajorFloats(m, dst);

            // Column-major order: column 0 first (rows 0..3), then column 1, ...
            dst.Should().Equal(0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15);
        }

        [TestMethod]
        public void ToColumnMajorFloats_OfIdentity_IsTheIdentityLayout()
        {
            var dst = new float[16];
            Camera.ToColumnMajorFloats(Matrix.Identity, dst);
            dst.Should().Equal(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
        }

        [TestMethod]
        public void VulkanClipCorrection_FlipsYAndRemapsDepthToZeroOne()
        {
            // Apply the correction to points (w = 1). Y flips; Z maps z' = 0.5z + 0.5.
            var up = Camera.VulkanClipCorrection * new Vector3d(0, 1, 0);
            up.X.Should().BeApproximately(0, 1e-9);
            up.Y.Should().BeApproximately(-1, 1e-9);   // Vulkan clip +Y points down
            up.Z.Should().BeApproximately(0.5, 1e-9);

            var near = Camera.VulkanClipCorrection * new Vector3d(0, 0, -1); // GL near (z=-1)
            near.Z.Should().BeApproximately(0.0, 1e-9);                    // -> Vulkan near (0)

            var far = Camera.VulkanClipCorrection * new Vector3d(0, 0, 1);   // GL far (z=1)
            far.Z.Should().BeApproximately(1.0, 1e-9);                     // -> Vulkan far (1)
        }

        [TestMethod]
        public void ViewProjection_ForABasicSetup_IsAllFinite()
        {
            var cam = new Camera
            {
                Position = new Vector3d(2, 2, 5),
                Target = Vector3d.Origin,
                AspectRatio = 16.0 / 9.0,
            };

            Matrix vp = cam.ViewProjection;
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    double v = vp[r, c];
                    double.IsNaN(v).Should().BeFalse();
                    double.IsInfinity(v).Should().BeFalse();
                }
            }
        }
    }
}
