#version 450

// Receives the colour the vertex shader emitted, already interpolated across the triangle by the
// rasteriser (this smooth blend is the classic proof the pipeline really ran), and writes it out.
layout(location = 0) in vec3 fragColor;
layout(location = 0) out vec4 outColor;

void main()
{
    outColor = vec4(fragColor, 1.0);
}
