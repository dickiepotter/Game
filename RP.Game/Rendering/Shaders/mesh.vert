#version 450

// Instanced, lit mesh vertex shader. Per-vertex inputs (binding 0) describe the hull once; per-instance
// inputs (binding 1) place, scale and tint each copy. The push constant carries the camera's view-projection,
// a shared spin, and the camera position (for per-pixel lighting in the fragment stage).
layout(push_constant) uniform Push {
    mat4 viewProj;  // camera world -> clip (Vulkan-corrected)
    vec4 camPos;    // xyz = camera position in render space
} pc;

// Per-vertex (binding 0)
layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec3 inColor;

// Per-instance (binding 1)
layout(location = 3) in vec3 inOffset;
layout(location = 4) in vec3 inInstanceColor;
layout(location = 5) in float inScale;

layout(location = 0) out vec3 vColor;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec3 vWorldPos;

void main()
{
    // Instances carry no rotation, so object space and world space share an orientation; only scale + offset.
    vec3 worldPos = inPosition * inScale + inOffset;
    gl_Position = pc.viewProj * vec4(worldPos, 1.0);

    vColor = inColor * inInstanceColor;
    vNormal = normalize(inNormal);
    vWorldPos = worldPos;
}
