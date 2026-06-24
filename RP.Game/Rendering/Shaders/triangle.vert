#version 450

// Now reads its vertices from a real vertex buffer (Phase 1, step 2) instead of baking them in. The
// attribute locations must match the Vulkan VertexInputAttributeDescriptions and the CPU `Vertex` struct
// field order (position, then colour).
layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inColor;

layout(location = 0) out vec3 fragColor;

void main()
{
    gl_Position = vec4(inPosition, 1.0);
    fragColor = inColor;
}
