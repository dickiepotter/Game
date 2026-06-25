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

        /// <summary>
        /// A capital ship — a long faceted hull with a raised command tower, engine block, and an
        /// <b>open hangar bay</b> at the bow (−Z) lined with emissive panels, so it reads as a structure you
        /// can fly toward and <i>into</i>. Authored a few units long about the origin (≈3×1×0.7) so a uniform
        /// per-instance scale maps to "scale ≈ hull length ÷ 3"; the game decides this hull "is" a carrier.
        /// </summary>
        public static Mesh Carrier()
        {
            var v = new List<Vertex>(256);
            var idx = new List<ushort>(384);

            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 color)
            {
                Vector3 n = Vector3.Cross(b - a, c - a).Normalize();
                var i = (ushort)v.Count;
                v.Add(new Vertex(a, n, color));
                v.Add(new Vertex(b, n, color));
                v.Add(new Vertex(c, n, color));
                v.Add(new Vertex(d, n, color));
                idx.Add(i); idx.Add((ushort)(i + 1)); idx.Add((ushort)(i + 2));
                idx.Add(i); idx.Add((ushort)(i + 2)); idx.Add((ushort)(i + 3));
            }

            // An axis-aligned box [min,max] with outward faces (skip the ones named in `open` to leave a mouth).
            void Box(Vector3 lo, Vector3 hi, Vector3 color, string open = "")
            {
                Vector3 a000 = new(lo.X, lo.Y, lo.Z), a100 = new(hi.X, lo.Y, lo.Z);
                Vector3 a110 = new(hi.X, hi.Y, lo.Z), a010 = new(lo.X, hi.Y, lo.Z);
                Vector3 a001 = new(lo.X, lo.Y, hi.Z), a101 = new(hi.X, lo.Y, hi.Z);
                Vector3 a111 = new(hi.X, hi.Y, hi.Z), a011 = new(lo.X, hi.Y, hi.Z);
                if (!open.Contains("front")) Quad(a100, a000, a010, a110, color); // -Z
                if (!open.Contains("back")) Quad(a001, a101, a111, a011, color);  // +Z
                if (!open.Contains("right")) Quad(a101, a100, a110, a111, color); // +X
                if (!open.Contains("left")) Quad(a000, a001, a011, a010, color);  // -X
                if (!open.Contains("top")) Quad(a011, a111, a110, a010, color);   // +Y
                if (!open.Contains("bottom")) Quad(a000, a100, a101, a001, color);// -Y
            }

            var hull = new Vector3(0.42f, 0.46f, 0.55f);   // cold steel blue-grey
            var deck = new Vector3(0.30f, 0.33f, 0.40f);   // darker plating
            var tower = new Vector3(0.50f, 0.54f, 0.62f);
            var engine = new Vector3(0.30f, 1.10f, 1.80f); // bright blue drive wash (HDR > 1 → blooms)
            var bayGlow = new Vector3(0.40f, 1.30f, 1.40f);// inviting teal hangar light

            // Main hull, bow at -Z. Leave the bow face open for the hangar mouth.
            Box(new Vector3(-0.5f, -0.30f, -1.0f), new Vector3(0.5f, 0.30f, 1.5f), hull, open: "front");

            // Hangar bay: a recessed tunnel from the bow (z=-1.5) back to a glowing rear wall at z=-1.0.
            float bx = 0.30f, by = 0.18f, mouth = -1.5f, back = -1.0f;
            Box(new Vector3(-bx, -by, mouth), new Vector3(bx, by, back), bayGlow, open: "front back"); // walls only
            Quad(new Vector3(-bx, -by, back), new Vector3(bx, -by, back),
                 new Vector3(bx, by, back), new Vector3(-bx, by, back), bayGlow); // glowing rear wall

            // Bow shoulders that frame the hangar mouth (fill the hull face around the opening).
            Quad(new Vector3(-0.5f, -0.30f, -1.0f), new Vector3(-bx, -0.30f, -1.0f),
                 new Vector3(-bx, 0.30f, -1.0f), new Vector3(-0.5f, 0.30f, -1.0f), deck);
            Quad(new Vector3(bx, -0.30f, -1.0f), new Vector3(0.5f, -0.30f, -1.0f),
                 new Vector3(0.5f, 0.30f, -1.0f), new Vector3(bx, 0.30f, -1.0f), deck);
            Quad(new Vector3(-bx, by, -1.0f), new Vector3(bx, by, -1.0f),
                 new Vector3(bx, 0.30f, -1.0f), new Vector3(-bx, 0.30f, -1.0f), deck);
            Quad(new Vector3(-bx, -0.30f, -1.0f), new Vector3(bx, -0.30f, -1.0f),
                 new Vector3(bx, -by, -1.0f), new Vector3(-bx, -by, -1.0f), deck);

            // Spine deck, command tower, and the engine block at the stern.
            Box(new Vector3(-0.34f, 0.30f, -0.3f), new Vector3(0.34f, 0.40f, 1.2f), deck);
            Box(new Vector3(-0.16f, 0.40f, 0.7f), new Vector3(0.16f, 0.70f, 1.15f), tower);
            Box(new Vector3(-0.42f, -0.24f, 1.5f), new Vector3(0.42f, 0.24f, 1.62f), engine);

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
