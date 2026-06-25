namespace RP.Game.Rendering
{
    using System.Collections.Generic;
    using RP.Math;

    /// <summary>
    /// Procedurally-built meshes. Until an asset pipeline loads real models, the renderer draws these — a unit
    /// cube and a low-poly <see cref="Dart"/> hull that reads as a ship rather than a box. Each returns flat
    /// vertex/index arrays ready for a device-local buffer; normals are per-triangle (flat shading), which
    /// suits a faceted, low-poly look. Geometry is generic; the game decides a dart "is" a fighter.
    /// </summary>
    public static class Primitives
    {
        public readonly struct Mesh
        {
            public Mesh(Vertex[] vertices, ushort[] indices)
            {
                Vertices = vertices;
                Indices = indices;
            }

            public Vertex[] Vertices { get; }
            public ushort[] Indices { get; }
        }

        /// <summary>
        /// A low-poly ship hull: a long, faceted body tapering to a nose at −Z (the engine's forward), with a
        /// pair of swept wings. Built roughly within the unit box (±0.5) so the renderer's per-instance scale
        /// keeps mapping "scale = ship diameter". Mostly light grey so the per-instance faction tint reads.
        /// </summary>
        public static Mesh Dart()
        {
            var v = new List<Vertex>(48);
            var idx = new List<ushort>(72);

            void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 color)
            {
                Vector3 n = Vector3.Cross(b - a, c - a).Normalize();
                var i = (ushort)v.Count;
                v.Add(new Vertex(a, n, color));
                v.Add(new Vertex(b, n, color));
                v.Add(new Vertex(c, n, color));
                idx.Add(i);
                idx.Add((ushort)(i + 1));
                idx.Add((ushort)(i + 2));
            }

            var hull = new Vector3(0.78f, 0.80f, 0.85f);  // light steel — tint multiplies this
            var wing = new Vector3(0.62f, 0.64f, 0.70f);
            var engine = new Vector3(1.00f, 0.55f, 0.20f); // warm exhaust at the tail

            // Fuselage: a 4-sided spindle. Nose at -Z, a cross-section ring at z=+0.1, tail cap at +Z.
            var nose = new Vector3(0, 0, -0.5f);
            var rT = new Vector3(0, 0.13f, 0.1f);   // top
            var rB = new Vector3(0, -0.10f, 0.1f);  // bottom
            var rR = new Vector3(0.17f, 0, 0.1f);   // right
            var rL = new Vector3(-0.17f, 0, 0.1f);  // left
            var tail = new Vector3(0, 0.01f, 0.5f);

            // Nose facets (front half).
            Tri(nose, rR, rT, hull);
            Tri(nose, rT, rL, hull);
            Tri(nose, rL, rB, hull);
            Tri(nose, rB, rR, hull);

            // Tail facets (back half) — exhaust-tinted.
            Tri(tail, rT, rR, engine);
            Tri(tail, rL, rT, engine);
            Tri(tail, rB, rL, engine);
            Tri(tail, rR, rB, engine);

            // Swept wings off the sides: a flat delta on each side, slightly behind the mid-ring.
            var wingMidR = new Vector3(0.16f, 0, 0.15f);
            var wingTipR = new Vector3(0.5f, 0, 0.42f);
            var wingAftR = new Vector3(0.12f, 0, 0.48f);
            Tri(wingMidR, wingTipR, wingAftR, wing);

            var wingMidL = new Vector3(-0.16f, 0, 0.15f);
            var wingTipL = new Vector3(-0.5f, 0, 0.42f);
            var wingAftL = new Vector3(-0.12f, 0, 0.48f);
            Tri(wingMidL, wingAftL, wingTipL, wing);

            // A small dorsal fin so roll/pitch is legible.
            var finBase1 = new Vector3(0, 0.10f, 0.2f);
            var finBase2 = new Vector3(0, 0.10f, 0.46f);
            var finTip = new Vector3(0, 0.30f, 0.45f);
            Tri(finBase1, finBase2, finTip, wing);

            return new Mesh(v.ToArray(), idx.ToArray());
        }

        /// <summary>A unit cube (±0.5), per-face normals and colours. Kept for debris and as a fallback.</summary>
        public static Mesh Cube()
        {
            var v = new List<Vertex>(24);
            var idx = new List<ushort>(36);

            void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Vector3 color)
            {
                var i = (ushort)v.Count;
                v.Add(new Vertex(a, normal, color));
                v.Add(new Vertex(b, normal, color));
                v.Add(new Vertex(c, normal, color));
                v.Add(new Vertex(d, normal, color));
                idx.Add(i);
                idx.Add((ushort)(i + 1));
                idx.Add((ushort)(i + 2));
                idx.Add(i);
                idx.Add((ushort)(i + 2));
                idx.Add((ushort)(i + 3));
            }

            Vector3 P(float x, float y, float z) => new(x, y, z);
            var grey = new Vector3(0.8f, 0.8f, 0.85f);
            var c000 = P(-0.5f, -0.5f, -0.5f);
            var c100 = P(0.5f, -0.5f, -0.5f);
            var c110 = P(0.5f, 0.5f, -0.5f);
            var c010 = P(-0.5f, 0.5f, -0.5f);
            var c001 = P(-0.5f, -0.5f, 0.5f);
            var c101 = P(0.5f, -0.5f, 0.5f);
            var c111 = P(0.5f, 0.5f, 0.5f);
            var c011 = P(-0.5f, 0.5f, 0.5f);

            Face(c001, c101, c111, c011, P(0, 0, 1), grey);
            Face(c100, c000, c010, c110, P(0, 0, -1), grey);
            Face(c101, c100, c110, c111, P(1, 0, 0), grey);
            Face(c000, c001, c011, c010, P(-1, 0, 0), grey);
            Face(c011, c111, c110, c010, P(0, 1, 0), grey);
            Face(c000, c100, c101, c001, P(0, -1, 0), grey);

            return new Mesh(v.ToArray(), idx.ToArray());
        }
    }
}
