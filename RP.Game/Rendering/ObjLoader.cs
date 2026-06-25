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
            => Load(objText, color, null, 0, 0);

        /// <summary>
        /// As <see cref="Load(string, Vector3)"/>, but when a raw-RGBA <paramref name="texRgba"/> texture is
        /// supplied it bakes the texture's <b>luminance</b> into each vertex's colour (a touch of the material
        /// hue kept), so the painted panel detail survives while the per-instance faction tint still colours
        /// the hull. A pragmatic stand-in for full UV texture sampling that needs no extra GPU plumbing.
        /// </summary>
        public static Primitives.Mesh Load(string objText, Vector3 color, byte[]? texRgba, int texW, int texH)
        {
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var texcoords = new List<Vector2>();
            var verts = new List<Vertex>();
            var indices = new List<ushort>();
            var dedupe = new Dictionary<(int, int, int), ushort>();
            byte[]? tex = (texRgba != null && texW > 0 && texH > 0 && texRgba.Length >= texW * texH * 4) ? texRgba : null;

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
                else if (c0 == 'v' && line[1] == 't')
                {
                    texcoords.Add(ParseVec2(line));
                }
                else if (c0 == 'f' && line[1] == ' ')
                {
                    ParseFace(line, positions, normals, texcoords, verts, indices, dedupe, color, tex, texW, texH);
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
            string line, List<Vector3> positions, List<Vector3> normals, List<Vector2> texcoords,
            List<Vertex> verts, List<ushort> indices, Dictionary<(int, int, int), ushort> dedupe,
            Vector3 fallbackColor, byte[]? tex, int texW, int texH)
        {
            string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int n = tokens.Length - 1;
            if (n < 3) return;

            Span<ushort> corner = n <= 16 ? stackalloc ushort[n] : new ushort[n];
            bool haveFaceNormal = false;
            Vector3 faceNormal = default;

            for (int i = 0; i < n; i++)
            {
                ParseCorner(tokens[i + 1], positions.Count, texcoords.Count, normals.Count, out int vi, out int ti, out int ni);

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

                Vector3 vcol = fallbackColor;
                if (tex != null && ti >= 0 && ti < texcoords.Count)
                {
                    vcol = SampleLuminance(tex, texW, texH, texcoords[ti]);
                }
                else
                {
                    ti = -1;
                }

                var key = (vi, ti, ni);
                if (!dedupe.TryGetValue(key, out ushort index))
                {
                    index = (ushort)verts.Count;
                    verts.Add(new Vertex(positions[vi], normal, vcol));
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
        private static void ParseCorner(string token, int vCount, int tCount, int nCount, out int vi, out int ti, out int ni)
        {
            string[] parts = token.Split('/');
            vi = Resolve(parts[0], vCount);
            ti = parts.Length >= 2 && parts[1].Length > 0 ? Resolve(parts[1], tCount) : -1;
            ni = parts.Length >= 3 && parts[2].Length > 0 ? Resolve(parts[2], nCount) : -1;
        }

        private static Vector2 ParseVec2(string line)
        {
            string[] t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new Vector2(float.Parse(t[1], CultureInfo.InvariantCulture), float.Parse(t[2], CultureInfo.InvariantCulture));
        }

        // Samples the texture's luminance at a UV (V flipped to image rows; UVs wrapped), keeping a little of
        // the material hue. The result is the per-vertex base colour the faction tint then multiplies.
        private static Vector3 SampleLuminance(byte[] rgba, int w, int h, Vector2 uv)
        {
            float u = uv.X - MathF.Floor(uv.X);
            float v = uv.Y - MathF.Floor(uv.Y);
            int px = Math.Clamp((int)(u * (w - 1)), 0, w - 1);
            int py = Math.Clamp((int)((1f - v) * (h - 1)), 0, h - 1);
            int idx = (py * w + px) * 4;
            float r = rgba[idx] / 255f, g = rgba[idx + 1] / 255f, b = rgba[idx + 2] / 255f;
            float lum = 0.299f * r + 0.587f * g + 0.114f * b;
            const float hue = 0.18f; // mostly luminance, a hint of material colour
            float scale = 1.35f;     // lift so lit hulls aren't muddy
            return new Vector3(
                MathF.Min(1f, (lum + (r - lum) * hue) * scale),
                MathF.Min(1f, (lum + (g - lum) * hue) * scale),
                MathF.Min(1f, (lum + (b - lum) * hue) * scale));
        }

        private static int Resolve(string s, int count)
        {
            int i = int.Parse(s, CultureInfo.InvariantCulture);
            return i > 0 ? i - 1 : count + i; // 1-based, or negative-relative
        }

        private static Vector3 FaceNormal(List<Vector3> positions, string[] tokens)
        {
            ParseCorner(tokens[1], positions.Count, 0, 0, out int a, out _, out _);
            ParseCorner(tokens[2], positions.Count, 0, 0, out int b, out _, out _);
            ParseCorner(tokens[3], positions.Count, 0, 0, out int c, out _, out _);
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

            // These hulls are authored in the usual game basis: +X right, +Y up, and the long axis along Z.
            // We do NOT permute axes (that would roll a wide-but-short fighter onto its side); we only centre,
            // uniformly scale, and — if the nose ended up at +Z — yaw 180° so it points down −Z (forward).
            bool flip = NoseAtPositiveZ(verts, centre, size.Z);

            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 p = (verts[i].Position - centre) * scale;
                Vector3 n = verts[i].Normal;

                // 180° about Y (x→−x, z→−z) keeps handedness/winding while turning the nose to −Z.
                if (flip)
                {
                    p = new Vector3(-p.X, p.Y, -p.Z);
                    n = new Vector3(-n.X, n.Y, -n.Z);
                }

                verts[i] = new Vertex(p, n, verts[i].Color);
            }
        }

        // True if the model's nose currently points +Z (so it needs a 180° yaw to face −Z). The nose is the
        // slimmer end of the long axis: compares the mean cross-sectional radius of the +Z third against the
        // −Z third (the middle third is ignored, where a hull is fattest and least telling).
        private static bool NoseAtPositiveZ(Vertex[] verts, Vector3 centre, float lengthZ)
        {
            float band = lengthZ / 6f; // a third of the length, split either side of the centre
            double posSum = 0, negSum = 0; int posN = 0, negN = 0;
            foreach (Vertex v in verts)
            {
                float z = v.Position.Z - centre.Z;
                float dx = v.Position.X - centre.X, dy = v.Position.Y - centre.Y;
                double r = Math.Sqrt(dx * dx + dy * dy);
                if (z > band) { posSum += r; posN++; }
                else if (z < -band) { negSum += r; negN++; }
            }

            double posMean = posN > 0 ? posSum / posN : double.MaxValue;
            double negMean = negN > 0 ? negSum / negN : double.MaxValue;
            return posMean < negMean; // +Z end is slimmer -> nose is at +Z -> flip needed
        }

        private static Vector3 Min(Vector3 a, Vector3 b) => new(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Min(a.Z, b.Z));
        private static Vector3 Max(Vector3 a, Vector3 b) => new(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y), MathF.Max(a.Z, b.Z));
    }
}
