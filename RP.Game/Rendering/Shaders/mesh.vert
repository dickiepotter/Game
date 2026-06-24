#version 450

// Instanced, lit mesh vertex shader. Per-vertex inputs (binding 0) describe the cube once; per-instance
// inputs (binding 1) place and tint each of the thousands of copies. The push constant carries the
// camera's view-projection and a shared spin applied to every instance.
layout(push_constant) uniform Push {
    mat4 viewProj; // camera world -> clip (Vulkan-corrected)
    mat4 spin;     // shared rotation applied to every instance
} pc;

// Per-vertex (binding 0)
layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec3 inColor;

// Per-instance (binding 1)
layout(location = 3) in vec3 inOffset;
layout(location = 4) in vec3 inInstanceColor;

layout(location = 0) out vec3 fragColor;

void main()
{
    // Spin the cube about its own centre, then translate to the instance's grid slot.
    vec3 spun = (pc.spin * vec4(inPosition, 1.0)).xyz;
    vec3 worldPos = spun + inOffset;
    gl_Position = pc.viewProj * vec4(worldPos, 1.0);

    vec3 worldNormal = normalize(mat3(pc.spin) * inNormal);
    vec3 lightDir = normalize(vec3(0.4, 1.0, 0.6));
    float diffuse = max(dot(worldNormal, lightDir), 0.0);
    float ambient = 0.25;

    fragColor = inColor * inInstanceColor * (ambient + diffuse);
}
