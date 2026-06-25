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

        // Capital ships: a separate, larger hull mesh drawn with the same pipeline as a second instanced batch
        // (a handful of structures, so no culling — they are vast and almost always relevant when present).
        private const int MaxCapitals = 16;
        private Buffer _capitalVertexBuffer;
        private DeviceMemory _capitalVertexMemory;
        private Buffer _capitalIndexBuffer;
        private DeviceMemory _capitalIndexMemory;
        private uint _capitalIndexCount;
        private readonly Vector3d[] _capitalWorld = new Vector3d[MaxCapitals];
        private readonly Vector3[] _capitalColor = new Vector3[MaxCapitals];
        private readonly float[] _capitalScale = new float[MaxCapitals];
        private readonly Vector4[] _capitalRotation = new Vector4[MaxCapitals];
        private int _capitalCount;
        private readonly InstanceData[] _capitalRender = new InstanceData[MaxCapitals];
        private readonly Buffer[] _capitalInstanceBuffers = new Buffer[MaxFramesInFlight];
        private readonly DeviceMemory[] _capitalInstanceMemories = new DeviceMemory[MaxFramesInFlight];
        private readonly nint[] _capitalInstanceMapped = new nint[MaxFramesInFlight];
        private uint _capitalVisible;

        // Ship hulls: their own mesh + instanced batch, so the fighters can be a detailed loaded model while
        // debris/tracers/dust stay cheap points on the default mesh. Defaults to the Dart so ships still draw
        // if no model is supplied; SetShipModel swaps in a loaded hull.
        private const int MaxShips = 512;
        private Buffer _shipVertexBuffer;
        private DeviceMemory _shipVertexMemory;
        private Buffer _shipIndexBuffer;
        private DeviceMemory _shipIndexMemory;
        private uint _shipIndexCount;
        private readonly Vector3d[] _shipWorld = new Vector3d[MaxShips];
        private readonly Vector3[] _shipColor = new Vector3[MaxShips];
        private readonly float[] _shipScale = new float[MaxShips];
        private readonly Vector4[] _shipRotation = new Vector4[MaxShips];
        private int _shipCount;
        private readonly InstanceData[] _shipRender = new InstanceData[MaxShips];
        private readonly Buffer[] _shipInstanceBuffers = new Buffer[MaxFramesInFlight];
        private readonly DeviceMemory[] _shipInstanceMemories = new DeviceMemory[MaxFramesInFlight];
        private readonly nint[] _shipInstanceMapped = new nint[MaxFramesInFlight];
        private uint _shipVisible;

        // Phase 2 instancing + culling, now fed from outside via SetInstances. The scene's instances live on
        // the CPU as true (double) world positions + colour + scale; each frame we rebase them to render
        // space, cull to the camera frustum into _cullScratch, and copy the survivors into that frame's own
        // host-visible, persistently-mapped instance buffer (one per frame-in-flight, so the CPU never
        // overwrites data the GPU is still reading).
        private const int MaxInstances = 8192;
        private readonly Vector3d[] _instanceWorld = new Vector3d[MaxInstances]; // true-space positions
        private readonly Vector3[] _instanceColor = new Vector3[MaxInstances];
        private readonly float[] _instanceScale = new float[MaxInstances];
        private readonly Vector4[] _instanceRotation = new Vector4[MaxInstances]; // unit quaternion per instance
        private int _instanceCount;
        private readonly InstanceData[] _renderInstances = new InstanceData[MaxInstances]; // rebased to render space
        private readonly InstanceData[] _cullScratch = new InstanceData[MaxInstances];
        private readonly Buffer[] _dynamicInstanceBuffers = new Buffer[MaxFramesInFlight];
        private readonly DeviceMemory[] _dynamicInstanceMemories = new DeviceMemory[MaxFramesInFlight];
        private readonly nint[] _dynamicInstanceMapped = new nint[MaxFramesInFlight];
        private uint _visibleInstanceCount;
        private bool _loggedCull;

        // The unit cube's circumradius (sqrt(3)/2): the smallest sphere that contains it in any orientation,
        // so it stays a correct bound while the cube spins.
        private const float CubeBoundingRadius = 0.8660254f;

        /// <summary>
        /// Allocates the per-frame, host-visible instance buffers (one per frame-in-flight) the culled
        /// survivors are streamed into. The scene's instances are supplied each frame by <c>SetInstances</c>.
        /// </summary>
        private void CreateInstanceBuffers()
        {
            ulong capacity = (ulong)(MaxInstances * sizeof(InstanceData));
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

            _log.Info("Vulkan", $"Instance buffers ready: capacity {MaxInstances}, frustum-culled each frame.");
        }

        /// <summary>
        /// Replaces the set of instances drawn from next frame: true (double) world positions, per-instance
        /// colours, and uniform scales (so one cube can be a fighter or a capital). Excess past
        /// <see cref="MaxInstances"/> is dropped. This is how the game hands the renderer the live scene —
        /// ships, debris, projectiles — each frame.
        /// </summary>
        public void SetInstances(ReadOnlySpan<Vector3d> worldPositions, ReadOnlySpan<Vector3> colors, ReadOnlySpan<float> scales)
            => SetInstances(worldPositions, colors, scales, default);

        /// <summary>
        /// As the three-argument <c>SetInstances</c>, but each instance also carries an orientation
        /// (unit quaternion). Where <paramref name="rotations"/>
        /// is shorter than the instance list (or empty), the remaining instances use the identity rotation —
        /// so callers that only orient ships can leave debris and tracers un-rotated for free.
        /// </summary>
        public void SetInstances(
            ReadOnlySpan<Vector3d> worldPositions, ReadOnlySpan<Vector3> colors, ReadOnlySpan<float> scales,
            ReadOnlySpan<Vector4> rotations)
        {
            int count = Math.Min(worldPositions.Length, MaxInstances);
            for (int i = 0; i < count; i++)
            {
                _instanceWorld[i] = worldPositions[i];
                _instanceColor[i] = colors[i];
                _instanceScale[i] = scales[i];
                _instanceRotation[i] = i < rotations.Length ? rotations[i] : Vector4.UnitW;
            }

            _instanceCount = count;
        }

        /// <summary>
        /// Rebases the live instances into render space, culls them to the camera frustum, and copies the
        /// survivors into the given frame's mapped instance buffer. Sets <see cref="_visibleInstanceCount"/>
        /// for the draw call.
        /// </summary>
        private void CullAndUploadInstances(int frameIndex)
        {
            // Rebase every instance from true world space into render space (subtract the floating origin),
            // so culling and drawing happen in the small-coordinate space centred on the player.
            float maxScale = 1f;
            for (int i = 0; i < _instanceCount; i++)
            {
                var renderOffset = (Vector3)(_instanceWorld[i] - RenderOrigin);
                _renderInstances[i] = new InstanceData(renderOffset, _instanceColor[i], _instanceScale[i], _instanceRotation[i]);
                if (_instanceScale[i] > maxScale) maxScale = _instanceScale[i];
            }

            Frustum frustum = Camera.Frustum;
            int visible = Scene.FrustumCuller.Cull(
                frustum,
                _renderInstances.AsSpan(0, _instanceCount),
                CubeBoundingRadius * maxScale, // conservative bound so big hulls aren't wrongly culled at the edge
                _cullScratch.AsSpan(0, _instanceCount));
            _visibleInstanceCount = (uint)visible;

            if (visible > 0)
            {
                ulong bytes = (ulong)(visible * sizeof(InstanceData));
                ulong capacity = (ulong)(MaxInstances * sizeof(InstanceData));
                fixed (InstanceData* src = _cullScratch)
                {
                    System.Buffer.MemoryCopy(src, (void*)_dynamicInstanceMapped[frameIndex], capacity, bytes);
                }
            }

            if (!_loggedCull && _instanceCount > 0)
            {
                _log.Info("Vulkan", $"Frustum culling: {visible}/{_instanceCount} instances visible this frame.");
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
        /// Builds the instanced mesh every object is drawn with: a low-poly <see cref="Primitives.Dart"/> hull
        /// (a ship, not a box), uploaded once to device-local vertex + index buffers. Per-instance scale and
        /// colour (set each frame) turn the one mesh into fighters, capitals and debris.
        /// </summary>
        private void CreateCubeMesh()
        {
            Primitives.Mesh mesh = Primitives.Dart();
            _meshIndexCount = (uint)mesh.Indices.Length;
            (_meshVertexBuffer, _meshVertexMemory) =
                CreateDeviceLocalBuffer<Vertex>(mesh.Vertices, BufferUsageFlags.VertexBufferBit);
            (_meshIndexBuffer, _meshIndexMemory) =
                CreateDeviceLocalBuffer<ushort>(mesh.Indices, BufferUsageFlags.IndexBufferBit);
        }

        /// <summary>Uploads the capital-ship hull mesh and allocates its per-frame instance buffers.</summary>
        private void CreateCapitalMesh()
        {
            Primitives.Mesh mesh = Primitives.Carrier();
            _capitalIndexCount = (uint)mesh.Indices.Length;
            (_capitalVertexBuffer, _capitalVertexMemory) =
                CreateDeviceLocalBuffer<Vertex>(mesh.Vertices, BufferUsageFlags.VertexBufferBit);
            (_capitalIndexBuffer, _capitalIndexMemory) =
                CreateDeviceLocalBuffer<ushort>(mesh.Indices, BufferUsageFlags.IndexBufferBit);

            ulong capacity = (ulong)(MaxCapitals * sizeof(InstanceData));
            for (int i = 0; i < MaxFramesInFlight; i++)
            {
                (_capitalInstanceBuffers[i], _capitalInstanceMemories[i]) = CreateBuffer(
                    capacity,
                    BufferUsageFlags.VertexBufferBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
                void* mapped;
                _vk.MapMemory(_device, _capitalInstanceMemories[i], 0, capacity, 0, &mapped);
                _capitalInstanceMapped[i] = (nint)mapped;
            }
        }

        /// <summary>
        /// Supplies the capital ships drawn this frame (true world positions, colour tint, uniform scale and
        /// orientation). Few and large, so they are not culled — every supplied capital is drawn.
        /// </summary>
        public void SetCapitals(
            ReadOnlySpan<Vector3d> worldPositions, ReadOnlySpan<Vector3> colors, ReadOnlySpan<float> scales,
            ReadOnlySpan<Vector4> rotations)
        {
            int count = Math.Min(worldPositions.Length, MaxCapitals);
            for (int i = 0; i < count; i++)
            {
                _capitalWorld[i] = worldPositions[i];
                _capitalColor[i] = colors[i];
                _capitalScale[i] = scales[i];
                _capitalRotation[i] = i < rotations.Length ? rotations[i] : Vector4.UnitW;
            }

            _capitalCount = count;
        }

        // Rebase the capital instances into render space and stream them into this frame's buffer.
        private void UploadCapitals(int frameIndex)
        {
            for (int i = 0; i < _capitalCount; i++)
            {
                var renderOffset = (Vector3)(_capitalWorld[i] - RenderOrigin);
                _capitalRender[i] = new InstanceData(renderOffset, _capitalColor[i], _capitalScale[i], _capitalRotation[i]);
            }

            _capitalVisible = (uint)_capitalCount;
            if (_capitalCount == 0) return;

            ulong bytes = (ulong)(_capitalCount * sizeof(InstanceData));
            ulong capacity = (ulong)(MaxCapitals * sizeof(InstanceData));
            fixed (InstanceData* src = _capitalRender)
            {
                System.Buffer.MemoryCopy(src, (void*)_capitalInstanceMapped[frameIndex], capacity, bytes);
            }
        }

        /// <summary>Creates the ship batch: a default (Dart) hull mesh plus its per-frame instance buffers.</summary>
        private void CreateShipBatch()
        {
            UploadShipMesh(Primitives.Dart());

            ulong capacity = (ulong)(MaxShips * sizeof(InstanceData));
            for (int i = 0; i < MaxFramesInFlight; i++)
            {
                (_shipInstanceBuffers[i], _shipInstanceMemories[i]) = CreateBuffer(
                    capacity,
                    BufferUsageFlags.VertexBufferBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
                void* mapped;
                _vk.MapMemory(_device, _shipInstanceMemories[i], 0, capacity, 0, &mapped);
                _shipInstanceMapped[i] = (nint)mapped;
            }
        }

        private void UploadShipMesh(Primitives.Mesh mesh)
        {
            if (_shipVertexBuffer.Handle != 0) _vk.DestroyBuffer(_device, _shipVertexBuffer, null);
            if (_shipVertexMemory.Handle != 0) _vk.FreeMemory(_device, _shipVertexMemory, null);
            if (_shipIndexBuffer.Handle != 0) _vk.DestroyBuffer(_device, _shipIndexBuffer, null);
            if (_shipIndexMemory.Handle != 0) _vk.FreeMemory(_device, _shipIndexMemory, null);

            _shipIndexCount = (uint)mesh.Indices.Length;
            (_shipVertexBuffer, _shipVertexMemory) =
                CreateDeviceLocalBuffer<Vertex>(mesh.Vertices, BufferUsageFlags.VertexBufferBit);
            (_shipIndexBuffer, _shipIndexMemory) =
                CreateDeviceLocalBuffer<ushort>(mesh.Indices, BufferUsageFlags.IndexBufferBit);
        }

        /// <summary>Replaces the hull mesh the ship batch draws (e.g. a model loaded from disk). Safe to call
        /// after start-up; it waits for the GPU to go idle before swapping the buffers.</summary>
        public void SetShipModel(Vertex[] vertices, ushort[] indices)
        {
            if (vertices is null || indices is null || vertices.Length == 0 || indices.Length == 0) return;
            _vk.DeviceWaitIdle(_device);
            UploadShipMesh(new Primitives.Mesh(vertices, indices));
            _log.Info("Vulkan", $"Ship model set: {vertices.Length} vertices, {indices.Length / 3} triangles.");
        }

        /// <summary>Supplies the ship hulls drawn this frame (true positions, tint, scale, orientation).</summary>
        public void SetShipInstances(
            ReadOnlySpan<Vector3d> worldPositions, ReadOnlySpan<Vector3> colors, ReadOnlySpan<float> scales,
            ReadOnlySpan<Vector4> rotations)
        {
            int count = Math.Min(worldPositions.Length, MaxShips);
            for (int i = 0; i < count; i++)
            {
                _shipWorld[i] = worldPositions[i];
                _shipColor[i] = colors[i];
                _shipScale[i] = scales[i];
                _shipRotation[i] = i < rotations.Length ? rotations[i] : Vector4.UnitW;
            }

            _shipCount = count;
        }

        private void UploadShips(int frameIndex)
        {
            for (int i = 0; i < _shipCount; i++)
            {
                var renderOffset = (Vector3)(_shipWorld[i] - RenderOrigin);
                _shipRender[i] = new InstanceData(renderOffset, _shipColor[i], _shipScale[i], _shipRotation[i]);
            }

            _shipVisible = (uint)_shipCount;
            if (_shipCount == 0) return;

            ulong bytes = (ulong)(_shipCount * sizeof(InstanceData));
            ulong capacity = (ulong)(MaxShips * sizeof(InstanceData));
            fixed (InstanceData* src = _shipRender)
            {
                System.Buffer.MemoryCopy(src, (void*)_shipInstanceMapped[frameIndex], capacity, bytes);
            }
        }

        private void DestroyShipResources()
        {
            for (int i = 0; i < MaxFramesInFlight; i++)
            {
                if (_shipInstanceMemories[i].Handle != 0)
                {
                    _vk.UnmapMemory(_device, _shipInstanceMemories[i]);
                    _vk.FreeMemory(_device, _shipInstanceMemories[i], null);
                }
                if (_shipInstanceBuffers[i].Handle != 0)
                {
                    _vk.DestroyBuffer(_device, _shipInstanceBuffers[i], null);
                }
            }

            if (_shipVertexBuffer.Handle != 0) _vk.DestroyBuffer(_device, _shipVertexBuffer, null);
            if (_shipVertexMemory.Handle != 0) _vk.FreeMemory(_device, _shipVertexMemory, null);
            if (_shipIndexBuffer.Handle != 0) _vk.DestroyBuffer(_device, _shipIndexBuffer, null);
            if (_shipIndexMemory.Handle != 0) _vk.FreeMemory(_device, _shipIndexMemory, null);
        }

        private void DestroyCapitalResources()
        {
            for (int i = 0; i < MaxFramesInFlight; i++)
            {
                if (_capitalInstanceMemories[i].Handle != 0)
                {
                    _vk.UnmapMemory(_device, _capitalInstanceMemories[i]);
                    _vk.FreeMemory(_device, _capitalInstanceMemories[i], null);
                }
                if (_capitalInstanceBuffers[i].Handle != 0)
                {
                    _vk.DestroyBuffer(_device, _capitalInstanceBuffers[i], null);
                }
            }

            if (_capitalVertexBuffer.Handle != 0) _vk.DestroyBuffer(_device, _capitalVertexBuffer, null);
            if (_capitalVertexMemory.Handle != 0) _vk.FreeMemory(_device, _capitalVertexMemory, null);
            if (_capitalIndexBuffer.Handle != 0) _vk.DestroyBuffer(_device, _capitalIndexBuffer, null);
            if (_capitalIndexMemory.Handle != 0) _vk.FreeMemory(_device, _capitalIndexMemory, null);
        }

        /// <summary>Creates an image and binds a fresh device-local memory block to it. Used for the depth
        /// buffer, the HDR/bloom targets, and the MSAA attachments.</summary>
        private (Image Image, DeviceMemory Memory) CreateImage(
            uint width, uint height, Format format, ImageUsageFlags usage,
            SampleCountFlags samples = SampleCountFlags.Count1Bit)
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
                Samples = samples,
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
