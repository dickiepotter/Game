#version 450

// Voxel chunk fragment shader.
//
// The look this is aiming for is "the blocky world, lit properly" rather than "the blocky world with
// better textures". The geometry is deliberately crude -- axis-aligned unit cubes -- so everything that
// makes it read as a real place has to come from the shading:
//
//   * baked ambient occlusion and smooth per-vertex light from the mesher, so corners darken and light
//     falls off through a cave the way it should;
//   * a real sun with a day cycle, a sky-coloured ambient that changes with it, and a warm separate
//     colour for torch light so a lit room never looks like daylight;
//   * procedural surface detail derived from world position, which does the job a texture atlas would
//     without any texture memory, any atlas bleeding, or any mip seams -- and, because it is a function of
//     world position rather than of UV, it does not stretch across greedy-merged quads;
//   * per-material response: metal glints, crystal glows faintly, foliage lets light through, fluid
//     brightens toward the horizon;
//   * distance fog into the sky colour, which is what turns a hard chunk-boundary horizon into depth.
//
// Output is linear HDR; the post chain blooms and tonemaps it.

layout(push_constant) uniform Push {
    mat4 viewProj;
    vec4 camPos;        // xyz = camera in render space, w = fog density
    vec4 sunDir;        // xyz = unit direction toward the sun, w = daylight in [0,1]
    vec4 sunColor;      // rgb = sun colour, a = fog start distance
    vec4 chunkOffset;   // xyz = chunk origin in render space, w = detail (0..2) + 4 * submerged fluid (0..3)
} pc;

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vTexCoord;
layout(location = 2) in vec3 vWorldPos;
layout(location = 3) flat in uint vMaterial;
layout(location = 4) in float vAmbientOcclusion;
layout(location = 5) in float vSkyLight;
layout(location = 6) in float vBlockLight;

layout(location = 0) out vec4 outColor;

// Surface kinds, matching RP.Game.Rendering.VoxelSurface.
const uint SURFACE_MATTE       = 0u;
const uint SURFACE_METALLIC    = 1u;
const uint SURFACE_CRYSTAL     = 2u;
const uint SURFACE_FOLIAGE     = 3u;
const uint SURFACE_FLUID       = 4u;
const uint SURFACE_EMISSIVE    = 5u;
const uint SURFACE_GRANULAR    = 6u;
const uint SURFACE_CONSTRUCTED = 7u;

// ---------------------------------------------------------------------------------------------------
// Cheap value noise. Not a full gradient field: this only ever breaks up a flat colour, and at that job
// the difference is invisible while the cost is a third.
// ---------------------------------------------------------------------------------------------------
float hash13(vec3 p)
{
    p = fract(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return fract((p.x + p.y) * p.z);
}

float valueNoise(vec3 p)
{
    vec3 i = floor(p);
    vec3 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);   // smoothstep the blend, or the cell edges show as a grid

    float n000 = hash13(i + vec3(0, 0, 0));
    float n100 = hash13(i + vec3(1, 0, 0));
    float n010 = hash13(i + vec3(0, 1, 0));
    float n110 = hash13(i + vec3(1, 1, 0));
    float n001 = hash13(i + vec3(0, 0, 1));
    float n101 = hash13(i + vec3(1, 0, 1));
    float n011 = hash13(i + vec3(0, 1, 1));
    float n111 = hash13(i + vec3(1, 1, 1));

    return mix(mix(mix(n000, n100, f.x), mix(n010, n110, f.x), f.y),
               mix(mix(n001, n101, f.x), mix(n011, n111, f.x), f.y), f.z);
}

// One octave. Two read slightly richer and cost twice as much, and at full-screen fill rate on an
// integrated GPU that is not a trade worth making -- the second octave is almost invisible once ambient
// occlusion and the light grid are multiplying over it.
float detailNoise(vec3 p)
{
    return valueNoise(p);
}

