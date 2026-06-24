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
        // The Phase 1 test mesh: a unit cube in device-local vertex + index buffers.
        private Buffer _meshVertexBuffer;
        private DeviceMemory _meshVertexMemory;
        private Buffer _meshIndexBuffer;
        private DeviceMemory _meshIndexMemory;
        private uint _meshIndexCount;

        // Phase 2 instancing + culling. The full grid lives on the CPU (_instanceMaster); each frame we
        // cull it to the camera frustum into _cullScratch and copy the survivors into that frame's own
        // host-visible, persistently-mapped instance buffer (one per frame-in-flight, so the CPU never
        // overwrites data the GPU is still reading).
        private InstanceData[] _instanceMaster = System.Array.Empty<InstanceData>();
        private InstanceData[] _cullScratch = System.Array.Empty<InstanceData>();
        private readonly Buffer[] _dynamicInstanceBuffers = new Buffer[MaxFramesInFlight];
        private readonly DeviceMemory[] _dynamicInstanceMemories = new DeviceMemory[MaxFramesInFlight];
        private readonly nint[] _dynamicInstanceMapped = new nint[MaxFramesInFlight];
        private uint _visibleInstanceCount;
        private bool _loggedCull;

        // The unit cube's circumradius (sqrt(3)/2): the smallest sphere that contains it in any orientation,
        // so it stays a correct bound while the cube spins.
        private const float CubeBoundingRadius = 0.8660254f;

        /// <summary>
        /// Builds a 3D grid of cube instances centred on the origin — the Phase 2 "thousands of instanced
        /// objects" load — and the per-frame buffers the culled survivors are streamed into.
        /// </summary>
        private void CreateInstanceGrid(int countPerAxis = 16, float spacing = 2.0f)
        {
            int n = countPerAxis;
            float half = (n - 1) * spacing * 0.5f;
            var instances = new System.Collections.Generic.List<InstanceData>(n * n * n);

            for (int x = 0; x < n; x++)
            {
                for (int y = 0; y < n; y++)
                {
                    for (int z = 0; z < n; z++)
                    {
                        var offset = new Vector3(x * spacing - half, y * spacing - half, z * spacing - half);
                        var color = new Vector3((float)x / (n - 1), (float)y / (n - 1), (float)z / (n - 1));
                        instances.Add(new InstanceData(offset, color));
                    }
                }
            }

            _instanceMaster = instances.ToArray();
            _cullScratch = new InstanceData[_instanceMaster.Length];

            // One host-visible, persistently-mapped instance buffer per frame in flight.
            ulong capacity = (ulong)(_instanceMaster.Length * sizeof(InstanceData));
            for (int i = 0; i < MaxFramesInFlight; i++)
            {
                (_dynamicInstanceBuffers[i], _dynamicInstanceMemories[i]) = CreateBuffer(
                    capacity,
                    BufferUsageFlags.VertexBufferBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

                void* mapped;
                _vk.MapMemory(_device, _dynamicInstanceMemories[i], 0, capacity, 0, &mapped);
                _dynamicInstanceMapped[i] = (nint)mapped;
            }

            _log.Info("Vulkan", $"Instance grid: {_instanceMaster.Length} cubes, frustum-culled each frame.");
        }

        /// <summary>
        /// Culls the master grid against the camera frustum and copies the survivors into the given
        /// frame's mapped instance buffer. Sets <see cref="_visibleInstanceCount"/> for the draw call.
        /// </summary>
        private void CullAndUploadInstances(int frameIndex)
        {
            Frustum frustum = Camera.Frustum;
            int visible = Scene.FrustumCuller.Cull(frustum, _instanceMaster, CubeBoundingRadius, _cullScratch);
            _visibleInstanceCount = (uint)visible;

            if (visible > 0)
            {
                ulong bytes = (ulong)(visible * sizeof(InstanceData));
                ulong capacity = (ulong)(_instanceMaster.Length * sizeof(InstanceData));
                fixed (InstanceData* src = _cullScratch)
                {
                    System.Buffer.MemoryCopy(src, (void*)_dynamicInstanceMapped[frameIndex], capacity, bytes);
                }
            }

            if (!_loggedCull)
            {
                _log.Info("Vulkan", $"Frustum culling: {visible}/{_instanceMaster.Length} instances visible this frame.");
                _loggedCull = true;
            }
        }

        private void DestroyDynamicInstances()
        {
            for (int i = 0; i < MaxFramesInFlight; i++)
            {
                if (_dynamicInstanceMemories[i].Handle != 0)
                {
                    _vk.UnmapMemory(_device, _dynamicInstanceMemories[i]);
                    _vk.FreeMemory(_device, _dynamicInstanceMemories[i], null);
                }

                if (_dynamicInstanceBuffers[i].Handle != 0)
                {
                    _vk.DestroyBuffer(_device, _dynamicInstanceBuffers[i], null);
                }
            }
        }

        /// <summary>
        /// Builds a unit cube (centred on the origin, ±0.5) with a distinct colour and an outward normal
        /// per face, in device-local vertex and index buffers. Each face is 4 vertices and 2 triangles;
        /// sharing the 8 corners would break per-face normals, so we use 24 vertices (4 per face).
        /// </summary>
        private void CreateCubeMesh()
        {
            // Eight corners of the unit cube.
            Vector3 P(float x, float y, float z) => new Vector3(x, y, z);
            var c000 = P(-0.5f, -0.5f, -0.5f);
            var c100 = P(0.5f, -0.5f, -0.5f);
            var c110 = P(0.5f, 0.5f, -0.5f);
            var c010 = P(-0.5f, 0.5f, -0.5f);
            var c001 = P(-0.5f, -0.5f, 0.5f);
            var c101 = P(0.5f, -0.5f, 0.5f);
            var c111 = P(0.5f, 0.5f, 0.5f);
            var c011 = P(-0.5f, 0.5f, 0.5f);

            // Six faces, each: four corners (CCW from outside) + outward normal + a face colour.
            var vertices = new System.Collections.Generic.List<Vertex>(24);
            void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Vector3 color)
            {
                vertices.Add(new Vertex(a, normal, color));
                vertices.Add(new Vertex(b, normal, color));
                vertices.Add(new Vertex(c, normal, color));
                vertices.Add(new Vertex(d, normal, color));
            }

            Face(c001, c101, c111, c011, P(0, 0, 1), P(0.90f, 0.20f, 0.20f)); // +Z front  red
            Face(c100, c000, c010, c110, P(0, 0, -1), P(0.20f, 0.80f, 0.30f)); // -Z back   green
            Face(c101, c100, c110, c111, P(1, 0, 0), P(0.25f, 0.45f, 0.95f)); // +X right  blue
            Face(c000, c001, c011, c010, P(-1, 0, 0), P(0.95f, 0.80f, 0.25f)); // -X left   yellow
            Face(c011, c111, c110, c010, P(0, 1, 0), P(0.30f, 0.85f, 0.90f)); // +Y top    cyan
            Face(c000, c100, c101, c001, P(0, -1, 0), P(0.90f, 0.35f, 0.85f)); // -Y bottom magenta

            // Two triangles per face (0,1,2) + (0,2,3), offset by 4 per face.
            var indices = new System.Collections.Generic.List<ushort>(36);
            for (ushort face = 0; face < 6; face++)
            {
                ushort b = (ushort)(face * 4);
                indices.Add((ushort)(b + 0));
                indices.Add((ushort)(b + 1));
                indices.Add((ushort)(b + 2));
                indices.Add((ushort)(b + 0));
                indices.Add((ushort)(b + 2));
                indices.Add((ushort)(b + 3));
            }

            _meshIndexCount = (uint)indices.Count;
            (_meshVertexBuffer, _meshVertexMemory) =
                CreateDeviceLocalBuffer<Vertex>(vertices.ToArray(), BufferUsageFlags.VertexBufferBit);
            (_meshIndexBuffer, _meshIndexMemory) =
                CreateDeviceLocalBuffer<ushort>(indices.ToArray(), BufferUsageFlags.IndexBufferBit);
        }

        /// <summary>Creates an image and binds a fresh device-local memory block to it. Used for the depth
        /// buffer (and, later, textures).</summary>
        private (Image Image, DeviceMemory Memory) CreateImage(
            uint width, uint height, Format format, ImageUsageFlags usage)
        {
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Extent = new Extent3D(width, height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Format = format,
                Tiling = ImageTiling.Optimal,
                InitialLayout = ImageLayout.Undefined,
                Usage = usage,
                Samples = SampleCountFlags.Count1Bit,
                SharingMode = SharingMode.Exclusive,
            };

            if (_vk.CreateImage(_device, in imageInfo, null, out Image image) != Result.Success)
            {
                throw new VulkanException("vkCreateImage failed", Result.ErrorUnknown);
            }

            _vk.GetImageMemoryRequirements(_device, image, out MemoryRequirements memReq);
            var allocInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = memReq.Size,
                MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
            };

            if (_vk.AllocateMemory(_device, in allocInfo, null, out DeviceMemory memory) != Result.Success)
            {
                throw new VulkanException("vkAllocateMemory (image) failed", Result.ErrorUnknown);
            }

            _vk.BindImageMemory(_device, image, memory, 0);
            return (image, memory);
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
