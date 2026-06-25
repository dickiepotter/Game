namespace RP.Game.Graphics.Vulkan
{
    using Silk.NET.Core.Native;
    using Silk.NET.Vulkan;

    /// <summary>
    /// HDR off-screen rendering and the bloom post-process chain — the "stunning" pass (build brief S4.2,
    /// rendering quality). The scene is rendered into a floating-point HDR colour target so bright sources
    /// (engine plumes, tracers, rim glints) keep values above 1.0; a bright-pass extracts that overshoot, a
    /// separable Gaussian blurs it (ping-pong between two half-res targets), and a final composite adds the
    /// bloom back and tonemaps to the swapchain. This is the renderer's first use of descriptor sets (the post
    /// shaders sample textures), kept in its own file so the device spine stays readable.
    /// </summary>
    public sealed unsafe partial class VulkanRenderer
    {
        internal const Format HdrFormat = Format.R16G16B16A16Sfloat;

        // Multisampling: the scene renders to a multisampled HDR colour + depth, then resolves to the single-
        // sample HDR target the post chain samples. Set once from the device's limits.
        private SampleCountFlags _msaaSamples = SampleCountFlags.Count1Bit;
        private Image _hdrMsaaImage;
        private DeviceMemory _hdrMsaaMemory;
        private ImageView _hdrMsaaView;

        // Off-screen targets: the HDR scene (resolve target), and two half-res bloom buffers the blur
        // ping-pongs between.
        private Image _hdrImage;
        private DeviceMemory _hdrMemory;
        private ImageView _hdrView;
        private Image _bloomImageA, _bloomImageB;
        private DeviceMemory _bloomMemoryA, _bloomMemoryB;
        private ImageView _bloomViewA, _bloomViewB;
        private Extent2D _bloomExtent;

        private Sampler _linearSampler;

        // Descriptor plumbing for the texture-sampling post passes.
        private DescriptorPool _postDescriptorPool;
        private DescriptorSetLayout _oneSamplerLayout;  // bright / blur (1 texture)
        private DescriptorSetLayout _twoSamplerLayout;  // composite (scene + bloom)
        private DescriptorSet _brightSet;     // samples the HDR scene
        private DescriptorSet _blurSetA;      // samples bloom A (blur H: A -> B)
        private DescriptorSet _blurSetB;      // samples bloom B (blur V: B -> A)
        private DescriptorSet _compositeSet;  // samples HDR scene + bloom A

        private Pipeline _brightPipeline, _blurPipeline, _compositePipeline;
        private PipelineLayout _brightLayout, _blurLayout, _compositeLayout;

        private const int BlurIterations = 2;

        /// <summary>Picks the largest sample count the device supports for both colour and depth, capped at 4x
        /// (plenty for clean edges without the bandwidth of 8x). Call once after the device is up.</summary>
        private void DetermineSampleCount()
        {
            _vk.GetPhysicalDeviceProperties(_physicalDevice, out PhysicalDeviceProperties props);
            SampleCountFlags supported = props.Limits.FramebufferColorSampleCounts & props.Limits.FramebufferDepthSampleCounts;

            if (supported.HasFlag(SampleCountFlags.Count4Bit)) _msaaSamples = SampleCountFlags.Count4Bit;
            else if (supported.HasFlag(SampleCountFlags.Count2Bit)) _msaaSamples = SampleCountFlags.Count2Bit;
            else _msaaSamples = SampleCountFlags.Count1Bit;

            _log.Info("Vulkan", $"MSAA: {(int)_msaaSamples}x.");
        }

        /// <summary>Creates the HDR + bloom images, the multisampled scene target, and the sampler
        /// (swapchain-sized; bloom at half res).</summary>
        private void CreatePostResources()
        {
            (_hdrImage, _hdrMemory) = CreateImage(
                _swapchainExtent.Width, _swapchainExtent.Height, HdrFormat,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit);
            _hdrView = CreateColorView(_hdrImage, HdrFormat);

            // Multisampled HDR colour the scene draws into; resolved into _hdrImage at the end of the pass.
            if (_msaaSamples != SampleCountFlags.Count1Bit)
            {
                (_hdrMsaaImage, _hdrMsaaMemory) = CreateImage(
                    _swapchainExtent.Width, _swapchainExtent.Height, HdrFormat,
                    ImageUsageFlags.ColorAttachmentBit, _msaaSamples);
                _hdrMsaaView = CreateColorView(_hdrMsaaImage, HdrFormat);
            }

            _bloomExtent = new Extent2D(
                System.Math.Max(1, _swapchainExtent.Width / 2),
                System.Math.Max(1, _swapchainExtent.Height / 2));

            (_bloomImageA, _bloomMemoryA) = CreateImage(
                _bloomExtent.Width, _bloomExtent.Height, HdrFormat,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit);
            _bloomViewA = CreateColorView(_bloomImageA, HdrFormat);

            (_bloomImageB, _bloomMemoryB) = CreateImage(
                _bloomExtent.Width, _bloomExtent.Height, HdrFormat,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit);
            _bloomViewB = CreateColorView(_bloomImageB, HdrFormat);

            if (_linearSampler.Handle == 0)
            {
                var samplerInfo = new SamplerCreateInfo
                {
                    SType = StructureType.SamplerCreateInfo,
                    MagFilter = Filter.Linear,
                    MinFilter = Filter.Linear,
                    AddressModeU = SamplerAddressMode.ClampToEdge,
                    AddressModeV = SamplerAddressMode.ClampToEdge,
                    AddressModeW = SamplerAddressMode.ClampToEdge,
                    MipmapMode = SamplerMipmapMode.Linear,
                    BorderColor = BorderColor.FloatOpaqueBlack,
                };
                if (_vk.CreateSampler(_device, in samplerInfo, null, out _linearSampler) != Result.Success)
                {
                    throw new VulkanException("vkCreateSampler failed", Result.ErrorUnknown);
                }
            }
        }

        private ImageView CreateColorView(Image image, Format format)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = format,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };
            if (_vk.CreateImageView(_device, in viewInfo, null, out ImageView view) != Result.Success)
            {
                throw new VulkanException("vkCreateImageView (color) failed", Result.ErrorUnknown);
            }
            return view;
        }

        /// <summary>One-off: descriptor pool, the two set layouts, the allocated sets, and the three post
        /// pipelines. The sets are pointed at the images by <see cref="UpdatePostDescriptorSets"/>.</summary>
        private void CreatePostObjects()
        {
            _oneSamplerLayout = CreateSamplerSetLayout(1);
            _twoSamplerLayout = CreateSamplerSetLayout(2);

            var poolSize = new DescriptorPoolSize
            {
                Type = DescriptorType.CombinedImageSampler,
                DescriptorCount = 8,
            };
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize,
                MaxSets = 4,
            };
            if (_vk.CreateDescriptorPool(_device, in poolInfo, null, out _postDescriptorPool) != Result.Success)
            {
                throw new VulkanException("vkCreateDescriptorPool failed", Result.ErrorUnknown);
            }

            _brightSet = AllocateSet(_oneSamplerLayout);
            _blurSetA = AllocateSet(_oneSamplerLayout);
            _blurSetB = AllocateSet(_oneSamplerLayout);
            _compositeSet = AllocateSet(_twoSamplerLayout);

            // post.vert is shared by all three passes.
            _brightPipeline = CreatePostPipeline("bright.frag.spv", _oneSamplerLayout, HdrFormat, 0, out _brightLayout);
            _blurPipeline = CreatePostPipeline("blur.frag.spv", _oneSamplerLayout, HdrFormat, 2 * sizeof(float), out _blurLayout);
            _compositePipeline = CreatePostPipeline("composite.frag.spv", _twoSamplerLayout, _swapchainFormat, 0, out _compositeLayout);
        }

        private DescriptorSetLayout CreateSamplerSetLayout(int count)
        {
            var bindings = stackalloc DescriptorSetLayoutBinding[count];
            for (int i = 0; i < count; i++)
            {
                bindings[i] = new DescriptorSetLayoutBinding
                {
                    Binding = (uint)i,
                    DescriptorType = DescriptorType.CombinedImageSampler,
                    DescriptorCount = 1,
                    StageFlags = ShaderStageFlags.FragmentBit,
                };
            }
            var info = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)count,
                PBindings = bindings,
            };
            if (_vk.CreateDescriptorSetLayout(_device, in info, null, out DescriptorSetLayout layout) != Result.Success)
            {
                throw new VulkanException("vkCreateDescriptorSetLayout failed", Result.ErrorUnknown);
            }
            return layout;
        }

        private DescriptorSet AllocateSet(DescriptorSetLayout layout)
        {
            var info = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _postDescriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &layout,
            };
            if (_vk.AllocateDescriptorSets(_device, in info, out DescriptorSet set) != Result.Success)
            {
                throw new VulkanException("vkAllocateDescriptorSets failed", Result.ErrorUnknown);
            }
            return set;
        }

        /// <summary>Points each post set at the current image views. Re-run whenever the images are recreated
        /// (initial setup and every resize).</summary>
        private void UpdatePostDescriptorSets()
        {
            WriteSampler(_brightSet, 0, _hdrView);
            WriteSampler(_blurSetA, 0, _bloomViewA);
            WriteSampler(_blurSetB, 0, _bloomViewB);
            WriteSampler(_compositeSet, 0, _hdrView);
            WriteSampler(_compositeSet, 1, _bloomViewA);
        }

        private void WriteSampler(DescriptorSet set, uint binding, ImageView view)
        {
            var imageInfo = new DescriptorImageInfo
            {
                Sampler = _linearSampler,
                ImageView = view,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            };
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = binding,
                DstArrayElement = 0,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                PImageInfo = &imageInfo,
            };
            _vk.UpdateDescriptorSets(_device, 1, in write, 0, null);
        }

        private Pipeline CreatePostPipeline(string fragResource, DescriptorSetLayout setLayout, Format colorFormat, uint pushSize, out PipelineLayout pipelineLayout)
        {
            ShaderModule vertModule = CreateShaderModule("post.vert.spv");
            ShaderModule fragModule = CreateShaderModule(fragResource);
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

            var vertexInput = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList,
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
            var colorBlendAttachment = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = false,
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

            DescriptorSetLayout localSetLayout = setLayout;
            var pushRange = new PushConstantRange { StageFlags = ShaderStageFlags.FragmentBit, Offset = 0, Size = pushSize };
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &localSetLayout,
                PushConstantRangeCount = pushSize > 0 ? 1u : 0u,
                PPushConstantRanges = pushSize > 0 ? &pushRange : null,
            };
            if (_vk.CreatePipelineLayout(_device, in layoutInfo, null, out pipelineLayout) != Result.Success)
            {
                throw new VulkanException("vkCreatePipelineLayout (post) failed", Result.ErrorUnknown);
            }

            Format localColorFormat = colorFormat;
            var renderingCreateInfo = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = 1, PColorAttachmentFormats = &localColorFormat,
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
                Layout = pipelineLayout,
            };

            Result result = _vk.CreateGraphicsPipelines(_device, default, 1, in pipelineInfo, null, out Pipeline pipeline);
            _vk.DestroyShaderModule(_device, vertModule, null);
            _vk.DestroyShaderModule(_device, fragModule, null);
            SilkMarshal.Free((nint)entryPoint);
            if (result != Result.Success) throw new VulkanException("vkCreateGraphicsPipelines (post) failed", result);
            return pipeline;
        }

        /// <summary>
        /// Runs the bloom chain and final composite into the swapchain image. Assumes the HDR scene has just
        /// been rendered and is about to be transitioned to shader-read by the caller.
        /// </summary>
        private void RecordPostChain(CommandBuffer cb, uint imageIndex)
        {
            // HDR scene -> shader read so the bright-pass can sample it.
            TransitionImage(cb, _hdrImage,
                ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal,
                AccessFlags.ColorAttachmentWriteBit, AccessFlags.ShaderReadBit,
                PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.FragmentShaderBit);

            // Bright-pass: HDR overshoot -> bloom A.
            TransitionImage(cb, _bloomImageA, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
                0, AccessFlags.ColorAttachmentWriteBit,
                PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.ColorAttachmentOutputBit);
            FullscreenPass(cb, _brightPipeline, _brightLayout, _brightSet, _bloomViewA, _bloomExtent, null, 0);
            ToShaderRead(cb, _bloomImageA);

            // Separable Gaussian, ping-ponging A<->B.
            float texelX = 1.0f / _bloomExtent.Width;
            float texelY = 1.0f / _bloomExtent.Height;
            float* dir = stackalloc float[2];
            for (int i = 0; i < BlurIterations; i++)
            {
                // Horizontal: A -> B
                dir[0] = texelX; dir[1] = 0f;
                TransitionImage(cb, _bloomImageB, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
                    0, AccessFlags.ColorAttachmentWriteBit,
                    PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.ColorAttachmentOutputBit);
                FullscreenPass(cb, _blurPipeline, _blurLayout, _blurSetA, _bloomViewB, _bloomExtent, dir, 2 * sizeof(float));
                ToShaderRead(cb, _bloomImageB);

                // Vertical: B -> A
                dir[0] = 0f; dir[1] = texelY;
                TransitionImage(cb, _bloomImageA, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.ColorAttachmentOptimal,
                    AccessFlags.ShaderReadBit, AccessFlags.ColorAttachmentWriteBit,
                    PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.ColorAttachmentOutputBit);
                FullscreenPass(cb, _blurPipeline, _blurLayout, _blurSetB, _bloomViewA, _bloomExtent, dir, 2 * sizeof(float));
                ToShaderRead(cb, _bloomImageA);
            }

            // Composite scene + bloom -> swapchain.
            TransitionImage(cb, _swapchainImages[imageIndex], ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
                0, AccessFlags.ColorAttachmentWriteBit,
                PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.ColorAttachmentOutputBit);
            FullscreenPass(cb, _compositePipeline, _compositeLayout, _compositeSet, _swapchainImageViews[imageIndex], _swapchainExtent, null, 0);
            TransitionImage(cb, _swapchainImages[imageIndex], ImageLayout.ColorAttachmentOptimal, ImageLayout.PresentSrcKhr,
                AccessFlags.ColorAttachmentWriteBit, 0,
                PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.BottomOfPipeBit);
        }

        private void ToShaderRead(CommandBuffer cb, Image image)
        {
            TransitionImage(cb, image, ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal,
                AccessFlags.ColorAttachmentWriteBit, AccessFlags.ShaderReadBit,
                PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.FragmentShaderBit);
        }

        private void FullscreenPass(
            CommandBuffer cb, Pipeline pipeline, PipelineLayout layout, DescriptorSet set,
            ImageView targetView, Extent2D extent, float* push, uint pushSize)
        {
            var colorAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = targetView,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = AttachmentLoadOp.DontCare, // the fullscreen triangle covers every pixel
                StoreOp = AttachmentStoreOp.Store,
            };
            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D(new Offset2D(0, 0), extent),
                LayerCount = 1,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorAttachment,
            };

            _vk.CmdBeginRendering(cb, in renderingInfo);

            var viewport = new Viewport { X = 0, Y = 0, Width = extent.Width, Height = extent.Height, MinDepth = 0, MaxDepth = 1 };
            _vk.CmdSetViewport(cb, 0, 1, in viewport);
            var scissor = new Rect2D(new Offset2D(0, 0), extent);
            _vk.CmdSetScissor(cb, 0, 1, in scissor);

            _vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
            _vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, layout, 0, 1, in set, 0, null);
            if (pushSize > 0 && push != null)
            {
                _vk.CmdPushConstants(cb, layout, ShaderStageFlags.FragmentBit, 0, pushSize, push);
            }

            _vk.CmdDraw(cb, 3, 1, 0, 0);
            _vk.CmdEndRendering(cb);
        }

        private void DestroyPostResources()
        {
            if (_hdrMsaaView.Handle != 0) _vk.DestroyImageView(_device, _hdrMsaaView, null);
            if (_hdrMsaaImage.Handle != 0) _vk.DestroyImage(_device, _hdrMsaaImage, null);
            if (_hdrMsaaMemory.Handle != 0) _vk.FreeMemory(_device, _hdrMsaaMemory, null);
            _hdrMsaaView = default; _hdrMsaaImage = default; _hdrMsaaMemory = default;

            if (_hdrView.Handle != 0) _vk.DestroyImageView(_device, _hdrView, null);
            if (_hdrImage.Handle != 0) _vk.DestroyImage(_device, _hdrImage, null);
            if (_hdrMemory.Handle != 0) _vk.FreeMemory(_device, _hdrMemory, null);
            if (_bloomViewA.Handle != 0) _vk.DestroyImageView(_device, _bloomViewA, null);
            if (_bloomImageA.Handle != 0) _vk.DestroyImage(_device, _bloomImageA, null);
            if (_bloomMemoryA.Handle != 0) _vk.FreeMemory(_device, _bloomMemoryA, null);
            if (_bloomViewB.Handle != 0) _vk.DestroyImageView(_device, _bloomViewB, null);
            if (_bloomImageB.Handle != 0) _vk.DestroyImage(_device, _bloomImageB, null);
            if (_bloomMemoryB.Handle != 0) _vk.FreeMemory(_device, _bloomMemoryB, null);
            _hdrView = default; _hdrImage = default; _hdrMemory = default;
            _bloomViewA = default; _bloomImageA = default; _bloomMemoryA = default;
            _bloomViewB = default; _bloomImageB = default; _bloomMemoryB = default;
        }

        private void DestroyPostObjects()
        {
            if (_brightPipeline.Handle != 0) _vk.DestroyPipeline(_device, _brightPipeline, null);
            if (_blurPipeline.Handle != 0) _vk.DestroyPipeline(_device, _blurPipeline, null);
            if (_compositePipeline.Handle != 0) _vk.DestroyPipeline(_device, _compositePipeline, null);
            if (_brightLayout.Handle != 0) _vk.DestroyPipelineLayout(_device, _brightLayout, null);
            if (_blurLayout.Handle != 0) _vk.DestroyPipelineLayout(_device, _blurLayout, null);
            if (_compositeLayout.Handle != 0) _vk.DestroyPipelineLayout(_device, _compositeLayout, null);
            if (_postDescriptorPool.Handle != 0) _vk.DestroyDescriptorPool(_device, _postDescriptorPool, null);
            if (_oneSamplerLayout.Handle != 0) _vk.DestroyDescriptorSetLayout(_device, _oneSamplerLayout, null);
            if (_twoSamplerLayout.Handle != 0) _vk.DestroyDescriptorSetLayout(_device, _twoSamplerLayout, null);
            if (_linearSampler.Handle != 0) _vk.DestroySampler(_device, _linearSampler, null);
        }
    }
}
