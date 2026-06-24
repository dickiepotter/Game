namespace RP.Game.Graphics.Vulkan
{
    using Silk.NET.Vulkan;

    /// <summary>
    /// The depth buffer. Without it, triangles drawn later overwrite earlier ones regardless of distance,
    /// so a 3D shape looks inside-out. The depth buffer records, per pixel, how far away the nearest
    /// surface so far is; a new fragment is kept only if it is closer (the pipeline's depth test).
    /// </summary>
    /// <remarks>
    /// The depth image is sized to the swapchain, so it is created and destroyed alongside it (it must be
    /// rebuilt on resize). With dynamic rendering there is no framebuffer object — the depth image view is
    /// handed to <c>CmdBeginRendering</c> directly each frame.
    /// </remarks>
    public sealed unsafe partial class VulkanRenderer
    {
        private Image _depthImage;
        private DeviceMemory _depthMemory;
        private ImageView _depthView;
        private Format _depthFormat = Format.D32Sfloat; // widely supported, plenty of precision

        private void CreateDepthResources()
        {
            (_depthImage, _depthMemory) = CreateImage(
                _swapchainExtent.Width, _swapchainExtent.Height, _depthFormat,
                ImageUsageFlags.DepthStencilAttachmentBit);

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _depthImage,
                ViewType = ImageViewType.Type2D,
                Format = _depthFormat,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1),
            };

            if (_vk.CreateImageView(_device, in viewInfo, null, out _depthView) != Result.Success)
            {
                throw new VulkanException("vkCreateImageView (depth) failed", Result.ErrorUnknown);
            }
        }

        private void DestroyDepthResources()
        {
            if (_depthView.Handle != 0) _vk.DestroyImageView(_device, _depthView, null);
            if (_depthImage.Handle != 0) _vk.DestroyImage(_device, _depthImage, null);
            if (_depthMemory.Handle != 0) _vk.FreeMemory(_device, _depthMemory, null);
        }
    }
}
