namespace RP.Game.Graphics.Vulkan
{
    using System;
    using RP.Game.Rendering;
    using Silk.NET.Vulkan;
    using Buffer = Silk.NET.Vulkan.Buffer;

    /// <summary>
    /// Reading a finished frame back off the GPU and writing it to a file.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a renderer needs this.</b> Everything else the suite can check about a frame is
    /// structural: that the geometry is valid, that no buffer overflowed, that the frame rate held, that
    /// the validation layer stayed quiet. All of that can be perfectly green while the image is wrong --
    /// this renderer once drew its entire interface upside down and every one of those signals said it was
    /// fine, because the Y flip that is correct under OpenGL produces a completely valid frame under
    /// Vulkan. The only way to catch that class of fault is to look, and the only way to look without a
    /// person sitting in front of the window is to write the pixels out.</para>
    ///
    /// <para><b>Deliberately slow.</b> The capture waits for the device to go idle, submits its own copy,
    /// waits for that, then maps and encodes on the calling thread. A frame or two is lost every time it
    /// runs, which is exactly right for something that runs when asked and never during play.</para>
    /// </remarks>
    public sealed unsafe partial class VulkanRenderer
    {
        private string? _captureRequest;

        /// <summary>
        /// Asks for the next presented frame to be written to this path as a PNG. Cleared once written.
        /// </summary>
        public string? CaptureNextFrameTo
        {
            get => _captureRequest;
            set => _captureRequest = value;
        }

        /// <summary>Whether a capture is waiting to happen.</summary>
        public bool CapturePending => _captureRequest != null;

        /// <summary>Whether this device's swapchain can be read back at all.</summary>
        public bool CanCaptureFrames => _canCaptureFrames;

        /// <summary>
        /// Copies the finished swapchain image into host memory and writes it out.
        /// </summary>
        /// <remarks>
        /// <para>Two details, both of which the validation layer is quick to point out. The layout is
        /// PRESENT_SRC, because the frame's own command buffer already transitioned it there and the wait
        /// for idle above means that has happened -- and it is put back the same way, since the presentation
        /// engine is about to be handed it. Transitioning from UNDEFINED instead would be legal and would
        /// discard the contents, which produces a plausible-looking black image rather than an error.</para>
        ///
        /// <para>And this runs before vkQueuePresentKHR, not after. Presenting hands the image back to the
        /// presentation engine; touching it afterwards is using something we no longer own, whatever
        /// layout it happens to be in.</para>
        /// </remarks>
        private void CaptureFrame(uint imageIndex, string path)
        {
            if (!_canCaptureFrames)
            {
                _log.Warning("Vulkan", "This surface does not allow reading the swapchain back; no capture written.");
                return;
            }

            _vk.DeviceWaitIdle(_device);

            int width = (int)_swapchainExtent.Width;
            int height = (int)_swapchainExtent.Height;
            ulong size = (ulong)(width * height * 4);

            (Buffer buffer, DeviceMemory memory) = CreateBuffer(
                size,
                BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            try
            {
                var allocInfo = new CommandBufferAllocateInfo
                {
                    SType = StructureType.CommandBufferAllocateInfo,
                    Level = CommandBufferLevel.Primary,
                    CommandPool = _commandPool,
                    CommandBufferCount = 1,
                };
                _vk.AllocateCommandBuffers(_device, in allocInfo, out CommandBuffer cb);

                var beginInfo = new CommandBufferBeginInfo
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                _vk.BeginCommandBuffer(cb, in beginInfo);

                Image image = _swapchainImages[imageIndex];

                TransitionImage(cb, image, ImageLayout.PresentSrcKhr, ImageLayout.TransferSrcOptimal,
                    0, AccessFlags.TransferReadBit,
                    PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit);

                var region = new BufferImageCopy
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = 0,
                        BaseArrayLayer = 0,
                        LayerCount = 1,
                    },
                    ImageOffset = new Offset3D(0, 0, 0),
                    ImageExtent = new Extent3D((uint)width, (uint)height, 1),
                };

                _vk.CmdCopyImageToBuffer(cb, image, ImageLayout.TransferSrcOptimal, buffer, 1, in region);

                TransitionImage(cb, image, ImageLayout.TransferSrcOptimal, ImageLayout.PresentSrcKhr,
                    AccessFlags.TransferReadBit, 0,
                    PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit);

                _vk.EndCommandBuffer(cb);

                var submit = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &cb,
                };
                _vk.QueueSubmit(_graphicsQueue, 1, in submit, default);
                _vk.QueueWaitIdle(_graphicsQueue);
                _vk.FreeCommandBuffers(_device, _commandPool, 1, in cb);

                void* mapped;
                _vk.MapMemory(_device, memory, 0, size, 0, &mapped);

                var pixels = new byte[size];
                new Span<byte>(mapped, (int)size).CopyTo(pixels);
                _vk.UnmapMemory(_device, memory);

                // Swapchains are commonly BGRA. PNG is not, and a screenshot with the red and blue channels
                // exchanged looks enough like a colour-graded frame to be believed.
                if (IsBlueFirst(_swapchainFormat))
                {
                    for (int i = 0; i < pixels.Length; i += 4)
                    {
                        (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
                    }
                }

                // The alpha channel out of a swapchain is whatever the composite left behind and is not
                // meaningful. Forcing it opaque avoids a screenshot that renders as a transparent mess in
                // whatever opens it.
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

                PngWriter.Write(path, pixels, width, height);
                _log.Info("Vulkan", $"Frame captured to {path} ({width}x{height}).");
            }
            finally
            {
                _vk.DestroyBuffer(_device, buffer, null);
                _vk.FreeMemory(_device, memory, null);
            }
        }

        private static bool IsBlueFirst(Format format) => format switch
        {
            Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb or Format.B8G8R8Unorm or Format.B8G8R8Srgb => true,
            _ => false,
        };
    }
}
