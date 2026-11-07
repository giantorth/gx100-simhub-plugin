using System;
using System.IO;
using System.Media;

namespace GX100Plugin
{
    /// <summary>
    /// Audible confirmation that the shifter has switched between H-pattern and
    /// sequential mode, so the user knows without looking away from the road. A falling
    /// two-note chime means sequential on, a rising three-note chime means off. The
    /// tones are synthesised once at startup, so the plugin ships no sound files.
    /// </summary>
    internal sealed class ModeChime : IDisposable
    {
        private const int SampleRate = 44100;
        private const double NoteSeconds = 0.09;
        private const double FadeSeconds = 0.008;
        private const double Amplitude = 0.3;

        private const double LowHz = 660.0;   // E5
        private const double HighHz = 880.0;  // A5
        private const double TopHz = 1108.73; // C#6

        private readonly SoundPlayer _on = CreatePlayer(HighHz, LowHz);
        private readonly SoundPlayer _off = CreatePlayer(LowHz, HighHz, TopHz);

        public void Play(bool sequential)
        {
            try
            {
                // Play() returns immediately and plays on its own thread, so the
                // action handler isn't held up waiting for the chime to finish.
                (sequential ? _on : _off).Play();
            }
            catch (Exception ex)
            {
                // No audio device, or the device is busy: the mode change itself
                // has already happened and matters more than the confirmation.
                SimHub.Logging.Current.Warn($"[GX100] Could not play mode chime: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _on.Dispose();
            _off.Dispose();
        }

        private static SoundPlayer CreatePlayer(params double[] notesHz)
        {
            var player = new SoundPlayer(new MemoryStream(BuildWav(notesHz)));
            player.Load();
            return player;
        }

        private static byte[] BuildWav(double[] notesHz)
        {
            int noteSamples = (int)(SampleRate * NoteSeconds);
            int fadeSamples = (int)(SampleRate * FadeSeconds);
            int dataBytes = noteSamples * notesHz.Length * sizeof(short);

            using var ms = new MemoryStream(44 + dataBytes);
            using var w = new BinaryWriter(ms);

            // RIFF/WAVE header, 16-bit mono PCM.
            w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
            w.Write(36 + dataBytes);
            w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
            w.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(SampleRate);
            w.Write(SampleRate * sizeof(short));
            w.Write((short)sizeof(short));
            w.Write((short)16);
            w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
            w.Write(dataBytes);

            foreach (double hz in notesHz)
            {
                for (int i = 0; i < noteSamples; i++)
                {
                    // Ramp each note in and out; a hard start or stop on a sine
                    // wave is heard as a click.
                    double envelope = Math.Min(1.0, Math.Min(i, noteSamples - 1 - i) / (double)fadeSamples);
                    double sample = Math.Sin(2 * Math.PI * hz * i / SampleRate) * Amplitude * envelope;
                    w.Write((short)(sample * short.MaxValue));
                }
            }

            w.Flush();
            return ms.ToArray();
        }
    }
}
