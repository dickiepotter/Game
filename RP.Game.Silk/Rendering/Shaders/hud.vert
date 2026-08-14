#version 450

// 2D HUD line vertex: position is already in NDC, colour passed straight through.
layout(location = 0) in vec2 inPos;
layout(location = 1) in vec3 inColor;

layout(location = 0) out vec3 vColor;

void main()
{
    vColor = inColor;
    gl_Position = vec4(inPos, 0.0, 1.0);
}
