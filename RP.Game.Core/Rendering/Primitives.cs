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

        /// <summary>
        /// A jagged rock within the unit box (±0.5): an icosphere with each vertex pushed in/out by a
        /// deterministic hash of its direction, flat-shaded so the facets catch light like fractured stone.
        /// One seed = one shape, so a field of asteroids can draw many differently-lumpy rocks from a few
        /// uploaded variants (per-instance scale/rotation does the rest of the variety).
        /// </summary>
        public static Mesh Rock(int seed = 1)
        {
            // Icosahedron base: 12 vertices, 20 faces, subdivided once to 80 faces — enough silhouette
            // for a boulder while staying trivially cheap next to the ship hull.
            float t = (1f + (float)System.Math.Sqrt(5.0)) / 2f;
            var baseVerts = new[]
            {
                new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
            };
            var faces = new[]
            {
                (0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11),
                (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
                (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9),
                (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1),
            };

            // Deterministic per-direction bumpiness: hash the (rounded) unit direction with the seed. The
            // rounding keeps shared edge vertices hashing identically, so subdivided faces stay watertight.
            float Bump(Vector3 unit)
            {
                int hx = (int)System.Math.Round(unit.X * 512f);
                int hy = (int)System.Math.Round(unit.Y * 512f);
                int hz = (int)System.Math.Round(unit.Z * 512f);
                uint h = (uint)(seed * 374761393 + hx * 668265263 + hy * 2246822519 + hz * 3266489917);
                h = (h ^ (h >> 15)) * 2246822519u;
                h = (h ^ (h >> 13)) * 3266489917u;
                return ((h ^ (h >> 16)) & 0xFFFF) / 65535f; // 0..1
            }

            // Displaced radius: 0.30 .. 0.50, so the rock always fits the unit box in any orientation.
            Vector3 Place(Vector3 v)
            {
                Vector3 unit = v.Normalize();
                return unit * (0.30f + 0.20f * Bump(unit));
            }

            var v = new List<Vertex>(240);
            var idx = new List<ushort>(240);
            var rockLight = new Vector3(0.46f, 0.42f, 0.37f); // sunlit dusty stone
            var rockDark = new Vector3(0.28f, 0.26f, 0.24f);  // crevice

            void Tri(Vector3 a, Vector3 b, Vector3 c)
            {
                Vector3 n = Vector3.Cross(b - a, c - a).Normalize();
                // Facets that dip inward (short mean radius) read as darker, cracked stone.
                float mean = (a.Length + b.Length + c.Length) / 3f;
                float shade = (mean - 0.30f) / 0.20f;
                Vector3 col = rockDark + (rockLight - rockDark) * System.Math.Clamp(shade, 0f, 1f);
                var i = (ushort)v.Count;
                v.Add(new Vertex(a, n, col));
                v.Add(new Vertex(b, n, col));
                v.Add(new Vertex(c, n, col));
                idx.Add(i); idx.Add((ushort)(i + 1)); idx.Add((ushort)(i + 2));
            }

            foreach ((int fa, int fb, int fc) in faces)
            {
                Vector3 a = baseVerts[fa], b = baseVerts[fb], c = baseVerts[fc];
                // Midpoint (1-level) subdivision: 4 triangles per face.
                Vector3 ab = (a + b) * 0.5f, bc = (b + c) * 0.5f, ca = (c + a) * 0.5f;
                Tri(Place(a), Place(ab), Place(ca));
                Tri(Place(ab), Place(b), Place(bc));
                Tri(Place(ca), Place(bc), Place(c));
                Tri(Place(ab), Place(bc), Place(ca));
            }

            return new Mesh(v.ToArray(), idx.ToArray());
        }

        /// <summary>
        /// A mesh planet: a twice-subdivided icosphere (radius 0.5) painted with seeded latitudinal
        /// climate bands with noise-wobbled boundaries. Colours stay below 1 (a planet reflects, it
        /// doesn't emit); the standard mesh lighting gives it a day side, a night side, and a blue fresnel
        /// rim. <b>Scope:</b> at 320 faces this suits mid-range set dressing — moons, orbs, holotable
        /// globes — where the silhouette stays small. A planet that fills real sky must never be a mesh
        /// (facets betray it); use the renderer's analytic backdrop planet
        /// (<c>VulkanRenderer.PlanetPosition</c>/<c>PlanetRadius</c>), which ray-traces a perfect sphere
        /// per pixel in the sky pass.
        /// </summary>
        public static Mesh Planet(int seed = 1)
        {
            float t = (1f + (float)System.Math.Sqrt(5.0)) / 2f;
            var raw = new[]
            {
                new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
            };
            var baseFaces = new[]
            {
                (0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11),
                (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
                (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9),
                (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1),
            };

            // Deterministic per-direction wobble, shared by co-located vertices (quantised input).
            float Wobble(Vector3 unit, int salt)
            {
                int hx = (int)System.Math.Round(unit.X * 256f);
                int hy = (int)System.Math.Round(unit.Y * 256f);
                int hz = (int)System.Math.Round(unit.Z * 256f);
                uint h = (uint)((seed + salt) * 374761393 + hx * 668265263 + hy * 2246822519 + hz * 3266489917);
                h = (h ^ (h >> 15)) * 2246822519u;
                h = (h ^ (h >> 13)) * 3266489917u;
                return ((h ^ (h >> 16)) & 0xFFFF) / 65535f; // 0..1
            }

            // Seeded palette: ocean, two land belts, ice.
            var ocean = new Vector3(0.10f + Wobble(new Vector3(1, 0, 0), 1) * 0.06f, 0.22f, 0.38f);
            var lowland = new Vector3(0.24f, 0.30f + Wobble(new Vector3(0, 1, 0), 2) * 0.10f, 0.18f);
            var highland = new Vector3(0.42f, 0.34f, 0.22f);
            var ice = new Vector3(0.80f, 0.84f, 0.90f);

            Vector3 ColourAt(Vector3 unit)
            {
                // Latitude with a wobbled boundary, plus a continents/ocean noise field.
                float lat = System.MathF.Abs(unit.Y) + (Wobble(unit, 3) - 0.5f) * 0.18f;
                if (lat > 0.82f) return ice;
                float continents = Wobble(unit * 1.7f, 4);
                if (continents < 0.55f) return ocean;
                return continents < 0.8f ? lowland : highland;
            }

            var v = new List<Vertex>(960);
            var idx = new List<ushort>(960);
            void Tri(Vector3 a, Vector3 b, Vector3 c)
            {
                Vector3 pa = a.Normalize() * 0.5f, pb = b.Normalize() * 0.5f, pc = c.Normalize() * 0.5f;
                // Smooth normals (the radial direction): a planet is round, not faceted.
                Vector3 centreUnit = ((pa + pb + pc) * (1f / 3f)).Normalize();
                Vector3 col = ColourAt(centreUnit);
                var i = (ushort)v.Count;
                v.Add(new Vertex(pa, pa.Normalize(), col));
                v.Add(new Vertex(pb, pb.Normalize(), col));
                v.Add(new Vertex(pc, pc.Normalize(), col));
                idx.Add(i); idx.Add((ushort)(i + 1)); idx.Add((ushort)(i + 2));
            }

            foreach ((int fa, int fb, int fc) in baseFaces)
            {
                // Two midpoint subdivisions: 20 -> 80 -> 320 faces.
                Vector3 a = raw[fa], b = raw[fb], c = raw[fc];
                Vector3 ab = (a + b) * 0.5f, bc = (b + c) * 0.5f, ca = (c + a) * 0.5f;
                foreach ((Vector3 p, Vector3 q, Vector3 r) in new[]
                {
                    (a, ab, ca), (ab, b, bc), (ca, bc, c), (ab, bc, ca),
                })
                {
                    Vector3 pq = (p + q) * 0.5f, qr = (q + r) * 0.5f, rp = (r + p) * 0.5f;
                    Tri(p, pq, rp);
                    Tri(pq, q, qr);
                    Tri(rp, qr, r);
                    Tri(pq, qr, rp);
                }
            }

            return new Mesh(v.ToArray(), idx.ToArray());
        }

        /// <summary>
        /// A glow orb for point FX (particles, dust, engine bloom, flares): an icosahedron of radius 0.5
        /// with pure-white vertices, so the per-instance tint IS the final colour and the HDR bloom turns
        /// each one into a soft point of light rather than a recognisable solid.
        /// </summary>
        public static Mesh Orb()
        {
            float t = (1f + (float)System.Math.Sqrt(5.0)) / 2f;
            var raw = new[]
            {
                new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
            };
            var faces = new[]
            {
                (0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11),
                (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
                (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9),
                (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1),
            };

            var v = new List<Vertex>(60);
            var idx = new List<ushort>(60);
            var white = new Vector3(1f, 1f, 1f);
            foreach ((int fa, int fb, int fc) in faces)
            {
                Vector3 a = raw[fa].Normalize() * 0.5f;
                Vector3 b = raw[fb].Normalize() * 0.5f;
                Vector3 c = raw[fc].Normalize() * 0.5f;
                Vector3 n = Vector3.Cross(b - a, c - a).Normalize();
                var i = (ushort)v.Count;
                v.Add(new Vertex(a, n, white));
                v.Add(new Vertex(b, n, white));
                v.Add(new Vertex(c, n, white));
                idx.Add(i); idx.Add((ushort)(i + 1)); idx.Add((ushort)(i + 2));
            }

            return new Mesh(v.ToArray(), idx.ToArray());
        }

        /// <summary>
        /// A weapon bolt: a long, thin octahedral shard of light, nose at −Z, with an HDR-hot core that the
        /// bloom pass streaks into a beam. Authored 1 unit long and very slender, so uniform instance scale
        /// sets the bolt's length; the per-instance tint colours the light (its vertex colours are
        /// intensity, &gt; 1 on purpose). Oriented per instance to fly along its velocity.
        /// </summary>
        public static Mesh Bolt()
        {
            var v = new List<Vertex>(24);
            var idx = new List<ushort>(24);

            // Intensity gradient: blinding at the head, cooling toward the tail tip.
            var head = new Vector3(3.2f, 3.2f, 3.0f);
            var tail = new Vector3(0.9f, 0.8f, 0.7f);

            var nose = new Vector3(0, 0, -0.5f);
            var aft = new Vector3(0, 0, 0.5f);
            const float w = 0.035f;      // half-width: a needle, not a rod
            const float waistZ = -0.25f; // widest near the head, like a droplet of light
            var ring = new[]
            {
                new Vector3(w, 0, waistZ), new Vector3(0, w, waistZ),
                new Vector3(-w, 0, waistZ), new Vector3(0, -w, waistZ),
            };

            void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 colA, Vector3 colB, Vector3 colC)
            {
                Vector3 n = Vector3.Cross(b - a, c - a).Normalize();
                var i = (ushort)v.Count;
                v.Add(new Vertex(a, n, colA));
                v.Add(new Vertex(b, n, colB));
                v.Add(new Vertex(c, n, colC));
                idx.Add(i); idx.Add((ushort)(i + 1)); idx.Add((ushort)(i + 2));
            }

            for (int k = 0; k < 4; k++)
            {
                Vector3 a = ring[k], b = ring[(k + 1) % 4];
                Tri(nose, a, b, head, head, head);     // head facets: white-hot
                Tri(aft, b, a, tail, head, head);      // tail facets: fading to the tip
            }

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
