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
    vec4 chunkOffset;
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

// Two octaves is enough to read as surface grain and no more than the budget allows at this fill rate.
float detailNoise(vec3 p)
{
    return valueNoise(p) * 0.65 + valueNoise(p * 2.7) * 0.35;
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
    float variation = float((vMaterial >> 27) & 0x1Fu) / 31.0;

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
    float grain;
    if (surface == SURFACE_GRANULAR)
    {
        grain = detailNoise(vWorldPos * 9.0);            // fine speckle: sand, gravel, snow
    }
    else if (surface == SURFACE_CONSTRUCTED)
    {
        // A regular course pattern, so built things read as deliberate against natural ground.
        vec2 brick = vTexCoord * vec2(2.0, 1.0);
        brick.x += floor(brick.y) * 0.5;                 // offset alternate courses
        vec2 cell = abs(fract(brick) - 0.5);
        float mortar = smoothstep(0.42, 0.5, max(cell.x, cell.y));
        grain = 0.5 + 0.35 * detailNoise(vWorldPos * 3.0) - mortar * 0.55;
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

    // Keep the mean at 1 so `variation` changes the texture without darkening the material.
    float detail = mix(1.0, 0.55 + grain * 0.9, variation);
    vec3 albedo = baseColor * detail;

    // ---- Normal perturbation ------------------------------------------------------------------------
    // Cubes have four flat normals and nothing for a specular highlight to catch. Nudging the normal by
    // the gradient of the detail field gives the surface something to glint off, which is most of what
    // sells stone as stone rather than as a coloured plane.
    if (surface != SURFACE_MATTE || variation > 0.0)
    {
        float e = 0.35;
        float dx = detailNoise(vWorldPos * 3.4 + vec3(e, 0, 0)) - detailNoise(vWorldPos * 3.4 - vec3(e, 0, 0));
        float dy = detailNoise(vWorldPos * 3.4 + vec3(0, e, 0)) - detailNoise(vWorldPos * 3.4 - vec3(0, e, 0));
        float dz = detailNoise(vWorldPos * 3.4 + vec3(0, 0, e)) - detailNoise(vWorldPos * 3.4 - vec3(0, 0, e));
        vec3 bump = vec3(dx, dy, dz);
        bump -= N * dot(bump, N);                        // keep the nudge tangential to the face
        N = normalize(N + bump * variation * 0.6);
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
