namespace RP.Game.Voxels
{
    /// <summary>
    /// How a voxel of a given type behaves, as far as the <i>engine</i> needs to know. This is the seam
    /// between the generic voxel machinery in <see cref="RP.Game.Voxels"/> and a specific game's block list.
    /// </summary>
    /// <remarks>
    /// <para><b>The boundary this enforces.</b> The engine must mesh, light, collide with and raycast
    /// against a voxel world without ever knowing that block 7 is "granite" or that block 12 drops three
    /// iron when mined. It needs exactly five things: is there anything here, does it stop light, does it
    /// stop a body, does it emit light, and what does each face look like. A game supplies those answers
    /// from its own block registry; nothing about stone, ore or machinery leaks downward.</para>
    ///
    /// <para><b>Why an interface rather than flags on the block id.</b> Packing properties into spare bits
    /// of the id is tempting and fast, but it fixes the property set at engine level forever, and every
    /// game then bends its block list to fit the engine's idea of a block. An interface costs one virtual
    /// call per lookup, and in the hot paths (meshing, lighting) the implementation is a single array index,
    /// so the JIT inlines it away when the call site is monomorphic — which it is.</para>
    ///
    /// <para><b>Implementations must be pure and thread-safe.</b> Meshing and lighting run on worker
    /// threads, several chunks at a time; a palette that mutated or cached per-call would corrupt them.</para>
    /// </remarks>
    public interface IVoxelPalette
    {
        /// <summary>
        /// Whether this block is empty space that the mesher should skip entirely. Air is the overwhelming
        /// majority of any world, so this is the first question asked about every voxel.
        /// </summary>
        bool IsAir(ushort block);

        /// <summary>
        /// Whether this block completely blocks sight. Two consequences follow, and they are the two things
        /// that make voxel rendering affordable at all: an opaque block stops light propagating, and it
        /// hides the face of the block behind it, so that face is never meshed. A solid cube of a million
        /// opaque voxels produces only the faces on its outer shell.
        /// </summary>
        bool IsOpaque(ushort block);

        /// <summary>
        /// Whether a body collides with this block. Distinct from <see cref="IsOpaque"/>: glass is opaque to
        /// neither light nor sight but stops a player, while tall grass stops nothing at all, and water
        /// stops a body only in the sense of slowing it.
        /// </summary>
        bool IsSolid(ushort block);

        /// <summary>
        /// How much light this block gives off, <c>0</c> to <see cref="VoxelLight.MaxLevel"/>. The source
        /// term for the block-light flood fill: a torch or a lava block seeds the fill at its own level and
        /// the propagation does the rest.
        /// </summary>
        byte LightEmission(ushort block);

        /// <summary>
        /// How many levels of light this block removes from anything passing through it, at minimum 1.
        /// </summary>
        /// <remarks>
        /// Every step through open air costs one level, which is what makes light fall off with distance.
        /// A translucent block such as water or leaves costs more, so a lake darkens with depth and a canopy
        /// dapples the ground beneath it without being opaque. Opaque blocks are never traversed at all, so
        /// their value here is not consulted.
        /// </remarks>
        byte LightAttenuation(ushort block);

        /// <summary>
        /// An opaque token describing how one face of this block should be drawn — in practice a material
        /// or texture index.
        /// </summary>
        /// <remarks>
        /// <para>The engine never interprets this value. It does exactly two things with it: hands it to the
        /// vertex so the shader can look the material up, and compares it between neighbouring faces to
        /// decide whether they may be merged into one larger quad by
        /// <see cref="VoxelMesher"/>. That comparison is why the token must be exact rather than
        /// approximate — two faces merge only if they would be drawn identically.</para>
        /// <para>Taking the face as a parameter is what lets a block differ on each side: grass with a green
        /// top, earthy sides and a plain underside is one block, not three.</para>
        /// </remarks>
        uint FaceAppearance(ushort block, BlockFace face);

        /// <summary>
        /// Whether this block's faces should be drawn against a neighbour of the <i>same</i> type.
        /// </summary>
        /// <remarks>
        /// Almost always false, and the reason matters. Two adjacent water blocks should not draw the
        /// surface between them — it would be an invisible-from-outside sheet of geometry costing fill rate,
        /// and with transparency it would double-blend and read as a darker band through the middle of every
        /// pond. Returning true is for the rare block whose internal faces genuinely should show, such as a
        /// stack of panes with visible frames.
        /// </remarks>
        bool DrawsAgainstSelf(ushort block);
    }
}
