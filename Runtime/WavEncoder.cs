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
        internal static byte[] EncodeWav(byte[] pcmData, int channels, int sampleRate, int bitsPerSample)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);

            // We'll calculate the file size and protect against overflow.
            int fileSize;
            var blockAlign = bitsPerSample * channels / 8;
            var bytesPerSecond = sampleRate * blockAlign;

            checked
            {
                fileSize = 36 + pcmData.Length;
            }

            // Marks the file as a riff file. Characters are each 1 byte long.
            writer.Write(Constants.RIFF_BYTES);
            // Size of the overall file - 8 bytes, in bytes (32-bit integer). Typically, you’d fill this in after creation.
            writer.Write(fileSize - 8); // Subtract the RIFF header (4 bytes) and file size field (4 bytes).
            // File Type Header. For our purposes, it always equals “WAVE”.
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
            // “data” chunk header. Marks the beginning of the data section.
            writer.Write(Constants.DATA_BYTES);
            // Size of the data section.
            writer.Write(pcmData.Length);
            // The audio data
            writer.Write(pcmData);
            writer.Flush();
            return stream.ToArray();
        }

        [Preserve]
        public async Task<Tuple<string, AudioClip>> StreamSaveToDiskAsync(AudioClip clip, string saveDirectory, CancellationToken cancellationToken, Action<Tuple<string, AudioClip>> callback = null, [CallerMemberName] string callingMethodName = null)
        {
            if (callingMethodName != nameof(RecordingManager.StartRecordingAsync))
            {
                throw new InvalidOperationException($"{nameof(StreamSaveToDiskAsync)} can only be called from {nameof(RecordingManager.StartRecordingAsync)}");
            }

            var device = RecordingManager.DefaultRecordingDevice;

            if (!Microphone.IsRecording(device))
            {
                throw new InvalidOperationException("Microphone is not initialized!");
            }

            if (RecordingManager.IsProcessing)
            {
                throw new AccessViolationException("Recording already in progress!");
            }

            RecordingManager.IsProcessing = true;

            if (RecordingManager.EnableDebug)
            {
                Debug.Log($"[{nameof(RecordingManager)}] Recording started...");
            }

            var sampleCount = 0;
            var clipName = clip.name;
            var channels = clip.channels;
            var bufferSize = clip.samples;
            var sampleRate = clip.frequency;
            var sampleBuffer = new float[bufferSize];
            var maxSamples = RecordingManager.MaxRecordingLength * sampleRate;
            var finalSamples = new float[maxSamples];

            if (RecordingManager.EnableDebug)
            {
                Debug.Log($"[{nameof(RecordingManager)}] Initializing data for {clipName}. Channels: {channels}, Sample Rate: {sampleRate}, Sample buffer size: {bufferSize}, Max Sample Length: {maxSamples}");
            }

            Stream finalStream;
            var outputPath = string.Empty;

            if (!string.IsNullOrWhiteSpace(saveDirectory))
            {
                if (!Directory.Exists(saveDirectory))
                {
                    Directory.CreateDirectory(saveDirectory);
                }

                outputPath = $"{saveDirectory}/{clipName}.wav";

                if (File.Exists(outputPath))
                {
                    Debug.LogWarning($"[{nameof(RecordingManager)}] {outputPath} already exists, attempting to delete...");
                    File.Delete(outputPath);
                }

                finalStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            }
            else
            {
                finalStream = new MemoryStream();
            }

            var writer = new BinaryWriter(finalStream);

            try
            {
                // setup recording
                var shouldStop = false;
                var lastMicrophonePosition = 0;

                // initialize file header
                var header = EncodeWav(Array.Empty<byte>(), channels, sampleRate, 16);
                writer.Write(header);

                try
                {
                    do
                    {
                        // Expected to be on the Unity Main Thread.
                        await Awaiters.UnityMainThread;
                        var microphonePosition = Microphone.GetPosition(device);

                        if (microphonePosition <= 0 && lastMicrophonePosition == 0)
                        {
                            // Skip this iteration if there's no new data
                            // wait for next update
                            await Awaiters.UnityMainThread;
                            continue;
                        }

                        var isLooping = microphonePosition < lastMicrophonePosition;
                        int samplesToWrite;

                        if (isLooping)
                        {
                            // Microphone loopback detected.
                            samplesToWrite = bufferSize - lastMicrophonePosition;

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
                            clip.GetData(sampleBuffer, 0);

                            for (var i = 0; i < samplesToWrite; i++)
                            {
                                // Write pcm data to file.
                                var bufferIndex = (lastMicrophonePosition + i) % bufferSize; // Wrap around index.
                                var value = sampleBuffer[bufferIndex];
                                var sample = (short)(Math.Max(-1f, Math.Min(1f, value)) * short.MaxValue);
                                writer.Write((byte)(sample & byte.MaxValue));
                                writer.Write((byte)((sample >> 8) & byte.MaxValue));

                                // Store the sample in the final samples array.
                                finalSamples[sampleCount * channels + i] = sampleBuffer[bufferIndex];
                            }

                            lastMicrophonePosition = microphonePosition;
                            sampleCount += samplesToWrite;

                            if (RecordingManager.EnableDebug)
                            {
                                Debug.Log($"[{nameof(RecordingManager)}] State: {nameof(RecordingManager.IsRecording)}? {RecordingManager.IsRecording} | Wrote {samplesToWrite} samples | last mic pos: {lastMicrophonePosition} | total samples: {sampleCount} | isCancelled? {cancellationToken.IsCancellationRequested}");
                            }
                        }

                        // Check if we have recorded enough samples or if cancellation has been requested
                        if (sampleCount >= maxSamples || cancellationToken.IsCancellationRequested)
                        {
                            // Finalize the WAV file and cleanup
                            shouldStop = true;
                        }
                    } while (!shouldStop);
                }
                finally
                {
                    RecordingManager.IsRecording = false;
                    Microphone.End(device);

                    if (RecordingManager.EnableDebug)
                    {
                        Debug.Log($"[{nameof(RecordingManager)}] Recording stopped, writing end of stream...");
                    }

                    var fileSize = finalStream.Position;
                    // rewind and write header file size
                    writer.Seek(4, SeekOrigin.Begin);
                    // Size of the overall file - 8 bytes, in bytes (32-bit integer).
                    writer.Write((int)(fileSize - 8));
                    // rewind and write data size
                    writer.Seek(40, SeekOrigin.Begin);
                    // Size of the data section.
                    writer.Write(fileSize - Constants.WavHeaderSize);

                    if (RecordingManager.EnableDebug)
                    {
                        Debug.Log($"[{nameof(RecordingManager)}] Flush stream...");
                    }

                    writer.Flush();
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[{nameof(RecordingManager)}] Failed to record clip!\n{e}");
                RecordingManager.IsRecording = false;
                RecordingManager.IsProcessing = false;
                return null;
            }
            finally
            {
                if (RecordingManager.EnableDebug)
                {
                    Debug.Log($"[{nameof(RecordingManager)}] Dispose stream...");
                }

                await writer.DisposeAsync().ConfigureAwait(false);
                await finalStream.DisposeAsync().ConfigureAwait(false);
            }

            if (RecordingManager.EnableDebug)
            {
                Debug.Log($"[{nameof(RecordingManager)}] Finalized file write. Copying recording into new AudioClip");
            }

            // Trim the final samples down into the recorded range.
            var microphoneData = new float[sampleCount * channels];
            Array.Copy(finalSamples, microphoneData, microphoneData.Length);

            // Expected to be on the Unity Main Thread.
            await Awaiters.UnityMainThread;

            // Create a new copy of the final recorded clip.
            var newClip = AudioClip.Create(clipName, microphoneData.Length, channels, sampleRate, false);
            newClip.SetData(microphoneData, 0);
            var result = new Tuple<string, AudioClip>(outputPath, newClip);

            RecordingManager.IsProcessing = false;

            if (RecordingManager.EnableDebug)
            {
                Debug.Log($"[{nameof(RecordingManager)}] Finished processing...");
            }

            callback?.Invoke(result);
            return result;
        }
    }
}
