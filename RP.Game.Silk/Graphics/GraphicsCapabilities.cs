namespace RP.Game.Graphics
{
    using System;

    /// <summary>
    /// How much the hardware can be asked to do. Chosen once at start-up from what the device reports, and
    /// read by everything that has a quality dial.
    /// </summary>
    /// <remarks>
    /// Named for the machine rather than for the settings, so that adding a dial later does not mean
    /// revisiting every tier: "what does a 2016 laptop do about shadows" has an obvious answer, whereas
    /// "what does quality level 2 do about shadows" does not.
    /// </remarks>
    public enum GraphicsTier
    {
        /// <summary>
        /// Integrated graphics, or a discrete card from around a decade ago. Everything optional is off and
        /// the view distance is short. The target is a steady frame rate on hardware that cannot be asked
        /// for more, not a pretty screenshot.
        /// </summary>
        Minimum = 0,

        /// <summary>A mid-range discrete card, or modern integrated graphics. Modest antialiasing, bloom
        /// on, a reasonable view distance.</summary>
        Standard = 1,

        /// <summary>A current discrete card. Everything on, long view distance.</summary>
        High = 2,
    }

    /// <summary>
    /// What the graphics device can do, what was chosen because of it, and — when it cannot run the game at
    /// all — why not, in terms a player can act on.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists as a type.</b> Capability decisions otherwise end up scattered: a sample
    /// count picked in the renderer, a view distance picked in the game, a shader branch picked in a
    /// shader. Each is reasonable alone and together they cannot be reasoned about, which is how a game
    /// ends up running at four frames a second on a machine that could manage forty if asked for less.
    /// Gathering them means the whole scaling policy can be read, and overridden, in one place.</para>
    ///
    /// <para><b>Both directions.</b> The brief is to run on a machine from a decade ago <i>and</i> to take
    /// advantage of a modern one. Those are the same mechanism — detect, then choose — and the failure mode
    /// of ignoring either is the same: a game tuned for exactly one machine.</para>
    /// </remarks>
    public sealed class GraphicsCapabilities
    {
        /// <summary>The device's reported name.</summary>
        public string DeviceName { get; init; } = "unknown";

        /// <summary>Whether the device is integrated rather than discrete.</summary>
        public bool Integrated { get; init; }

        /// <summary>The Vulkan version the device supports, as major.minor.</summary>
        public (int Major, int Minor) ApiVersion { get; init; }

        /// <summary>How much memory the device's own heap has, in megabytes. Zero if it could not be read.</summary>
        public long DeviceMemoryMb { get; init; }

        /// <summary>Whether dynamic rendering is available at all — currently a hard requirement.</summary>
        public bool DynamicRendering { get; init; }

        /// <summary>Whether it comes from core Vulkan 1.3 rather than the extension.</summary>
        public bool DynamicRenderingIsCore { get; init; }

        /// <summary>The highest sample count the device offers for the colour and depth targets.</summary>
        public int MaxSampleCount { get; init; } = 1;

        /// <summary>The tier chosen from all of the above.</summary>
        public GraphicsTier Tier { get; init; } = GraphicsTier.Standard;

        // ---- What the tier decides ----------------------------------------------------------------------

        /// <summary>How many samples to render the scene with. One means no multisampling.</summary>
        /// <remarks>
        /// The first thing to give up, and by a distance. Four-times multisampling costs four times the
        /// fill rate of the whole scene, and a voxel world is mostly large flat faces where the only thing
        /// it improves is the silhouette of distant terrain against the sky.
        /// </remarks>
        public int SampleCount => Tier switch
        {
            GraphicsTier.Minimum => 1,
            GraphicsTier.Standard => System.Math.Min(2, MaxSampleCount),
            _ => System.Math.Min(4, MaxSampleCount),
        };

        /// <summary>Whether to run the bloom and tonemap chain.</summary>
        /// <remarks>
        /// Several full-screen passes at reduced resolution — cheap on anything modern, and a measurable
        /// fraction of a frame on integrated graphics from a decade ago, where it buys a glow that machine's
        /// owner would happily trade for ten frames a second.
        /// </remarks>
        public bool Bloom => Tier != GraphicsTier.Minimum;

        /// <summary>How detailed the procedural surface shading should be, from 0 (flat colour) to 2.</summary>
        /// <remarks>
        /// The per-pixel noise and the normal perturbation derived from it. At tier 0 the world is flat
        /// shaded and still perfectly readable, because the shape, the ambient occlusion and the light grid
        /// are doing the real work.
        /// </remarks>
        public int SurfaceDetail => (int)Tier;

        /// <summary>How many chunks out, sideways, to keep drawable.</summary>
        /// <remarks>
        /// Now that the world is built before play rather than during it, this trades <i>load time</i> and
        /// mesh slots rather than frame rate — measured, all three tiers hold sixty on the same machine, and
        /// what separates them is six seconds of loading against fifty. The top tier is capped at nine
        /// rather than further out because each drawable chunk costs a buffer and an allocation, and drivers
        /// cap total allocations at around four thousand.
        /// </remarks>
        public int ViewDistanceChunks => Tier switch
        {
            GraphicsTier.Minimum => 5,
            GraphicsTier.Standard => 7,
            _ => 9,
        };

        /// <summary>How many chunks up and down to keep drawable.</summary>
        public int VerticalViewChunks => Tier switch
        {
            GraphicsTier.Minimum => 2,
            GraphicsTier.Standard => 3,
            _ => 4,
        };

        /// <summary>
        /// How many milliseconds per frame the world may spend generating and meshing chunks.
        /// </summary>
        /// <remarks>
        /// Smaller on weaker hardware, not larger. The instinct is the reverse — a slow machine needs more
        /// time to keep up — but the frame budget is what the player experiences, and a machine that cannot
        /// afford the work should fill the world in more gradually rather than stuttering while it does.
        /// </remarks>
        public double StreamMillisecondsPerFrame => Tier switch
        {
            GraphicsTier.Minimum => 2.5,
            GraphicsTier.Standard => 4.0,
            _ => 8.0,
        };

        /// <summary>A one-line summary for the log.</summary>
        public string Describe()
            => $"{DeviceName} ({(Integrated ? "integrated" : "discrete")}, Vulkan {ApiVersion.Major}.{ApiVersion.Minor}" +
               (DeviceMemoryMb > 0 ? $", {DeviceMemoryMb} MB" : string.Empty) + ") -> " +
               $"{Tier} [MSAA {SampleCount}x, bloom {(Bloom ? "on" : "off")}, detail {SurfaceDetail}, " +
               $"view {ViewDistanceChunks} chunks]";

        /// <summary>
        /// Chooses a tier from what the device reports.
        /// </summary>
        /// <remarks>
        /// <para>Deliberately crude, and crude in the pessimistic direction. There is no reliable way to
        /// ask a graphics device how fast it is — the only honest signals are whether it is integrated,
        /// roughly how much memory it has, and how new its driver is — so the rule guesses low and lets the
        /// player raise it. A game that opens too high on a weak machine gives a first impression of four
        /// frames a second; one that opens too low gives a first impression that runs, and a settings
        /// screen.</para>
        /// <para>Device memory is the most useful single number, because it correlates with the class of
        /// card far better than any feature flag: a card with under 2 GB of its own memory is, in practice,
        /// either integrated or old.</para>
        /// </remarks>
        public static GraphicsTier ChooseTier(bool integrated, long deviceMemoryMb, int apiMinor)
        {
            // Integrated graphics never gets the top tier, whatever it reports: it shares bandwidth with the
            // CPU, and bandwidth is what a voxel world consumes. But Standard rather than Minimum, because
            // the thing that used to make weak hardware stutter was the CPU building chunks inside the
            // frame, and the world is now built before play starts. A modern integrated GPU measured at
            // sixty frames a second on every tier; the honest difference between them is load time.
            if (integrated) return apiMinor >= 2 ? GraphicsTier.Standard : GraphicsTier.Minimum;

            if (deviceMemoryMb >= 4096) return GraphicsTier.High;
            if (deviceMemoryMb >= 1536) return GraphicsTier.Standard;

            // A discrete card with under 1.5 GB is from around 2013 or earlier. It can very likely run
            // this, but not at anything above the floor.
            return GraphicsTier.Minimum;
        }
    }
}
