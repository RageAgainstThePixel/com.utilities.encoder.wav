// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;
using Utilities.Async;
using Utilities.Audio;
using Microphone = Utilities.Audio.Microphone;

namespace Utilities.Encoding.Wav
{
    [Preserve]
    public class WavEncoder : IEncoder
    {
        [Preserve]
        public WavEncoder() { }

        [Preserve]
        internal static byte[] EncodeWav(byte[] pcmData, int channels, int sampleRate, int bitsPerSample = 16)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            WriteWavHeader(writer, channels, sampleRate, bitsPerSample, pcmData.Length);
            writer.Write(pcmData);
            writer.Flush();
            return stream.ToArray();
        }

        private static void WriteWavHeader(BinaryWriter writer, int channels, int sampleRate, int bitsPerSample = 16, int pcmDataLength = 0)
        {
            // We'll calculate the file size and protect against overflow.
            int fileSize;
            var blockAlign = bitsPerSample * channels / 8;
            var bytesPerSecond = sampleRate * blockAlign;

            checked
            {
                fileSize = 36 + pcmDataLength;
            }
            // Marks the file as a riff file. Characters are each 1 byte long.
            writer.Write(Constants.RIFF_BYTES);
            // Size of the overall file - 8 bytes, in bytes (32-bit integer). Typically, you'd fill this in after creation.
            writer.Write(fileSize - 8); // Subtract the RIFF header (4 bytes) and file size field (4 bytes).
            // File Type Header. For our purposes, it always equals 'WAVE'.
            writer.Write(Constants.WAVE_BYTES);
            // Format chunk marker. Includes trailing null.
            writer.Write(Constants.FMT_BYTES);
            // Length of format data as listed above.
            writer.Write(16);
            // Type of format (1 is PCM) - 2 byte integer.
            writer.Write((ushort)1);
            // Number of Channels - 2 byte integer.
            writer.Write((ushort)channels);
            // Sample Rate - 32 byte integer.
            writer.Write(sampleRate);
            // Bytes per second.
            writer.Write(bytesPerSecond);
            // Block align.
            writer.Write((ushort)blockAlign);
            // Bits per sample.
            writer.Write((ushort)bitsPerSample);
            // 'data' chunk header. Marks the beginning of the data section.
            writer.Write(Constants.DATA_BYTES);
            // Size of the data section.
            writer.Write(pcmDataLength);
        }

        /// <inheritdoc />
        [Preserve]
        public async Task StreamRecordingAsync(ClipData clipData, Func<ReadOnlyMemory<byte>, Task> bufferCallback, CancellationToken cancellationToken, [CallerMemberName] string callingMethodName = null)
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
                WriteWavHeader(writer, clipData.Channels, clipData.SampleRate);
                writer.Flush();
                var headerData = stream.ToArray();

                if (headerData.Length != Constants.WavHeaderSize)
                {
                    Debug.LogWarning($"Failed to read all header content! {headerData.Length} != {Constants.WavHeaderSize}");
                }

                await bufferCallback.Invoke(headerData);
                await InternalStreamRecordAsync(clipData, null, bufferCallback, cancellationToken).ConfigureAwait(false);
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
        public async Task<Tuple<string, AudioClip>> StreamSaveToDiskAsync(ClipData clipData, string saveDirectory, Action<Tuple<string, AudioClip>> callback, CancellationToken cancellationToken, [CallerMemberName] string callingMethodName = null)
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
                var finalSamples = new float[clipData.MaxSamples ?? clipData.SampleRate * RecordingManager.MaxRecordingLength];
                var writer = new BinaryWriter(outStream);

