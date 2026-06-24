namespace RP.Game.Graphics.Vulkan
{
    using System;
    using System.IO;
    using System.Reflection;
    using Silk.NET.Core.Native;
    using Silk.NET.Vulkan;

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
            ShaderModule vertModule = CreateShaderModule("triangle.vert.spv");
            ShaderModule fragModule = CreateShaderModule("triangle.frag.spv");

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

            // No vertex buffers yet — the triangle's vertices live in the shader (Phase 1 step 1).
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 0,
                VertexAttributeDescriptionCount = 0,
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
                RasterizationSamples = SampleCountFlags.Count1Bit,
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

            // Empty layout — no descriptor sets or push constants yet.
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 0,
                PushConstantRangeCount = 0,
            };
            if (_vk.CreatePipelineLayout(_device, in layoutInfo, null, out _pipelineLayout) != Result.Success)
            {
                throw new VulkanException("vkCreatePipelineLayout failed", Result.ErrorUnknown);
            }

            // Dynamic rendering: tell the pipeline the colour format it will render to (no render pass).
            Format colorFormat = _swapchainFormat;
            var renderingCreateInfo = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = 1,
                PColorAttachmentFormats = &colorFormat,
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
        /// Records the triangle draw inside an active dynamic-rendering scope: set the (dynamic) viewport
        /// and scissor to the current swapchain size, bind the pipeline, and draw 3 vertices.
        /// </summary>
        private void RecordTriangle(CommandBuffer cb)
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
            _vk.CmdDraw(cb, vertexCount: 3, instanceCount: 1, firstVertex: 0, firstInstance: 0);
        }

        private void DestroyGraphicsPipeline()
        {
            if (_graphicsPipeline.Handle != 0) _vk.DestroyPipeline(_device, _graphicsPipeline, null);
            if (_pipelineLayout.Handle != 0) _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
        }
    }
}
