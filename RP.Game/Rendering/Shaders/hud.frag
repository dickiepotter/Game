#version 450

// HUD lines are drawn at a slight over-brightness so they read crisply over the bloomed scene.
layout(location = 0) in vec3 vColor;
layout(location = 0) out vec4 outColor;

void main()
{
    outColor = vec4(vColor, 1.0);
}
