namespace RP.Game.Graphics.Vulkan
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using RP.Game.Core.Logging;
    using RP.Game.Rendering;
    using RP.Math;
    using Silk.NET.Core;
    using Silk.NET.Core.Native;
    using Silk.NET.Vulkan;
    using Silk.NET.Vulkan.Extensions.EXT;
    using Silk.NET.Vulkan.Extensions.KHR;
    using Silk.NET.Windowing;

    /// <summary>
    /// A from-scratch Vulkan 1.3 renderer. At Phase 0 it does one thing — clear the screen to a colour
    /// every frame — but it stands up the entire Vulkan spine needed for everything after: instance,
    /// validation, device and queues, swapchain, command buffers, and multi-frame synchronisation.
    /// </summary>
    /// <remarks>
    /// <para><b>Why Vulkan is so much code for a clear.</b> Older APIs hid the GPU behind a fat driver.
    /// Vulkan instead exposes the machine almost directly: you choose a physical GPU, create a logical
    /// connection to it, allocate a chain of images to present, record command buffers, and orchestrate
    /// the CPU/GPU hand-off yourself. The pay-off is total control over the parallelism a huge space
    /// battle needs; the cost is this bootstrap. Each region below is one rung of that ladder, commented
    /// as a lesson.</para>
    ///
    /// <para><b>The objects, top to bottom.</b>
    /// <list type="number">
    ///   <item><description><b>Instance</b> — the per-application Vulkan connection; carries validation.</description></item>
    ///   <item><description><b>Surface</b> — the bridge between Vulkan and our OS window.</description></item>
    ///   <item><description><b>Physical device</b> — a specific GPU we picked.</description></item>
    ///   <item><description><b>Logical device + queues</b> — our private channel to that GPU and the
    ///     lanes (graphics, present) we submit work on.</description></item>
    ///   <item><description><b>Swapchain</b> — the rotating set of images the screen shows.</description></item>
    ///   <item><description><b>Command buffers + sync</b> — recorded GPU work and the fences/semaphores
    ///     that keep CPU and GPU from tripping over each other.</description></item>
    /// </list></para>
    ///
    /// <para>This single file keeps the whole spine together while it is small enough to read end-to-end;
    /// as rendering grows (Phase 1+) the swapchain, device and sync split into their own files.</para>
    /// </remarks>
    public sealed unsafe partial class VulkanRenderer : IRenderer
    {
        // How many frames the CPU may be recording ahead of the GPU. Two ("double buffering") lets the
        // CPU build frame N+1 while the GPU still draws frame N, without them sharing mutable state.
        private const int MaxFramesInFlight = 2;

        private const string ValidationLayerName = "VK_LAYER_KHRONOS_validation";

        private readonly IWindow _window;
        private readonly Logger _log;
        private readonly bool _enableValidation;

        private readonly Vk _vk;

        // ---- Instance + validation ----
        private Instance _instance;
        private ExtDebugUtils? _debugUtils;
        private DebugUtilsMessengerEXT _debugMessenger;
        private PfnDebugUtilsMessengerCallbackEXT _debugCallback; // kept alive so the GC won't collect it

        // ---- Surface ----
        private KhrSurface _khrSurface = null!;
        private SurfaceKHR _surface;

        // ---- Devices + queues ----
        private PhysicalDevice _physicalDevice;
        private Device _device;
        private uint _graphicsFamily;
        private uint _presentFamily;
        private Queue _graphicsQueue;
        private Queue _presentQueue;

        // ---- Swapchain ----
        private KhrSwapchain _khrSwapchain = null!;
        private SwapchainKHR _swapchain;
        private Image[] _swapchainImages = Array.Empty<Image>();
        private ImageView[] _swapchainImageViews = Array.Empty<ImageView>();
        private Format _swapchainFormat;
        private Extent2D _swapchainExtent;

        // ---- Per-frame command + sync objects ----
        private CommandPool _commandPool;
        private CommandBuffer[] _commandBuffers = Array.Empty<CommandBuffer>();
        private Semaphore[] _imageAvailableSemaphores = Array.Empty<Semaphore>();
        private Semaphore[] _renderFinishedSemaphores = Array.Empty<Semaphore>();
        private Fence[] _inFlightFences = Array.Empty<Fence>();
        private int _currentFrame;

        private bool _framebufferResized;
        private bool _disposed;

        /// <inheritdoc />
        public (float R, float G, float B, float A) ClearColor { get; set; } = (0.02f, 0.02f, 0.04f, 1f);

        /// <summary>The camera used to view the scene. Position it from game code; its aspect ratio is kept
        /// in step with the swapchain automatically.</summary>
        public Camera Camera { get; } = new Camera();

        /// <summary>The model transform applied to the test cube (world placement/orientation). Game code
        /// sets this each frame to move or spin the cube.</summary>
        public Matrix ModelTransform { get; set; } = Matrix.Identity;

        /// <summary>
        /// The floating-origin offset: instances are authored in true (double) world space, and each frame
        /// the renderer subtracts this origin so everything is drawn relative to the player. Keeping the
        /// origin near the player keeps render coordinates small and jitter-free at any world distance.
        /// </summary>
        public Vector3d RenderOrigin { get; set; } = Vector3d.Origin;

        /// <summary>
        /// Brings the entire Vulkan stack up against an already-initialised window.
        /// </summary>
        /// <param name="window">An initialised Vulkan window (see <see cref="Platform.VulkanWindow"/>).</param>
        /// <param name="log">Where Vulkan validation and lifecycle messages go.</param>
        /// <param name="enableValidation">Turn the validation layers on. Do this in debug builds; the
        /// brief makes it non-negotiable when no human is reviewing mid-build (S4.2). Strip for release.</param>
        public VulkanRenderer(IWindow window, Logger log, bool enableValidation)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _enableValidation = enableValidation;

            // VkSurface is only valid after the window is initialised; the renderer is built post-init.
            if (_window.VkSurface is null)
            {
                throw new NotSupportedException(
                    "The window provides no Vulkan surface — initialise it first, and ensure a Vulkan loader/driver is present.");
            }

            _vk = Vk.GetApi();

            CreateInstance();
            SetupDebugMessenger();
            CreateSurface();
            PickPhysicalDevice();
            CreateLogicalDevice();
            CreateSwapchain();
            CreateImageViews();
            CreateDepthResources();
            CreateGraphicsPipeline();
            CreateSkyPipeline();
            CreatePostResources();   // HDR + bloom targets + sampler
            CreatePostObjects();     // descriptor pool/layouts/sets + bright/blur/composite pipelines
            UpdatePostDescriptorSets();
            CreateCommandPool();
            CreateCommandBuffers();
            CreateCubeMesh(); // needs the command pool + graphics queue for the staging upload
            CreateInstanceBuffers();
            CreateSyncObjects();

            _log.Info("Vulkan", $"Renderer up: {_swapchainImages.Length} swapchain images at " +
                                $"{_swapchainExtent.Width}x{_swapchainExtent.Height}, validation " +
                                (_enableValidation ? "ON" : "off") + ".");
        }

        // =====================================================================================
        // Instance + validation
        // =====================================================================================

        /// <summary>
        /// The instance is Vulkan's root object: it records which API version and which global
        /// extensions/layers this application wants. Two things are non-obvious here — the window tells us
        /// which surface extensions it needs (they differ per OS), and validation is a <i>layer</i> we opt
        /// into, which intercepts every call to check it for misuse.
        /// </summary>
        private void CreateInstance()
        {
            if (_enableValidation && !ValidationLayerSupported())
            {
                _log.Warning("Vulkan", $"{ValidationLayerName} not available; continuing without validation.");
            }

            var appInfo = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = (byte*)SilkMarshal.StringToPtr("Spectre"),
                ApplicationVersion = new Version32(1, 0, 0),
                PEngineName = (byte*)SilkMarshal.StringToPtr("RP.Game"),
                EngineVersion = new Version32(1, 0, 0),
                ApiVersion = Vk.Version13,
            };

            var createInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo,
            };

            // The window needs surface extensions (e.g. VK_KHR_surface + a per-OS one). Add the debug
            // utils extension when validating so the messenger below can attach.
            var extensions = GetRequiredInstanceExtensions();
            byte** extPtr = (byte**)SilkMarshal.StringArrayToPtr(extensions);
            createInfo.EnabledExtensionCount = (uint)extensions.Count;
            createInfo.PpEnabledExtensionNames = extPtr;

            bool useValidation = _enableValidation && ValidationLayerSupported();
            byte** layerPtr = null;
            if (useValidation)
            {
                layerPtr = (byte**)SilkMarshal.StringArrayToPtr(new List<string> { ValidationLayerName });
                createInfo.EnabledLayerCount = 1;
                createInfo.PpEnabledLayerNames = layerPtr;

                // Chaining a messenger create-info here (via pNext) makes validation cover instance
                // creation and destruction themselves — the window when the standalone messenger is not
                // yet alive. We build the same struct the standalone messenger uses.
                DebugUtilsMessengerCreateInfoEXT dbg = BuildDebugMessengerInfo();
                createInfo.PNext = &dbg;
            }
            else
            {
                createInfo.EnabledLayerCount = 0;
                createInfo.PNext = null;
            }

            Result result = _vk.CreateInstance(in createInfo, null, out _instance);

            // Free the temporary native strings regardless of success.
            SilkMarshal.Free((nint)appInfo.PApplicationName);
            SilkMarshal.Free((nint)appInfo.PEngineName);
            SilkMarshal.Free((nint)extPtr);
            if (layerPtr != null) SilkMarshal.Free((nint)layerPtr);

            if (result != Result.Success)
            {
                throw new VulkanException("vkCreateInstance failed", result);
            }
        }

        private bool ValidationLayerSupported()
        {
            uint count = 0;
            _vk.EnumerateInstanceLayerProperties(ref count, null);
            var available = new LayerProperties[count];
            fixed (LayerProperties* p = available)
            {
                _vk.EnumerateInstanceLayerProperties(ref count, p);
            }

            foreach (var layer in available)
            {
                string name = SilkMarshal.PtrToString((nint)layer.LayerName) ?? string.Empty;
                if (name == ValidationLayerName) return true;
            }

            return false;
        }

        private List<string> GetRequiredInstanceExtensions()
        {
            // The window hands back the surface extensions it needs as a byte** of `count` C strings.
            byte** windowExt = _window.VkSurface!.GetRequiredExtensions(out uint count);
            var extensions = new List<string>(SilkMarshal.PtrToStringArray((nint)windowExt, (int)count));

            if (_enableValidation && ValidationLayerSupported())
            {
                extensions.Add(ExtDebugUtils.ExtensionName); // "VK_EXT_debug_utils"
            }

            return extensions;
        }

        /// <summary>
        /// Stands up the standalone debug messenger that routes Vulkan's validation output into the
        /// engine's <see cref="Logger"/>. Without this, validation messages would go to the system
        /// debugger only — useless when the build runs unattended (brief S4.2).
        /// </summary>
        private void SetupDebugMessenger()
        {
            if (!_enableValidation || !ValidationLayerSupported()) return;

            if (!_vk.TryGetInstanceExtension(_instance, out ExtDebugUtils debugUtils))
            {
                _log.Warning("Vulkan", "VK_EXT_debug_utils not present; no validation messages will be routed.");
                return;
            }

            _debugUtils = debugUtils;
            DebugUtilsMessengerCreateInfoEXT createInfo = BuildDebugMessengerInfo();
            Result result = _debugUtils.CreateDebugUtilsMessenger(_instance, in createInfo, null, out _debugMessenger);
            if (result != Result.Success)
            {
                _log.Warning("Vulkan", $"CreateDebugUtilsMessenger failed ({result}); continuing.");
            }
        }

        private DebugUtilsMessengerCreateInfoEXT BuildDebugMessengerInfo()
        {
            // Keep the delegate rooted in a field; Silk marshals it to an unmanaged function pointer the
            // driver calls, and the GC must not move or collect it while the messenger lives.
            _debugCallback = new PfnDebugUtilsMessengerCallbackEXT(DebugCallback);

            return new DebugUtilsMessengerCreateInfoEXT
            {
                SType = StructureType.DebugUtilsMessengerCreateInfoExt,
                MessageSeverity =
                    DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt |
                    DebugUtilsMessageSeverityFlagsEXT.InfoBitExt |
                    DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
                    DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
                MessageType =
                    DebugUtilsMessageTypeFlagsEXT.GeneralBitExt |
                    DebugUtilsMessageTypeFlagsEXT.ValidationBitExt |
                    DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
                PfnUserCallback = _debugCallback,
            };
        }

        private uint DebugCallback(
            DebugUtilsMessageSeverityFlagsEXT severity,
            DebugUtilsMessageTypeFlagsEXT type,
            DebugUtilsMessengerCallbackDataEXT* data,
            void* userData)
        {
            string message = SilkMarshal.PtrToString((nint)data->PMessage) ?? string.Empty;
            LogLevel level = severity switch
            {
                var s when s.HasFlag(DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) => LogLevel.Error,
                var s when s.HasFlag(DebugUtilsMessageSeverityFlagsEXT.WarningBitExt) => LogLevel.Warning,
                var s when s.HasFlag(DebugUtilsMessageSeverityFlagsEXT.InfoBitExt) => LogLevel.Debug,
                _ => LogLevel.Trace,
            };

            _log.Log(level, "Vulkan", message);

            // Returning false tells Vulkan "do not abort the call that triggered this". Validation is for
            // observing, not for killing the call.
            return Vk.False;
        }

        // =====================================================================================
        // Surface
        // =====================================================================================

        /// <summary>
        /// The surface is the handshake between Vulkan and the windowing system: a Vulkan-side handle to
        /// "the pixels of this OS window". The window knows how to create it for whatever OS we are on.
        /// </summary>
        private void CreateSurface()
        {
            if (!_vk.TryGetInstanceExtension(_instance, out _khrSurface))
            {
                throw new NotSupportedException("VK_KHR_surface extension is unavailable.");
            }

            _surface = _window.VkSurface!.Create<AllocationCallbacks>(_instance.ToHandle(), null).ToSurface();
        }

        // =====================================================================================
        // Physical + logical device
        // =====================================================================================

        /// <summary>
        /// Picks a GPU. A device is "suitable" only if it can do graphics, can present to our surface, and
        /// supports the swapchain extension. We prefer a discrete GPU (the big battles want it) but accept
        /// any device that qualifies — a box that runs beats a perfect device that is absent.
        /// </summary>
        private void PickPhysicalDevice()
        {
            uint count = 0;
            _vk.EnumeratePhysicalDevices(_instance, ref count, null);
            if (count == 0) throw new NotSupportedException("No Vulkan-capable GPU found.");

            var devices = new PhysicalDevice[count];
            fixed (PhysicalDevice* p = devices)
            {
                _vk.EnumeratePhysicalDevices(_instance, ref count, p);
            }

            PhysicalDevice chosen = default;
            int bestScore = -1;
            foreach (var device in devices)
            {
                if (!TryFindQueueFamilies(device, out _, out _)) continue;
                if (!SupportsSwapchain(device)) continue;
                if (!SwapchainAdequate(device)) continue;

                _vk.GetPhysicalDeviceProperties(device, out PhysicalDeviceProperties props);
                int score = props.DeviceType == PhysicalDeviceType.DiscreteGpu ? 1000 : 100;
                if (score > bestScore)
                {
                    bestScore = score;
                    chosen = device;
                }
            }

            if (bestScore < 0) throw new NotSupportedException("No suitable Vulkan GPU (graphics+present+swapchain).");

            _physicalDevice = chosen;
            TryFindQueueFamilies(chosen, out _graphicsFamily, out _presentFamily);

            _vk.GetPhysicalDeviceProperties(chosen, out PhysicalDeviceProperties chosenProps);
            string name = SilkMarshal.PtrToString((nint)chosenProps.DeviceName) ?? "(unknown)";
            _log.Info("Vulkan", $"Selected GPU: {name} ({chosenProps.DeviceType}).");
        }

        // A queue family is a group of GPU "lanes" with the same capabilities. We need one that can do
        // graphics and one that can present to our surface; very often it is the same family.
        private bool TryFindQueueFamilies(PhysicalDevice device, out uint graphics, out uint present)
        {
            graphics = 0;
            present = 0;
            bool foundGraphics = false, foundPresent = false;

            uint count = 0;
            _vk.GetPhysicalDeviceQueueFamilyProperties(device, ref count, null);
            var families = new QueueFamilyProperties[count];
            fixed (QueueFamilyProperties* p = families)
            {
                _vk.GetPhysicalDeviceQueueFamilyProperties(device, ref count, p);
            }

            for (uint i = 0; i < count; i++)
            {
                if (!foundGraphics && families[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
                {
                    graphics = i;
                    foundGraphics = true;
                }

                _khrSurface.GetPhysicalDeviceSurfaceSupport(device, i, _surface, out Bool32 presentSupport);
                if (!foundPresent && presentSupport)
                {
                    present = i;
                    foundPresent = true;
                }

                if (foundGraphics && foundPresent) break;
            }

            return foundGraphics && foundPresent;
        }

        private bool SupportsSwapchain(PhysicalDevice device)
        {
            uint count = 0;
            _vk.EnumerateDeviceExtensionProperties(device, (byte*)null, ref count, null);
            var available = new ExtensionProperties[count];
            fixed (ExtensionProperties* p = available)
            {
                _vk.EnumerateDeviceExtensionProperties(device, (byte*)null, ref count, p);
            }

            foreach (var ext in available)
            {
                string name = SilkMarshal.PtrToString((nint)ext.ExtensionName) ?? string.Empty;
                if (name == KhrSwapchain.ExtensionName) return true;
            }

            return false;
        }

        private bool SwapchainAdequate(PhysicalDevice device)
        {
            var support = QuerySwapchainSupport(device);
            return support.Formats.Length > 0 && support.PresentModes.Length > 0;
        }

        /// <summary>
        /// Creates the logical device — our private, configured connection to the chosen GPU — and grabs
        /// handles to the graphics and present queues. Here is also where we switch on the Vulkan 1.3
        /// feature this engine relies on: <b>dynamic rendering</b>, which lets us draw without authoring
        /// render-pass and framebuffer objects (brief S4.2).
        /// </summary>
        private void CreateLogicalDevice()
        {
            // One DeviceQueueCreateInfo per *unique* family (graphics and present may be the same).
            var uniqueFamilies = new HashSet<uint> { _graphicsFamily, _presentFamily }.ToArray();
            var queueCreateInfos = stackalloc DeviceQueueCreateInfo[uniqueFamilies.Length];
            float priority = 1.0f;
            for (int i = 0; i < uniqueFamilies.Length; i++)
            {
                queueCreateInfos[i] = new DeviceQueueCreateInfo
                {
                    SType = StructureType.DeviceQueueCreateInfo,
                    QueueFamilyIndex = uniqueFamilies[i],
                    QueueCount = 1,
                    PQueuePriorities = &priority,
                };
            }

            var deviceFeatures = new PhysicalDeviceFeatures();

            // Vulkan 1.3 features are opted into through a struct chained on pNext.
            var features13 = new PhysicalDeviceVulkan13Features
            {
                SType = StructureType.PhysicalDeviceVulkan13Features,
                DynamicRendering = true,
                Synchronization2 = true,
            };

            byte** deviceExtensions = (byte**)SilkMarshal.StringArrayToPtr(new List<string> { KhrSwapchain.ExtensionName });

            var createInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                PNext = &features13,
                QueueCreateInfoCount = (uint)uniqueFamilies.Length,
                PQueueCreateInfos = queueCreateInfos,
                PEnabledFeatures = &deviceFeatures,
                EnabledExtensionCount = 1,
                PpEnabledExtensionNames = deviceExtensions,
                EnabledLayerCount = 0,
            };

            Result result = _vk.CreateDevice(_physicalDevice, in createInfo, null, out _device);
            SilkMarshal.Free((nint)deviceExtensions);
            if (result != Result.Success) throw new VulkanException("vkCreateDevice failed", result);

            _vk.GetDeviceQueue(_device, _graphicsFamily, 0, out _graphicsQueue);
            _vk.GetDeviceQueue(_device, _presentFamily, 0, out _presentQueue);

            if (!_vk.TryGetDeviceExtension(_instance, _device, out _khrSwapchain))
            {
                throw new NotSupportedException("VK_KHR_swapchain device extension is unavailable.");
            }
        }

        // =====================================================================================
        // Swapchain
        // =====================================================================================

        private readonly struct SwapchainSupport
        {
            public SwapchainSupport(SurfaceCapabilitiesKHR caps, SurfaceFormatKHR[] formats, PresentModeKHR[] modes)
            {
                Capabilities = caps;
                Formats = formats;
                PresentModes = modes;
            }

            public SurfaceCapabilitiesKHR Capabilities { get; }
            public SurfaceFormatKHR[] Formats { get; }
            public PresentModeKHR[] PresentModes { get; }
        }

        private SwapchainSupport QuerySwapchainSupport(PhysicalDevice device)
        {
            _khrSurface.GetPhysicalDeviceSurfaceCapabilities(device, _surface, out SurfaceCapabilitiesKHR caps);

            uint formatCount = 0;
            _khrSurface.GetPhysicalDeviceSurfaceFormats(device, _surface, ref formatCount, null);
            var formats = new SurfaceFormatKHR[formatCount];
            fixed (SurfaceFormatKHR* p = formats)
            {
                _khrSurface.GetPhysicalDeviceSurfaceFormats(device, _surface, ref formatCount, p);
            }

            uint modeCount = 0;
            _khrSurface.GetPhysicalDeviceSurfacePresentModes(device, _surface, ref modeCount, null);
            var modes = new PresentModeKHR[modeCount];
            fixed (PresentModeKHR* p = modes)
            {
                _khrSurface.GetPhysicalDeviceSurfacePresentModes(device, _surface, ref modeCount, p);
            }

            return new SwapchainSupport(caps, formats, modes);
        }

        /// <summary>
        /// Builds the swapchain — the small ring of images the GPU draws into and the OS shows in turn.
        /// Three independent choices matter: the pixel <b>format</b> (we want an sRGB one for correct
        /// colour), the <b>present mode</b> (how frames are paced — mailbox for low-latency, FIFO as the
        /// always-available v-synced fallback), and the <b>extent</b> (pixel size, clamped to what the
        /// surface allows and taken from the window's framebuffer).
        /// </summary>
        private void CreateSwapchain()
        {
            var support = QuerySwapchainSupport(_physicalDevice);

            SurfaceFormatKHR surfaceFormat = ChooseSurfaceFormat(support.Formats);
            PresentModeKHR presentMode = ChoosePresentMode(support.PresentModes);
            Extent2D extent = ChooseExtent(support.Capabilities);

            // One more than the minimum reduces the chance of waiting on the driver, but never exceed the
            // maximum (0 means "no maximum").
            uint imageCount = support.Capabilities.MinImageCount + 1;
            if (support.Capabilities.MaxImageCount > 0 && imageCount > support.Capabilities.MaxImageCount)
            {
                imageCount = support.Capabilities.MaxImageCount;
            }

            var createInfo = new SwapchainCreateInfoKHR
            {
                SType = StructureType.SwapchainCreateInfoKhr,
                Surface = _surface,
                MinImageCount = imageCount,
                ImageFormat = surfaceFormat.Format,
                ImageColorSpace = surfaceFormat.ColorSpace,
                ImageExtent = extent,
                ImageArrayLayers = 1,
                // We draw into these images (ColorAttachment). TransferDst is handy for later (blits/clears).
                ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
                PreTransform = support.Capabilities.CurrentTransform,
                CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
                PresentMode = presentMode,
                Clipped = true,
                OldSwapchain = default,
            };

            // If graphics and present are different families, the images are shared between them; otherwise
            // keep them exclusive (faster) to one family.
            var indices = stackalloc uint[] { _graphicsFamily, _presentFamily };
            if (_graphicsFamily != _presentFamily)
            {
                createInfo.ImageSharingMode = SharingMode.Concurrent;
                createInfo.QueueFamilyIndexCount = 2;
                createInfo.PQueueFamilyIndices = indices;
            }
            else
            {
                createInfo.ImageSharingMode = SharingMode.Exclusive;
            }

            Result result = _khrSwapchain.CreateSwapchain(_device, in createInfo, null, out _swapchain);
            if (result != Result.Success) throw new VulkanException("vkCreateSwapchainKHR failed", result);

            uint actualCount = 0;
            _khrSwapchain.GetSwapchainImages(_device, _swapchain, ref actualCount, null);
            _swapchainImages = new Image[actualCount];
            fixed (Image* p = _swapchainImages)
            {
                _khrSwapchain.GetSwapchainImages(_device, _swapchain, ref actualCount, p);
            }

            _swapchainFormat = surfaceFormat.Format;
            _swapchainExtent = extent;

            // Keep the camera's aspect ratio matched to the render target so the image never stretches.
            if (extent.Height != 0) Camera.AspectRatio = (double)extent.Width / extent.Height;
        }

        private SurfaceFormatKHR ChooseSurfaceFormat(SurfaceFormatKHR[] formats)
        {
            foreach (var f in formats)
            {
                if (f.Format == Format.B8G8R8A8Srgb && f.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                {
                    return f;
                }
            }

            return formats[0]; // any supported format is acceptable if our preferred one is absent
        }

        private PresentModeKHR ChoosePresentMode(PresentModeKHR[] modes)
        {
            // Mailbox = "always show the newest finished frame", lowest latency without tearing. FIFO is
            // guaranteed to exist (classic v-sync) and is the safe fallback.
            foreach (var m in modes)
            {
                if (m == PresentModeKHR.MailboxKhr) return PresentModeKHR.MailboxKhr;
            }

            return PresentModeKHR.FifoKhr;
        }

        private Extent2D ChooseExtent(SurfaceCapabilitiesKHR caps)
        {
            // A special width of uint.MaxValue means "you choose"; otherwise we must use the surface's
            // current extent exactly. When we choose, take the window's framebuffer size and clamp it.
            if (caps.CurrentExtent.Width != uint.MaxValue)
            {
                return caps.CurrentExtent;
            }

            var fb = _window.FramebufferSize;
            uint width = Math.Clamp((uint)fb.X, caps.MinImageExtent.Width, caps.MaxImageExtent.Width);
            uint height = Math.Clamp((uint)fb.Y, caps.MinImageExtent.Height, caps.MaxImageExtent.Height);
            return new Extent2D(width, height);
        }

        private void CreateImageViews()
        {
            // An image view describes how to read an image (format, which slice). We need one per
            // swapchain image to use it as a colour attachment.
            _swapchainImageViews = new ImageView[_swapchainImages.Length];
            for (int i = 0; i < _swapchainImages.Length; i++)
            {
                var createInfo = new ImageViewCreateInfo
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = _swapchainImages[i],
                    ViewType = ImageViewType.Type2D,
                    Format = _swapchainFormat,
                    Components =
                    {
                        R = ComponentSwizzle.Identity,
                        G = ComponentSwizzle.Identity,
                        B = ComponentSwizzle.Identity,
                        A = ComponentSwizzle.Identity,
                    },
                    SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
                };

                Result result = _vk.CreateImageView(_device, in createInfo, null, out _swapchainImageViews[i]);
                if (result != Result.Success) throw new VulkanException("vkCreateImageView failed", result);
            }
        }

        // =====================================================================================
        // Command buffers + synchronisation
        // =====================================================================================

        private void CreateCommandPool()
        {
            // A command pool owns the memory for command buffers. ResetCommandBuffer lets us re-record an
            // individual buffer each frame rather than freeing and reallocating.
            var createInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
                QueueFamilyIndex = _graphicsFamily,
            };

            Result result = _vk.CreateCommandPool(_device, in createInfo, null, out _commandPool);
            if (result != Result.Success) throw new VulkanException("vkCreateCommandPool failed", result);
        }

        private void CreateCommandBuffers()
        {
            _commandBuffers = new CommandBuffer[MaxFramesInFlight];
            var allocInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = MaxFramesInFlight,
            };

            fixed (CommandBuffer* p = _commandBuffers)
            {
                Result result = _vk.AllocateCommandBuffers(_device, in allocInfo, p);
                if (result != Result.Success) throw new VulkanException("vkAllocateCommandBuffers failed", result);
            }
        }

        /// <summary>
        /// Creates the fences and semaphores that choreograph CPU and GPU.
        /// </summary>
        /// <remarks>
        /// Two kinds of sync primitive, for two different conversations:
        /// <list type="bullet">
        ///   <item><description><b>Semaphore</b> — GPU-to-GPU ordering. "image available" is signalled when
        ///   the swapchain image is ready to draw into; "render finished" when drawing is done and the
        ///   image is safe to present.</description></item>
        ///   <item><description><b>Fence</b> — GPU-to-CPU. "in flight" lets the CPU wait until a frame's GPU
        ///   work is fully done before it reuses that frame's command buffer. Created pre-signalled so the
        ///   very first frame does not deadlock waiting on work that never ran.</description></item>
        /// </list>
        /// </remarks>
        private void CreateSyncObjects()
        {
            _imageAvailableSemaphores = new Semaphore[MaxFramesInFlight];
            _renderFinishedSemaphores = new Semaphore[MaxFramesInFlight];
            _inFlightFences = new Fence[MaxFramesInFlight];

            var semInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
            var fenceInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo,
                Flags = FenceCreateFlags.SignaledBit,
            };

            for (int i = 0; i < MaxFramesInFlight; i++)
            {
                if (_vk.CreateSemaphore(_device, in semInfo, null, out _imageAvailableSemaphores[i]) != Result.Success ||
                    _vk.CreateSemaphore(_device, in semInfo, null, out _renderFinishedSemaphores[i]) != Result.Success ||
                    _vk.CreateFence(_device, in fenceInfo, null, out _inFlightFences[i]) != Result.Success)
                {
                    throw new VulkanException("Failed to create per-frame synchronisation objects", Result.ErrorUnknown);
                }
            }
        }

        // =====================================================================================
        // The frame
        // =====================================================================================

        /// <inheritdoc />
        public void DrawFrame(double interpolationAlpha)
        {
            if (_disposed) return;

            Fence fence = _inFlightFences[_currentFrame];

            // 1. Wait until this frame slot's previous GPU work is done, so reusing its command buffer and
            //    semaphores is safe.
            _vk.WaitForFences(_device, 1, in fence, true, ulong.MaxValue);

            // 2. Acquire the next image to draw into. If the swapchain is out of date (e.g. the window
            //    resized), rebuild it and skip this frame.
            uint imageIndex = 0;
            Result acquire = _khrSwapchain.AcquireNextImage(
                _device, _swapchain, ulong.MaxValue,
                _imageAvailableSemaphores[_currentFrame], default, ref imageIndex);

            if (acquire == Result.ErrorOutOfDateKhr)
            {
                RecreateSwapchain();
                return;
            }

            if (acquire != Result.Success && acquire != Result.SuboptimalKhr)
            {
                throw new VulkanException("vkAcquireNextImageKHR failed", acquire);
            }

            // Only reset the fence once we know we are submitting work this frame (otherwise a skipped
            // frame would leave it unsignalled and the next wait would hang).
            _vk.ResetFences(_device, 1, in fence);

            // The fence wait above guarantees this frame slot's previous GPU work is done, so it is safe to
            // overwrite this slot's instance buffer with the freshly-culled set.
            CullAndUploadInstances(_currentFrame);

            CommandBuffer cb = _commandBuffers[_currentFrame];
            _vk.ResetCommandBuffer(cb, 0);
            RecordClearCommands(cb, imageIndex);

            // 3. Submit: wait for the image to be available before the colour-output stage, then signal
            //    "render finished" and the fence when done.
            Semaphore waitSemaphore = _imageAvailableSemaphores[_currentFrame];
            Semaphore signalSemaphore = _renderFinishedSemaphores[_currentFrame];
            PipelineStageFlags waitStage = PipelineStageFlags.ColorAttachmentOutputBit;

            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &waitSemaphore,
                PWaitDstStageMask = &waitStage,
                CommandBufferCount = 1,
                PCommandBuffers = &cb,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = &signalSemaphore,
            };

            Result submit = _vk.QueueSubmit(_graphicsQueue, 1, in submitInfo, fence);
            if (submit != Result.Success) throw new VulkanException("vkQueueSubmit failed", submit);

            // 4. Present: hand the finished image to the OS, waiting on "render finished" first.
            SwapchainKHR swapchain = _swapchain;
            var presentInfo = new PresentInfoKHR
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &signalSemaphore,
                SwapchainCount = 1,
                PSwapchains = &swapchain,
                PImageIndices = &imageIndex,
            };

            Result present = _khrSwapchain.QueuePresent(_presentQueue, in presentInfo);
            if (present == Result.ErrorOutOfDateKhr || present == Result.SuboptimalKhr || _framebufferResized)
            {
                _framebufferResized = false;
                RecreateSwapchain();
            }
            else if (present != Result.Success)
            {
                throw new VulkanException("vkQueuePresentKHR failed", present);
            }

            _currentFrame = (_currentFrame + 1) % MaxFramesInFlight;
        }

        /// <summary>
        /// Records the frame's GPU work: transition the image to a drawable layout, clear it via dynamic
        /// rendering, then transition it to a presentable layout. With dynamic rendering the clear is just
        /// the attachment's <c>LoadOp = Clear</c> — no render pass object required (brief S4.2).
        /// </summary>
        private void RecordClearCommands(CommandBuffer cb, uint imageIndex)
        {
            var beginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };
            if (_vk.BeginCommandBuffer(cb, in beginInfo) != Result.Success)
            {
                throw new VulkanException("vkBeginCommandBuffer failed", Result.ErrorUnknown);
            }

            // --- Scene pass: render sky + hulls into the off-screen HDR target (not the swapchain) ---
            TransitionImage(cb, _hdrImage,
                ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
                0, AccessFlags.ColorAttachmentWriteBit,
                PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.ColorAttachmentOutputBit);

            // The depth image starts fresh each frame (we clear it), so UNDEFINED is fine.
            TransitionImage(cb, _depthImage,
                ImageLayout.Undefined, ImageLayout.DepthAttachmentOptimal,
                0, AccessFlags.DepthStencilAttachmentWriteBit,
                PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.EarlyFragmentTestsBit,
                ImageAspectFlags.DepthBit);

            var clear = new ClearValue
            {
                Color = new ClearColorValue(ClearColor.R, ClearColor.G, ClearColor.B, ClearColor.A),
            };

            var colorAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _hdrView,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = clear,
            };

            // Depth attachment: clear to the far value (1.0) each frame; we don't need to keep it after.
            var depthAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _depthView,
                ImageLayout = ImageLayout.DepthAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.DontCare,
                ClearValue = new ClearValue { DepthStencil = new ClearDepthStencilValue(1.0f, 0) },
            };

            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D(new Offset2D(0, 0), _swapchainExtent),
                LayerCount = 1,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorAttachment,
                PDepthAttachment = &depthAttachment,
            };

            _vk.CmdBeginRendering(cb, in renderingInfo);
            RecordSky(cb);  // procedural starfield/nebula backdrop, behind everything
            RecordMesh(cb); // the lit hulls, depth-tested over the backdrop
            _vk.CmdEndRendering(cb);

            // --- Post: bloom the HDR scene and composite + tonemap into the swapchain image, ready to present ---
            RecordPostChain(cb, imageIndex);

            if (_vk.EndCommandBuffer(cb) != Result.Success)
            {
                throw new VulkanException("vkEndCommandBuffer failed", Result.ErrorUnknown);
            }
        }

        // A pipeline barrier both changes an image's layout and orders memory access around it, so the GPU
        // does not read/write the image before the transition completes.
        private void TransitionImage(
            CommandBuffer cb, Image image,
            ImageLayout oldLayout, ImageLayout newLayout,
            AccessFlags srcAccess, AccessFlags dstAccess,
            PipelineStageFlags srcStage, PipelineStageFlags dstStage,
            ImageAspectFlags aspect = ImageAspectFlags.ColorBit)
        {
            var barrier = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = oldLayout,
                NewLayout = newLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1),
                SrcAccessMask = srcAccess,
                DstAccessMask = dstAccess,
            };

            _vk.CmdPipelineBarrier(cb, srcStage, dstStage, 0, 0, null, 0, null, 1, in barrier);
        }

        // =====================================================================================
        // Resize / swapchain recreation
        // =====================================================================================

        /// <inheritdoc />
        public void NotifyResize() => _framebufferResized = true;

        /// <summary>
        /// Rebuilds the swapchain and its image views for the window's new size. Everything tied to the
        /// exact pixel dimensions must be torn down and remade. While the window is minimised
        /// (size 0×0) we simply wait, because a zero-area swapchain is invalid.
        /// </summary>
        private void RecreateSwapchain()
        {
            // Spin until the window has a non-zero size again (it may be minimised). DoEvents keeps the OS
            // message pump alive so a restore can actually arrive.
            var fb = _window.FramebufferSize;
            while (fb.X == 0 || fb.Y == 0)
            {
                fb = _window.FramebufferSize;
                _window.DoEvents();
            }

            _vk.DeviceWaitIdle(_device);

            DestroySwapchain();
            CreateSwapchain();
            CreateImageViews();
            CreateDepthResources();

            // The HDR + bloom targets are swapchain-sized, so rebuild them and re-point the post descriptor
            // sets at the new image views.
            DestroyPostResources();
            CreatePostResources();
            UpdatePostDescriptorSets();

            _log.Debug("Vulkan", $"Swapchain recreated at {_swapchainExtent.Width}x{_swapchainExtent.Height}.");
        }

        private void DestroySwapchain()
        {
            DestroyDepthResources();

            foreach (var view in _swapchainImageViews)
            {
                _vk.DestroyImageView(_device, view, null);
            }

            _khrSwapchain.DestroySwapchain(_device, _swapchain, null);
        }

        /// <inheritdoc />
        public void WaitIdle()
        {
            if (!_disposed) _vk.DeviceWaitIdle(_device);
        }

        // =====================================================================================
        // Teardown — destroy in reverse order of creation (brief S4.2: proper teardown is acceptance-relevant)
        // =====================================================================================

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Never destroy objects the GPU might still be using.
            _vk.DeviceWaitIdle(_device);

            for (int i = 0; i < MaxFramesInFlight; i++)
            {
                _vk.DestroySemaphore(_device, _renderFinishedSemaphores[i], null);
                _vk.DestroySemaphore(_device, _imageAvailableSemaphores[i], null);
                _vk.DestroyFence(_device, _inFlightFences[i], null);
            }

            _vk.DestroyCommandPool(_device, _commandPool, null);

            if (_meshVertexBuffer.Handle != 0) _vk.DestroyBuffer(_device, _meshVertexBuffer, null);
            if (_meshVertexMemory.Handle != 0) _vk.FreeMemory(_device, _meshVertexMemory, null);
            if (_meshIndexBuffer.Handle != 0) _vk.DestroyBuffer(_device, _meshIndexBuffer, null);
            if (_meshIndexMemory.Handle != 0) _vk.FreeMemory(_device, _meshIndexMemory, null);
            DestroyDynamicInstances();

            DestroyPostObjects();
            DestroyPostResources();
            DestroyGraphicsPipeline();
            DestroySwapchain();

            _vk.DestroyDevice(_device, null);

            if (_debugUtils is not null && _debugMessenger.Handle != 0)
            {
                _debugUtils.DestroyDebugUtilsMessenger(_instance, _debugMessenger, null);
            }

            _khrSurface.DestroySurface(_instance, _surface, null);
            _vk.DestroyInstance(_instance, null);
            _vk.Dispose();

            _log.Info("Vulkan", "Renderer torn down cleanly.");
        }
    }
}
