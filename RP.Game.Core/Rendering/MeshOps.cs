namespace RP.Game.Rendering
{
    using System;
    using System.Collections.Generic;
    using RP.Math;

    /// <summary>
    /// Pure operations over <see cref="Primitives.Mesh"/> values: stretch, tint, displace, crumple, merge,
    /// and normal reconstruction. Every operation returns a <b>new</b> mesh and leaves its input untouched
    /// (the same immutability convention as RP.Math), so meshes compose like values:
    /// <c>MeshOps.Tint(MeshOps.Stretch(hull, …), …)</c> is safe, repeatable and order-explicit.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this lives in the engine.</b> Games invariably need mesh <i>variants</i> — a stretched
    /// hull for a heavier class, a crumpled hull for a wreck, a merged batch for a static structure. Doing
    /// those with ad-hoc loops at the call site scatters subtle correctness rules (normal transforms under
    /// non-uniform scale, 16-bit index limits, watertight displacement) across the game. Centralising them
    /// here makes the rules single-sourced and testable.</para>
    /// <para><b>Normals under non-uniform scale.</b> Scaling positions by <c>(sx, sy, sz)</c> does NOT
    /// scale normals the same way: normals transform by the inverse-transpose, which for a diagonal scale
    /// is <c>(1/sx, 1/sy, 1/sz)</c> followed by renormalisation. <see cref="Stretch"/> applies exactly
    /// that, so lighting stays correct — squashing a sphere flattens its shading, it doesn't shear it.</para>
    /// </remarks>
    public static class MeshOps
    {
        /// <summary>
        /// Scales a mesh per-axis. Positions multiply by <paramref name="factors"/>; normals are transformed
        /// by the inverse-transpose (see class remarks) and renormalised, so the result lights correctly.
        /// </summary>
        /// <param name="mesh">Source mesh (unchanged).</param>
        /// <param name="factors">Per-axis scale; each component must be non-zero.</param>
        /// <exception cref="ArgumentException">If any factor is zero (the mesh would collapse and normals
        /// would be undefined).</exception>
        public static Primitives.Mesh Stretch(Primitives.Mesh mesh, Vector3 factors)
        {
            if (factors.X == 0 || factors.Y == 0 || factors.Z == 0)
            {
                throw new ArgumentException("Stretch factors must be non-zero on every axis.", nameof(factors));
            }

            var inv = new Vector3(1f / factors.X, 1f / factors.Y, 1f / factors.Z);
            Vertex[] src = mesh.Vertices;
            var vertices = new Vertex[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                var p = new Vector3(src[i].Position.X * factors.X, src[i].Position.Y * factors.Y, src[i].Position.Z * factors.Z);
                var n = new Vector3(src[i].Normal.X * inv.X, src[i].Normal.Y * inv.Y, src[i].Normal.Z * inv.Z);
                vertices[i] = new Vertex(p, n.LengthSquared > 1e-12f ? n.Normalize() : src[i].Normal, src[i].Color);
            }

            return new Primitives.Mesh(vertices, mesh.Indices);
        }

        /// <summary>Multiplies every vertex colour componentwise — darken a hull, warm its paint, or push a
        /// region into HDR (&gt; 1) so the bloom pass treats it as emissive.</summary>
        public static Primitives.Mesh Tint(Primitives.Mesh mesh, Vector3 multiplier)
        {
            Vertex[] src = mesh.Vertices;
            var vertices = new Vertex[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                var c = new Vector3(src[i].Color.X * multiplier.X, src[i].Color.Y * multiplier.Y, src[i].Color.Z * multiplier.Z);
                vertices[i] = new Vertex(src[i].Position, src[i].Normal, c);
            }

            return new Primitives.Mesh(vertices, mesh.Indices);
        }

        /// <summary>
        /// Moves every vertex by <paramref name="offset"/>(position) and rebuilds the normals from the
        /// deformed faces. The general deformation primitive: <see cref="Crumple"/> is one offset function,
        /// terrain-style height noise or a shockwave ripple are others. If co-located vertices must move
        /// together (to keep a panelled mesh watertight), make the offset function depend only on position —
        /// identical inputs then yield identical moves by construction.
        /// </summary>
        public static Primitives.Mesh Displace(Primitives.Mesh mesh, Func<Vector3, Vector3> offset)
        {
            if (offset is null) throw new ArgumentNullException(nameof(offset));

            Vertex[] src = mesh.Vertices;
            var vertices = new Vertex[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                vertices[i] = new Vertex(src[i].Position + offset(src[i].Position), src[i].Normal, src[i].Color);
            }

            RebuildNormals(vertices, mesh.Indices);
            return new Primitives.Mesh(vertices, mesh.Indices);
        }

        /// <summary>
        /// Battle-damage deformation: every vertex is pushed along a deterministic hash of its (quantised)
        /// position — inward-biased, because impacts cave panels in more than they blow them out — and the
        /// normals are rebuilt. Positions are quantised before hashing so shared panel corners move together
        /// and the mesh crumples without tearing. Purely geometric: recolouring (soot, embers) is the
        /// caller's concern, composed via <see cref="Tint"/> or a custom pass.
        /// </summary>
        /// <param name="mesh">Source mesh (unchanged).</param>
        /// <param name="seed">Selects one crumple out of the family; the same seed always produces the same wreck.</param>
        /// <param name="amount">Displacement as a fraction of the mesh's bounding radius (0.1 = a visibly
        /// battered hull that is still recognisable).</param>
        /// <param name="inwardBias">How strongly the displacement leans toward the mesh centre (0 = pure
        /// noise, 1 = mostly caving).</param>
        public static Primitives.Mesh Crumple(Primitives.Mesh mesh, int seed = 1, float amount = 0.10f, float inwardBias = 0.6f)
        {
            float maxR = 0.01f;
            foreach (Vertex v in mesh.Vertices)
            {
                float r = v.Position.Length;
                if (r > maxR) maxR = r;
            }
            float amp = maxR * amount;

            return Displace(mesh, p =>
            {
                int hx = (int)MathF.Round(p.X * 64f), hy = (int)MathF.Round(p.Y * 64f), hz = (int)MathF.Round(p.Z * 64f);
                uint h = (uint)(seed * 374761393 + hx * 668265263 + hy * 2246822519 + hz * 3266489917);
                h = (h ^ (h >> 15)) * 2246822519u;
                h = (h ^ (h >> 13)) * 3266489917u;
                float fx = ((h & 0x3FF) / 1023f) * 2f - 1f;
                float fy = (((h >> 10) & 0x3FF) / 1023f) * 2f - 1f;
                float fz = (((h >> 20) & 0x3FF) / 1023f) * 2f - 1f;
                var noise = new Vector3(fx, fy, fz);
                Vector3 inward = p.LengthSquared > 1e-6f ? p.Normalize() * -inwardBias : default;
                return (noise + inward) * amp;
            });
        }

        /// <summary>
        /// Concatenates meshes into one (for static structures assembled from parts, uploaded once). Index
        /// values are rebased per part; the combined vertex count must stay within the 16-bit index space.
        /// </summary>
        /// <exception cref="InvalidOperationException">If the merged mesh would exceed 65,535 vertices — the
        /// caller should split it into multiple draws rather than silently corrupt indices.</exception>
        public static Primitives.Mesh Merge(params Primitives.Mesh[] meshes)
        {
            if (meshes is null || meshes.Length == 0)
            {
                return new Primitives.Mesh(Array.Empty<Vertex>(), Array.Empty<ushort>());
            }

            int totalVertices = 0, totalIndices = 0;
            foreach (Primitives.Mesh m in meshes)
            {
                totalVertices += m.Vertices.Length;
                totalIndices += m.Indices.Length;
            }

            if (totalVertices > ushort.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Merged mesh has {totalVertices} vertices; 16-bit indices allow at most {ushort.MaxValue}.");
            }

            var vertices = new Vertex[totalVertices];
            var indices = new ushort[totalIndices];
            int vAt = 0, iAt = 0;
            foreach (Primitives.Mesh m in meshes)
            {
                Array.Copy(m.Vertices, 0, vertices, vAt, m.Vertices.Length);
                for (int i = 0; i < m.Indices.Length; i++)
                {
                    indices[iAt + i] = (ushort)(m.Indices[i] + vAt);
                }

                vAt += m.Vertices.Length;
                iAt += m.Indices.Length;
            }

            return new Primitives.Mesh(vertices, indices);
        }

        /// <summary>
        /// Recomputes vertex normals from the triangle faces, area-weighted, <b>in place</b>. Shared
        /// vertices receive the blend of their faces (smooth shading); unshared per-face vertices receive
        /// exactly their face normal (flat shading) — so the operation respects however the mesh was
        /// authored. Degenerate vertices (no faces, or zero-area faces only) keep their existing normal.
        /// </summary>
        public static void RebuildNormals(Vertex[] vertices, ushort[] indices)
        {
            if (vertices is null) throw new ArgumentNullException(nameof(vertices));
            if (indices is null) throw new ArgumentNullException(nameof(indices));

            var accum = new Vector3[vertices.Length];
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                Vector3 n = Vector3.Cross(
                    vertices[b].Position - vertices[a].Position,
                    vertices[c].Position - vertices[a].Position); // magnitude = 2×area → bigger faces weigh more
                accum[a] += n;
                accum[b] += n;
                accum[c] += n;
            }

            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 n = accum[i].LengthSquared > 1e-12f ? accum[i].Normalize() : vertices[i].Normal;
                vertices[i] = new Vertex(vertices[i].Position, n, vertices[i].Color);
            }
        }
    }
}
