namespace RP.Game.Graphics
{
    using System;

    /// <summary>
    /// The engine's renderer, seen from above. Scene and gameplay code talk to this interface and never
    /// touch Vulkan directly — that is the whole point of the abstraction (build brief S4.1, S4.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The interface is deliberately <b>thin</b>. Vulkan is the only planned backend, so this is not a
    /// speculative multi-API layer; it exists only to (a) keep Vulkan's types and lifetimes out of the
    /// higher layers and (b) make the renderer swappable for a headless/null implementation in tests.
    /// At Phase 0 the only thing a frame does is clear the screen, so the surface is correspondingly
    /// small; it grows as meshes, materials and passes arrive in later phases.
    /// </para>
    /// </remarks>
    public interface IRenderer : IDisposable
    {
        /// <summary>
        /// Renders and presents one frame.
        /// </summary>
        /// <param name="interpolationAlpha">How far rendering sits between the previous and current
        /// simulation step, in <c>[0, 1)</c> (from the fixed-timestep loop). Unused while the frame is a
        /// flat clear; later it drives interpolation of moving objects.</param>
        void DrawFrame(double interpolationAlpha);

        /// <summary>
        /// Tells the renderer the window's drawable size may have changed (resize, minimise, restore).
        /// The Vulkan swapchain is tied to an exact pixel size, so it must be rebuilt when that changes;
        /// this lets the backend defer that rebuild to a safe point rather than mid-frame.
        /// </summary>
        void NotifyResize();

        /// <summary>
        /// Blocks until the GPU has finished all outstanding work. Call once before teardown so no
        /// resource is destroyed while a frame still references it.
        /// </summary>
        void WaitIdle();

        /// <summary>The colour the framebuffer is cleared to each frame, as linear RGBA in <c>[0, 1]</c>.</summary>
        (float R, float G, float B, float A) ClearColor { get; set; }
    }
}
