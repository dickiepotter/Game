namespace RP.Game.Rendering
{
    using RP.Math;

    /// <summary>
    /// How a voxel surface behaves under light. Chosen by the game per block face and read by the voxel
    /// shader, which uses it to pick a lighting response and a procedural surface detail.
    /// </summary>
    /// <remarks>
    /// Deliberately a small, fixed set rather than a free-form material system. A voxel world draws millions
    /// of faces and cannot afford a per-face material lookup through a descriptor set; packing the kind into
    /// the vertex alongside the colour costs nothing and covers everything a blocky world actually needs.
    /// A game wanting a genuinely new response adds a case here and a branch in the shader, in that order.
    /// </remarks>
    public enum VoxelSurface : byte
    {
        /// <summary>Plain diffuse rock, soil and wood. No specular worth the name.</summary>
        Matte = 0,

        /// <summary>
        /// Metallic: a tight specular highlight tinted by the surface's own colour, and a stronger response
        /// to the sun at grazing angles. What makes an exposed ore vein catch the light and read as metal
        /// rather than as coloured stone.
        /// </summary>
        Metallic = 1,

        /// <summary>
        /// Crystalline: a broad specular, a touch of internal glow, and a faceted detail pattern. For the
        /// gemstone and arcane materials, which should look nothing like painted rock.
        /// </summary>
        Crystal = 2,

        /// <summary>
        /// Foliage: light scatters <i>through</i> it, so it brightens when the sun is behind rather than
        /// darkening. That single term is most of what stops a tree looking like a green cube.
        /// </summary>
        Foliage = 3,

        /// <summary>
        /// Fluid: a moving surface, a strong specular and a view-dependent fresnel that brightens the
        /// surface toward the horizon — the reason a lake reads as water rather than a flat blue sheet.
        /// </summary>
        Fluid = 4,

        /// <summary>
        /// Emissive: lights itself, and blooms in the post chain. For lava, glowstone, and the luminous
        /// ores. The block should also emit into the light grid, but that is a world concern, not a
        /// shading one.
        /// </summary>
        Emissive = 5,

        /// <summary>Loose grains — sand, gravel, snow. A fine speckled detail and a slightly soft response.</summary>
        Granular = 6,

        /// <summary>Worked, man-made surfaces: brick, plate, machinery. A regular detail pattern that reads
        /// as deliberate, so built structures stand out against natural ground.</summary>
        Constructed = 7,
    }

    /// <summary>
    /// The packing convention the voxel shader expects in <see cref="RP.Game.Voxels.VoxelVertex.Material"/>: a base colour
    /// and a <see cref="VoxelSurface"/>, in one 32-bit word.
    /// </summary>
    /// <remarks>
    /// <para><b>Why packed into the vertex rather than looked up.</b> The natural design puts material
    /// properties in a buffer and the vertex carries an index. That needs a descriptor set bound for the
    /// voxel pass, a storage buffer kept in sync with the game's block registry, and a dependent read in the
    /// fragment shader for every pixel of the world. Packing colour and kind into a word the vertex already
    /// carries removes all three, and the cost is that a material has exactly one colour and one kind —
    /// which, for cubes, is all it was ever going to have.</para>
    ///
    /// <para><b>Where the boundary sits.</b> <see cref="RP.Game.Voxels.IVoxelPalette"/> treats the material
    /// word as opaque: the mesher only ever compares it for equality when deciding whether two faces may
    /// merge. This type is the <i>renderer's</i> convention for what those bits mean, which is why it lives
    /// in <c>RP.Game.Rendering</c> next to the shader that reads it, and not in the voxel layer. A game
    /// using a different renderer is free to pack whatever it likes.</para>
    ///
    /// <para><b>Colour is linear.</b> The scene renders to an HDR target and is tonemapped at the end, so
    /// these are linear values, not sRGB. Authoring a colour that "looks right" in a picker and pasting the
    /// value here gives a surface that is noticeably too bright — <see cref="FromSrgb"/> converts.</para>
    /// </remarks>
    public static class VoxelMaterial
    {
        /// <summary>Packs a linear colour and a surface kind into the material word.</summary>
        /// <param name="r">Linear red in <c>[0, 1]</c>.</param>
        /// <param name="g">Linear green in <c>[0, 1]</c>.</param>
        /// <param name="b">Linear blue in <c>[0, 1]</c>.</param>
        /// <param name="surface">How the surface responds to light.</param>
        /// <param name="variation">
        /// How strongly the shader's procedural detail disturbs the surface, in <c>[0, 1]</c>. Zero gives a
        /// flat, plasticky face; higher values break it up. This is the dial that stops a world of solid
        /// colours looking like untextured geometry, which is the usual reason a from-scratch voxel
        /// renderer looks worse than one with a texture atlas despite doing more work per pixel.
        /// </param>
        public static uint Pack(double r, double g, double b, VoxelSurface surface, double variation = 0.5)
        {
            uint ri = Quantise(r);
            uint gi = Quantise(g);
            uint bi = Quantise(b);

            // Variation shares the top byte with the surface kind: 3 bits of kind, 5 of variation. Eight
            // kinds is all the shader branches on, and 32 steps of variation is far finer than the eye can
            // separate on a procedural detail term.
            uint kind = (uint)surface & 0x7u;
            uint detail = (uint)((Clamp01(variation) * 31.0) + 0.5) & 0x1Fu;

            return ri | (gi << 8) | (bi << 16) | (kind << 24) | (detail << 27);
        }

        /// <summary>Packs a colour given as a <see cref="Vector3"/> of linear components.</summary>
        public static uint Pack(Vector3 linearColor, VoxelSurface surface, double variation = 0.5)
            => Pack(linearColor.X, linearColor.Y, linearColor.Z, surface, variation);

        /// <summary>
        /// Packs a colour authored in sRGB — the space every colour picker and hex code is in — converting
        /// it to the linear values the HDR pipeline expects.
        /// </summary>
        /// <param name="hex">A colour as <c>0xRRGGBB</c>.</param>
        /// <param name="surface">How the surface responds to light.</param>
        /// <param name="variation">Procedural detail strength, in <c>[0, 1]</c>.</param>
        public static uint FromSrgb(uint hex, VoxelSurface surface, double variation = 0.5)
        {
            double r = SrgbToLinear(((hex >> 16) & 0xFF) / 255.0);
            double g = SrgbToLinear(((hex >> 8) & 0xFF) / 255.0);
            double b = SrgbToLinear((hex & 0xFF) / 255.0);
            return Pack(r, g, b, surface, variation);
        }

        /// <summary>Unpacks the linear base colour from a material word.</summary>
        public static Vector3 ColorOf(uint material)
            => new Vector3(
                (material & 0xFF) / 255f,
                ((material >> 8) & 0xFF) / 255f,
                ((material >> 16) & 0xFF) / 255f);

        /// <summary>Unpacks the surface kind from a material word.</summary>
        public static VoxelSurface SurfaceOf(uint material) => (VoxelSurface)((material >> 24) & 0x7);

        /// <summary>Unpacks the procedural detail strength from a material word, in <c>[0, 1]</c>.</summary>
        public static double VariationOf(uint material) => ((material >> 27) & 0x1F) / 31.0;

        /// <summary>
        /// The standard sRGB transfer function, inverted. Not a plain <c>pow(v, 2.2)</c>: the real curve is
        /// linear near black, and the difference is visible precisely in the dark parts of a cave, which is
        /// where a voxel game spends much of its time.
        /// </summary>
        public static double SrgbToLinear(double v)
            => v <= 0.04045 ? v / 12.92 : System.Math.Pow((v + 0.055) / 1.055, 2.4);

        private static uint Quantise(double v) => (uint)(Clamp01(v) * 255.0 + 0.5) & 0xFFu;

        private static double Clamp01(double v) => v < 0.0 ? 0.0 : (v > 1.0 ? 1.0 : v);
    }
}
