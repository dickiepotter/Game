#version 450

// Procedural deep-space backdrop: a faint nebula (fbm value-noise clouds) under layered starfields, lit per
// view ray so it parallaxes correctly as the camera turns. No textures — it's all generated from the ray
// direction reconstructed from the camera basis in the push constant. Output is linear; the _SRGB swapchain
// encodes it.
layout(push_constant) uniform Sky {
    vec4 right;        // xyz = camera right (render space), w = aspect ratio
    vec4 up;           // xyz = camera up,                  w = tan(fov/2)
    vec4 forward;      // xyz = camera forward,            w = daylight in [0,1]
    vec4 sunDir;       // xyz = unit direction toward the sun (matches the mesh key light)
    vec4 sunColor;     // rgb = the sun's light colour,    w = submerged fluid kind (0..3)
    vec4 planetCentre; // xyz = planet centre relative to the eye, w = radius (<= 0: no planet)
    vec4 planetSpin;   // xyz = unit rotation axis, w = current spin angle (radians)
    vec4 planetStyle;  // x = seed, y = ocean level (0..1), z = polar ice extent (0..1)
} sky;

layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 outColor;

float hash13(vec3 p)
{
    p = fract(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return fract((p.x + p.y) * p.z);
}

float vnoise(vec3 x)
{
    vec3 i = floor(x);
    vec3 f = fract(x);
    f = f * f * (3.0 - 2.0 * f);
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

float fbm(vec3 p)
{
    float a = 0.5;
    float s = 0.0;
    for (int i = 0; i < 5; i++)
    {
        s += a * vnoise(p);
        p *= 2.02;
        a *= 0.5;
    }
    return s;
}

// Rodrigues rotation of v about a unit axis.
vec3 rotateAxis(vec3 v, vec3 axis, float angle)
{
    float c = cos(angle);
    return v * c + cross(axis, v) * sin(angle) + axis * dot(axis, v) * (1.0 - c);
}

// Sparse, sharp stars: one candidate per grid cell, only the brightest few percent survive.
float starLayer(vec3 dir, float scale, float thresh)
{
    vec3 id = floor(dir * scale);
    float h = hash13(id);
    float b = max(h - thresh, 0.0) / (1.0 - thresh);
    return pow(b, 6.0);
}

void main()
{
    vec3 dir = normalize(
        sky.forward.xyz
        + vUV.x * sky.right.w * sky.up.w * sky.right.xyz
        + vUV.y * sky.up.w * sky.up.xyz);

    vec3 col = vec3(0.004, 0.006, 0.014); // deep space base

    // Nebula clouds, tinted between magenta and teal.
    float n = fbm(dir * 3.0 + 11.0);
    float n2 = fbm(dir * 6.0 - 4.0);
    float cloud = smoothstep(0.45, 0.95, n);
    vec3 neb = mix(vec3(0.12, 0.04, 0.20), vec3(0.02, 0.11, 0.17), n2);
    col += neb * cloud * 0.7;

    // Star layers of increasing density / decreasing size.
    float s = 0.0;
    s += starLayer(dir, 350.0, 0.992);
    s += starLayer(dir, 750.0, 0.995);
    s += starLayer(dir, 1600.0, 0.997);
    col += vec3(0.85, 0.92, 1.0) * s * 3.0;

    // The sun: a blinding HDR disc exactly along the light direction the meshes are lit from, with a
    // tight inner corona and a wide soft halo. The bloom pass streaks the disc; the halo carries the
    // glare the rest of the way. Nebula haze also brightens toward the sun (forward scattering).
    vec3 toSun = normalize(sky.sunDir.xyz);
    float sd = dot(dir, toSun);
    float disc = smoothstep(0.99988, 0.99996, sd);            // ~0.5 deg core
    float corona = pow(max(sd, 0.0), 2200.0);                 // hot rim hugging the disc
    float halo = pow(max(sd, 0.0), 40.0);                     // broad glare
    vec3 sunGlow = sky.sunColor.rgb * (disc * 60.0 + corona * 6.0 + halo * 0.5);
    col += neb * cloud * halo * 0.8;                          // lit haze near the star

    // ------------------------------------------------------------------------------------------
    // The backdrop planet: a per-pixel ray-traced sphere impostor — how space games actually draw
    // distant worlds. The silhouette is analytic (never faceted at any zoom), the terminator is
    // shaded per pixel, and the atmosphere is the classic fresnel rim approximation of scattering
    // on the disc plus a thin exponential halo just off the limb. Opaque where hit, so the planet
    // correctly stands in front of nebula, stars and sun.
    // ------------------------------------------------------------------------------------------
    float R = sky.planetCentre.w;
    if (R > 0.0)
    {
        vec3 pc = sky.planetCentre.xyz;
        float b = dot(pc, dir);              // distance along the ray to the closest approach
        if (b > 0.0)                          // in front of the eye
        {
            float h2 = dot(pc, pc) - b * b;   // squared miss distance at closest approach
            float R2 = R * R;
            vec3 atmCol = vec3(0.30, 0.55, 1.0);
            vec3 axis = normalize(sky.planetSpin.xyz);

            if (h2 < R2)
            {
                // --- On the disc: shade the near intersection point. ---
                float t = b - sqrt(R2 - h2);
                vec3 N = normalize(dir * t - pc);

                // Sample the surface in the planet's rotating frame (clouds drift a little faster).
                float seed = sky.planetStyle.x;
                vec3 s = rotateAxis(N, axis, -sky.planetSpin.w);
                vec3 cs = rotateAxis(N, axis, -sky.planetSpin.w * 1.35);

                // Continents from low-frequency fbm; terrain character from a higher octave.
                float cont = fbm(s * 3.1 + seed);
                float detail = fbm(s * 9.7 + seed * 2.0);
                float land = smoothstep(sky.planetStyle.y - 0.04, sky.planetStyle.y + 0.04, cont);

                vec3 ocean = mix(vec3(0.012, 0.05, 0.12), vec3(0.03, 0.15, 0.22),
                                 smoothstep(0.25, sky.planetStyle.y, cont));
                vec3 lowland = mix(vec3(0.06, 0.11, 0.05), vec3(0.24, 0.20, 0.11), detail);
                vec3 highland = vec3(0.36, 0.31, 0.23);
                vec3 ground = mix(lowland, highland, smoothstep(0.72, 0.95, cont + detail * 0.2));
                vec3 albedo = mix(ocean, ground, land);

                // Polar caps by axial latitude, edges roughened by the detail octave.
                float lat = abs(dot(s, axis));
                float ice = smoothstep(1.0 - sky.planetStyle.z - 0.06, 1.0 - sky.planetStyle.z + 0.04,
                                       lat + detail * 0.05);
                albedo = mix(albedo, vec3(0.75, 0.79, 0.85), ice);

                // A drifting cloud deck above it all.
                float clouds = smoothstep(0.52, 0.74, fbm(cs * 5.3 + seed * 3.0 + 7.0));
                albedo = mix(albedo, vec3(0.90, 0.92, 0.95), clouds * 0.85);

                // Day side, soft terminator, and a warm twilight band along it.
                float ndl = dot(N, toSun);
                float day = smoothstep(-0.05, 0.25, ndl);
                vec3 lit = albedo * sky.sunColor.rgb * day;
                lit += vec3(0.9, 0.35, 0.10) * exp(-abs(ndl) * 12.0) * 0.14;

                // Sun glint off open water (suppressed by land, cloud and night).
                float glint = pow(max(dot(reflect(-toSun, N), -dir), 0.0), 220.0)
                              * (1.0 - land) * (1.0 - clouds) * day;
                lit += sky.sunColor.rgb * glint * 1.6;

                // The night side is not dead: starlit floor plus warm city specks on clear land.
                float night = 1.0 - day;
                float cities = step(0.9985, hash13(floor(s * 340.0)))
                               * land * (1.0 - ice) * (1.0 - clouds);
                lit += vec3(1.0, 0.72, 0.35) * cities * night * 2.0;
                lit += albedo * 0.012;

                // In-disc atmosphere: the fresnel rim that approximates Rayleigh scattering.
                float fres = pow(1.0 - max(dot(N, -dir), 0.0), 3.0);
                lit += atmCol * fres * (0.30 + 0.70 * day);

                col = lit; // opaque
            }
            else
            {
                // --- Just off the limb: the thin scattering halo, brighter on the sunlit side. ---
                float shell = (sqrt(h2) - R) / (R * 0.045);
                if (shell < 1.0)
                {
                    vec3 limbN = normalize(dir * b - pc);
                    float litSide = clamp(dot(limbN, toSun) * 0.5 + 0.5, 0.0, 1.0);
                    col += atmCol * exp(-shell * 3.2) * (0.25 + 0.75 * litSide) * 0.8;
                }
            }
        }
    }

    // ------------------------------------------------------------------------------------------
    // Atmosphere.
    //
    // Everything above draws the view from orbit: nebula, stars, a planet hanging in the distance.
    // Stood on the surface of a world with air on it, you see almost none of that, and the sky the
    // voxel pass fades its distant hills into has to be the same sky drawn here -- otherwise the
    // horizon is a seam where a blue haze meets a starfield, which is precisely what it was.
    //
    // So the space backdrop is what remains when the daylight goes, and the atmosphere is laid over it
    // in proportion. The stars do not switch off at dawn; they wash out, which is what they do.
    // ------------------------------------------------------------------------------------------
    float daylight = sky.forward.w;

    if (daylight > 0.001)
    {
        float upness = dir.y * 0.5 + 0.5;

        vec3 dayZenith   = vec3(0.16, 0.34, 0.72);
        vec3 dayHorizon  = vec3(0.62, 0.74, 0.92);
        vec3 duskHorizon = vec3(0.85, 0.42, 0.22);
        vec3 nightZenith = vec3(0.012, 0.018, 0.045);
        vec3 nightHorizon = vec3(0.05, 0.06, 0.11);

        // Dusk peaks when the sun is near the horizon, which is where daylight is midway through its
        // range. The same curve the voxel pass uses, so the two agree through sunrise and sunset as
        // well as at noon.
        float dusk = 1.0 - abs(daylight * 2.0 - 1.0);
        dusk = pow(clamp(dusk, 0.0, 1.0), 2.0);

        vec3 horizon = mix(nightHorizon, mix(dayHorizon, duskHorizon, dusk * 0.8), daylight);
        vec3 zenith  = mix(nightZenith, dayZenith, daylight);
        vec3 atmosphere = mix(horizon, zenith, clamp(upness, 0.0, 1.0));

        // Air scatters sunlight forward, so the sky brightens toward the sun rather than being a flat
        // gradient. Cheap, and it is most of what stops a procedural sky looking painted.
        atmosphere += sky.sunColor.rgb * pow(max(sd, 0.0), 6.0) * 0.20 * daylight;

        col = mix(col, atmosphere, daylight);
    }

    // The sun itself is added after the blend, so the disc survives into full daylight instead of being
    // mixed away into the very sky it is lighting.
    col += sunGlow;

    // Under a fluid, the sky is the furthest thing there is, so almost none of it survives the water
    // between here and it. Almost, not none: a little light does come down through the surface, and
    // losing it entirely turns a lake bed into a cave.
    int submerged = int(sky.sunColor.w + 0.5);

    if (submerged > 0)
    {
        vec3 fluidColor = submerged == 1 ? vec3(0.09, 0.26, 0.34)
                        : (submerged == 2 ? vec3(0.65, 0.18, 0.03) : vec3(0.02, 0.02, 0.03));

        float remaining = submerged == 1 ? 0.22 : 0.04;
        col = mix(fluidColor, col, remaining);
    }

    outColor = vec4(col, 1.0);
}
