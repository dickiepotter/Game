namespace RP.Game.Rendering
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using RP.Math;

    /// <summary>
    /// A small Wavefront <b>OBJ</b> loader: enough to turn the resource ship models into the engine's
    /// <see cref="Vertex"/>/index mesh format. It reads positions (<c>v</c>) and normals (<c>vn</c>),
    /// triangulates faces (fans, so quads and n-gons are fine), and indexes by unique position+normal pair.
    /// Texture coordinates and materials are skipped — hulls are tinted per-instance, not textured (yet).
    /// </summary>
    /// <remarks>
    /// <para>Missing normals are synthesised per face (flat shading), which suits the faceted low-poly look.
    /// The result is <see cref="Normalize"/>d into the engine's hull convention — centred at the origin, scaled
    /// so its longest axis is one unit (so a per-instance scale still means "≈ hull length"), and turned so the
    /// nose points down −Z (forward) with +Y up. The nose is detected automatically as the slimmer end of the
    /// long axis, since exporters disagree on which way a model faces.</para>
    /// </remarks>
    public static class ObjLoader
    {
        /// <summary>Parses an OBJ document into a normalised, instance-ready mesh tinted with <paramref name="color"/>.</summary>
        public static Primitives.Mesh Load(string objText, Vector3 color)
        {
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var verts = new List<Vertex>();
            var indices = new List<ushort>();
            var dedupe = new Dictionary<(int, int), ushort>();

            using var reader = new StringReader(objText);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length < 2) continue;
                char c0 = line[0];
                if (c0 == 'v' && line[1] == ' ')
                {
                    positions.Add(ParseVec3(line));
                }
                else if (c0 == 'v' && line[1] == 'n')
                {
                    normals.Add(ParseVec3(line));
                }
                else if (c0 == 'f' && line[1] == ' ')
                {
                    ParseFace(line, positions, normals, verts, indices, dedupe);
                }
            }

            Vertex[] vArray = verts.ToArray();
            Normalize(vArray);
            return new Primitives.Mesh(vArray, indices.ToArray());
        }

        private static Vector3 ParseVec3(string line)
        {
            // "v x y z" / "vn x y z" — split on spaces, take the last three numeric tokens.
            string[] t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            float x = float.Parse(t[1], CultureInfo.InvariantCulture);
            float y = float.Parse(t[2], CultureInfo.InvariantCulture);
            float z = float.Parse(t[3], CultureInfo.InvariantCulture);
            return new Vector3(x, y, z);
        }

        private static void ParseFace(
            string line, List<Vector3> positions, List<Vector3> normals,
            List<Vertex> verts, List<ushort> indices, Dictionary<(int, int), ushort> dedupe)
        {
            string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int n = tokens.Length - 1;
            if (n < 3) return;

            Span<ushort> corner = n <= 16 ? stackalloc ushort[n] : new ushort[n];
            bool haveFaceNormal = false;
            Vector3 faceNormal = default;

            for (int i = 0; i < n; i++)
            {
                ParseCorner(tokens[i + 1], positions.Count, normals.Count, out int vi, out int ni);

                Vector3 normal;
                if (ni >= 0 && ni < normals.Count)
                {
                    normal = normals[ni];
                }
                else
                {
                    if (!haveFaceNormal)
                    {
                        faceNormal = FaceNormal(positions, tokens);
                        haveFaceNormal = true;
                    }
                    normal = faceNormal;
                    ni = -1; // key all flat-shaded corners of this face together by position only
                }

                var key = (vi, ni);
                if (!dedupe.TryGetValue(key, out ushort index))
                {
                    index = (ushort)verts.Count;
                    verts.Add(new Vertex(positions[vi], normal, new Vector3(1f, 1f, 1f)));
                    dedupe[key] = index;
                }

                corner[i] = index;
            }

            // Triangulate as a fan around the first corner.
            for (int i = 1; i + 1 < n; i++)
            {
                indices.Add(corner[0]);
                indices.Add(corner[i]);
                indices.Add(corner[i + 1]);
            }
        }

        // "v", "v/vt", "v//vn", "v/vt/vn" — 1-based, negatives are relative to the end.
        private static void ParseCorner(string token, int vCount, int nCount, out int vi, out int ni)
        {
            string[] parts = token.Split('/');
            vi = Resolve(parts[0], vCount);
            ni = parts.Length >= 3 && parts[2].Length > 0 ? Resolve(parts[2], nCount) : -1;
        }

        private static int Resolve(string s, int count)
        {
            int i = int.Parse(s, CultureInfo.InvariantCulture);
            return i > 0 ? i - 1 : count + i; // 1-based, or negative-relative
        }

        private static Vector3 FaceNormal(List<Vector3> positions, string[] tokens)
        {
            ParseCorner(tokens[1], positions.Count, 0, out int a, out _);
            ParseCorner(tokens[2], positions.Count, 0, out int b, out _);
            ParseCorner(tokens[3], positions.Count, 0, out int c, out _);
            Vector3 n = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            return n.LengthSquared > 1e-12f ? n.Normalize() : new Vector3(0, 1, 0);
        }

        /// <summary>
        /// Centres the mesh, scales it so its longest axis is one unit, and turns the slimmer end of that axis
        /// to face −Z (forward) with +Y up — the hull convention the renderer and AI assume.
        /// </summary>
        private static void Normalize(Vertex[] verts)
        {
            if (verts.Length == 0) return;

            Vector3 min = verts[0].Position, max = verts[0].Position;
            foreach (Vertex v in verts)
            {
                min = Min(min, v.Position);
                max = Max(max, v.Position);
            }

            Vector3 centre = (min + max) * 0.5f;
            Vector3 size = max - min;
            float longest = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
            float scale = longest > 1e-6f ? 1f / longest : 1f;

            // Map the model's longest axis onto Z (length), the next onto Y (up) — a stable basis for hulls
            // exported facing any which way. Build a permutation of axes by extent.
            int lengthAxis = LongestAxis(size);
            int upAxis = SecondAxis(size, lengthAxis);
            int rightAxis = 3 - lengthAxis - upAxis;

            // Decide which end of the length axis is the nose: the half with the smaller cross-section.
            float nose = NoseSign(verts, centre, lengthAxis, rightAxis, upAxis);

            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 p = (verts[i].Position - centre) * scale;
                Vector3 n = verts[i].Normal;

                // Reorder axes -> (right=X, up=Y, forward maps to -Z so multiply length component by -nose).
                var rp = new Vector3(Comp(p, rightAxis), Comp(p, upAxis), Comp(p, lengthAxis) * -nose);
                var rn = new Vector3(Comp(n, rightAxis), Comp(n, upAxis), Comp(n, lengthAxis) * -nose);
                verts[i] = new Vertex(rp, rn.LengthSquared > 1e-12f ? rn.Normalize() : rn, verts[i].Color);
            }
        }

        // +1 if the model's existing +length end is the nose, -1 if the -length end is. The nose is the slimmer
        // end (smaller mean cross-sectional radius), so it tapers to a point as a ship should.
        private static float NoseSign(Vertex[] verts, Vector3 centre, int lengthAxis, int a, int b)
        {
            double posSum = 0, negSum = 0; int posN = 0, negN = 0;
            foreach (Vertex v in verts)
            {
                float along = Comp(v.Position, lengthAxis) - Comp(centre, lengthAxis);
                float ca = Comp(v.Position, a) - Comp(centre, a);
                float cb = Comp(v.Position, b) - Comp(centre, b);
                double r = Math.Sqrt(ca * ca + cb * cb);
                if (along >= 0) { posSum += r; posN++; } else { negSum += r; negN++; }
            }

            double posMean = posN > 0 ? posSum / posN : 0;
            double negMean = negN > 0 ? negSum / negN : 0;
            // If the +end is slimmer, it's the nose -> we want +end to map to -Z, i.e. nose = +1.
            return posMean <= negMean ? 1f : -1f;
        }

        private static int LongestAxis(Vector3 s) =>
            s.X >= s.Y && s.X >= s.Z ? 0 : (s.Y >= s.Z ? 1 : 2);

        private static int SecondAxis(Vector3 s, int longest)
        {
            int a = (longest + 1) % 3, b = (longest + 2) % 3;
            return Comp(s, a) >= Comp(s, b) ? a : b;
        }

        private static float Comp(Vector3 v, int axis) => axis == 0 ? v.X : (axis == 1 ? v.Y : v.Z);
        private static Vector3 Min(Vector3 a, Vector3 b) => new(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Min(a.Z, b.Z));
        private static Vector3 Max(Vector3 a, Vector3 b) => new(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y), MathF.Max(a.Z, b.Z));
    }
}
