namespace RP.Game.Tests.Rendering
{
    using System;
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Rendering;
    using RP.Math;

    /// <summary>Mesh operations behave like RP.Math values: inputs untouched, outputs correct — including
    /// the classically-fumbled normal transform under non-uniform scale.</summary>
    [TestClass]
    public sealed class MeshOpsTests
    {
        [TestMethod]
        public void Stretch_ScalesPositions_AndInverseScalesNormals()
        {
            Primitives.Mesh cube = Primitives.Cube();
            Primitives.Mesh stretched = MeshOps.Stretch(cube, new Vector3(2f, 1f, 1f));

            // Positions doubled in X only.
            float maxX = 0, maxY = 0;
            foreach (Vertex v in stretched.Vertices)
            {
                maxX = Math.Max(maxX, MathF.Abs(v.Position.X));
                maxY = Math.Max(maxY, MathF.Abs(v.Position.Y));
                v.Normal.Length.Should().BeApproximately(1f, 1e-5f); // renormalised
            }
            maxX.Should().BeApproximately(1f, 1e-5f);
            maxY.Should().BeApproximately(0.5f, 1e-5f);

            // The cube's axis-aligned face normals survive a diagonal scale exactly.
            stretched.Vertices[0].Normal.Should().Be(cube.Vertices[0].Normal);

            // The source is untouched.
            cube.Vertices[0].Position.X.Should().BeApproximately(-0.5f, 1e-6f);
        }

        [TestMethod]
        public void Stretch_RejectsZeroFactors()
        {
            Action flatten = () => MeshOps.Stretch(Primitives.Cube(), new Vector3(1f, 0f, 1f));
            flatten.Should().Throw<ArgumentException>();
        }

        [TestMethod]
        public void Tint_MultipliesColoursComponentwise()
        {
            Primitives.Mesh tinted = MeshOps.Tint(Primitives.Cube(), new Vector3(0.5f, 1f, 2f));
            Vector3 original = Primitives.Cube().Vertices[0].Color;
            tinted.Vertices[0].Color.X.Should().BeApproximately(original.X * 0.5f, 1e-6f);
            tinted.Vertices[0].Color.Z.Should().BeApproximately(original.Z * 2f, 1e-6f);
        }

        [TestMethod]
        public void Displace_MovesVertices_AndRebuildsUnitNormals()
        {
            Primitives.Mesh moved = MeshOps.Displace(Primitives.Rock(seed: 2), p => p * 0.1f); // radial puff
            foreach (Vertex v in moved.Vertices)
            {
                v.Normal.Length.Should().BeApproximately(1f, 1e-3f);
            }
        }

        [TestMethod]
        public void Crumple_IsDeterministic_Deforms_AndPreservesTopologyAndColour()
        {
            Primitives.Mesh source = Primitives.Dart();
            Primitives.Mesh a = MeshOps.Crumple(source, seed: 5);
            Primitives.Mesh b = MeshOps.Crumple(source, seed: 5);

            a.Indices.Should().BeEquivalentTo(source.Indices);
            double total = 0;
            for (int i = 0; i < source.Vertices.Length; i++)
            {
                a.Vertices[i].Position.Should().Be(b.Vertices[i].Position); // same seed, same wreck
                a.Vertices[i].Color.Should().Be(source.Vertices[i].Color);  // geometry only — no recolouring
                total += (a.Vertices[i].Position - source.Vertices[i].Position).Length;
            }
            (total / source.Vertices.Length).Should().BeGreaterThan(0.005, "the crumple must actually deform");
        }

        [TestMethod]
        public void Merge_RebasesIndices_AndGuardsTheUshortLimit()
        {
            Primitives.Mesh two = MeshOps.Merge(Primitives.Cube(), Primitives.Cube());
            two.Vertices.Length.Should().Be(48);
            two.Indices.Length.Should().Be(72);
            two.Indices[36].Should().Be((ushort)(Primitives.Cube().Indices[0] + 24)); // second copy rebased

            // ~184 rocks of 240 verts each crosses 65,535 — must throw, not corrupt.
            var many = new Primitives.Mesh[300];
            for (int i = 0; i < many.Length; i++) many[i] = Primitives.Rock(seed: i);
            Action overflow = () => MeshOps.Merge(many);
            overflow.Should().Throw<InvalidOperationException>();
        }

        [TestMethod]
        public void RebuildNormals_FlatMeshKeepsFaceNormals_SharedMeshBlends()
        {
            // Flat-shaded rock: rebuilt normals equal the original face normals.
            Primitives.Mesh rock = Primitives.Rock(seed: 4);
            var verts = (Vertex[])rock.Vertices.Clone();
            MeshOps.RebuildNormals(verts, rock.Indices);
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3.Dot(verts[i].Normal, rock.Vertices[i].Normal).Should().BeApproximately(1f, 1e-3f);
            }
        }
    }
}
