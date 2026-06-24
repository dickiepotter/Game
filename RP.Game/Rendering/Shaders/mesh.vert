#version 450

// A lit mesh vertex shader. The model-view-projection and model matrices arrive as a 128-byte push
// constant (two mat4) — the cheapest way to feed a handful of per-draw matrices without descriptor sets.
// Lighting is a simple Lambert term: brightness = ambient + max(0, normal · lightDir).
layout(push_constant) uniform Push {
    mat4 mvp;    // world -> clip (projection * view * model), Vulkan-corrected
    mat4 model;  // world transform, used to orient the normal
} pc;

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec3 inColor;

layout(location = 0) out vec3 fragColor;

void main()
{
    gl_Position = pc.mvp * vec4(inPosition, 1.0);

    // Rotate the normal into world space. For a rigid (rotation-only) model the upper 3x3 is orthonormal,
    // so this is correct without an inverse-transpose; that refinement comes when non-uniform scale does.
    vec3 worldNormal = normalize(mat3(pc.model) * inNormal);

    vec3 lightDir = normalize(vec3(0.4, 1.0, 0.6));
    float diffuse = max(dot(worldNormal, lightDir), 0.0);
    float ambient = 0.25;

    fragColor = inColor * (ambient + diffuse);
}
