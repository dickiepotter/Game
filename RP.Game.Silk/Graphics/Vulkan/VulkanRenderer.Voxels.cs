namespace RP.Game.Graphics.Vulkan
{
    using System;
    using System.Collections.Generic;
    using RP.Game.Rendering;
    using RP.Game.Voxels;
    using RP.Math;
    using Silk.NET.Core.Native;
    using Silk.NET.Vulkan;
    using Buffer = Silk.NET.Vulkan.Buffer;

    /// <summary>
    /// The voxel chunk pass: static chunk meshes uploaded once, frustum-culled, and drawn with a shader
    /// built for a blocky world rather than for spacecraft.
    /// </summary>
    /// <remarks>
    /// <para><b>Why chunks are not instances.</b> The existing mesh pass draws many copies of one hull. A
    /// voxel world is the opposite shape of problem: every chunk is a unique mesh, drawn once. So this pass
    /// keeps one vertex+index buffer per chunk and issues one draw per visible chunk, with no instance
    /// buffer at all.</para>
    ///
    /// <para><b>Chunk-local coordinates.</b> Vertices are stored in the chunk's own space (0..32), and the
    /// chunk's position relative to the render origin is a per-draw push constant. That is not a
    /// micro-optimisation: it means the floating origin can be rebased at any moment without re-uploading a
    /// single chunk, and it keeps vertex coordinates small enough that single-precision never loses
    /// resolution however far the player walks from the world origin.</para>
    ///
    /// <para><b>Deferred destruction.</b> A chunk that leaves the view cannot have its buffer destroyed on
    /// the spot, because frames already submitted may still be reading it. Freed slots go onto a pending
    /// list and are actually destroyed once <see cref="MaxFramesInFlight"/> frames have passed, which is the
    /// point at which no in-flight frame can still reference them. Skipping this is the classic
    /// use-after-free in a streaming renderer, and it presents as a random device-lost crash minutes into a
    /// session rather than as anything reproducible.</para>
    ///
    /// <para><b>The known limit.</b> Each chunk takes one buffer and one memory allocation. Vulkan drivers
    /// cap total allocations (commonly around 4,096), so the slot count is capped well below that. The real
    /// fix is sub-allocating chunks from a few large blocks, the same upgrade the rest of the buffer code
    /// wants; this is the honest stepping stone, and the cap is enforced rather than left to be discovered
    /// as an allocation failure.</para>
    /// </remarks>
    public sealed unsafe partial class VulkanRenderer
    {
        /// <summary>How many chunk meshes may be resident at once.</summary>
        /// <remarks>
        /// Each resident mesh costs one buffer and one device allocation, and drivers commonly cap total
        /// allocations at around four thousand — so this is bounded by allocation count rather than by
        /// memory or by draw calls. Raising it much further wants the sub-allocator the buffer code has
        /// always flagged; 1,600 covers a nine-chunk view distance with room to spare.
        /// </remarks>
        public const int MaxChunkMeshes = 1600;

        private struct ChunkSlot
        {
            public Buffer Buffer;
            public DeviceMemory Memory;
            public ulong IndexOffset;
            public uint IndexCount;
            public Vector3 Origin;       // chunk origin in *world* blocks, rebased at draw time
            public bool InUse;
        }

        private struct PendingFree
        {
            public Buffer Buffer;
            public DeviceMemory Memory;
            public int FramesRemaining;
        }

        private readonly ChunkSlot[] _chunkSlots = new ChunkSlot[MaxChunkMeshes];
        private readonly Dictionary<ChunkPos, int> _chunkSlotByPosition = new Dictionary<ChunkPos, int>();
        private readonly Stack<int> _freeChunkSlots = new Stack<int>();
        private readonly List<PendingFree> _pendingFrees = new List<PendingFree>();

        /// <summary>A staging buffer waiting to be copied into its chunk's device-local buffer.</summary>
        private struct PendingUpload
        {
            public Buffer Staging;
            public DeviceMemory StagingMemory;
            public Buffer Destination;
            public ulong Size;
        }

        private readonly List<PendingUpload> _pendingUploads = new List<PendingUpload>();

        private Pipeline _voxelPipeline;
        private PipelineLayout _voxelPipelineLayout;

        private double _timeOfDay = 0.35;

        /// <summary>How many chunk meshes are currently resident.</summary>
        public int ChunkMeshCount => _chunkSlotByPosition.Count;

        /// <summary>How many chunk meshes survived frustum culling on the last frame.</summary>
        public int ChunkMeshesDrawn { get; private set; }

        /// <summary>How many triangles the voxel pass drew on the last frame.</summary>
        public long ChunkTrianglesDrawn { get; private set; }

        /// <summary>
        /// Time of day in <c>[0, 1)</c>: 0 is midnight, 0.25 sunrise, 0.5 noon, 0.75 sunset.
        /// </summary>
        /// <remarks>
        /// Setting this also moves <see cref="SunDirection"/>, so the sky pass, the hull pass and the voxel
        /// pass all agree on where the light is coming from. Three passes each deciding independently where
        /// the sun is, is how a scene ends up with shadows pointing one way and a sun disc in the other.
        /// </remarks>
        public double TimeOfDay
        {
            get => _timeOfDay;
            set
            {
                _timeOfDay = value - System.Math.Floor(value);

                // The sun rises in the east, crosses the south, and sets in the west. Angle measured so
                // that noon puts it overhead and midnight directly below.
                double angle = (_timeOfDay - 0.25) * 2.0 * System.Math.PI;
                var direction = new Vector3(
                    (float)System.Math.Cos(angle),
                    (float)System.Math.Sin(angle),
                    (float)(0.28 * System.Math.Cos(angle * 0.5)));

                SunDirection = direction.Normalize();
                SunColor = SunColorFor(Daylight);
            }
        }

        /// <summary>
        /// How much the sun is contributing right now, in <c>[0, 1]</c>. Derived from
        /// <see cref="TimeOfDay"/>: full through the day, zero at night, with a soft twilight either side.
        /// </summary>
        /// <remarks>
        /// The twilight ramp is deliberately wide. A sun that switches off at the horizon makes night fall
        /// like a light switch, which is both ugly and — because block light suddenly has to carry the whole
        /// scene — a visible brightness pop.
        /// </remarks>
        public float Daylight
        {
            get
            {
                // Height of the sun above the horizon, as a signed value in [-1, 1].
                double elevation = System.Math.Sin((_timeOfDay - 0.25) * 2.0 * System.Math.PI);

                // Smoothstep across the twilight band rather than clamping at zero.
                double t = (elevation + 0.18) / 0.36;
                t = t < 0.0 ? 0.0 : (t > 1.0 ? 1.0 : t);
                return (float)(t * t * (3.0 - (2.0 * t)));
            }
        }

        /// <summary>How thick the distance haze is. Larger values close the world in.</summary>
        public float FogDensity { get; set; } = 0.0042f;

        /// <summary>How far from the camera fog begins, in blocks.</summary>
        public float FogStart { get; set; } = 40f;

        /// <summary>
        /// Uploads (or replaces) the mesh for one chunk. Returns false only if every slot is taken.
        /// </summary>
        /// <remarks>
        /// An empty mesh is not an error and not a waste: it is the normal result for a chunk of solid rock
        /// or open sky, and it releases the slot rather than holding a zero-triangle buffer.
        /// </remarks>
        public bool SetChunkMesh(ChunkPos position, VoxelMeshData mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));

            if (mesh.IsEmpty)
            {
                RemoveChunkMesh(position);
                return true;
            }

            // Replacing an existing mesh frees the old slot first, so a chunk being re-meshed repeatedly
            // (a player mining) cannot leak slots.
            RemoveChunkMesh(position);

            if (_freeChunkSlots.Count == 0) return false;
            int slot = _freeChunkSlots.Pop();

            VoxelVertex[] vertices = mesh.Vertices.ToArray();
            uint[] indices = mesh.Indices.ToArray();

            ulong vertexBytes = (ulong)(sizeof(VoxelVertex) * vertices.Length);
            ulong indexBytes = (ulong)(sizeof(uint) * indices.Length);

            // Vertices then indices in one allocation. VoxelVertex is a multiple of four bytes wide, so the
            // index region is already correctly aligned for uint32 reads.
            ulong total = vertexBytes + indexBytes;

            var (staging, stagingMemory) = CreateBuffer(
                total,
                BufferUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            void* mapped;
            _vk.MapMemory(_device, stagingMemory, 0, total, 0, &mapped);
            fixed (VoxelVertex* src = vertices)
            {
                System.Buffer.MemoryCopy(src, mapped, total, vertexBytes);
            }

            fixed (uint* src = indices)
            {
                System.Buffer.MemoryCopy(src, (byte*)mapped + vertexBytes, total - vertexBytes, indexBytes);
            }

            _vk.UnmapMemory(_device, stagingMemory);

            var (buffer, memory) = CreateBuffer(
                total,
                BufferUsageFlags.TransferDstBit | BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit,
                MemoryPropertyFlags.DeviceLocalBit);

            // Queued, not copied. Performing the copy here means a command buffer, a submit and a full
            // queue drain *per chunk* -- and the streamer uploads several chunks a frame, so that is
            // several complete GPU stalls every frame, which is exactly the stutter it was trying to avoid.
            // FlushChunkUploads does them all in one submit at the top of the frame instead.
            _pendingUploads.Add(new PendingUpload
            {
                Staging = staging,
                StagingMemory = stagingMemory,
                Destination = buffer,
                Size = total,
            });

            BlockPos origin = position.Origin();
            _chunkSlots[slot] = new ChunkSlot
            {
                Buffer = buffer,
                Memory = memory,
                IndexOffset = vertexBytes,
                IndexCount = (uint)indices.Length,
                Origin = new Vector3(origin.X, origin.Y, origin.Z),
                InUse = true,
            };

            _chunkSlotByPosition[position] = slot;
            return true;
        }

        /// <summary>Drops a chunk's mesh, deferring the actual buffer destruction until no frame in flight
        /// can still be reading it.</summary>
        public void RemoveChunkMesh(ChunkPos position)
        {
            if (!_chunkSlotByPosition.TryGetValue(position, out int slot)) return;

            ref ChunkSlot s = ref _chunkSlots[slot];
            _pendingFrees.Add(new PendingFree
            {
                Buffer = s.Buffer,
                Memory = s.Memory,
                FramesRemaining = MaxFramesInFlight + 1,
            });

            s = default;
            _chunkSlotByPosition.Remove(position);
            _freeChunkSlots.Push(slot);
        }

        /// <summary>Drops every chunk mesh — for a world unload or a teleport across the map.</summary>
        public void ClearChunkMeshes()
        {
            var positions = new ChunkPos[_chunkSlotByPosition.Count];
            _chunkSlotByPosition.Keys.CopyTo(positions, 0);
            foreach (ChunkPos position in positions) RemoveChunkMesh(position);
        }

        /// <summary>Whether a chunk currently has a resident mesh.</summary>
        public bool HasChunkMesh(ChunkPos position) => _chunkSlotByPosition.ContainsKey(position);

        /// <summary>
        /// Performs every queued chunk upload in one command buffer, one submit and one wait.
        /// </summary>
        /// <remarks>
        /// The cost of a staging upload is dominated by the round trip, not by the bytes: submitting and
        /// waiting costs the same whether it moves one chunk or forty. Batching turns a per-chunk stall
        /// into one per frame, and a frame that uploads nothing pays nothing at all.
        /// </remarks>
        private void FlushChunkUploads()
        {
            if (_pendingUploads.Count == 0) return;

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

            for (int i = 0; i < _pendingUploads.Count; i++)
            {
                PendingUpload upload = _pendingUploads[i];
                var copy = new BufferCopy { Size = upload.Size };
                _vk.CmdCopyBuffer(cb, upload.Staging, upload.Destination, 1, in copy);
            }

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

            // The staging buffers have served their purpose the moment the copy has completed, and the wait
            // above guarantees that.
            for (int i = 0; i < _pendingUploads.Count; i++)
            {
                PendingUpload upload = _pendingUploads[i];
                _vk.DestroyBuffer(_device, upload.Staging, null);
                _vk.FreeMemory(_device, upload.StagingMemory, null);
            }

            _pendingUploads.Clear();
        }

        /// <summary>Sets up the slot free-list. Called once at construction.</summary>
        private void CreateChunkSlots()
        {
            // Pushed in reverse so the first mesh uploaded takes slot 0, which makes a debugger session
            // over the slot array read in the order chunks arrived.
            for (int i = MaxChunkMeshes - 1; i >= 0; i--) _freeChunkSlots.Push(i);
        }

        /// <summary>
        /// Destroys buffers whose deferred-free countdown has expired. Called once per frame, before any
        /// new work is recorded.
        /// </summary>
        private void DrainPendingFrees()
        {
            for (int i = _pendingFrees.Count - 1; i >= 0; i--)
            {
                PendingFree pending = _pendingFrees[i];
                if (--pending.FramesRemaining > 0)
                {
                    _pendingFrees[i] = pending;
                    continue;
                }

                if (pending.Buffer.Handle != 0) _vk.DestroyBuffer(_device, pending.Buffer, null);
                if (pending.Memory.Handle != 0) _vk.FreeMemory(_device, pending.Memory, null);
                _pendingFrees.RemoveAt(i);
            }
        }

        /// <summary>
        /// The voxel pipeline. Differs from the hull pipeline in three ways that matter: back-face culling
        /// is <b>on</b> (a voxel world is closed, so every back face is genuinely hidden and skipping them
        /// halves the fill rate), the vertex layout carries baked lighting rather than instance transforms,
        /// and the push block reserves its last 16 bytes for per-chunk data.
        /// </summary>
        private void CreateVoxelPipeline()
        {
            ShaderModule vertModule = CreateShaderModule("voxel.vert.spv");
            ShaderModule fragModule = CreateShaderModule("voxel.frag.spv");

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

            // One binding, stepping per vertex. The layout must mirror RP.Game.Rendering.VoxelVertex field
            // for field: position, normal, texcoord, material, ao, sky light, block light.
            var binding = new VertexInputBindingDescription
            {
                Binding = 0,
                Stride = (uint)sizeof(VoxelVertex),
                InputRate = VertexInputRate.Vertex,
            };

            uint vec3Size = (uint)sizeof(Vector3);
            uint vec2Size = (uint)sizeof(Vector2);
            uint offsetNormal = vec3Size;
            uint offsetTexCoord = offsetNormal + vec3Size;
            uint offsetMaterial = offsetTexCoord + vec2Size;
            uint offsetAo = offsetMaterial + sizeof(uint);
            uint offsetSky = offsetAo + sizeof(float);
            uint offsetBlock = offsetSky + sizeof(float);

            var attributes = stackalloc VertexInputAttributeDescription[7];
            attributes[0] = new VertexInputAttributeDescription
            { Binding = 0, Location = 0, Format = Format.R32G32B32Sfloat, Offset = 0 };
            attributes[1] = new VertexInputAttributeDescription
            { Binding = 0, Location = 1, Format = Format.R32G32B32Sfloat, Offset = offsetNormal };
            attributes[2] = new VertexInputAttributeDescription
            { Binding = 0, Location = 2, Format = Format.R32G32Sfloat, Offset = offsetTexCoord };
            attributes[3] = new VertexInputAttributeDescription
            { Binding = 0, Location = 3, Format = Format.R32Uint, Offset = offsetMaterial };
            attributes[4] = new VertexInputAttributeDescription
            { Binding = 0, Location = 4, Format = Format.R32Sfloat, Offset = offsetAo };
            attributes[5] = new VertexInputAttributeDescription
            { Binding = 0, Location = 5, Format = Format.R32Sfloat, Offset = offsetSky };
            attributes[6] = new VertexInputAttributeDescription
            { Binding = 0, Location = 6, Format = Format.R32Sfloat, Offset = offsetBlock };

            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 7,
                PVertexAttributeDescriptions = attributes,
            };

            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
                PrimitiveRestartEnable = false,
            };

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

                // Unlike the hull pass, culling is on. The mesher emits counter-clockwise faces (asserted by
                // test against the face normals), a voxel world is closed, and back faces are therefore
                // always hidden — so this is free fill rate rather than a risk.
                CullMode = CullModeFlags.BackBit,
                FrontFace = FrontFace.CounterClockwise,
                DepthBiasEnable = false,
            };

            var multisampling = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                SampleShadingEnable = false,
                RasterizationSamples = _msaaSamples,
            };

            var depthStencil = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = true,
                DepthWriteEnable = true,
                DepthCompareOp = CompareOp.Less,
                DepthBoundsTestEnable = false,
                StencilTestEnable = false,
            };

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

            // The full 128 bytes, which is the guaranteed minimum every Vulkan implementation offers:
            // 112 bytes of per-frame data and a final vec4 of per-chunk data, updated between draws.
            var pushRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                Offset = 0,
                Size = 128,
            };

            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 0,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushRange,
            };

            if (_vk.CreatePipelineLayout(_device, in layoutInfo, null, out _voxelPipelineLayout) != Result.Success)
            {
                throw new VulkanException("vkCreatePipelineLayout (voxel) failed", Result.ErrorUnknown);
            }

            Format colorFormat = HdrFormat;
            Format depthFormat = _depthFormat;
            var renderingCreateInfo = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = 1,
                PColorAttachmentFormats = &colorFormat,
                DepthAttachmentFormat = depthFormat,
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
                PDepthStencilState = &depthStencil,
                PColorBlendState = &colorBlending,
                PDynamicState = &dynamicState,
                Layout = _voxelPipelineLayout,
            };

            Result result = _vk.CreateGraphicsPipelines(_device, default, 1, in pipelineInfo, null, out _voxelPipeline);

            SilkMarshal.Free((nint)entryPoint);
            _vk.DestroyShaderModule(_device, fragModule, null);
            _vk.DestroyShaderModule(_device, vertModule, null);

            if (result != Result.Success)
            {
                throw new VulkanException("vkCreateGraphicsPipelines (voxel) failed", result);
            }

            _log.Info("Vulkan", $"Voxel pipeline ready: up to {MaxChunkMeshes} chunk meshes, back-face culled.");
        }

        /// <summary>Records the voxel pass: one draw per visible chunk.</summary>
        private void RecordVoxels(CommandBuffer cb)
        {
            ChunkMeshesDrawn = 0;
            ChunkTrianglesDrawn = 0;

            if (_voxelPipeline.Handle == 0 || _chunkSlotByPosition.Count == 0) return;

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

            _vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _voxelPipeline);

            // ---- Per-frame push block (the first 112 bytes) ----
            var push = stackalloc float[32];
            Camera.ToColumnMajorFloats(Camera.ViewProjection, new Span<float>(push, 16));

            Vector3d camera = Camera.Position;
            push[16] = (float)camera.X;
            push[17] = (float)camera.Y;
            push[18] = (float)camera.Z;
            push[19] = FogDensity;

            Vector3 sun = SunDirection;
            push[20] = sun.X;
            push[21] = sun.Y;
            push[22] = sun.Z;
            push[23] = Daylight;

            Vector3 sunColor = SunColor;
            push[24] = sunColor.X;
            push[25] = sunColor.Y;
            push[26] = sunColor.Z;
            push[27] = FogStart;

            _vk.CmdPushConstants(
                cb, _voxelPipelineLayout,
                ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                0, 28 * sizeof(float), push);

            // ---- Per-chunk: cull, push the offset, draw ----
            Frustum frustum = Camera.Frustum;
            var chunkPush = stackalloc float[4];

            foreach (KeyValuePair<ChunkPos, int> entry in _chunkSlotByPosition)
            {
                ref ChunkSlot slot = ref _chunkSlots[entry.Value];
                if (!slot.InUse || slot.IndexCount == 0) continue;

                // Rebase the chunk into render space. Doing this here rather than at upload time is what
                // lets the floating origin move without touching a single vertex buffer.
                double ox = slot.Origin.X - RenderOrigin.X;
                double oy = slot.Origin.Y - RenderOrigin.Y;
                double oz = slot.Origin.Z - RenderOrigin.Z;

                var min = new Vector3d(ox, oy, oz);
                var max = new Vector3d(ox + VoxelChunk.Size, oy + VoxelChunk.Size, oz + VoxelChunk.Size);
                if (!frustum.Intersects(BoundingBox.FromMinMax(min, max))) continue;

                chunkPush[0] = (float)ox;
                chunkPush[1] = (float)oy;
                chunkPush[2] = (float)oz;
                // The spare slot in the per-chunk constant carries the detail level, so the fragment shader
                // can skip its noise entirely on hardware that asked for less.
                chunkPush[3] = Capabilities.SurfaceDetail;

                _vk.CmdPushConstants(
                    cb, _voxelPipelineLayout,
                    ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                    112, 4 * sizeof(float), chunkPush);

                Buffer vertexBuffer = slot.Buffer;
                ulong vertexOffset = 0;
                _vk.CmdBindVertexBuffers(cb, 0, 1, in vertexBuffer, in vertexOffset);
                _vk.CmdBindIndexBuffer(cb, slot.Buffer, slot.IndexOffset, IndexType.Uint32);
                _vk.CmdDrawIndexed(cb, slot.IndexCount, 1, 0, 0, 0);

                ChunkMeshesDrawn++;
                ChunkTrianglesDrawn += slot.IndexCount / 3;
            }
        }

        /// <summary>Destroys the voxel pipeline and every chunk buffer. Called from <see cref="Dispose"/>,
        /// after the device is idle, so the deferred-free list can be flushed immediately.</summary>
        private void DestroyVoxelResources()
        {
            for (int i = 0; i < _chunkSlots.Length; i++)
            {
                ref ChunkSlot slot = ref _chunkSlots[i];
                if (!slot.InUse) continue;
                if (slot.Buffer.Handle != 0) _vk.DestroyBuffer(_device, slot.Buffer, null);
                if (slot.Memory.Handle != 0) _vk.FreeMemory(_device, slot.Memory, null);
                slot = default;
            }

            foreach (PendingUpload upload in _pendingUploads)
            {
                if (upload.Staging.Handle != 0) _vk.DestroyBuffer(_device, upload.Staging, null);
                if (upload.StagingMemory.Handle != 0) _vk.FreeMemory(_device, upload.StagingMemory, null);
            }

            _pendingUploads.Clear();

            foreach (PendingFree pending in _pendingFrees)
            {
                if (pending.Buffer.Handle != 0) _vk.DestroyBuffer(_device, pending.Buffer, null);
                if (pending.Memory.Handle != 0) _vk.FreeMemory(_device, pending.Memory, null);
            }

            _pendingFrees.Clear();
            _chunkSlotByPosition.Clear();

            if (_voxelPipeline.Handle != 0) _vk.DestroyPipeline(_device, _voxelPipeline, null);
            if (_voxelPipelineLayout.Handle != 0) _vk.DestroyPipelineLayout(_device, _voxelPipelineLayout, null);
        }

        /// <summary>
        /// The sun's colour at a given daylight strength: warm and reddened near the horizon, white at
        /// noon. A constant sun colour is the difference between a sky that has a time of day and one that
        /// merely gets brighter.
        /// </summary>
        private static Vector3 SunColorFor(float daylight)
        {
            var horizon = new Vector3(1.0f, 0.45f, 0.18f);
            var noon = new Vector3(1.0f, 0.97f, 0.92f);
            float t = daylight * daylight;
            return new Vector3(
                horizon.X + ((noon.X - horizon.X) * t),
                horizon.Y + ((noon.Y - horizon.Y) * t),
                horizon.Z + ((noon.Z - horizon.Z) * t));
        }
    }
}
