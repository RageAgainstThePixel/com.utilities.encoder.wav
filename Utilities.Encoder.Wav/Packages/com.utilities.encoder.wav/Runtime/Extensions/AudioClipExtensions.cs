// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Utilities.Async;

namespace Utilities.Encoding.Wav
{
    public static class Constants
    {
        public const int WavHeaderSize = 44;
        public const float RescaleFactor = 32768f;
        public const string RIFF = "RIFF";
        public const string WAVE = "WAVE";
        public const string FMT = "fmt ";
        public const string DATA = "data";
    }

    /// <summary>
    /// https://docs.fileformat.com/audio/wav/
    /// </summary>
    public static class AudioClipExtensions
    {
        /// <summary>
        /// Converts an <see cref="AudioClip"/> to WAV encoded memory stream.
        /// </summary>
        /// <param name="audioClip"><see cref="AudioClip"/> to convert.</param>
        /// <param name="trim">Optional, trim the silence at beginning and end.</param>
        /// <returns><see cref="AudioClip"/> encoded to WAV as byte array.</returns>
        public static byte[] EncodeToWav(this AudioClip audioClip, bool trim = false)
        {
            if (audioClip == null)
            {
                throw new ArgumentNullException(nameof(audioClip));
            }

            // prep data
            var frequency = audioClip.frequency;
            var channels = audioClip.channels;
            var sampleCount = audioClip.samples;
            var samples = new float[sampleCount * channels];
            audioClip.GetData(samples, 0);

            // prep header
            using var stream = new MemoryStream();
            stream.Write(new byte[Constants.WavHeaderSize]);

            // trim data
            var start = 0;
            var end = sampleCount - 1;

            if (trim)
            {
                for (var i = 0; i < sampleCount; i++)
                {
                    if ((short)(samples[i] * Constants.RescaleFactor) == 0)
                    {
                        continue;
                    }

                    start = i;
                    break;
                }

                for (var i = sampleCount - 1; i >= 0; i--)
                {
                    if ((short)(samples[i] * Constants.RescaleFactor) == 0)
                    {
                        continue;
                    }

                    end = i;
                    break;
                }
            }

            // convert and write data
            for (var i = start; i <= end; i++)
            {
                stream.Write(BitConverter.GetBytes((short)(samples[i] * Constants.RescaleFactor)));
            }

            // write header
            stream.Seek(0, SeekOrigin.Begin);
            // Marks the file as a riff file. Characters are each 1 byte long.
            stream.Write(System.Text.Encoding.UTF8.GetBytes(Constants.RIFF));
            // Size of the overall file - 8 bytes, in bytes (32-bit integer). Typically, you’d fill this in after creation.
            stream.Write(BitConverter.GetBytes(stream.Length - 8));
            // File Type Header. For our purposes, it always equals “WAVE”.
            stream.Write(System.Text.Encoding.UTF8.GetBytes(Constants.WAVE));
            // Format chunk marker. Includes trailing null
            stream.Write(System.Text.Encoding.UTF8.GetBytes(Constants.FMT));
            // Length of format data as listed above
            stream.Write(BitConverter.GetBytes(16u));
            // Type of format (1 is PCM) - 2 byte integer
            stream.Write(BitConverter.GetBytes((ushort)1));
            // Number of Channels - 2 byte integer
            stream.Write(BitConverter.GetBytes(channels));
            // Sample Rate - 32 byte integer. Common values are 44100 (CD), 48000 (DAT). Sample Rate = Number of Samples per second, or Hertz.
            stream.Write(BitConverter.GetBytes(frequency));
            // (Sample Rate * BitsPerSample * Channels) / 8.
            stream.Write(BitConverter.GetBytes(frequency * channels * 2));
            // (BitsPerSample * Channels) / 8.1 - 8 bit mono2 - 8 bit stereo/16 bit mono4 - 16 bit stereo
            stream.Write(BitConverter.GetBytes((ushort)(channels * 2)));
            // Bits per sample
            stream.Write(BitConverter.GetBytes((ushort)16));
            // “data” chunk header. Marks the beginning of the data section.
            stream.Write(System.Text.Encoding.UTF8.GetBytes(Constants.DATA));
            // Size of the data section.
            stream.Write(BitConverter.GetBytes(sampleCount * channels * 2));

            return stream.ToArray();
        }