                try
                {
                    WriteWavHeader(writer, clipData.Channels, clipData.SampleRate);

                    try
                    {
                        (finalSamples, totalSampleCount) = await InternalStreamRecordAsync(clipData, finalSamples, async buffer =>
                        {
                            writer.Write(buffer.Span);
                            await Task.Yield();
                        }, cancellationToken).ConfigureAwait(true);
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
                var newClip = AudioClip.Create(clipData.Name, microphoneData.Length, clipData.Channels, clipData.SampleRate, false);
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
                var bitsPerSample = 8 * (int)bitDepth;
                await using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
                await using var writer = new BinaryWriter(fileStream);
                WriteWavHeader(writer, channels, sampleRate, bitsPerSample, pcmData.Length);
                writer.Write(pcmData);
                writer.Flush();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private static async Task<(float[], int)> InternalStreamRecordAsync(ClipData clipData, float[] finalSamples, Func<ReadOnlyMemory<byte>, Task> bufferCallback, CancellationToken cancellationToken)
        {
            try
            {
                var sampleCount = 0;
                var shouldStop = false;
                var lastMicrophonePosition = 0;
                var sampleBuffer = new float[clipData.BufferSize];
                do
                {
                    await Awaiters.UnityMainThread; // ensure we're on main thread to call unity apis
                    var microphonePosition = Microphone.GetPosition(clipData.Device);

                    if (microphonePosition <= 0 && lastMicrophonePosition == 0)
                    {
                        // Skip this iteration if there's no new data
                        // wait for next update
                        continue;
                    }

                    var isLooping = microphonePosition < lastMicrophonePosition;
                    int samplesToWrite;

                    if (isLooping)
                    {
                        // Microphone loopback detected.
                        samplesToWrite = clipData.BufferSize - lastMicrophonePosition;

                        if (RecordingManager.EnableDebug)
                        {
                            Debug.LogWarning($"[{nameof(RecordingManager)}] Microphone loopback detected! [{microphonePosition} < {lastMicrophonePosition}] samples to write: {samplesToWrite}");
                        }
                    }
                    else
                    {
                        // No loopback, process normally.
                        samplesToWrite = microphonePosition - lastMicrophonePosition;
                    }

                    if (samplesToWrite > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        clipData.Clip.GetData(sampleBuffer, 0);

                        for (var i = 0; i < samplesToWrite; i++)
                        {
                            var bufferIndex = (lastMicrophonePosition + i) % clipData.BufferSize; // Wrap around index.
                            var value = sampleBuffer[bufferIndex];
                            var sample = (short)(Math.Max(-1f, Math.Min(1f, value)) * short.MaxValue);
                            var sampleData = new ReadOnlyMemory<byte>(new[]
                            {
                                (byte)(sample & byte.MaxValue),
                                (byte)(sample >> 8 & byte.MaxValue)
                            });

                            try
                            {
                                await bufferCallback.Invoke(sampleData).ConfigureAwait(false);
                            }
                            catch (Exception e)
                            {
                                Debug.LogException(new Exception($"[{nameof(WavEncoder)}] error occurred when buffering audio", e));
                            }

                            if (finalSamples is { Length: > 0 })
                            {
                                finalSamples[sampleCount * clipData.Channels + i] = sampleBuffer[bufferIndex];
                            }
                        }

                        lastMicrophonePosition = microphonePosition;
                        sampleCount += samplesToWrite;

                        if (RecordingManager.EnableDebug)
                        {
                            Debug.Log($"[{nameof(RecordingManager)}] State: {nameof(RecordingManager.IsRecording)}? {RecordingManager.IsRecording} | Wrote {samplesToWrite} samples | last mic pos: {lastMicrophonePosition} | total samples: {sampleCount} | isCancelled? {cancellationToken.IsCancellationRequested}");
                        }
                    }

                    if (clipData.MaxSamples.HasValue && sampleCount >= clipData.MaxSamples || cancellationToken.IsCancellationRequested)
                    {
                        if (RecordingManager.EnableDebug)
                        {
                            Debug.Log("Breaking internal record loop!");
                        }

                        shouldStop = true;
                    }
                } while (!shouldStop);
                return (finalSamples, sampleCount);
            }
            finally
            {
                RecordingManager.IsRecording = false;
                Microphone.End(clipData.Device);

                if (RecordingManager.EnableDebug)
                {
                    Debug.Log($"[{nameof(RecordingManager)}] Recording stopped");
                }
            }
        }
    }
}
