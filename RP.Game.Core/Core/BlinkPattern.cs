namespace RP.Game.Core
{
    /// <summary>
    /// Anti-collision light timing, borrowed from aviation practice because players have absorbed those
    /// rhythms from every night sky they've ever watched: a slow red <b>beacon</b> pulses steadily, a white
    /// <b>strobe</b> double-flashes, and position lamps burn <b>steady</b>. Pure time functions — feed sim
    /// time plus a per-vehicle phase (so a fleet doesn't blink in lockstep, which reads as fake) and draw
    /// the light while the function returns true. Usable for ships, buoys, radio masts, drones — anything
    /// that should read as "powered and crewed" from a distance.
    /// </summary>
    public static class BlinkPattern
    {
        /// <summary>The rotating-beacon pulse: on for 0.12 s once per second. The "vehicle is live" idiom.</summary>
        public static bool Beacon(double time, double phase = 0)
        {
            double t = Wrap(time + phase, 1.0);
            return t < 0.12;
        }

        /// <summary>The high-intensity strobe: two 60 ms flashes 180 ms apart, then dark for the rest of a
        /// 1.4 s cycle — the unmistakable "flash-flash … flash-flash" of a wingtip strobe.</summary>
        public static bool Strobe(double time, double phase = 0)
        {
            double t = Wrap(time + phase, 1.4);
            return t < 0.06 || (t >= 0.18 && t < 0.24);
        }

        /// <summary>A slow distress pulse: a long half-second burn every two seconds — deliberate and
        /// urgent, distinct from both beacon and strobe. For escape pods and mayday transmitters.</summary>
        public static bool Distress(double time, double phase = 0)
        {
            double t = Wrap(time + phase, 2.0);
            return t < 0.5;
        }

        /// <summary>Steady burn — position lamps. Provided so call sites can treat every lamp uniformly
        /// as a (pattern, colour) pair rather than special-casing the always-on ones.</summary>
        public static bool Steady(double time, double phase = 0) => true;

        private static double Wrap(double value, double period)
        {
            double t = value % period;
            return t < 0 ? t + period : t;
        }
    }
}
