// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Scripting;
using Utilities.Async;
using Utilities.Audio;
using Utilities.Extensions;

namespace Utilities.Encoding.Wav
{
    [Preserve]
    public class WavEncoder : IEncoder
    {
        [Preserve]
        public WavEncoder() { }

        [Preserve]
        internal static NativeArray<byte> EncodeWav(NativeArray<byte> pcmData, int channels, int sampleRate, int bitsPerSample = 16)
        {
            var wavData = WriteWavHeader(channels, sampleRate, bitsPerSample, pcmData.Length);

            var count = pcmData.Length;

            for (var i = Constants.WavHeaderSize + 1; i < count; i++)
            {
                wavData[i] = pcmData[i];
            }

            return wavData;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static NativeArray<byte> WriteWavHeader(int channels, int sampleRate, int bitsPerSample = 16, int pcmDataLength = 0)
        {
            // We'll calculate the file size and protect against overflow.
            int fileSize;
            var blockAlign = bitsPerSample * channels / 8;
            var bytesPerSecond = sampleRate * blockAlign;

            checked
            {
                fileSize = 36 + pcmDataLength;
            }

            var headerData = new NativeArray<byte>(Constants.WavHeaderSize + pcmDataLength, Allocator.Temp);

            // Marks the file as a riff file. Characters are each 1 byte long.
            var offset = CopyData(Constants.RIFF_BYTES, headerData, 0);
            // Size of the overall file - 8 bytes, in bytes (32-bit integer). Typically, you'd fill this in after creation.
            offset = CopyData(BitConverter.GetBytes(fileSize - 8), headerData, offset); // Subtract the RIFF header (4 bytes) and file size field (4 bytes).
            // File Type Header. For our purposes, it always equals 'WAVE'.
            offset = CopyData(Constants.WAVE_BYTES, headerData, offset);
            // Format chunk marker. Includes trailing null.
            offset = CopyData(Constants.FMT_BYTES, headerData, offset);
            // Length of format data as listed above.
            offset = CopyData(BitConverter.GetBytes(16), headerData, offset);
            // Type of format (1 is PCM) - 2 byte integer.
            offset = CopyData(BitConverter.GetBytes((ushort)1), headerData, offset);
            // Number of Channels - 2 byte integer.
            offset = CopyData(BitConverter.GetBytes((ushort)channels), headerData, offset);
            // Sample Rate - 32 byte integer.
            offset = CopyData(BitConverter.GetBytes(sampleRate), headerData, offset);
            // Bytes per second.
            offset = CopyData(BitConverter.GetBytes(bytesPerSecond), headerData, offset);
            // Block align.
            offset = CopyData(BitConverter.GetBytes((ushort)blockAlign), headerData, offset);
            // Bits per sample.
            offset = CopyData(BitConverter.GetBytes((ushort)bitsPerSample), headerData, offset);
            // 'data' chunk header. Marks the beginning of the data section.
            offset = CopyData(Constants.DATA_BYTES, headerData, offset);
            // Size of the data section.
            CopyData(BitConverter.GetBytes(pcmDataLength), headerData, offset);

            return headerData;
        }

        /// <summary>
        /// Copies data from a managed array to a NativeArray using unsafe code for performance.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="array"></param>
        /// <param name="dst"></param>
        /// <param name="offset">
        /// The offset (in elements) in the destination NativeArray at which to start writing.
        /// </param>
        /// <param name="count">
        /// If null, copies the entire array length.
        /// </param>
        /// <returns>
        /// The number count of elements copied.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe int CopyData<T>(T[] array, NativeArray<T> dst, int offset, int? count = null) where T : unmanaged
        {
            count ??= array.Length;

            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            if (count.Value > array.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            // Ensure the destination has enough space
            if (offset + count.Value > dst.Length)
            {
                throw new ArgumentException("Destination NativeArray does not have enough space for the copy.");
            }

            fixed (T* srcPtr = array)
            {
                var dstPtr = (T*)dst.GetUnsafePtr();
                // Copy `count` elements from the start of the source array into destination at `offset`.
                UnsafeUtility.MemCpy(dstPtr + offset, srcPtr, sizeof(T) * count.Value);
            }

            // Return the new destination offset (previous offset + number of elements copied)
            return offset + count.Value;
        }

        /// <inheritdoc />
        [Preserve]
        public async Task StreamRecordingAsync(
            ClipData clipData,
            Func<NativeArray<byte>, Task> bufferCallback,
            Action<NativeArray<float>, int> sampleCallback,
            CancellationToken cancellationToken,
            [CallerMemberName] string callingMethodName = null)
        {
            if (callingMethodName != nameof(RecordingManager.StartRecordingStreamAsync))
            {
                throw new InvalidOperationException($"{nameof(StreamSaveToDiskAsync)} can only be called from {nameof(RecordingManager.StartRecordingStreamAsync)} not {callingMethodName}");
            }

            RecordingManager.IsProcessing = true;

            try
            {
                using var stream = new MemoryStream();
                await using var writer = new BinaryWriter(stream);
                writer.Flush();
                var headerData = WriteWavHeader(clipData.Channels, clipData.OutputSampleRate);

                try
                {
                    if (headerData.Length != Constants.WavHeaderSize)
                    {
                        Debug.LogWarning($"Failed to read all header content! {headerData.Length} != {Constants.WavHeaderSize}");
                    }

                    await bufferCallback.Invoke(headerData);
                }
                finally
                {
                    headerData.Dispose();
                }

                await PCMEncoder.InternalStreamRecordAsync(clipData, null, bufferCallback, sampleCallback, PCMEncoder.DefaultSampleProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                switch (e)
                {
                    case TaskCanceledException:
                    case OperationCanceledException:
                        // ignore
                        break;
                    default:
                        Debug.LogException(e);
                        break;
                }
            }
            finally
            {
                RecordingManager.IsProcessing = false;

                if (RecordingManager.EnableDebug)
                {
                    Debug.Log($"[{nameof(RecordingManager)}] Finished processing...");
                }
            }
        }

        /// <inheritdoc />
        [Preserve]
        public async Task<Tuple<string, AudioClip>> StreamSaveToDiskAsync(
            ClipData clipData,
            string saveDirectory,
            Action<Tuple<string, AudioClip>> callback,
            CancellationToken cancellationToken,
            [CallerMemberName] string callingMethodName = null)
        {
            if (callingMethodName != nameof(RecordingManager.StartRecordingAsync))
            {
                throw new InvalidOperationException($"{nameof(StreamSaveToDiskAsync)} can only be called from {nameof(RecordingManager.StartRecordingAsync)} not {callingMethodName}");
            }

            var outputPath = string.Empty;
            RecordingManager.IsProcessing = true;
            Tuple<string, AudioClip> result;

            try
            {
                Stream outStream;

                if (!string.IsNullOrWhiteSpace(saveDirectory))
                {

                    if (!Directory.Exists(saveDirectory))
                    {
                        Directory.CreateDirectory(saveDirectory);
                    }

                    outputPath = $"{saveDirectory}/{clipData.Name}.wav";

                    if (File.Exists(outputPath))
                    {
                        Debug.LogWarning($"[{nameof(RecordingManager)}] {outputPath} already exists, attempting to delete...");
                        File.Delete(outputPath);
                    }

                    outStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
                }
                else
                {
                    outStream = new MemoryStream();
                }

                var totalSampleCount = 0;
                var maxSampleLength = clipData.MaxSamples ?? clipData.OutputSampleRate * RecordingManager.MaxRecordingLength * clipData.Channels;
                var finalSamples = new float[maxSampleLength];
                var writer = new BinaryWriter(outStream);

                try
                {
                    var headerData = WriteWavHeader(clipData.Channels, clipData.OutputSampleRate);
                    writer.Write(headerData.AsSpan());

                    try
                    {
                        async Task BufferCallback(NativeArray<byte> buffer)
                        {
                            writer.Write(buffer.AsSpan());
                            await Task.Yield();
                        }

                        (finalSamples, totalSampleCount) = await PCMEncoder.InternalStreamRecordAsync(clipData, finalSamples, BufferCallback, null, PCMEncoder.DefaultSampleProvider, cancellationToken).ConfigureAwait(true);
                    }
                    finally
                    {
                        if (RecordingManager.EnableDebug)
                        {
                            Debug.Log($"[{nameof(RecordingManager)}] writing end of stream...");
                        }

                        var pcmDataLength = outStream.Position;
                        // rewind and write header file size
                        writer.Seek(4, SeekOrigin.Begin);
                        // Size of the overall file - 8 bytes, in bytes (32-bit integer).
                        writer.Write((int)(pcmDataLength - 8));
                        // rewind and write data size
                        writer.Seek(40, SeekOrigin.Begin);
                        // Size of the data section.
                        writer.Write(pcmDataLength - Constants.WavHeaderSize);

                        if (RecordingManager.EnableDebug)
                        {
                            Debug.Log($"[{nameof(RecordingManager)}] Flush stream...");
                        }

                        writer.Flush();
                    }
                }
                catch (Exception e)
                {
                    switch (e)
                    {
                        case TaskCanceledException:
                        case OperationCanceledException:
                            // ignore
                            break;
                        default:
                            Debug.LogError($"[{nameof(RecordingManager)}] Failed to record clip!\n{e}");
                            break;
                    }
                }
                finally
                {
                    if (RecordingManager.EnableDebug)
                    {
                        Debug.Log($"[{nameof(RecordingManager)}] Dispose stream...");
                    }

                    await writer.DisposeAsync().ConfigureAwait(false);
                    await outStream.DisposeAsync().ConfigureAwait(false);
                }

                if (RecordingManager.EnableDebug)
                {
                    Debug.Log($"[{nameof(RecordingManager)}] Finalized file write. Copying recording into new AudioClip");
                }

                // Trim the final samples down into the recorded range.
                var microphoneData = new float[totalSampleCount * clipData.Channels];
                Array.Copy(finalSamples, microphoneData, microphoneData.Length);
                await Awaiters.UnityMainThread; // switch back to main thread to call unity apis
                // Create a new copy of the final recorded clip.
                var newClip = AudioClip.Create(clipData.Name, microphoneData.Length, clipData.Channels, clipData.OutputSampleRate, false);
                newClip.SetData(microphoneData, 0);
                result = new Tuple<string, AudioClip>(outputPath, newClip);
                callback?.Invoke(result);
            }
            finally
            {
                RecordingManager.IsProcessing = false;

                if (RecordingManager.EnableDebug)
                {
                    Debug.Log($"[{nameof(RecordingManager)}] Finished processing...");
                }
            }
            return result;
        }

        [Preserve]
        public static async Task WriteToFileAsync(string path, byte[] pcmData, int channels, int sampleRate, PCMFormatSize bitDepth = PCMFormatSize.SixteenBit, CancellationToken cancellationToken = default)
        {
            try
            {
                await Awaiters.BackgroundThread;
                await using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
                await using var writer = new BinaryWriter(fileStream);
                cancellationToken.ThrowIfCancellationRequested();
                var bitsPerSample = 8 * (int)bitDepth;
                var headerData = WriteWavHeader(channels, sampleRate, bitsPerSample);
                writer.Write(headerData.AsSpan());
                writer.Write(pcmData);
                writer.Flush();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}
