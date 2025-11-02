// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
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
        internal static unsafe NativeArray<byte> EncodeToWav(NativeArray<byte> pcmData, int channels, int sampleRate, int bitsPerSample = 16)
        {
            var count = pcmData.Length;
            var wavData = WriteWavHeader(
                channels: channels,
                sampleRate: sampleRate,
                bitsPerSample: bitsPerSample,
                pcmDataLength: count,
                allocator: Allocator.Persistent);
            var pcmPtr = (byte*)pcmData.GetUnsafeReadOnlyPtr();
            var wavPtr = (byte*)wavData.GetUnsafePtr();
            UnsafeUtility.MemCpy(wavPtr + Constants.WavHeaderSize, pcmPtr, count);
            return wavData;
        }

        internal static NativeArray<byte> WriteWavHeader(int channels, int sampleRate, int bitsPerSample = 16, int pcmDataLength = 0, Allocator allocator = Allocator.Temp)
        {
            // We'll calculate the file size and protect against overflow.
            int fileSize;
            var blockAlign = bitsPerSample * channels / 8;
            var bytesPerSecond = sampleRate * blockAlign;

            checked
            {
                fileSize = 36 + pcmDataLength;
            }

            var headerData = new NativeArray<byte>(Constants.WavHeaderSize + pcmDataLength, allocator);

            try
            {
                var position = 0;
                // Marks the file as a riff file. Characters are each 1 byte long.
                UnsafeFastCopy(Constants.RIFF_BYTES, headerData, ref position);
                // Subtract the RIFF header (4 bytes) and file size field (4 bytes) to get a 32-bit integer (4 bytes).
                UnsafeFastCopy(BitConverter.GetBytes(fileSize - 8), headerData, ref position);
                // File Type Header. For our purposes, it always equals 'WAVE'. Characters are each 1 byte long.
                UnsafeFastCopy(Constants.WAVE_BYTES, headerData, ref position);
                // Format chunk marker. Includes trailing null. Characters are each 1 byte long.
                UnsafeFastCopy(Constants.FMT_BYTES, headerData, ref position);
                // Length of format data as listed above which is 16 bytes set as a 32-bit integer (4 bytes).
                UnsafeFastCopy(BitConverter.GetBytes(16), headerData, ref position);
                // Type of format (1 is PCM) - 16-bit integer (2 bytes).
                UnsafeFastCopy(BitConverter.GetBytes((ushort)1), headerData, ref position);
                // Number of Channels -16-bit integer (2 bytes).
                UnsafeFastCopy(BitConverter.GetBytes((ushort)channels), headerData, ref position);
                // Sample Rate - 32-bit integer (4 bytes).
                UnsafeFastCopy(BitConverter.GetBytes(sampleRate), headerData, ref position);
                // Bytes per second - 32-bit integer (4 bytes).
                UnsafeFastCopy(BitConverter.GetBytes(bytesPerSecond), headerData, ref position);
                // Block align - 16-bit integer (2 bytes).
                UnsafeFastCopy(BitConverter.GetBytes((ushort)blockAlign), headerData, ref position);
                // Bits per sample - 16-bit integer (2 bytes).
                UnsafeFastCopy(BitConverter.GetBytes((ushort)bitsPerSample), headerData, ref position);
                // 'data' chunk header. Marks the beginning of the data section.
                UnsafeFastCopy(Constants.DATA_BYTES, headerData, ref position);
                // Size of the data section - 32-bit integer (4 bytes).
                UnsafeFastCopy(BitConverter.GetBytes(pcmDataLength), headerData, ref position);

                if (position != Constants.WavHeaderSize)
                {
                    throw new InvalidOperationException($"WAV header size mismatch! {position} != {Constants.WavHeaderSize}");
                }
            }
            catch
            {
                headerData.Dispose();
                throw;
            }

            return headerData;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void UnsafeFastCopy<T>(T[] array, NativeArray<T> dst, ref int offset) where T : unmanaged
        {
            var count = array.Length;

            fixed (T* srcPtr = array)
            {
                var dstPtr = (T*)dst.GetUnsafePtr();
                UnsafeUtility.MemCpy(dstPtr + offset, srcPtr, sizeof(T) * count);
            }

            offset += count;
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
                    try
                    {
                        writer.Write(headerData.AsSpan());
                    }
                    finally
                    {
                        headerData.Dispose();
                    }

                    try
                    {
                        async Task BufferCallback(NativeArray<byte> buffer)
                        {
                            writer.Write(buffer.AsSpan()); // native array disposed by caller
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
                var headerData = WriteWavHeader(
                    channels: channels,
                    sampleRate: sampleRate,
                    bitsPerSample: bitsPerSample,
                    pcmDataLength: pcmData.Length);
                try
                {
                    writer.Write(headerData.AsSpan());
                    writer.Write(pcmData);
                }
                finally
                {
                    headerData.Dispose();
                }
                writer.Flush();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}
