#version 450

// Per-pixel lit fragment shader for the hulls. The look is built for space: a warm key light, a cool fill,
// a strong cool RIM light that carves each silhouette out of the black, a self-emissive term so engines and
// tracers glow, and an ACES-ish tonemap + gamma so bright sources roll off toward white instead of clipping.
layout(push_constant) uniform Push {
    mat4 viewProj;
    vec4 camPos; // xyz = camera position in render space
} pc;

layout(location = 0) in vec3 vColor;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec3 vWorldPos;

layout(location = 0) out vec4 outColor;

void main()
{
    vec3 N = normalize(vNormal);
    vec3 V = normalize(pc.camPos.xyz - vWorldPos);

    vec3 keyDir = normalize(vec3(0.5, 0.85, 0.45));
    vec3 fillDir = normalize(vec3(-0.5, -0.25, -0.6));

    float key = max(dot(N, keyDir), 0.0);
    float fill = max(dot(N, fillDir), 0.0);
    float ambient = 0.08;

    vec3 keyColor = vec3(1.0, 0.96, 0.88);   // warm sun
    vec3 fillColor = vec3(0.25, 0.40, 0.65); // cool bounce

    vec3 lit = vColor * (ambient + key * keyColor + fill * 0.25 * fillColor);

    // Rim/fresnel: glowy edge where the surface turns away from the camera — sells the silhouette in the dark.
    float rim = pow(1.0 - max(dot(N, V), 0.0), 3.0);
    vec3 rimColor = vec3(0.35, 0.65, 1.0) * rim * 1.3;

    // Specular glint from the key light.
    vec3 H = normalize(keyDir + V);
    float spec = pow(max(dot(N, H), 0.0), 48.0);
    vec3 specColor = keyColor * spec * 0.6;

    // Emissive: only the brightest source colours (engine tail, tracers) glow on their own.
    float lum = max(max(vColor.r, vColor.g), vColor.b);
    float emissive = smoothstep(0.8, 1.1, lum);
    vec3 emit = vColor * emissive * 2.2;

    vec3 hdr = lit + rimColor + specColor + emit;

    // Output linear HDR (the target is a float format); the post chain blooms then tonemaps it.
    outColor = vec4(hdr, 1.0);
}
