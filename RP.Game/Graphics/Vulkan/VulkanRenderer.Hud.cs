namespace RP.Game.Graphics.Vulkan
{
    using System;
    using RP.Game.Rendering;
    using RP.Math;
    using Silk.NET.Core.Native;
    using Silk.NET.Vulkan;
    using Buffer = Silk.NET.Vulkan.Buffer;

    /// <summary>
    /// The 2D HUD overlay: a list of coloured line segments (boresight, prograde marker, target brackets,
    /// gauge bars) drawn over the final composited image each frame. The game builds the segments in NDC from
    /// the <c>HudModel</c> + camera projection and hands them over via <see cref="SetHudLines"/>; this owns the
    /// line pipeline and the per-frame vertex buffers. Kept deliberately geometric (no font yet) so it is a
    /// peripheral, diegetic layer rather than a wall of text.
    /// </summary>
    public sealed unsafe partial class VulkanRenderer
    {
        private const int MaxHudVertices = 8192;
        private Pipeline _hudPipeline;
        private PipelineLayout _hudPipelineLayout;
        private readonly Buffer[] _hudBuffers = new Buffer[MaxFramesInFlight];
        private readonly DeviceMemory[] _hudMemories = new DeviceMemory[MaxFramesInFlight];
        private readonly nint[] _hudMapped = new nint[MaxFramesInFlight];
        private readonly HudVertex[] _hudCpu = new HudVertex[MaxHudVertices];
        private int _hudVertexCount;

        private void CreateHudBuffers()
        {
            ulong capacity = (ulong)(MaxHudVertices * sizeof(HudVertex));
            for (int i = 0; i < MaxFramesInFlight; i++)
            {
                (_hudBuffers[i], _hudMemories[i]) = CreateBuffer(
                    capacity, BufferUsageFlags.VertexBufferBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
                void* mapped;
                _vk.MapMemory(_device, _hudMemories[i], 0, capacity, 0, &mapped);
                _hudMapped[i] = (nint)mapped;
            }
        }

        /// <summary>Sets the HUD line segments for the next frame (two vertices per segment, NDC positions).</summary>
        public void SetHudLines(ReadOnlySpan<HudVertex> vertices)
        {
            int count = Math.Min(vertices.Length, MaxHudVertices);
            for (int i = 0; i < count; i++) _hudCpu[i] = vertices[i];
            _hudVertexCount = count - (count % 2); // whole segments only
        }

        private void UploadHud(int frameIndex)
        {
            if (_hudVertexCount == 0) return;
            ulong bytes = (ulong)(_hudVertexCount * sizeof(HudVertex));
            ulong capacity = (ulong)(MaxHudVertices * sizeof(HudVertex));
            fixed (HudVertex* src = _hudCpu)
            {
                System.Buffer.MemoryCopy(src, (void*)_hudMapped[frameIndex], capacity, bytes);
            }
        }

        private void CreateHudPipeline()
        {
            ShaderModule vertModule = CreateShaderModule("hud.vert.spv");
            ShaderModule fragModule = CreateShaderModule("hud.frag.spv");
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

            var binding = new VertexInputBindingDescription
            {
                Binding = 0, Stride = (uint)sizeof(HudVertex), InputRate = VertexInputRate.Vertex,
            };
            var attributes = stackalloc VertexInputAttributeDescription[2];
            attributes[0] = new VertexInputAttributeDescription { Binding = 0, Location = 0, Format = Format.R32G32Sfloat, Offset = 0 };
            attributes[1] = new VertexInputAttributeDescription { Binding = 0, Location = 1, Format = Format.R32G32B32Sfloat, Offset = (uint)sizeof(Vector2) };
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1, PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 2, PVertexAttributeDescriptions = attributes,
            };
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.LineList,
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
                SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit,
            };
            // Additive-ish alpha blend so the lines sit over the scene cleanly.
            var colorBlendAttachment = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.One,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.One,
                AlphaBlendOp = BlendOp.Add,
            };
            var colorBlending = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &colorBlendAttachment,
            };
            var dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamicState = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamicStates,
            };
            var depthStencil = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = false, DepthWriteEnable = false, DepthCompareOp = CompareOp.Always,
            };
            var layoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo };
            if (_vk.CreatePipelineLayout(_device, in layoutInfo, null, out _hudPipelineLayout) != Result.Success)
            {
                throw new VulkanException("vkCreatePipelineLayout (hud) failed", Result.ErrorUnknown);
            }

            Format colorFormat = _swapchainFormat;
            var renderingCreateInfo = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = 1, PColorAttachmentFormats = &colorFormat,
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
                Layout = _hudPipelineLayout,
            };
            Result result = _vk.CreateGraphicsPipelines(_device, default, 1, in pipelineInfo, null, out _hudPipeline);
            _vk.DestroyShaderModule(_device, vertModule, null);
            _vk.DestroyShaderModule(_device, fragModule, null);
            SilkMarshal.Free((nint)entryPoint);
            if (result != Result.Success) throw new VulkanException("vkCreateGraphicsPipelines (hud) failed", result);
        }

        /// <summary>Records the HUD line draw into an already-open rendering on the swapchain image.</summary>
        private void RecordHudDraw(CommandBuffer cb)
        {
            if (_hudVertexCount == 0) return;

            _vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _hudPipeline);
            Buffer vb = _hudBuffers[_currentFrame];
            ulong offset = 0;
            _vk.CmdBindVertexBuffers(cb, 0, 1, in vb, in offset);
            _vk.CmdDraw(cb, (uint)_hudVertexCount, 1, 0, 0);
        }

        private void DestroyHud()
        {
            if (_hudPipeline.Handle != 0) _vk.DestroyPipeline(_device, _hudPipeline, null);
            if (_hudPipelineLayout.Handle != 0) _vk.DestroyPipelineLayout(_device, _hudPipelineLayout, null);
            for (int i = 0; i < MaxFramesInFlight; i++)
            {
                if (_hudMemories[i].Handle != 0)
                {
                    _vk.UnmapMemory(_device, _hudMemories[i]);
                    _vk.FreeMemory(_device, _hudMemories[i], null);
                }
                if (_hudBuffers[i].Handle != 0) _vk.DestroyBuffer(_device, _hudBuffers[i], null);
            }
        }
    }
}
