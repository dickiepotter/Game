#version 450

// Full-screen triangle for the post-process passes; emits a 0..1 UV for sampling the source image.
layout(location = 0) out vec2 vUV;

void main()
{
    vec2 p = vec2(float((gl_VertexIndex << 1) & 2), float(gl_VertexIndex & 2));
    vUV = p;
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}
