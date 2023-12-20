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
        [Obsolete("Use new overload with bitDepth")]
        public static byte[] EncodeToWav(this AudioClip audioClip, bool trim)
            => EncodeToWav(audioClip, PCMFormatSize.SixteenBit, trim);

        /// <summary>
        /// Converts an <see cref="AudioClip"/> to WAV encoded memory stream.
        /// </summary>
        /// <param name="audioClip"><see cref="AudioClip"/> to convert.</param>
        /// <param name="bitDepth">Optional, bit depth to encode. Defaults to <see cref="PCMFormatSize.SixteenBit"/>.</param>
        /// <param name="trim">Optional, trim the silence at beginning and end.</param>
        /// <returns><see cref="AudioClip"/> encoded to WAV as byte array.</returns>
        public static byte[] EncodeToWav(this AudioClip audioClip, PCMFormatSize bitDepth = PCMFormatSize.SixteenBit, bool trim = false)
        {
            if (audioClip == null)
            {
                throw new ArgumentNullException(nameof(audioClip));
            }

            // prep data
            var bitsPerSample = 8 * (int)bitDepth;
            var sampleRate = audioClip.frequency;
            var channels = audioClip.channels;
            var pcmData = audioClip.EncodeToPCM(bitDepth, trim);
            return WavEncoder.EncodeWav(pcmData, channels, sampleRate, bitsPerSample);
        }

        [Obsolete("Use new overload with bitDepth")]
        public static async Task<byte[]> EncodeToWavAsync(this AudioClip audioClip, bool trim)
            => await EncodeToWavAsync(audioClip, PCMFormatSize.SixteenBit, trim);

        /// <summary>
        /// Converts an <see cref="AudioClip"/> to WAV encoded memory stream.
        /// </summary>
        /// <param name="audioClip"><see cref="AudioClip"/> to convert.</param>
        /// <param name="bitDepth">Optional, bit depth to encode. Defaults to <see cref="PCMFormatSize.SixteenBit"/>.</param>
        /// <param name="trim">Optional, trim the silence at beginning and end.</param>
        /// <param name="cancellationToken">Optional, <see cref="CancellationToken"/>.</param>
        /// <returns><see cref="MemoryStream"/>.</returns>
        public static async Task<byte[]> EncodeToWavAsync(this AudioClip audioClip, PCMFormatSize bitDepth = PCMFormatSize.SixteenBit, bool trim = false, CancellationToken cancellationToken = default)
        {
            if (audioClip == null)
            {
                throw new ArgumentNullException(nameof(audioClip));
            }

            await Awaiters.UnityMainThread;

            // prep data
            var bitsPerSample = 8 * (int)bitDepth;
            var sampleRate = audioClip.frequency;
            var channels = audioClip.channels;
            var pcmData = audioClip.EncodeToPCM(bitDepth, trim);

            // Switch to background thread
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);

            var encodedBytes = WavEncoder.EncodeWav(pcmData, channels, sampleRate, bitsPerSample);
            await Awaiters.UnityMainThread;
            return encodedBytes;
        }
    }
}
