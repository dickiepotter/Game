namespace RP.Game.Graphics.Vulkan
{
    using System;
    using RP.Game.Rendering;
    using RP.Math;
    using Silk.NET.Vulkan;
    using Buffer = Silk.NET.Vulkan.Buffer;

    /// <summary>
    /// GPU buffer creation and uploading. Split out because allocating and filling device memory is a
    /// concern of its own, and the cube/mesh work all rests on it.
    /// </summary>
    /// <remarks>
    /// <para><b>The two-buffer "staging" upload.</b> The fastest memory for the GPU to read (DEVICE_LOCAL)
    /// is usually <i>not</i> visible to the CPU, so we cannot write our vertices into it directly. The
    /// standard move: create a small CPU-visible <i>staging</i> buffer, copy the data in, then ask the GPU
    /// to copy staging → a DEVICE_LOCAL buffer, and throw the staging buffer away. The mesh then lives in
    /// the fast memory the shader reads every frame.</para>
    ///
    /// <para><b>On allocation strategy.</b> This allocates one <see cref="DeviceMemory"/> block per buffer.
    /// That is the simplest correct thing and fine for the handful of buffers in Phase 1, but it does not
    /// scale — real Vulkan apps sub-allocate many resources from a few large blocks (the brief calls for a
    /// block sub-allocator / VMA). That upgrade lands in Phase 2 ("renderer at scale"); this is the honest
    /// stepping stone, flagged so it is easy to find and replace.</para>
    /// </remarks>
    public sealed unsafe partial class VulkanRenderer
    {
        // The Phase 1 test triangle, now living in a real device-local vertex buffer.
        private Buffer _triangleVertexBuffer;
        private DeviceMemory _triangleVertexMemory;

        /// <summary>
        /// Builds the test triangle's vertex buffer: three corners, each with a position (in normalised
        /// device coordinates for now) and a colour the rasteriser blends across the face.
        /// </summary>
        private void CreateTriangleMesh()
        {
            var vertices = new[]
            {
                new Vertex(new Vector3(0.0f, -0.5f, 0.0f), new Vector3(1f, 0f, 0f)),
                new Vertex(new Vector3(0.5f, 0.5f, 0.0f), new Vector3(0f, 1f, 0f)),
                new Vertex(new Vector3(-0.5f, 0.5f, 0.0f), new Vector3(0f, 0f, 1f)),
            };

            (_triangleVertexBuffer, _triangleVertexMemory) =
                CreateDeviceLocalBuffer<Vertex>(vertices, BufferUsageFlags.VertexBufferBit);
        }

        /// <summary>
        /// Finds a memory type the device offers that satisfies both the resource's allowed types
        /// (<paramref name="typeFilter"/>, a bitmask) and the properties we need (e.g. DEVICE_LOCAL, or
        /// HOST_VISIBLE | HOST_COHERENT so CPU writes are seen without manual flushing).
        /// </summary>
        private uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
        {
            _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out PhysicalDeviceMemoryProperties memProps);
            for (uint i = 0; i < memProps.MemoryTypeCount; i++)
            {
                bool typeAllowed = (typeFilter & (1u << (int)i)) != 0;
                bool hasProps = (memProps.MemoryTypes[(int)i].PropertyFlags & properties) == properties;
                if (typeAllowed && hasProps) return i;
            }

            throw new VulkanException("No suitable GPU memory type for the requested properties", Result.ErrorUnknown);
        }

        /// <summary>Creates a raw buffer and binds a fresh memory block of the requested properties to it.</summary>
        private (Buffer Buffer, DeviceMemory Memory) CreateBuffer(
            ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties)
        {
            var bufferInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = size,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
            };

            if (_vk.CreateBuffer(_device, in bufferInfo, null, out Buffer buffer) != Result.Success)
            {
                throw new VulkanException("vkCreateBuffer failed", Result.ErrorUnknown);
            }

            _vk.GetBufferMemoryRequirements(_device, buffer, out MemoryRequirements memReq);
            var allocInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = memReq.Size,
                MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits, properties),
            };

            if (_vk.AllocateMemory(_device, in allocInfo, null, out DeviceMemory memory) != Result.Success)
            {
                throw new VulkanException("vkAllocateMemory failed", Result.ErrorUnknown);
            }

            _vk.BindBufferMemory(_device, buffer, memory, 0);
            return (buffer, memory);
        }

        /// <summary>
        /// Uploads an array of unmanaged values into a new DEVICE_LOCAL buffer via a staging copy, and
        /// returns the device-local buffer + its memory. Use for data the GPU reads but the CPU writes
        /// once (vertex/index buffers).
        /// </summary>
        private (Buffer Buffer, DeviceMemory Memory) CreateDeviceLocalBuffer<T>(
            ReadOnlySpan<T> data, BufferUsageFlags usage) where T : unmanaged
        {
            ulong size = (ulong)(sizeof(T) * data.Length);

            // 1. Staging buffer: CPU-visible, coherent (no manual flush), usable as a copy source.
            var (staging, stagingMem) = CreateBuffer(
                size,
                BufferUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            // 2. Map it and copy the bytes in.
            void* mapped;
            _vk.MapMemory(_device, stagingMem, 0, size, 0, &mapped);
            fixed (T* src = data)
            {
                System.Buffer.MemoryCopy(src, mapped, size, size);
            }
            _vk.UnmapMemory(_device, stagingMem);

            // 3. Device-local destination: fast for the GPU, also a copy destination + the real usage.
            var (buffer, memory) = CreateBuffer(
                size,
                BufferUsageFlags.TransferDstBit | usage,
                MemoryPropertyFlags.DeviceLocalBit);

            // 4. GPU-side copy staging -> device-local, then discard staging.
            CopyBuffer(staging, buffer, size);
            _vk.DestroyBuffer(_device, staging, null);
            _vk.FreeMemory(_device, stagingMem, null);

            return (buffer, memory);
        }

        /// <summary>
        /// Records and submits a one-off command buffer that copies <paramref name="size"/> bytes from one
        /// buffer to another, then waits for it. Simple and synchronous — fine for load-time uploads.
        /// </summary>
        private void CopyBuffer(Buffer source, Buffer destination, ulong size)
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

            var copy = new BufferCopy { Size = size };
            _vk.CmdCopyBuffer(cb, source, destination, 1, in copy);

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
        }
    }
}
