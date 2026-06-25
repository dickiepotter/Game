#version 450

// A full-screen triangle generated from gl_VertexIndex alone (no vertex buffer). Three verts at
// (-1,-1), (3,-1), (-1,3) cover the whole screen; vUV carries clip-space xy in [-1,1] at the edges,
// which the fragment shader turns into a view ray for the procedural starfield.
layout(location = 0) out vec2 vUV;

void main()
{
    vec2 p = vec2(float((gl_VertexIndex << 1) & 2), float(gl_VertexIndex & 2));
    vUV = p * 2.0 - 1.0;
    gl_Position = vec4(vUV, 1.0, 1.0); // z=1 (far) so the scene's depth-tested hulls draw in front
}
