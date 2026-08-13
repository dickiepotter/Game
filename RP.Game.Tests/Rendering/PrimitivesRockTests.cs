namespace RP.Game.Tests.Rendering
{
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Rendering;
    using RP.Math;

    /// <summary>The procedural asteroid mesh: deterministic, watertight-by-construction, and always inside
    /// the unit box so per-instance scale keeps meaning "diameter".</summary>
    [TestClass]
    public sealed class PrimitivesRockTests
    {
        [TestMethod]
        public void Rock_FitsTheUnitBox_InAnyOrientation()
        {
            Primitives.Mesh rock = Primitives.Rock(seed: 42);
            foreach (Vertex v in rock.Vertices)
            {
                v.Position.Length.Should().BeLessThanOrEqualTo(0.5f + 1e-4f); // circumradius bound
                v.Position.Length.Should().BeGreaterThanOrEqualTo(0.3f - 1e-4f); // never collapses inward
            }
        }

        [TestMethod]
        public void Rock_IsDeterministicPerSeed_AndVariesAcrossSeeds()
        {
            Primitives.Mesh a1 = Primitives.Rock(seed: 7);
            Primitives.Mesh a2 = Primitives.Rock(seed: 7);
            Primitives.Mesh b = Primitives.Rock(seed: 8);

            a1.Vertices.Length.Should().Be(a2.Vertices.Length);
            for (int i = 0; i < a1.Vertices.Length; i++)
            {
                a1.Vertices[i].Position.Should().Be(a2.Vertices[i].Position);
            }

            bool anyDifferent = false;
            for (int i = 0; i < a1.Vertices.Length && !anyDifferent; i++)
            {
                anyDifferent = (a1.Vertices[i].Position - b.Vertices[i].Position).Length > 1e-6f;
            }
            anyDifferent.Should().BeTrue("different seeds should produce different rocks");
        }

        [TestMethod]
        public void Orb_IsAUnitLightSphere()
        {
            Primitives.Mesh orb = Primitives.Orb();
            foreach (Vertex v in orb.Vertices)
            {
                v.Position.Length.Should().BeApproximately(0.5f, 1e-4f); // a true sphere shell
                v.Color.Should().Be(new Vector3(1f, 1f, 1f));            // tint decides the colour
            }
        }

        [TestMethod]
        public void Bolt_IsALongThinShard_WithAnHdrHotHead()
        {
            Primitives.Mesh bolt = Primitives.Bolt();
            float minZ = 0, maxZ = 0, maxLateral = 0, maxIntensity = 0;
            foreach (Vertex v in bolt.Vertices)
            {
                minZ = System.Math.Min(minZ, v.Position.Z);
                maxZ = System.Math.Max(maxZ, v.Position.Z);
                maxLateral = System.Math.Max(maxLateral, System.MathF.Abs(v.Position.X));
                maxLateral = System.Math.Max(maxLateral, System.MathF.Abs(v.Position.Y));
                maxIntensity = System.Math.Max(maxIntensity, v.Color.X);
            }

            (maxZ - minZ).Should().BeApproximately(1f, 1e-5f);       // unit length: scale = bolt length
            maxLateral.Should().BeLessThan(0.06f);                    // a needle, not a rod
            maxIntensity.Should().BeGreaterThan(1.5f, "the head must be HDR-hot so the bloom streaks it");
        }

        [TestMethod]
        public void Rock_HasUnitNormals_AndValidIndices()
        {
            Primitives.Mesh rock = Primitives.Rock(seed: 3);
            rock.Indices.Length.Should().Be(rock.Vertices.Length); // flat-shaded: 3 unique verts per tri
            foreach (Vertex v in rock.Vertices)
            {
                v.Normal.Length.Should().BeApproximately(1f, 1e-3f);
            }
            foreach (ushort index in rock.Indices)
            {
                ((int)index).Should().BeLessThan(rock.Vertices.Length);
            }
        }
    }
}
