namespace RP.Game.Graphics.Vulkan
{
    using System;
    using RP.Game.Rendering;
    using RP.Math;
    using Silk.NET.Core.Native;
    using Silk.NET.Vulkan;
    using Buffer = Silk.NET.Vulkan.Buffer;

    /// <summary>
    /// The 2D overlay, drawn over the final composited image each frame in two passes: filled triangles
    /// first, then lines on top of them.
    /// </summary>
    /// <remarks>
    /// <para><b>Why two passes and not one.</b> They exist for opposite reasons and blend in opposite ways.
    /// Lines are diegetic instrumentation — a reticle, a bracket, a gauge — and blend additively so they
    /// glow over the scene. Fills are the substrate a readable interface needs: a panel exists precisely to
    /// darken what is behind it, which additive blending cannot do at all. One vertex format, one shader,
    /// two pipelines differing only in topology and blend state.</para>
    /// <para>Ordering is fixed rather than sorted: fills, then lines. That is what every panel-and-text
    /// interface wants, and making it configurable would only invite getting it wrong.</para>
    /// </remarks>
    public sealed unsafe partial class VulkanRenderer
    {
        private const int MaxHudVertices = 8192;

        /// <summary>
        /// How many filled overlay vertices may be submitted per frame.
        /// </summary>
        /// <remarks>
        /// Larger than the line budget because panels are cheap to emit and easy to want a lot of: a full
        /// crafting screen is a few hundred quads before anything is drawn on top of them, and six vertices
        /// per quad adds up faster than people expect.
        /// </remarks>
        private const int MaxHudFillVertices = 24576;

        private Pipeline _hudPipeline;
        private Pipeline _hudFillPipeline;
        private PipelineLayout _hudPipelineLayout;

        private readonly Buffer[] _hudBuffers = new Buffer[MaxFramesInFlight];
        private readonly DeviceMemory[] _hudMemories = new DeviceMemory[MaxFramesInFlight];
        private readonly nint[] _hudMapped = new nint[MaxFramesInFlight];
        private readonly HudVertex[] _hudCpu = new HudVertex[MaxHudVertices];
        private int _hudVertexCount;

        private readonly Buffer[] _hudFillBuffers = new Buffer[MaxFramesInFlight];
        private readonly DeviceMemory[] _hudFillMemories = new DeviceMemory[MaxFramesInFlight];
        private readonly nint[] _hudFillMapped = new nint[MaxFramesInFlight];
        private readonly HudVertex[] _hudFillCpu = new HudVertex[MaxHudFillVertices];
        private int _hudFillVertexCount;

        private void CreateHudBuffers()
        {
            for (int i = 0; i < MaxFramesInFlight; i++)
            {
                (_hudBuffers[i], _hudMemories[i], _hudMapped[i]) = CreateMappedVertexBuffer(MaxHudVertices);
                (_hudFillBuffers[i], _hudFillMemories[i], _hudFillMapped[i]) = CreateMappedVertexBuffer(MaxHudFillVertices);
            }
        }

