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
layout(location = 6) in vec4 inRotation; // unit quaternion (x,y,z,w)

layout(location = 0) out vec3 vColor;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec3 vWorldPos;

// Rotate a vector by a unit quaternion: v + 2*cross(q.xyz, cross(q.xyz, v) + q.w*v).
vec3 qrot(vec4 q, vec3 v)
{
    return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v);
}

void main()
{
    // Each instance carries its own orientation now: rotate the hull into its heading, then scale + offset.
    vec3 local = qrot(inRotation, inPosition);
    vec3 worldPos = local * inScale + inOffset;
    gl_Position = pc.viewProj * vec4(worldPos, 1.0);

    vColor = inColor * inInstanceColor;
    vNormal = normalize(qrot(inRotation, inNormal));
    vWorldPos = worldPos;
}
