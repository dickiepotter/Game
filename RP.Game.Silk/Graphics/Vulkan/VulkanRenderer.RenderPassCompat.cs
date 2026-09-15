namespace RP.Game.Graphics.Vulkan
{
    using System;
    using System.Collections.Generic;
    using Silk.NET.Vulkan;

    /// <summary>
    /// Drawing without <c>VK_KHR_dynamic_rendering</c>, for hardware that has never heard of it.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> The renderer is written against dynamic rendering, which is the right
    /// way to write a Vulkan renderer in 2020 and later and the only way this one stays legible: attachments
    /// are named at the point of drawing rather than declared months earlier in an object that has to be
    /// kept in step with six pipelines. The cost is that a device without the extension cannot run the game
    /// at all -- it was skipped outright during device selection, and the player was told "No suitable
    /// Vulkan GPU". For a game whose stated target is machines from about ten years ago, that is the single
    /// largest compatibility hole in it.</para>
    ///
    /// <para><b>What it does instead.</b> Render passes and framebuffers are built on demand from the very
    /// same <see cref="RenderingInfo"/> the dynamic path would have consumed, and cached by the shape of
    /// that description. Every call site is unchanged: <c>BeginRendering</c> either begins a dynamic
    /// rendering block or begins a render pass, and nothing above it knows which. That is the whole reason
    /// the wrappers were there in the first place.</para>
    ///
    /// <para><b>Why the cache is keyed on the description and not the images.</b> A frame uses a handful of
    /// distinct attachment shapes -- scene, bright pass, two blur steps, swapchain -- and cycles through
    /// several images for each. Keying the render pass on the formats and operations gives about five of
    /// them for the lifetime of the swapchain; framebuffers additionally key on the views, which is a few
    /// dozen. Both are built once and then found.</para>
    ///
    /// <para><b>It can be switched on deliberately.</b> Without that this code would only ever run on
    /// hardware nobody testing it owns, which is the same as not having written it. <see cref="ForceRenderPass"/>
    /// makes the fallback the path taken on any device at all.</para>
    /// </remarks>
    public sealed unsafe partial class VulkanRenderer
    {
        /// <summary>
        /// Forces the render-pass path even on hardware that supports dynamic rendering.
        /// </summary>
        /// <remarks>
        /// Set before the renderer is constructed. Exists so the compatibility path can be exercised on a
        /// modern machine, because a fallback that only runs on hardware nobody has is a fallback nobody
        /// has tested.
        /// </remarks>
        public static bool ForceRenderPass { get; set; }

        /// <summary>Whether this renderer is drawing through render passes rather than dynamic rendering.</summary>
        public bool UsingRenderPasses => _useRenderPasses;

        private bool _useRenderPasses;

        private readonly Dictionary<PassShape, RenderPass> _passCache = new Dictionary<PassShape, RenderPass>();
        private readonly Dictionary<FrameShape, Framebuffer> _framebufferCache = new Dictionary<FrameShape, Framebuffer>();

        private RenderPass _activePass;

        /// <summary>
        /// What makes one render pass different from another: formats, sample count and what happens to the
        /// attachments at each end.
        /// </summary>
        private readonly struct PassShape : IEquatable<PassShape>
        {
            public readonly Format Color;
            public readonly Format Depth;
            public readonly SampleCountFlags Samples;
            public readonly AttachmentLoadOp ColorLoad;
            public readonly AttachmentStoreOp ColorStore;
            public readonly AttachmentLoadOp DepthLoad;
            public readonly bool HasDepth;
            public readonly bool Present;

            public PassShape(
                Format color, Format depth, SampleCountFlags samples,
                AttachmentLoadOp colorLoad, AttachmentStoreOp colorStore, AttachmentLoadOp depthLoad,
                bool hasDepth, bool present)
            {
                Color = color;
                Depth = depth;
                Samples = samples;
                ColorLoad = colorLoad;
                ColorStore = colorStore;
                DepthLoad = depthLoad;
                HasDepth = hasDepth;
                Present = present;
            }

            public bool Equals(PassShape other)
                => Color == other.Color && Depth == other.Depth && Samples == other.Samples
                && ColorLoad == other.ColorLoad && ColorStore == other.ColorStore
                && DepthLoad == other.DepthLoad && HasDepth == other.HasDepth && Present == other.Present;

            public override bool Equals(object? obj) => obj is PassShape other && Equals(other);

            public override int GetHashCode()
                => HashCode.Combine((int)Color, (int)Depth, (int)Samples, (int)ColorLoad, (int)ColorStore, (int)DepthLoad, HasDepth, Present);
        }

        /// <summary>A render pass plus the exact images it is pointed at.</summary>
        private readonly struct FrameShape : IEquatable<FrameShape>
        {
            public readonly ulong Pass;
            public readonly ulong Color;
            public readonly ulong Depth;
            public readonly uint Width;
            public readonly uint Height;

            public FrameShape(ulong pass, ulong color, ulong depth, uint width, uint height)
            {
                Pass = pass;
                Color = color;
                Depth = depth;
                Width = width;
                Height = height;
            }

            public bool Equals(FrameShape other)
                => Pass == other.Pass && Color == other.Color && Depth == other.Depth
                && Width == other.Width && Height == other.Height;

            public override bool Equals(object? obj) => obj is FrameShape other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(Pass, Color, Depth, Width, Height);
        }

        /// <summary>
        /// Begins a render pass equivalent to a dynamic-rendering block.
        /// </summary>
        /// <remarks>
        /// The clear values are lifted straight out of the attachment descriptions, so a caller that asked
        /// for a clear gets one and a caller that asked to load gets its contents back. Getting that wrong
        /// is how a fallback path produces a black screen that looks like a driver fault.
        /// </remarks>
        private void BeginRenderPassCompat(CommandBuffer cb, in RenderingInfo info)
        {
            RenderingAttachmentInfo* color = info.PColorAttachments;
            RenderingAttachmentInfo* depth = info.PDepthAttachment;

            bool hasDepth = depth != null && depth->ImageView.Handle != 0;
            bool present = color != null && IsSwapchainView(color->ImageView);

            var shape = new PassShape(
                FormatOfView(color != null ? color->ImageView : default, _swapchainFormat),
                hasDepth ? _depthFormat : Format.Undefined,
                SampleCountOfView(color != null ? color->ImageView : default),
                color != null ? color->LoadOp : AttachmentLoadOp.DontCare,
                color != null ? color->StoreOp : AttachmentStoreOp.Store,
                hasDepth ? depth->LoadOp : AttachmentLoadOp.DontCare,
                hasDepth,
                present);

            RenderPass pass = GetOrCreatePass(shape);
            Framebuffer framebuffer = GetOrCreateFramebuffer(
                pass,
                color != null ? color->ImageView : default,
                hasDepth ? depth->ImageView : default,
                info.RenderArea.Extent);

            var clears = stackalloc ClearValue[2];
            uint clearCount = 0;

            if (color != null)
            {
                clears[clearCount++] = new ClearValue { Color = color->ClearValue.Color };
            }

            if (hasDepth)
            {
                clears[clearCount++] = new ClearValue { DepthStencil = depth->ClearValue.DepthStencil };
            }

            var begin = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = pass,
                Framebuffer = framebuffer,
                RenderArea = info.RenderArea,
                ClearValueCount = clearCount,
                PClearValues = clears,
            };

            _vk.CmdBeginRenderPass(cb, in begin, SubpassContents.Inline);
            _activePass = pass;
        }

        private RenderPass GetOrCreatePass(PassShape shape)
        {
            if (_passCache.TryGetValue(shape, out RenderPass existing)) return existing;

            var attachments = stackalloc AttachmentDescription[2];
            uint count = 0;

            // The attachment is left exactly where the dynamic-rendering path leaves it: in
            // COLOR_ATTACHMENT_OPTIMAL, with every other transition still done by the explicit barriers the
            // renderer already issues around each pass.
            //
            // Having the render pass move the layout somewhere more useful is the obvious thing to do and
            // it is wrong here, because those barriers do not know it happened. They then transition from a
            // layout the image is no longer in, which the validation layer reports immediately and which on
            // a driver without validation is undefined behaviour. One owner of layout, and it is the code
            // that was already doing it.
            attachments[count++] = new AttachmentDescription
            {
                Format = shape.Color,
                Samples = shape.Samples,
                LoadOp = shape.ColorLoad,
                StoreOp = shape.ColorStore,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,

                // Undefined whenever the contents are being thrown away, which lets the driver skip a
                // decompress and is always legal.
                InitialLayout = shape.ColorLoad == AttachmentLoadOp.Load
                    ? ImageLayout.ColorAttachmentOptimal
                    : ImageLayout.Undefined,

                FinalLayout = ImageLayout.ColorAttachmentOptimal,
            };

            var colorRef = new AttachmentReference { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
            var depthRef = new AttachmentReference { Attachment = 1, Layout = ImageLayout.DepthStencilAttachmentOptimal };

            if (shape.HasDepth)
            {
                attachments[count++] = new AttachmentDescription
                {
                    Format = shape.Depth,
                    Samples = shape.Samples,
                    LoadOp = shape.DepthLoad,
                    StoreOp = AttachmentStoreOp.DontCare,
                    StencilLoadOp = AttachmentLoadOp.DontCare,
                    StencilStoreOp = AttachmentStoreOp.DontCare,
                    InitialLayout = shape.DepthLoad == AttachmentLoadOp.Load
                        ? ImageLayout.DepthStencilAttachmentOptimal
                        : ImageLayout.Undefined,

                    FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
                };
            }

            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorRef,
                PDepthStencilAttachment = shape.HasDepth ? &depthRef : null,
            };

            // No subpass dependencies.
            //
            // This renderer manages every layout and every hazard with explicit barriers, because that is
            // what dynamic rendering requires of it. Under render passes those barriers are still there and
            // still correct, so a dependency against SUBPASS_EXTERNAL would be declaring the same
            // synchronisation a second time. Measured with and without: no difference to the frame rate,
            // and one fewer thing that has to be kept in step with the barriers that are doing the work.
            var info = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = count,
                PAttachments = attachments,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 0,
                PDependencies = null,
            };

            if (_vk.CreateRenderPass(_device, in info, null, out RenderPass pass) != Result.Success)
            {
                throw new VulkanException("vkCreateRenderPass failed", Result.ErrorUnknown);
            }

            _passCache[shape] = pass;
            return pass;
        }

        private Framebuffer GetOrCreateFramebuffer(RenderPass pass, ImageView color, ImageView depth, Extent2D extent)
        {
            var key = new FrameShape(pass.Handle, color.Handle, depth.Handle, extent.Width, extent.Height);
            if (_framebufferCache.TryGetValue(key, out Framebuffer existing)) return existing;

            var views = stackalloc ImageView[2];
            uint count = 0;
            views[count++] = color;
            if (depth.Handle != 0) views[count++] = depth;

            var info = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = pass,
                AttachmentCount = count,
                PAttachments = views,
                Width = extent.Width,
                Height = extent.Height,
                Layers = 1,
            };

            if (_vk.CreateFramebuffer(_device, in info, null, out Framebuffer framebuffer) != Result.Success)
            {
                throw new VulkanException("vkCreateFramebuffer failed", Result.ErrorUnknown);
            }

            _framebufferCache[key] = framebuffer;
            return framebuffer;
        }

        /// <summary>
        /// A render pass a pipeline can be created against, matching what it will actually be drawn into.
        /// </summary>
        /// <remarks>
        /// A pipeline built for dynamic rendering declares its attachment formats and nothing else; one
        /// built for a render pass has to name a compatible pass at creation time. "Compatible" is a
        /// weaker relation than "identical" -- only formats and sample counts have to match, not load and
        /// store operations -- so one pass per format combination is enough, and it need not be the same
        /// object the draw eventually uses.
        /// </remarks>
        private RenderPass PipelineCompatibilityPass(Format color, Format depth, SampleCountFlags samples)
        {
            return GetOrCreatePass(new PassShape(
                color,
                depth,
                samples,
                AttachmentLoadOp.DontCare,
                AttachmentStoreOp.Store,
                AttachmentLoadOp.DontCare,
                hasDepth: depth != Format.Undefined,
                present: false));
        }

        /// <summary>
        /// The render pass a pipeline should be created against, read from the dynamic-rendering
        /// description it would otherwise have used.
        /// </summary>
        /// <remarks>
        /// Taking it from the same structure means the two paths cannot drift: a pipeline whose formats
        /// change for the dynamic path changes for this one in the same edit, and there is no second
        /// declaration to forget.
        /// </remarks>
        private RenderPass CompatibilityPassFor(in PipelineRenderingCreateInfo info)
        {
            Format color = info.ColorAttachmentCount > 0 && info.PColorAttachmentFormats != null
                ? info.PColorAttachmentFormats[0]
                : _swapchainFormat;

            return PipelineCompatibilityPass(color, info.DepthAttachmentFormat, _msaaSamples);
        }

        /// <summary>Whether an image view belongs to the swapchain, which decides the final layout.</summary>
        private bool IsSwapchainView(ImageView view)
        {
            for (int i = 0; i < _swapchainImageViews.Length; i++)
            {
                if (_swapchainImageViews[i].Handle == view.Handle) return true;
            }

            return false;
        }

        /// <summary>
        /// The format an attachment view carries.
        /// </summary>
        /// <remarks>
        /// Vulkan will not tell you: an image view's format is not queryable after creation. It is
        /// recorded here as each target is made, which is a small ledger to keep and much less fragile
        /// than inferring it from which pass happens to be running.
        /// </remarks>
        private Format FormatOfView(ImageView view, Format fallback)
            => _viewFormats.TryGetValue(view.Handle, out Format format) ? format : fallback;

        private SampleCountFlags SampleCountOfView(ImageView view)
            => _viewSamples.TryGetValue(view.Handle, out SampleCountFlags samples) ? samples : SampleCountFlags.Count1Bit;

        private readonly Dictionary<ulong, Format> _viewFormats = new Dictionary<ulong, Format>();
        private readonly Dictionary<ulong, SampleCountFlags> _viewSamples = new Dictionary<ulong, SampleCountFlags>();

        /// <summary>Records what an attachment view is, so a render pass can be built to match it.</summary>
        private void RegisterAttachmentView(ImageView view, Format format, SampleCountFlags samples)
        {
            if (view.Handle == 0) return;
            _viewFormats[view.Handle] = format;
            _viewSamples[view.Handle] = samples;
        }

        /// <summary>Drops every cached pass and framebuffer, for a swapchain rebuild or teardown.</summary>
        private void DestroyRenderPassCache()
        {
            foreach (Framebuffer framebuffer in _framebufferCache.Values)
            {
                _vk.DestroyFramebuffer(_device, framebuffer, null);
            }

            foreach (RenderPass pass in _passCache.Values)
            {
                _vk.DestroyRenderPass(_device, pass, null);
            }

            _framebufferCache.Clear();
            _passCache.Clear();
            _viewFormats.Clear();
            _viewSamples.Clear();
        }
    }
}