        private (Buffer Buffer, DeviceMemory Memory, nint Mapped) CreateMappedVertexBuffer(int vertexCapacity)
        {
            ulong capacity = (ulong)(vertexCapacity * sizeof(HudVertex));
            var (buffer, memory) = CreateBuffer(
                capacity, BufferUsageFlags.VertexBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            void* mapped;
            _vk.MapMemory(_device, memory, 0, capacity, 0, &mapped);
            return (buffer, memory, (nint)mapped);
        }

        /// <summary>Sets the overlay line segments for the next frame (two vertices per segment, NDC positions).</summary>
        public void SetHudLines(ReadOnlySpan<HudVertex> vertices)
        {
            int count = Math.Min(vertices.Length, MaxHudVertices);
            for (int i = 0; i < count; i++) _hudCpu[i] = vertices[i];
            _hudVertexCount = count - (count % 2); // whole segments only
        }

        /// <summary>
        /// Sets the filled overlay triangles for the next frame (three vertices per triangle, NDC positions).
        /// Drawn beneath the lines, with ordinary alpha blending.
        /// </summary>
        public void SetHudTriangles(ReadOnlySpan<HudVertex> vertices)
        {
            int count = Math.Min(vertices.Length, MaxHudFillVertices);
            for (int i = 0; i < count; i++) _hudFillCpu[i] = vertices[i];
            _hudFillVertexCount = count - (count % 3); // whole triangles only
        }

        /// <summary>How many filled overlay vertices fit in one frame, so a caller can budget its interface.</summary>
        public static int HudFillCapacity => MaxHudFillVertices;

        /// <summary>How many overlay line vertices fit in one frame.</summary>
        public static int HudLineCapacity => MaxHudVertices;

        private void UploadHud(int frameIndex)
        {
            CopyHud(_hudCpu, _hudVertexCount, _hudMapped[frameIndex], MaxHudVertices);
            CopyHud(_hudFillCpu, _hudFillVertexCount, _hudFillMapped[frameIndex], MaxHudFillVertices);
        }

        private static void CopyHud(HudVertex[] source, int count, nint destination, int capacityVertices)
        {
            if (count == 0) return;

            ulong bytes = (ulong)(count * sizeof(HudVertex));
            ulong capacity = (ulong)(capacityVertices * sizeof(HudVertex));
            fixed (HudVertex* src = source)
            {
                System.Buffer.MemoryCopy(src, (void*)destination, capacity, bytes);
            }
        }

        private void CreateHudPipeline()
        {
            // Two pipelines from one description: the fills underneath, the lines on top.
            _hudFillPipeline = BuildHudPipeline(PrimitiveTopology.TriangleList, additive: false, out _hudPipelineLayout);
            _hudPipeline = BuildHudPipeline(PrimitiveTopology.LineList, additive: true, out PipelineLayout lineLayout);

            // Both use an empty layout, so the second is redundant and must not leak.
            if (lineLayout.Handle != 0 && lineLayout.Handle != _hudPipelineLayout.Handle)
            {
                _vk.DestroyPipelineLayout(_device, lineLayout, null);
            }
        }

        private Pipeline BuildHudPipeline(PrimitiveTopology topology, bool additive, out PipelineLayout layout)
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
            var attributes = stackalloc VertexInputAttributeDescription[3];
            attributes[0] = new VertexInputAttributeDescription { Binding = 0, Location = 0, Format = Format.R32G32Sfloat, Offset = 0 };
            attributes[1] = new VertexInputAttributeDescription { Binding = 0, Location = 1, Format = Format.R32G32B32Sfloat, Offset = (uint)sizeof(Vector2) };
            attributes[2] = new VertexInputAttributeDescription
            {
                Binding = 0, Location = 2, Format = Format.R32Sfloat,
                Offset = (uint)(sizeof(Vector2) + sizeof(Vector3)),
            };
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1, PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 3, PVertexAttributeDescriptions = attributes,
            };
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = topology,
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
            // Two blends, for two jobs. Lines go on additively so a reticle glows over the scene; fills go
            // on with ordinary source-alpha blending, because a panel's whole purpose is to darken what is
            // behind it enough that text on top can be read. An additive panel is a bright fog.
            var colorBlendAttachment = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = additive ? BlendFactor.One : BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = additive ? BlendFactor.One : BlendFactor.OneMinusSrcAlpha,
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
            if (_vk.CreatePipelineLayout(_device, in layoutInfo, null, out layout) != Result.Success)
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
                Layout = layout,
            };
            Result result = _vk.CreateGraphicsPipelines(_device, default, 1, in pipelineInfo, null, out Pipeline pipeline);
            _vk.DestroyShaderModule(_device, vertModule, null);
            _vk.DestroyShaderModule(_device, fragModule, null);
            SilkMarshal.Free((nint)entryPoint);
            if (result != Result.Success) throw new VulkanException("vkCreateGraphicsPipelines (hud) failed", result);
            return pipeline;
        }

        /// <summary>
        /// Records the overlay into an already-open rendering on the swapchain image: fills first, then the
        /// lines over them.
        /// </summary>
        private void RecordHudDraw(CommandBuffer cb)
        {
            ulong offset = 0;

            if (_hudFillVertexCount > 0 && _hudFillPipeline.Handle != 0)
            {
                _vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _hudFillPipeline);
                Buffer fills = _hudFillBuffers[_currentFrame];
                _vk.CmdBindVertexBuffers(cb, 0, 1, in fills, in offset);
                _vk.CmdDraw(cb, (uint)_hudFillVertexCount, 1, 0, 0);
            }

            if (_hudVertexCount == 0) return;

            _vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _hudPipeline);
            Buffer lines = _hudBuffers[_currentFrame];
            _vk.CmdBindVertexBuffers(cb, 0, 1, in lines, in offset);
            _vk.CmdDraw(cb, (uint)_hudVertexCount, 1, 0, 0);
        }

        private void ReleaseMappedVertexBuffer(ref Buffer buffer, ref DeviceMemory memory)
        {
            if (memory.Handle != 0)
            {
                _vk.UnmapMemory(_device, memory);
                _vk.FreeMemory(_device, memory, null);
                memory = default;
            }

            if (buffer.Handle != 0)
            {
                _vk.DestroyBuffer(_device, buffer, null);
                buffer = default;
            }
        }

        private void DestroyHud()
        {
            if (_hudPipeline.Handle != 0) _vk.DestroyPipeline(_device, _hudPipeline, null);
            if (_hudFillPipeline.Handle != 0) _vk.DestroyPipeline(_device, _hudFillPipeline, null);
            if (_hudPipelineLayout.Handle != 0) _vk.DestroyPipelineLayout(_device, _hudPipelineLayout, null);

            for (int i = 0; i < MaxFramesInFlight; i++)
            {
                ReleaseMappedVertexBuffer(ref _hudBuffers[i], ref _hudMemories[i]);
                ReleaseMappedVertexBuffer(ref _hudFillBuffers[i], ref _hudFillMemories[i]);
            }
        }
    }
}
