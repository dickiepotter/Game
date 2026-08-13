#version 450

// Procedural deep-space backdrop: a faint nebula (fbm value-noise clouds) under layered starfields, lit per
// view ray so it parallaxes correctly as the camera turns. No textures — it's all generated from the ray
// direction reconstructed from the camera basis in the push constant. Output is linear; the _SRGB swapchain
// encodes it.
layout(push_constant) uniform Sky {
    vec4 right;    // xyz = camera right (render space), w = aspect ratio
    vec4 up;       // xyz = camera up,                  w = tan(fov/2)
    vec4 forward;  // xyz = camera forward
    vec4 sunDir;   // xyz = unit direction toward the sun (matches the mesh key light)
    vec4 sunColor; // rgb = the sun's light colour
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
    col += sky.sunColor.rgb * (disc * 60.0 + corona * 6.0 + halo * 0.5);
    col += neb * cloud * halo * 0.8;                          // lit haze near the star

    outColor = vec4(col, 1.0);
}
