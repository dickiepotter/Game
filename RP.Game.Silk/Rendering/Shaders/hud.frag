#version 450

// Shared by both overlay passes. What differs between them is the blend state, not the shading: the filled
// pass blends normally so a panel darkens the scene behind it, while the line pass blends additively so a
// reticle or a bracket glows over it.
layout(location = 0) in vec3 vColor;
layout(location = 1) in float vAlpha;

layout(location = 0) out vec4 outColor;

void main()
{
    outColor = vec4(vColor, vAlpha);
}
