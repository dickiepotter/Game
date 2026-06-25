namespace RP.Game.Audio
{
    using System;
    using System.Collections.Generic;
    using RP.Game.Core.Logging;
    using RP.Math;
    using Silk.NET.OpenAL;

    /// <summary>
    /// A minimal 3D positional audio engine over <b>OpenAL</b> (via Silk.NET). It opens the audio device,
    /// places a listener in the world, and plays sounds at world positions — OpenAL applies the distance
    /// attenuation and panning itself, which is why a game wants a real spatial audio API rather than
    /// stereo playback (build brief S15.2). The pure DSP behind it is in <see cref="AudioMath"/>.
    /// </summary>
    /// <remarks>
    /// This is the Phase 2 bring-up: enough to prove a positioned sound plays. The bus/mixer model, music
    /// stems, and the interior-muffling DSP come later (build brief S15). Playback can only be verified by
    /// ear, so the engine is built defensively — it never throws into the game loop for a missing device.
    /// </remarks>
    public sealed unsafe class AudioEngine : IDisposable
    {
        private readonly ALContext _alc;
        private readonly AL _al;
        private readonly Device* _device;
        private readonly Context* _context;
        private readonly Logger _log;

        private readonly List<uint> _sources = new List<uint>();
        private readonly List<uint> _buffers = new List<uint>();

        // One-shot voices are recycled: a firefight would otherwise leak a source+buffer per shot and exhaust
        // OpenAL's source limit. We reuse any voice that has finished playing, and cap the live count.
        private const int MaxOneShots = 48;
        private readonly List<(uint Source, uint Buffer)> _oneShots = new List<(uint, uint)>();
        private bool _disposed;

        /// <summary>Opens the default audio device and makes a current context.</summary>
        /// <exception cref="InvalidOperationException">If no audio device can be opened.</exception>
        public AudioEngine(Logger log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _alc = ALContext.GetApi();
            _al = AL.GetApi();

            _device = _alc.OpenDevice("");
            if (_device == null)
            {
                throw new InvalidOperationException("OpenAL: no audio device could be opened.");
            }

            _context = _alc.CreateContext(_device, null);
            if (_context == null || !_alc.MakeContextCurrent(_context))
            {
                throw new InvalidOperationException("OpenAL: failed to create/activate an audio context.");
            }

            // A natural metre-scale falloff for the world (1 unit = 1 metre, build brief S5).
            _al.SetListenerProperty(ListenerVector3.Position, 0, 0, 0);
            SetListenerOrientation(new Vector3(0, 0, -1), new Vector3(0, 1, 0));

            _log.Info("Audio", "OpenAL device opened; listener placed at origin.");
        }

        /// <summary>Positions and aims the listener (the player's ears) in the world.</summary>
        public void SetListener(Vector3 position, Vector3 velocity, Vector3 forward, Vector3 up)
        {
            _al.SetListenerProperty(ListenerVector3.Position, position.X, position.Y, position.Z);
            _al.SetListenerProperty(ListenerVector3.Velocity, velocity.X, velocity.Y, velocity.Z);
            SetListenerOrientation(forward, up);
        }

        private void SetListenerOrientation(Vector3 forward, Vector3 up)
        {
            // OpenAL takes orientation as six floats: forward (x,y,z) then up (x,y,z).
            Span<float> orientation = stackalloc float[6]
            {
                forward.X, forward.Y, forward.Z,
                up.X, up.Y, up.Z,
            };
            fixed (float* p = orientation)
            {
                _al.SetListenerProperty(ListenerFloatArray.Orientation, p);
            }
        }

        /// <summary>
        /// Plays a one-shot 16-bit mono PCM clip at a world position, with optional pitch. Finished voices are
        /// recycled so a sustained firefight never exhausts OpenAL's sources; once <see cref="MaxOneShots"/>
        /// are live and none are free, the quietest-to-spare new sound is simply dropped.
        /// </summary>
        public void PlayAt(short[] pcm, int sampleRate, Vector3 position, float gain = 1f, float pitch = 1f)
        {
            if (_disposed) return;

            // Reuse a finished voice if there is one; otherwise grow up to the cap.
            uint source, buffer;
            int free = FindStoppedOneShot();
            if (free >= 0)
            {
                (source, buffer) = _oneShots[free];
                _al.SetSourceProperty(source, SourceInteger.Buffer, 0); // detach before re-filling the buffer
                _al.DeleteBuffer(buffer);
                buffer = _al.GenBuffer();
                _oneShots[free] = (source, buffer);
            }
            else if (_oneShots.Count < MaxOneShots)
            {
                source = _al.GenSource();
                buffer = _al.GenBuffer();
                _oneShots.Add((source, buffer));
            }
            else
            {
                return; // all voices busy — drop this one rather than stall
            }

            _al.BufferData(buffer, BufferFormat.Mono16, pcm, sampleRate);
            _al.SetSourceProperty(source, SourceInteger.Buffer, (int)buffer);
            _al.SetSourceProperty(source, SourceVector3.Position, position.X, position.Y, position.Z);
            _al.SetSourceProperty(source, SourceFloat.Gain, gain);
            _al.SetSourceProperty(source, SourceFloat.Pitch, pitch <= 0 ? 1f : pitch);
            _al.SetSourceProperty(source, SourceBoolean.Looping, false);
            _al.SourcePlay(source);
        }

        private int FindStoppedOneShot()
        {
            for (int i = 0; i < _oneShots.Count; i++)
            {
                _al.GetSourceProperty(_oneShots[i].Source, GetSourceInteger.SourceState, out int state);
                if (state != (int)SourceState.Playing && state != (int)SourceState.Paused) return i;
            }

            return -1;
        }

        /// <summary>
        /// Starts a looping voice (e.g. the engine drone) and returns its source handle so it can be retuned
        /// each frame via <see cref="UpdateLoop"/>. When <paramref name="relative"/> is true the source is
        /// pinned to the listener (always centred), which suits a cockpit hum.
        /// </summary>
        public uint StartLoop(short[] pcm, int sampleRate, float gain = 1f, float pitch = 1f, bool relative = true)
        {
            if (_disposed) return 0;

            uint buffer = _al.GenBuffer();
            _al.BufferData(buffer, BufferFormat.Mono16, pcm, sampleRate);

            uint source = _al.GenSource();
            _al.SetSourceProperty(source, SourceInteger.Buffer, (int)buffer);
            _al.SetSourceProperty(source, SourceBoolean.Looping, true);
            _al.SetSourceProperty(source, SourceBoolean.SourceRelative, relative);
            _al.SetSourceProperty(source, SourceVector3.Position, 0, 0, 0);
            _al.SetSourceProperty(source, SourceFloat.Gain, gain);
            _al.SetSourceProperty(source, SourceFloat.Pitch, pitch <= 0 ? 1f : pitch);
            _al.SourcePlay(source);

            _buffers.Add(buffer);
            _sources.Add(source);
            return source;
        }

        /// <summary>Retunes a looping voice's gain and pitch (e.g. to track throttle).</summary>
        public void UpdateLoop(uint source, float gain, float pitch)
        {
            if (_disposed || source == 0) return;
            _al.SetSourceProperty(source, SourceFloat.Gain, gain);
            _al.SetSourceProperty(source, SourceFloat.Pitch, pitch <= 0 ? 1f : pitch);
        }

        /// <summary>
        /// Synthesises a <paramref name="seconds"/>-long sine tone at <paramref name="frequencyHz"/> as
        /// 16-bit mono PCM — a placeholder sound for the audio smoke test, with a short fade in/out so it
        /// does not click. Real clips load from files later.
        /// </summary>
        public static short[] GenerateSineTone(float frequencyHz, float seconds, int sampleRate = 44100, float amplitude = 0.4f)
        {
            int count = Math.Max(1, (int)(seconds * sampleRate));
            var samples = new short[count];
            int fade = Math.Min(count / 10, sampleRate / 50); // ~20 ms fade

            for (int i = 0; i < count; i++)
            {
                double t = (double)i / sampleRate;
                double envelope = 1.0;
                if (i < fade) envelope = (double)i / fade;
                else if (i > count - fade) envelope = (double)(count - i) / fade;

                double value = Math.Sin(2.0 * Math.PI * frequencyHz * t) * amplitude * envelope;
                samples[i] = (short)(value * short.MaxValue);
            }

            return samples;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach ((uint source, uint buffer) in _oneShots)
            {
                _al.DeleteSource(source);
                _al.DeleteBuffer(buffer);
            }

            foreach (uint source in _sources) _al.DeleteSource(source);
            foreach (uint buffer in _buffers) _al.DeleteBuffer(buffer);

            _alc.MakeContextCurrent(null);
            if (_context != null) _alc.DestroyContext(_context);
            if (_device != null) _alc.CloseDevice(_device);

            _al.Dispose();
            _alc.Dispose();
            _log.Info("Audio", "OpenAL torn down.");
        }
    }
}