        /// <summary>
        /// Converts an <see cref="AudioClip"/> to WAV encoded memory stream.
        /// </summary>
        /// <param name="audioClip"><see cref="AudioClip"/> to convert.</param>
        /// <param name="trim">Optional, trim the silence at beginning and end.</param>
        /// <param name="cancellationToken">Optional, <see cref="CancellationToken"/>.</param>
        /// <returns><see cref="MemoryStream"/>.</returns>
        public static async Task<byte[]> EncodeToWavAsync(this AudioClip audioClip, bool trim = false, CancellationToken cancellationToken = default)
        {
            if (audioClip == null)
            {
                throw new ArgumentNullException(nameof(audioClip));
            }

            await Awaiters.UnityMainThread;

            // prep data
            var frequency = audioClip.frequency;
            var channels = audioClip.channels;
            var sampleCount = audioClip.samples;
            var samples = new float[sampleCount * channels];
            audioClip.GetData(samples, 0);

            await Task.Delay(1, cancellationToken).ConfigureAwait(false);

            // prep header
            using var stream = new MemoryStream();
            await stream.WriteAsync(new byte[Constants.WavHeaderSize], cancellationToken);

            // trim data
            var start = 0;
            var end = sampleCount - 1;

            if (trim)
            {
                for (var i = 0; i < sampleCount; i++)
                {
                    if ((short)(samples[i] * Constants.RescaleFactor) == 0)
                    {
                        continue;
                    }

                    start = i;
                    break;
                }

                for (var i = sampleCount - 1; i >= 0; i--)
                {
                    if ((short)(samples[i] * Constants.RescaleFactor) == 0)
                    {
                        continue;
                    }

                    end = i;
                    break;
                }
            }

            // convert and write data
            for (var i = start; i <= end; i++)
            {
                await stream.WriteAsync(BitConverter.GetBytes((short)(samples[i] * Constants.RescaleFactor)), cancellationToken);
            }

            // write header
            stream.Seek(0, SeekOrigin.Begin);
            // Marks the file as a riff file. Characters are each 1 byte long.
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Constants.RIFF), cancellationToken);
            // Size of the overall file - 8 bytes, in bytes (32-bit integer). Typically, you’d fill this in after creation.
            await stream.WriteAsync(BitConverter.GetBytes(stream.Length - 8), cancellationToken);
            // File Type Header. For our purposes, it always equals “WAVE”.
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Constants.WAVE), cancellationToken);
            // Format chunk marker. Includes trailing null
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Constants.FMT), cancellationToken);
            // Length of format data as listed above
            await stream.WriteAsync(BitConverter.GetBytes(16u), cancellationToken);
            // Type of format (1 is PCM) - 2 byte integer
            await stream.WriteAsync(BitConverter.GetBytes((ushort)1), cancellationToken);
            // Number of Channels - 2 byte integer
            await stream.WriteAsync(BitConverter.GetBytes(channels), cancellationToken);
            // Sample Rate - 32 byte integer. Common values are 44100 (CD), 48000 (DAT). Sample Rate = Number of Samples per second, or Hertz.
            await stream.WriteAsync(BitConverter.GetBytes(frequency), cancellationToken);
            // (Sample Rate * BitsPerSample * Channels) / 8.
            await stream.WriteAsync(BitConverter.GetBytes(frequency * channels * 2), cancellationToken);
            // (BitsPerSample * Channels) / 8.1 - 8 bit mono2 - 8 bit stereo/16 bit mono4 - 16 bit stereo
            await stream.WriteAsync(BitConverter.GetBytes((ushort)(channels * 2)), cancellationToken);
            // Bits per sample
            await stream.WriteAsync(BitConverter.GetBytes((ushort)16), cancellationToken);
            // “data” chunk header. Marks the beginning of the data section.
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Constants.DATA), cancellationToken);
            // Size of the data section.
            await stream.WriteAsync(BitConverter.GetBytes(sampleCount * channels * 2), cancellationToken);

            var data = stream.ToArray();
            await stream.DisposeAsync();

            return data;
        }
    }
}
