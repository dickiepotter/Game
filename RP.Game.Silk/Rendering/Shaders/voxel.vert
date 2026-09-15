#version 450

// Voxel chunk vertex shader.
//
// A chunk mesh is static geometry in its own local space (0..32 on each axis), so the only per-draw data
// is where that chunk sits relative to the render origin. That is the last 16 bytes of the push block, set
// per chunk; everything before it is set once per frame. Keeping chunk positions local rather than baking
// world coordinates into the vertices is what lets the floating origin move without re-uploading a single
// chunk -- it just changes one push constant.

layout(push_constant) uniform Push {
    mat4 viewProj;      //  0..63   camera world -> clip (Vulkan-corrected)
    vec4 camPos;        // 64..79   xyz = camera in render space, w = fog density
    vec4 sunDir;        // 80..95   xyz = unit direction toward the sun, w = daylight in [0,1]
    vec4 sunColor;      // 96..111  rgb = sun colour, a = fog start distance
    vec4 chunkOffset;   // 112..127 xyz = this chunk's origin relative to the render origin
} pc;

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inTexCoord;
layout(location = 3) in uint inMaterial;
layout(location = 4) in float inAmbientOcclusion;
layout(location = 5) in float inSkyLight;
layout(location = 6) in float inBlockLight;

layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec2 vTexCoord;
layout(location = 2) out vec3 vWorldPos;
layout(location = 3) flat out uint vMaterial;
layout(location = 4) out float vAmbientOcclusion;
layout(location = 5) out float vSkyLight;
layout(location = 6) out float vBlockLight;

void main()
{
    vec3 worldPos = inPosition + pc.chunkOffset.xyz;

    gl_Position = pc.viewProj * vec4(worldPos, 1.0);

    vNormal = inNormal;
    vTexCoord = inTexCoord;
    vWorldPos = worldPos;

    // `flat` because the material word is an integer identifier, not a quantity. Interpolating it across a
    // triangle would produce meaningless intermediate values -- and since greedy meshing guarantees every
    // vertex of a quad carries the same material, there is nothing to interpolate anyway.
    vMaterial = inMaterial;

    vAmbientOcclusion = inAmbientOcclusion;
    vSkyLight = inSkyLight;
    vBlockLight = inBlockLight;
}