// One stable value per integer block, so two blocks of the same material are not the same block. This
// is what stops a stone wall reading as one enormous flat quantity of stone.
float hash31(vec3 cell)
{
    vec3 p = fract(cell * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return fract((p.x + p.y) * p.z);
}

// One stable value per integer cell, for anything that wants to differ per brick or per board rather
// than per pixel. Cheap enough to call in a branch the whole screen takes.
float hash21(vec2 cell)
{
    vec3 p = fract(vec3(cell.xyx) * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return fract((p.x + p.y) * p.z);
}

// The sky colour at a given elevation, for a given time of day. Shared by the fog so that a distant
// hillside fades into exactly the sky behind it rather than into a fixed grey -- which is the single
// tell that separates convincing distance from "the fog colour is wrong".
vec3 skyColor(float upness, float daylight)
{
    vec3 dayZenith  = vec3(0.16, 0.34, 0.72);
    vec3 dayHorizon = vec3(0.62, 0.74, 0.92);
    vec3 duskHorizon = vec3(0.85, 0.42, 0.22);
    vec3 nightZenith = vec3(0.012, 0.018, 0.045);
    vec3 nightHorizon = vec3(0.05, 0.06, 0.11);

    // Dusk peaks when the sun is near the horizon, i.e. when daylight is midway through its range.
    float dusk = 1.0 - abs(daylight * 2.0 - 1.0);
    dusk = pow(clamp(dusk, 0.0, 1.0), 2.0);

    vec3 horizon = mix(nightHorizon, mix(dayHorizon, duskHorizon, dusk * 0.8), daylight);
    vec3 zenith  = mix(nightZenith, dayZenith, daylight);

    return mix(horizon, zenith, clamp(upness, 0.0, 1.0));
}

void main()
{
    // ---- Unpack the material ----------------------------------------------------------------------
    vec3 baseColor = vec3(
        float(vMaterial & 0xFFu),
        float((vMaterial >> 8) & 0xFFu),
        float((vMaterial >> 16) & 0xFFu)) / 255.0;

    uint surface = (vMaterial >> 24) & 0x7u;
    bool variant = ((vMaterial >> 27) & 1u) != 0u;
    float variation = float((vMaterial >> 28) & 0xFu) / 15.0;

    // The quality tier scales the procedural detail down, and at the bottom removes it entirely. Every
    // noise fetch below is per-pixel over the whole screen, so this is the single biggest lever the
    // fragment shader has -- and at detail 0 the world is still perfectly readable, because the shape, the
    // ambient occlusion and the light grid are doing the real work.
    // Two values packed into one float: the detail level in 0..2, with the submerged-fluid kind riding
    // above it in steps of four. Taking them apart here rather than at each use keeps the packing in one
    // place, where it can be changed without hunting for the other half of it.
    float detailLevel = mod(pc.chunkOffset.w, 4.0);
    variation *= clamp(detailLevel * 0.5, 0.0, 1.0);

    vec3 N = normalize(vNormal);
    vec3 toCam = pc.camPos.xyz - vWorldPos;
    float distance = length(toCam);
    vec3 V = toCam / max(distance, 1e-4);

    vec3 sunDir = normalize(pc.sunDir.xyz);
    float daylight = pc.sunDir.w;

    // ---- Procedural surface detail ------------------------------------------------------------------
    // Sampled in *world* space, not UV space. That matters twice over: a greedy-merged quad covering 32
    // blocks gets 32 blocks of detail rather than one stretched cell, and two adjacent blocks of the same
    // material line up seamlessly instead of each restarting the pattern.
    float grain = 0.5;
    if (variation <= 0.0)
    {
        // No detail wanted: skip every noise fetch and every derivative. This is the whole point of the
        // bottom tier.
    }
    else if (surface == SURFACE_GRANULAR)
    {
        grain = detailNoise(vWorldPos * 9.0);            // fine speckle: sand, gravel, snow
    }
    else if (surface == SURFACE_CONSTRUCTED && !variant)
    {
        // Brickwork. Courses of two bricks to a block, every other row offset by half, with mortar in the
        // joints and a little tint per brick so a wall is not one flat colour repeated.
        vec2 brick = vTexCoord * vec2(2.0, 2.0);
        brick.x += floor(brick.y) * 0.5;

        vec2 cell = abs(fract(brick) - 0.5);
        float mortar = smoothstep(0.40, 0.48, max(cell.x * 0.9, cell.y));

        // One value per brick, so neighbours differ. Hashing the brick's own index rather than the world
        // position is what keeps the tint constant across the face of a single brick instead of drifting
        // across it.
        float perBrick = hash21(floor(brick));

        grain = 0.5 + (perBrick - 0.5) * 0.30 + 0.12 * detailNoise(vWorldPos * 6.0) - mortar * 0.55;
    }
    else if (surface == SURFACE_CONSTRUCTED && variant)
    {
        // Planking. Long boards with a seam between them, each board its own shade, and a lengthwise
        // grain running along it -- which is what separates a plank floor from a brick one at a glance.
        vec2 board = vTexCoord * vec2(1.0, 4.0);
        float row = floor(board.y);
        board.x += row * 0.37;                            // stagger the butt joints

        float alongSeam = smoothstep(0.46, 0.5, abs(fract(board.y) - 0.5));
        float buttSeam  = smoothstep(0.47, 0.5, abs(fract(board.x * 0.5) - 0.5));

        float perBoard = hash21(vec2(floor(board.x * 0.5), row));
        float lengthwise = detailNoise(vec3(vTexCoord.x * 26.0, row * 7.0, vTexCoord.y * 3.0));

        grain = 0.5 + (perBoard - 0.5) * 0.26 + (lengthwise - 0.5) * 0.30
              - max(alongSeam, buttSeam) * 0.45;
    }
    else if (surface == SURFACE_METALLIC && variant)
    {
        // Ore: dull rock with bright inclusions in it, rather than a block of solid metal. The blobs are
        // a thresholded noise so they clump instead of speckling evenly, which is what makes a vein read
        // as something embedded rather than as paint.
        float rock = detailNoise(vWorldPos * 3.8);
        float blob = detailNoise(vWorldPos * 7.5);
        float inclusion = smoothstep(0.58, 0.74, blob);

        grain = 0.35 + rock * 0.30 + inclusion * 0.85;
    }
    else if (surface == SURFACE_MATTE && variant)
    {
        // Wood, along the grain: rings stretched down the trunk, with the fine streaks that make the
        // difference between timber and brown stone.
        float rings = sin((vTexCoord.x * 7.0 + detailNoise(vWorldPos * 2.0) * 4.0) * 3.14159);
        float streak = detailNoise(vec3(vWorldPos.x * 14.0, vWorldPos.y * 2.2, vWorldPos.z * 14.0));

        grain = 0.5 + rings * 0.16 + (streak - 0.5) * 0.34;
    }
    else if (surface == SURFACE_CRYSTAL)
    {
        grain = detailNoise(vWorldPos * 2.2);
        grain = pow(grain, 0.6);                         // faceted: push toward the bright end
    }
    else if (surface == SURFACE_FLUID)
    {
        grain = 0.5;                                     // fluid detail comes from the normal, below
    }
    else
    {
        grain = detailNoise(vWorldPos * 3.4);            // general rock/soil/wood grain
    }

    // ---- Per-block character ------------------------------------------------------------------------
    //
    // Three cheap terms that between them do more for how the world reads than any amount of surface
    // detail, because they work on the thing a voxel world is actually made of: individual cubes.
    //
    // The first is an edge bevel. A greedy-meshed wall of one material is a single enormous quad, and
    // without this it draws as one flat sheet of colour -- the blocks are there in the geometry and
    // invisible to the eye. Darkening the last few per cent of each block's face puts every cube back,
    // and costs two fract calls.
    //
    // The second is a tint per block, hashed from the block's own coordinates, so no two neighbouring
    // stones are quite the same stone. Natural materials get more of it than worked ones, because a
    // brick wall that varied as much as a cliff face would look derelict.
    //
    // Both are scaled by the detail level, so the bottom quality tier still gets the bevel -- it is the
    // cheapest of the three and by far the most valuable -- while the tint follows the noise.
    float blockShape = clamp(detailLevel, 0.0, 1.0);

    if (blockShape > 0.0 && surface != SURFACE_FLUID)
    {
        // Position within this block's face. vTexCoord tiles once per block even across a merged quad,
        // which is exactly the coordinate this needs and the reason it is worth having.
        vec2 inFace = abs(fract(vTexCoord) - 0.5) * 2.0;
        float edge = max(inFace.x, inFace.y);

        // A wide, shallow falloff and no line at all.
        //
        // The first attempt drew a hard dark seam at every block border. It certainly made the cubes
        // legible, and it made a stone wall look like tiling -- a grid of outlined squares rather than a
        // face of rock. Minecraft has no edge darkening whatever; what separates its blocks is that the
        // texture restarts at each one, and that ambient occlusion darkens the corners where blocks
        // actually meet at an angle. Both of those are already here and doing the work.
        //
        // What is left is a gentle doming over the outer two thirds of each face, at a tenth the strength
        // of the old seam: enough that a wall is visibly made of blocks when you look for them, not enough
        // to draw a line anyone notices when they are not.
        float bevel = smoothstep(0.30, 1.0, edge) * 0.085;
        grain *= 1.0 - bevel * blockShape;

        // The block this face belongs to: step just inside the surface before flooring, or a face exactly
        // on an integer boundary lands in whichever block the rounding happens to pick and the tint
        // flickers as the camera moves.
        vec3 cell = floor(vWorldPos - N * 0.25);
        float tint = hash31(cell) - 0.5;

        float spread = (surface == SURFACE_CONSTRUCTED) ? 0.07 : 0.19;
        grain += tint * spread * blockShape;
    }

    // Crystals and anything that makes its own light get a slow shimmer. The precious materials are the
    // reward for going a long way down, and a block that glitters as you move past it is worth more to a
    // player than one that merely has a different colour.
    if (surface == SURFACE_CRYSTAL || surface == SURFACE_EMISSIVE)
    {
        // Driven by view angle rather than by a clock: the sparkle moves when the player does, which reads
        // as light catching a facet rather than as the block flashing on its own. The per-block phase
        // decides how bright each one's facet is, so a vein glitters unevenly the way a real one would.
        vec3 cell = floor(vWorldPos - N * 0.25);
        float facet = 0.4 + 0.6 * hash31(cell);
        float facing = dot(normalize(V + sunDir), N);

        grain += facet * pow(max(facing, 0.0), 24.0) * 0.9;
    }

    // Keep the mean at 1 so `variation` changes the texture without darkening the material.
    float detail = mix(1.0, 0.55 + grain * 0.9, variation);
    vec3 albedo = baseColor * detail;

    // ---- Normal perturbation ------------------------------------------------------------------------
    // Cubes have four flat normals and nothing for a specular highlight to catch. Perturbing the normal by
    // the gradient of the detail field gives the surface something to glint off, which is most of what
    // sells stone as stone rather than as a coloured plane.
    //
    // The gradient comes from screen-space derivatives of the value we already have, not from sampling the
    // noise field again either side of the point on each axis. That naive form needs six extra fetches per
    // pixel -- and since each fetch is itself eight hashes, it was costing ninety-odd hash evaluations per
    // pixel and dominating the entire frame. The surface-gradient formulation below reconstructs the same
    // slope from two hardware derivatives, which the GPU computes across the quad for nothing.
    if (variation > 0.0)
    {
        vec3 dpdx = dFdx(vWorldPos);
        vec3 dpdy = dFdy(vWorldPos);
        float dhdx = dFdx(grain);
        float dhdy = dFdy(grain);

        vec3 r1 = cross(dpdy, N);
        vec3 r2 = cross(N, dpdx);
        float det = dot(dpdx, r1);

        if (abs(det) > 1e-8)
        {
            vec3 slope = (r1 * dhdx + r2 * dhdy) / det;
            N = normalize(N - slope * variation * 0.25);
        }
    }

    // ---- Light ---------------------------------------------------------------------------------------
    // Sky and block light are combined by taking the stronger, never the sum: two half-lit sources do not
    // make daylight, and adding them would brighten a sealed room as the sun rose outside it.
    float sky = vSkyLight * daylight;
    float block = vBlockLight;

    // Torchlight is warm and slightly over-driven so it reads as fire rather than as white light.
    vec3 blockTint = vec3(1.0, 0.72, 0.42);

    float ao = vAmbientOcclusion;
    // AO is a contact term: it should darken ambient and bounce light, not direct sunlight, or every
    // sunlit corner looks like it is in shadow. Softened for the direct term, full strength for ambient.
    float aoDirect = mix(1.0, ao, 0.35);

    // Direct sun, gated by how much sky reaches this voxel -- which is what stops the sun lighting the
    // inside of a cave through solid rock without needing a shadow map.
    float ndl = max(dot(N, sunDir), 0.0);
    if (surface == SURFACE_FOLIAGE)
    {
        // Light scatters through a leaf, so the face away from the sun is lit too, and warmer.
        ndl = max(ndl, max(dot(-N, sunDir), 0.0) * 0.55);
    }

    vec3 sunLight = pc.sunColor.rgb * ndl * sky * aoDirect;

    // Ambient comes from the sky itself, so it changes colour with the time of day and a surface facing
    // up catches more of it than one facing down.
    float upness = N.y * 0.5 + 0.5;
    vec3 ambient = skyColor(upness, daylight) * (0.10 + 0.55 * sky) * ao;

    // A cave with no torch is not pitch black -- a floor of absolute zero reads as a hole in the world
    // rather than as darkness -- but it is close.
    ambient += vec3(0.012, 0.014, 0.020) * ao;

    vec3 blockLight = blockTint * block * block * 1.35 * ao;

    vec3 lit = albedo * (sunLight + ambient + blockLight);

    // ---- Per-material response -----------------------------------------------------------------------
    vec3 H = normalize(sunDir + V);
    float specBase = max(dot(N, H), 0.0);
    float fresnel = pow(1.0 - max(dot(N, V), 0.0), 5.0);

    if (surface == SURFACE_METALLIC)
    {
        // Tinted by the metal's own colour, which is what distinguishes gold from a white highlight on
        // yellow paint.
        vec3 spec = pc.sunColor.rgb * baseColor * pow(specBase, 42.0) * 1.6 * sky;
        lit += spec + baseColor * fresnel * 0.18 * (sky * 0.7 + 0.3);
    }
    else if (surface == SURFACE_CRYSTAL)
    {
        vec3 spec = pc.sunColor.rgb * pow(specBase, 96.0) * 1.2 * sky;
        // A faint internal glow so a vein is visible in an unlit cave: this is the thing that makes the
        // player's torch beam find something worth walking toward.
        lit += spec + baseColor * 0.22 + baseColor * fresnel * 0.4;
    }
    else if (surface == SURFACE_FLUID)
    {
        vec3 spec = pc.sunColor.rgb * pow(specBase, 128.0) * 2.2 * sky;
        // Fresnel toward the horizon: water is dark underfoot and mirror-bright in the distance.
        lit = mix(lit, skyColor(0.75, daylight), fresnel * 0.65);
        lit += spec;
    }
    else if (surface == SURFACE_EMISSIVE)
    {
        // Ignore the light grid entirely -- this surface makes its own. Driven well above 1 so the post
        // chain's bloom picks it up.
        lit = albedo * 2.6;
    }
    else if (surface == SURFACE_CONSTRUCTED)
    {
        lit += pc.sunColor.rgb * pow(specBase, 24.0) * 0.18 * sky;
    }

    // ---- Fog ------------------------------------------------------------------------------------------
    // Exponential-squared, which falls off gently near the player and hard in the distance -- so the far
    // edge of the loaded world dissolves into sky instead of ending at a visible wall of chunks.
    float fogStart = pc.sunColor.a;
    float fogDensity = pc.camPos.w;
    float fogDistance = max(distance - fogStart, 0.0) * fogDensity;
    float fog = 1.0 - exp(-fogDistance * fogDistance);

    // ---- Under a fluid -------------------------------------------------------------------------------
    //
    // Being submerged is not fog. Fog tints toward the sky and brightens toward the sun, which under
    // water produces a pale haze that reads as a spring morning rather than as being underneath
    // something. What it has to do instead is absorb: the fluid has its own colour, that colour gets
    // stronger with distance, and no amount of sunlight makes the far wall of a flooded cavern brighter.
    //
    // The three fluids are deliberately very different. Water is blue-green and you can see a fair way
    // through it, because a player who cannot see is a player who drowns for reasons they cannot learn
    // from. Lava is opaque and orange -- you are dead, and you should at least know why. Oil and tar are
    // near-black, which is the entire hazard.
    int submerged = int(pc.chunkOffset.w * 0.25);

    if (submerged > 0)
    {
        vec3 fluidColor;
        float absorb;

        if (submerged == 1)       { fluidColor = vec3(0.09, 0.26, 0.34); absorb = 0.055; }
        else if (submerged == 2)  { fluidColor = vec3(0.65, 0.18, 0.03); absorb = 0.900; }
        else                      { fluidColor = vec3(0.02, 0.02, 0.03); absorb = 0.400; }

        // Beer-Lambert rather than the squared ramp the distance fog uses. Absorption through a medium
        // really is exponential in depth, and the squared form has a near-field shelf that makes the
        // first few blocks suspiciously clear before everything beyond goes at once.
        float murk = 1.0 - exp(-distance * absorb);

        // The surface keeps a little of its own colour all the way out, so a wall of ore under water is
        // still recognisably ore rather than a uniform sheet of blue.
        vec3 drowned = mix(lit * mix(vec3(1.0), fluidColor * 2.2, 0.55), fluidColor, murk);

        outColor = vec4(drowned, 1.0);
        return;
    }

    // Fog toward the sky in the direction we are actually looking, so it matches the backdrop behind it.
    vec3 viewDir = -V;
    vec3 fogTint = skyColor(viewDir.y * 0.5 + 0.5, daylight);

    // Sunlight scatters forward through the air, so fog brightens toward the sun. Cheap, and it is what
    // makes a hazy morning read as a morning.
    float towardSun = max(dot(viewDir, sunDir), 0.0);
    fogTint += pc.sunColor.rgb * pow(towardSun, 8.0) * 0.35 * daylight;

    vec3 finalColor = mix(lit, fogTint, clamp(fog, 0.0, 1.0));

    outColor = vec4(finalColor, 1.0);
}
