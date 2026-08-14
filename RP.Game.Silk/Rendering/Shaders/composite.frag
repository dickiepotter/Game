#version 450

// Final composite: add the blurred bloom back onto the HDR scene, tonemap (ACES) to [0,1] linear, and write
// to the swapchain (an _SRGB format, so the hardware does the sRGB encode). This is the only tonemap in the
// chain — the scene and bloom are kept in linear HDR until here so bright sources bloom before they clip.
layout(set = 0, binding = 0) uniform sampler2D sceneTex;
layout(set = 0, binding = 1) uniform sampler2D bloomTex;

layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 outColor;

vec3 acesTonemap(vec3 x)
{
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}

void main()
{
    vec3 scene = texture(sceneTex, vUV).rgb;
    vec3 bloom = texture(bloomTex, vUV).rgb;
    vec3 hdr = scene + bloom * 0.9;
    outColor = vec4(acesTonemap(hdr), 1.0);
}
