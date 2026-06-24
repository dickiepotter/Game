#version 450

// Writes the lit colour the vertex shader computed, interpolated across the triangle.
layout(location = 0) in vec3 fragColor;
layout(location = 0) out vec4 outColor;

void main()
{
    outColor = vec4(fragColor, 1.0);
}
