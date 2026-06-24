namespace RP.Game.Platform
{
    using System;
    using Silk.NET.Maths;
    using Silk.NET.Windowing;

    /// <summary>
    /// Creates an OS window configured for Vulkan rendering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A "window" from the engine's point of view is a rectangle of pixels the OS lets us draw into, plus
    /// a stream of events (resize, close, input). We use <b>Silk.NET.Windowing</b> for this — it wraps GLFW
    /// underneath and, crucially, knows how to hand Vulkan a drawable <i>surface</i> for the window
    /// (<c>IWindow.VkSurface</c>). We do <i>not</i> ask it for an OpenGL context
    /// (<see cref="WindowOptions.DefaultVulkan"/> sets <c>API = ContextAPI.None</c>), because Vulkan manages
    /// its own connection to the GPU.
    /// </para>
    /// <para>
    /// This is intentionally a one-method factory rather than a wrapper class: Silk's <see cref="IWindow"/>
    /// is already a clean abstraction, so re-wrapping every member would be noise. The engine drives the
    /// loop itself (initialise the window, then poll events each frame) rather than handing control to
    /// <c>IWindow.Run</c>, so the fixed-timestep loop stays in charge of pacing.
    /// </para>
    /// </remarks>
    public static class VulkanWindow
    {
        /// <summary>
        /// Builds (but does not yet show or initialise) a Vulkan-ready window.
        /// </summary>
        /// <param name="title">The window caption.</param>
        /// <param name="width">Initial client width in pixels.</param>
        /// <param name="height">Initial client height in pixels.</param>
        /// <returns>An uninitialised <see cref="IWindow"/>. Call <c>IWindow.Initialize()</c> before
        /// reading <c>IWindow.VkSurface</c> or creating the renderer.</returns>
        public static IWindow Create(string title, int width = 1280, int height = 720)
        {
            var options = WindowOptions.DefaultVulkan;
            options.Title = title;
            options.Size = new Vector2D<int>(width, height);

            // Note: do not read window.VkSurface here — it is only valid after IWindow.Initialize().
            // The renderer (which runs post-init) verifies a Vulkan surface is actually available.
            return Silk.NET.Windowing.Window.Create(options);
        }
    }
}
