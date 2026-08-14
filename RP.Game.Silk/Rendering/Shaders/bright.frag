#version 450

// Bright-pass: keep only the HDR overshoot (the part of the image brighter than white), which is what should
// bloom — engine plumes, tracers, the rim glints. Everything else contributes nothing to the blur.
layout(set = 0, binding = 0) uniform sampler2D srcTex;

layout(location = 0) in vec2 vUV;
layout(location = 0) out vec4 outColor;

void main()
{
    vec3 c = texture(srcTex, vUV).rgb;
    vec3 overshoot = max(c - vec3(1.0), vec3(0.0));
    outColor = vec4(overshoot, 1.0);
}
