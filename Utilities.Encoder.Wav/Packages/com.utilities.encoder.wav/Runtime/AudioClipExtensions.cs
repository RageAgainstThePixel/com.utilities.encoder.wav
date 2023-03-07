// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Utilities.Async;
using Utilities.Audio;

namespace Utilities.Encoding.Wav
{
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
            var pcmData = audioClip.EncodeToPCM(trim);

            // prep header
            using var stream = new MemoryStream();
            // Marks the file as a riff file. Characters are each 1 byte long.
            stream.Write(System.Text.Encoding.UTF8.GetBytes(Constants.RIFF), 0, 4);
            // Size of the overall file - 8 bytes, in bytes (32-bit integer). Typically, you’d fill this in after creation.
            stream.Write(BitConverter.GetBytes(Constants.WavHeaderSize + pcmData.Length - 8), 0, 4);
            // File Type Header. For our purposes, it always equals “WAVE”.
            stream.Write(System.Text.Encoding.UTF8.GetBytes(Constants.WAVE), 0, 4);
            // Format chunk marker. Includes trailing null
            stream.Write(System.Text.Encoding.UTF8.GetBytes(Constants.FMT), 0, 4);
            // Length of format data as listed above
            stream.Write(BitConverter.GetBytes(16u), 0, 4);
            // Type of format (1 is PCM) - 2 byte integer
            stream.Write(BitConverter.GetBytes((ushort)1), 0, 2);
            // Number of Channels - 2 byte integer
            stream.Write(BitConverter.GetBytes(channels), 0, 2);
            // Sample Rate - 32 byte integer. Common values are 44100 (CD), 48000 (DAT). Sample Rate = Number of Samples per second, or Hertz.
            stream.Write(BitConverter.GetBytes(frequency), 0, 4);
            // (Sample Rate * BitsPerSample * Channels) / 8.
            stream.Write(BitConverter.GetBytes(frequency * channels * 2), 0, 4);
            // (BitsPerSample * Channels) / 8.1 - 8 bit mono2 - 8 bit stereo/16 bit mono4 - 16 bit stereo
            stream.Write(BitConverter.GetBytes((ushort)(channels * 2)), 0, 2);
            // Bits per sample
            stream.Write(BitConverter.GetBytes((ushort)16), 0, 2);
            // “data” chunk header. Marks the beginning of the data section.
            stream.Write(System.Text.Encoding.UTF8.GetBytes(Constants.DATA), 0, 4);
            // Size of the data section.
            stream.Write(BitConverter.GetBytes(sampleCount * channels * 2), 0, 4);
            // The audio data
            stream.Write(pcmData);

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
            var pcmData = audioClip.EncodeToPCM(trim);

            await Task.Delay(1, cancellationToken).ConfigureAwait(false);

            // prep header
            using var stream = new MemoryStream();
            // Marks the file as a riff file. Characters are each 1 byte long.
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Constants.RIFF), 0, 4, cancellationToken).ConfigureAwait(false);
            // Size of the overall file - 8 bytes, in bytes (32-bit integer). Typically, you’d fill this in after creation.
            await stream.WriteAsync(BitConverter.GetBytes(Constants.WavHeaderSize + pcmData.Length - 8), 0, 4, cancellationToken).ConfigureAwait(false);
            // File Type Header. For our purposes, it always equals “WAVE”.
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Constants.WAVE), 0, 4, cancellationToken).ConfigureAwait(false);
            // Format chunk marker. Includes trailing null
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Constants.FMT), 0, 4, cancellationToken).ConfigureAwait(false);
            // Length of format data as listed above
            await stream.WriteAsync(BitConverter.GetBytes(16u), 0, 4, cancellationToken).ConfigureAwait(false);
            // Type of format (1 is PCM) - 2 byte integer
            await stream.WriteAsync(BitConverter.GetBytes((ushort)1), 0, 2, cancellationToken).ConfigureAwait(false);
            // Number of Channels - 2 byte integer
            await stream.WriteAsync(BitConverter.GetBytes(channels), 0, 2, cancellationToken).ConfigureAwait(false);
            // Sample Rate - 32 byte integer. Common values are 44100 (CD), 48000 (DAT). Sample Rate = Number of Samples per second, or Hertz.
            await stream.WriteAsync(BitConverter.GetBytes(frequency), 0, 4, cancellationToken).ConfigureAwait(false);
            // (Sample Rate * BitsPerSample * Channels) / 8.
            await stream.WriteAsync(BitConverter.GetBytes(frequency * channels * 2), 0, 4, cancellationToken).ConfigureAwait(false);
            // (BitsPerSample * Channels) / 8.1 - 8 bit mono2 - 8 bit stereo/16 bit mono4 - 16 bit stereo
            await stream.WriteAsync(BitConverter.GetBytes((ushort)(channels * 2)), 0, 2, cancellationToken).ConfigureAwait(false);
            // Bits per sample
            await stream.WriteAsync(BitConverter.GetBytes((ushort)16), 0, 2, cancellationToken).ConfigureAwait(false);
            // “data” chunk header. Marks the beginning of the data section.
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Constants.DATA), 0, 4, cancellationToken).ConfigureAwait(false);
            // Size of the data section.
            await stream.WriteAsync(BitConverter.GetBytes(sampleCount * channels * 2), 0, 4, cancellationToken).ConfigureAwait(false);
            // The audio data
            await stream.WriteAsync(pcmData, cancellationToken).ConfigureAwait(false);

            var data = stream.ToArray();
            await stream.DisposeAsync().ConfigureAwait(false);

            return data;
        }
    }
}
