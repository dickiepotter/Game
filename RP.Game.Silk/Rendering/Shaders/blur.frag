#version 450

// Separable Gaussian blur: run once horizontally, once vertically (the push-constant 'dir' is the per-tap
// UV step along one axis). Two ping-pong iterations give a wide, soft bloom cheaply.
layout(set = 0, binding = 0) uniform sampler2D srcTex;

layout(push_constant) uniform Push {
    vec2 dir; // UV-space step to the next tap (1/width,0) or (0,1/height)
} pc;

layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 outColor;

void main()
{
    float w[5] = float[](0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216);
    vec3 result = texture(srcTex, vUV).rgb * w[0];
    for (int i = 1; i < 5; i++)
    {
        result += texture(srcTex, vUV + pc.dir * float(i)).rgb * w[i];
        result += texture(srcTex, vUV - pc.dir * float(i)).rgb * w[i];
    }
    outColor = vec4(result, 1.0);
}
