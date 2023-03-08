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
            var pcmData = audioClip.EncodeToPCM(PCMFormatSize.EightBit, trim);

            // prep header
            var stream = new MemoryStream();
            var writer = new BinaryWriter(stream);
            byte[] result = null;

            try
            {
                // Marks the file as a riff file. Characters are each 1 byte long.
                writer.Write(Constants.RIFF.ToCharArray());
                // Size of the overall file - 8 bytes, in bytes (32-bit integer). Typically, you’d fill this in after creation.
                writer.Write(Constants.WavHeaderSize + pcmData.Length - 8);
                // File Type Header. For our purposes, it always equals “WAVE”.
                writer.Write(Constants.WAVE.ToCharArray());
                // Format chunk marker. Includes trailing null
                writer.Write(Constants.FMT.ToCharArray());
                // Length of format data as listed above
                writer.Write(16);
                // Type of format (1 is PCM) - 2 byte integer
                writer.Write((ushort)1);
                // Number of Channels - 2 byte integer
                writer.Write((ushort)channels);
                // Sample Rate - 32 byte integer. Common values are 44100 (CD), 48000 (DAT). Sample Rate = Number of Samples per second, or Hertz.
                writer.Write(frequency);
                // (Sample Rate * BitsPerSample * Channels) / 8.
                writer.Write(frequency * channels * sizeof(short));
                // (BitsPerSample * Channels) / 8.1 - 8 bit mono2 - 8 bit stereo/16 bit mono4 - 16 bit stereo
                writer.Write((ushort)(channels * sizeof(short)));
                // Bits per sample
                writer.Write((ushort)16);
                // “data” chunk header. Marks the beginning of the data section.
                writer.Write(Constants.DATA.ToCharArray());
                // Size of the data section.
                writer.Write(pcmData.Length);
                // The audio data
                writer.Write(pcmData);
                writer.Flush();
                result = stream.ToArray();
            }
            catch (Exception e)
            {
                Debug.LogError(e);
            }
            finally
            {
                writer.Dispose();
                stream.Dispose();
            }

            return result;
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
            var pcmData = audioClip.EncodeToPCM(PCMFormatSize.EightBit, trim);

            await Task.Delay(1, cancellationToken).ConfigureAwait(false);

            // prep header
            var stream = new MemoryStream();
            var writer = new BinaryWriter(stream);
            byte[] result = null;

            try
            {
                // Marks the file as a riff file. Characters are each 1 byte long.
                writer.Write(Constants.RIFF.ToCharArray());
                // Size of the overall file - 8 bytes, in bytes (32-bit integer). Typically, you’d fill this in after creation.
                writer.Write(Constants.WavHeaderSize + pcmData.Length - 8);
                // File Type Header. For our purposes, it always equals “WAVE”.
                writer.Write(Constants.WAVE.ToCharArray());
                // Format chunk marker. Includes trailing null
                writer.Write(Constants.FMT.ToCharArray());
                // Length of format data as listed above
                writer.Write(16);
                // Type of format (1 is PCM) - 2 byte integer
                writer.Write((ushort)1);
                // Number of Channels - 2 byte integer
                writer.Write((ushort)channels);
                // Sample Rate - 32 byte integer. Common values are 44100 (CD), 48000 (DAT). Sample Rate = Number of Samples per second, or Hertz.
                writer.Write(frequency);
                // (Sample Rate * BitsPerSample * Channels) / 8.
                writer.Write(frequency * channels * sizeof(short));
                // (BitsPerSample * Channels) / 8.1 - 8 bit mono2 - 8 bit stereo/16 bit mono4 - 16 bit stereo
                writer.Write((ushort)(channels * sizeof(short)));
                // Bits per sample
                writer.Write((ushort)16);
                // “data” chunk header. Marks the beginning of the data section.
                writer.Write(Constants.DATA.ToCharArray());
                // Size of the data section.
                writer.Write(pcmData.Length);
                // The audio data
                writer.Write(pcmData);
                writer.Flush();
                result = stream.ToArray();
            }
            catch (Exception e)
            {
                Debug.LogError(e);
            }
            finally
            {
                await writer.DisposeAsync().ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            return result;
        }
    }
}
