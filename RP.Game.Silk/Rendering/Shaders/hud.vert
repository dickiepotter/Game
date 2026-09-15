#version 450

// 2D overlay vertex: position is already in NDC, colour and opacity pass straight through. Shared by both
// overlay passes -- the filled triangles and the lines drawn on top of them.
layout(location = 0) in vec2 inPos;
layout(location = 1) in vec3 inColor;
layout(location = 2) in float inAlpha;

layout(location = 0) out vec3 vColor;
layout(location = 1) out float vAlpha;

void main()
{
    vColor = inColor;
    vAlpha = inAlpha;
    gl_Position = vec4(inPos, 0.0, 1.0);
}
