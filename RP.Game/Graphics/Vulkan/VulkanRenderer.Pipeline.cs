namespace RP.Game.Graphics.Vulkan
{
    using System;
    using System.IO;
    using System.Reflection;
    using RP.Game.Rendering;
    using RP.Math;
    using Silk.NET.Core.Native;
    using Silk.NET.Vulkan;
    using Buffer = Silk.NET.Vulkan.Buffer;

    /// <summary>
    /// Phase 1: the graphics pipeline and the test triangle. Split into its own file because the device
    /// spine (instance/swapchain/sync) and the <i>drawing</i> on top of it are separate concerns; keeping
    /// them apart is how the renderer stays readable as it grows.
    /// </summary>
    public sealed unsafe partial class VulkanRenderer
    {
        // A pipeline bakes nearly all GPU draw state into one immutable object: which shaders run, how
        // vertices are assembled, rasterised, blended. Vulkan front-loads this so per-draw cost is tiny.
        private Pipeline _graphicsPipeline;
        private PipelineLayout _pipelineLayout;

        // The full-screen procedural starfield/nebula backdrop, drawn before the scene (no depth write).
        private Pipeline _skyPipeline;
        private PipelineLayout _skyPipelineLayout;

        /// <summary>
        /// Builds the triangle pipeline. Two choices keep it resize-proof and render-pass-free:
        /// <list type="bullet">
        ///   <item><description><b>Dynamic viewport/scissor</b> — the viewport size is set at draw time,
        ///   not baked in, so a window resize needs no pipeline rebuild (only the swapchain rebuilds).</description></item>
        ///   <item><description><b>Dynamic rendering</b> — instead of a render-pass object, the pipeline is
        ///   told its colour attachment <i>format</i> via <see cref="PipelineRenderingCreateInfo"/>, matching
        ///   how <c>CmdBeginRendering</c> draws (build brief S4.2).</description></item>
        /// </list>
        /// </summary>
        private void CreateGraphicsPipeline()
        {
            ShaderModule vertModule = CreateShaderModule("mesh.vert.spv");
            ShaderModule fragModule = CreateShaderModule("mesh.frag.spv");

            // Entry-point name shared by both stages.
            byte* entryPoint = (byte*)SilkMarshal.StringToPtr("main");

            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vertModule,
                PName = entryPoint,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragModule,
                PName = entryPoint,
            };

            // Vertex input: one buffer binding holding tightly-packed Vertex structs, with two attributes
            // (position at offset 0, colour after it). These must mirror the `Vertex` struct and the
            // shader's `layout(location = …) in` declarations exactly.
            uint vec3Size = (uint)sizeof(Vector3);

            // Two bindings: binding 0 steps per vertex (the cube), binding 1 steps per instance (grid).
            var bindingDescriptions = stackalloc VertexInputBindingDescription[2];
            bindingDescriptions[0] = new VertexInputBindingDescription
            {
                Binding = 0, Stride = (uint)sizeof(Vertex), InputRate = VertexInputRate.Vertex,
            };
            bindingDescriptions[1] = new VertexInputBindingDescription
            {
                Binding = 1, Stride = (uint)sizeof(InstanceData), InputRate = VertexInputRate.Instance,
            };

            // Per-vertex: position @0, normal @12, colour @24. Per-instance: offset @0, colour @12, scale @24,
            // rotation quaternion @28.
            var attributeDescriptions = stackalloc VertexInputAttributeDescription[7];
            attributeDescriptions[0] = new VertexInputAttributeDescription
            {
                Binding = 0, Location = 0, Format = Format.R32G32B32Sfloat, Offset = 0,
            };
            attributeDescriptions[1] = new VertexInputAttributeDescription
            {
                Binding = 0, Location = 1, Format = Format.R32G32B32Sfloat, Offset = vec3Size,
            };
            attributeDescriptions[2] = new VertexInputAttributeDescription
            {
                Binding = 0, Location = 2, Format = Format.R32G32B32Sfloat, Offset = 2 * vec3Size,
            };
            attributeDescriptions[3] = new VertexInputAttributeDescription
            {
                Binding = 1, Location = 3, Format = Format.R32G32B32Sfloat, Offset = 0,
            };
            attributeDescriptions[4] = new VertexInputAttributeDescription
            {
                Binding = 1, Location = 4, Format = Format.R32G32B32Sfloat, Offset = vec3Size,
            };
            attributeDescriptions[5] = new VertexInputAttributeDescription
            {
                Binding = 1, Location = 5, Format = Format.R32Sfloat, Offset = 2 * vec3Size,
            };
            attributeDescriptions[6] = new VertexInputAttributeDescription
            {
                // Rotation quaternion, just past scale: offset = 2*vec3 + one float.
                Binding = 1, Location = 6, Format = Format.R32G32B32A32Sfloat, Offset = 2 * vec3Size + sizeof(float),
            };
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 2,
                PVertexBindingDescriptions = bindingDescriptions,
                VertexAttributeDescriptionCount = 7,
                PVertexAttributeDescriptions = attributeDescriptions,
            };

            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
                PrimitiveRestartEnable = false,
            };

            // Viewport/scissor are dynamic; one of each is still declared so counts line up.
            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1,
            };

            var rasterizer = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                DepthClampEnable = false,
                RasterizerDiscardEnable = false,
                PolygonMode = PolygonMode.Fill,
                LineWidth = 1.0f,
                CullMode = CullModeFlags.None, // Phase 1: don't cull, so winding can't hide the triangle.
                FrontFace = FrontFace.CounterClockwise,
                DepthBiasEnable = false,
            };

            var multisampling = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                SampleShadingEnable = false,
                RasterizationSamples = _msaaSamples, // scene renders into the multisampled HDR target
            };

            // Opaque: write all channels, no blending.
            var colorBlendAttachment = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = false,
            };

            var colorBlending = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                LogicOpEnable = false,
                AttachmentCount = 1,
                PAttachments = &colorBlendAttachment,
            };

            var dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamicState = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamicStates,
            };

            // One push-constant range: 80 bytes (a mat4 viewProj + a vec4 camera position), within the common
            // 128-byte limit, visible to both stages so the fragment shader can light per pixel.
            var pushRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                Offset = 0,
                Size = (16 + 4) * sizeof(float),
            };
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 0,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushRange,
            };
            if (_vk.CreatePipelineLayout(_device, in layoutInfo, null, out _pipelineLayout) != Result.Success)
            {
                throw new VulkanException("vkCreatePipelineLayout failed", Result.ErrorUnknown);
            }

            // Dynamic rendering: the scene renders into the offscreen HDR target (not the swapchain), so the
            // post-process chain can bloom and tonemap it.
            Format colorFormat = HdrFormat;
            var renderingCreateInfo = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = 1,
                PColorAttachmentFormats = &colorFormat,
                DepthAttachmentFormat = _depthFormat,
            };

            // Depth test on, writing nearer fragments and rejecting farther ones (standard opaque draw).
            var depthStencil = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = true,
                DepthWriteEnable = true,
                DepthCompareOp = CompareOp.LessOrEqual,
                DepthBoundsTestEnable = false,
                StencilTestEnable = false,
            };

            var pipelineInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &renderingCreateInfo,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterizer,
                PMultisampleState = &multisampling,
                PDepthStencilState = &depthStencil,
                PColorBlendState = &colorBlending,
                PDynamicState = &dynamicState,
                Layout = _pipelineLayout,
                RenderPass = default, // none — we use dynamic rendering
                Subpass = 0,
            };

            Result result = _vk.CreateGraphicsPipelines(_device, default, 1, in pipelineInfo, null, out _graphicsPipeline);

            // The modules and the marshalled entry-point string are only needed during creation.
            _vk.DestroyShaderModule(_device, vertModule, null);
            _vk.DestroyShaderModule(_device, fragModule, null);
            SilkMarshal.Free((nint)entryPoint);

            if (result != Result.Success) throw new VulkanException("vkCreateGraphicsPipelines failed", result);
        }

        /// <summary>
        /// Builds the backdrop pipeline: a full-screen triangle (no vertex input) running the procedural
        /// starfield/nebula shaders, with depth test/write off so it sits behind everything. Its push constant
        /// carries the camera basis so the shader can cast a view ray per pixel.
        /// </summary>
        private void CreateSkyPipeline()
        {
            ShaderModule vertModule = CreateShaderModule("sky.vert.spv");
            ShaderModule fragModule = CreateShaderModule("sky.frag.spv");
            byte* entryPoint = (byte*)SilkMarshal.StringToPtr("main");

            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit, Module = vertModule, PName = entryPoint,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit, Module = fragModule, PName = entryPoint,
            };

            // No vertex input: the vertex shader synthesises the triangle from gl_VertexIndex.
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
            };
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
            };
            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1,
            };
            var rasterizer = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise, LineWidth = 1.0f,
            };
            var multisampling = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = _msaaSamples, // matches the multisampled scene target
            };
            var colorBlendAttachment = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = false,
            };
            var colorBlending = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1, PAttachments = &colorBlendAttachment,
            };
            var dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamicState = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2, PDynamicStates = dynamicStates,
            };

            // Push constant: three vec4 (camera right/up/forward + aspect & tan-half-fov), fragment stage.
            var pushRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.FragmentBit, Offset = 0, Size = 3 * 4 * sizeof(float),
            };
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                PushConstantRangeCount = 1, PPushConstantRanges = &pushRange,
            };
            if (_vk.CreatePipelineLayout(_device, in layoutInfo, null, out _skyPipelineLayout) != Result.Success)
            {
                throw new VulkanException("vkCreatePipelineLayout (sky) failed", Result.ErrorUnknown);
            }

            Format colorFormat = HdrFormat; // the sky renders into the HDR scene target too
            var renderingCreateInfo = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = 1, PColorAttachmentFormats = &colorFormat,
                DepthAttachmentFormat = _depthFormat,
            };

            // Depth test/write OFF — the backdrop never occludes the scene and is never occluded by it; the
            // scene draws afterward with depth and paints over it.
            var depthStencil = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = false, DepthWriteEnable = false,
                DepthCompareOp = CompareOp.Always, DepthBoundsTestEnable = false, StencilTestEnable = false,
            };

            var pipelineInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &renderingCreateInfo,
                StageCount = 2, PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterizer,
                PMultisampleState = &multisampling,
                PDepthStencilState = &depthStencil,
                PColorBlendState = &colorBlending,
                PDynamicState = &dynamicState,
                Layout = _skyPipelineLayout,
            };

            Result result = _vk.CreateGraphicsPipelines(_device, default, 1, in pipelineInfo, null, out _skyPipeline);
            _vk.DestroyShaderModule(_device, vertModule, null);
            _vk.DestroyShaderModule(_device, fragModule, null);
            SilkMarshal.Free((nint)entryPoint);
            if (result != Result.Success) throw new VulkanException("vkCreateGraphicsPipelines (sky) failed", result);
        }

        /// <summary>Draws the full-screen backdrop, feeding the camera basis so the shader's view ray matches
        /// where the camera looks (so stars/nebula parallax as you turn).</summary>
        private void RecordSky(CommandBuffer cb)
        {
            var viewport = new Viewport
            {
                X = 0, Y = 0, Width = _swapchainExtent.Width, Height = _swapchainExtent.Height,
                MinDepth = 0, MaxDepth = 1,
            };
            _vk.CmdSetViewport(cb, 0, 1, in viewport);
            var scissor = new Rect2D(new Offset2D(0, 0), _swapchainExtent);
            _vk.CmdSetScissor(cb, 0, 1, in scissor);

            _vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _skyPipeline);

            // Camera basis from the look-at vectors (narrowed to float for the shader).
            var camPos = (Vector3)Camera.Position;
            var camTarget = (Vector3)Camera.Target;
            var camUp = (Vector3)Camera.Up;
            Vector3 forward = (camTarget - camPos).Normalize();
            Vector3 right = Vector3.Cross(forward, camUp).Normalize();
            Vector3 up = Vector3.Cross(right, forward).Normalize();
            float aspect = _swapchainExtent.Height == 0 ? 1.0f : (float)_swapchainExtent.Width / _swapchainExtent.Height;
            float tanHalfFov = (float)System.Math.Tan(Camera.FieldOfView.Rad * 0.5);

            float* push = stackalloc float[12];
            push[0] = right.X; push[1] = right.Y; push[2] = right.Z; push[3] = aspect;
            push[4] = up.X; push[5] = up.Y; push[6] = up.Z; push[7] = tanHalfFov;
            push[8] = forward.X; push[9] = forward.Y; push[10] = forward.Z; push[11] = 0f;
            _vk.CmdPushConstants(cb, _skyPipelineLayout, ShaderStageFlags.FragmentBit, 0, 12 * sizeof(float), push);

            _vk.CmdDraw(cb, 3, 1, 0, 0); // one full-screen triangle
        }

        /// <summary>
        /// Wraps a blob of SPIR-V bytecode in a <see cref="ShaderModule"/>. The bytecode is loaded from the
        /// assembly's embedded resources (the build's glslc step put it there), so there are no loose files
        /// to ship. SPIR-V is a stream of 32-bit words, hence the size/alignment care below.
        /// </summary>
        private ShaderModule CreateShaderModule(string resourceName)
        {
            byte[] code = ReadEmbeddedShader(resourceName);

            fixed (byte* codePtr = code)
            {
                var createInfo = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)code.Length,
                    PCode = (uint*)codePtr,
                };

                if (_vk.CreateShaderModule(_device, in createInfo, null, out ShaderModule module) != Result.Success)
                {
                    throw new VulkanException($"vkCreateShaderModule failed for '{resourceName}'", Result.ErrorUnknown);
                }

                return module;
            }
        }

        private static byte[] ReadEmbeddedShader(string logicalName)
        {
            Assembly assembly = typeof(VulkanRenderer).Assembly;
            using Stream? stream = assembly.GetManifestResourceStream(logicalName);
            if (stream is null)
            {
                throw new FileNotFoundException(
                    $"Embedded SPIR-V '{logicalName}' not found. Did the CompileShaders build step run?");
            }

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        /// <summary>
        /// Records the cube draw inside an active dynamic-rendering scope: set the (dynamic) viewport and
        /// scissor, push the MVP + model matrices, bind the vertex and index buffers, and draw indexed.
        /// </summary>
        private void RecordMesh(CommandBuffer cb)
        {
            var viewport = new Viewport
            {
                X = 0,
                Y = 0,
                Width = _swapchainExtent.Width,
                Height = _swapchainExtent.Height,
                MinDepth = 0,
                MaxDepth = 1,
            };
            _vk.CmdSetViewport(cb, 0, 1, in viewport);

            var scissor = new Rect2D(new Offset2D(0, 0), _swapchainExtent);
            _vk.CmdSetScissor(cb, 0, 1, in scissor);

            _vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _graphicsPipeline);

            // Push the camera's view-projection (column-major) and position (for per-pixel rim/spec lighting).
            Matrix viewProj = Camera.ViewProjection;

            float* push = stackalloc float[20];
            var span = new Span<float>(push, 20);
            Camera.ToColumnMajorFloats(viewProj, span.Slice(0, 16));
            span[16] = (float)Camera.Position.X;
            span[17] = (float)Camera.Position.Y;
            span[18] = (float)Camera.Position.Z;
            span[19] = 0f;
            _vk.CmdPushConstants(cb, _pipelineLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                0, 20 * sizeof(float), push);

            // Bind binding 0 (cube vertices) and binding 1 (this frame's culled per-instance data).
            var vertexBuffers = stackalloc Buffer[2] { _meshVertexBuffer, _dynamicInstanceBuffers[_currentFrame] };
            var offsets = stackalloc ulong[2] { 0, 0 };
            _vk.CmdBindVertexBuffers(cb, 0, 2, vertexBuffers, offsets);
            _vk.CmdBindIndexBuffer(cb, _meshIndexBuffer, 0, IndexType.Uint16);

            // One instanced draw renders every visible instance: indexCount × visibleInstanceCount.
            _vk.CmdDrawIndexed(cb, _meshIndexCount, _visibleInstanceCount, 0, 0, 0);

            // Capital ships: same pipeline + push constants, a second mesh and instance batch.
            if (_capitalVisible > 0)
            {
                var capBuffers = stackalloc Buffer[2] { _capitalVertexBuffer, _capitalInstanceBuffers[_currentFrame] };
                _vk.CmdBindVertexBuffers(cb, 0, 2, capBuffers, offsets);
                _vk.CmdBindIndexBuffer(cb, _capitalIndexBuffer, 0, IndexType.Uint16);
                _vk.CmdDrawIndexed(cb, _capitalIndexCount, _capitalVisible, 0, 0, 0);
            }
        }

        private void DestroyGraphicsPipeline()
        {
            if (_skyPipeline.Handle != 0) _vk.DestroyPipeline(_device, _skyPipeline, null);
            if (_skyPipelineLayout.Handle != 0) _vk.DestroyPipelineLayout(_device, _skyPipelineLayout, null);
            if (_graphicsPipeline.Handle != 0) _vk.DestroyPipeline(_device, _graphicsPipeline, null);
            if (_pipelineLayout.Handle != 0) _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
        }
    }
}
