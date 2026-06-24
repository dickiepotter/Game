namespace RP.Game.Graphics.Vulkan
{
    using System;
    using Silk.NET.Vulkan;

    /// <summary>
    /// Thrown when a Vulkan call returns a failure <see cref="Result"/> the engine cannot recover from.
    /// </summary>
    /// <remarks>
    /// Vulkan functions report success or failure by returning a <see cref="Result"/> code rather than
    /// throwing. Wrapping a fatal code in an exception lets the engine fail fast with a clear message and
    /// the offending code attached, instead of limping on with a half-built device.
    /// </remarks>
    public sealed class VulkanException : Exception
    {
        /// <summary>The Vulkan result code that caused the failure.</summary>
        public Result Result { get; }

        public VulkanException(string message, Result result)
            : base($"{message} (VkResult = {result}).")
        {
            Result = result;
        }
    }
}
