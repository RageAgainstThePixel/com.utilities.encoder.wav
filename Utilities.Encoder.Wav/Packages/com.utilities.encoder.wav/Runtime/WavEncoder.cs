// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Utilities.Async;
using Utilities.Audio;

namespace Utilities.Encoding.Wav
{
    public class WavEncoder : IEncoder
    {
        public async Task<Tuple<string, AudioClip>> StreamSaveToDiskAsync(AudioClip clip, string saveDirectory, CancellationToken cancellationToken, Action<Tuple<string, AudioClip>> callback = null, [CallerMemberName] string callingMethodName = null)
        {
            if (callingMethodName != nameof(RecordingManager.StartRecordingAsync))
            {
                throw new InvalidOperationException($"{nameof(StreamSaveToDiskAsync)} can only be called from {nameof(RecordingManager.StartRecordingAsync)}");
            }

            if (!Microphone.IsRecording(null))
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

            var lastPosition = 0;
            var clipName = clip.name;
            var channels = clip.channels;
            var frequency = clip.frequency;
            var sampleCount = clip.samples;
            var samples = new float[sampleCount * channels];
            var pcmData = new byte[samples.Length * sizeof(float)];

            // ReSharper disable once MethodSupportsCancellation
            await Task.Delay(1).ConfigureAwait(false);

            if (!Directory.Exists(saveDirectory))
            {
                Directory.CreateDirectory(saveDirectory);
            }

            var path = $"{saveDirectory}/{clipName}.wav";

            if (File.Exists(path))
            {
                Debug.LogWarning($"[{nameof(RecordingManager)}] {path} already exists, attempting to delete");
                File.Delete(path);
            }

            var outStream = new FileStream(path, FileMode.Create, FileAccess.Write);

            try
            {
                var shouldStop = false;

                // prep header
                // ReSharper disable once MethodSupportsCancellation
                await outStream.WriteAsync(new byte[Constants.WavHeaderSize]);
                lastPosition += Constants.WavHeaderSize;

                while (true)
                {
                    await Awaiters.UnityMainThread;
                    var currentPosition = Microphone.GetPosition(null);

                    // ReSharper disable once ConditionIsAlwaysTrueOrFalse
                    if (clip != null)
                    {
                        clip.GetData(samples, 0);
                    }

                    if (shouldStop)
                    {
                        Microphone.End(null);
                    }

                    // ReSharper disable once MethodSupportsCancellation
                    await Task.Delay(1).ConfigureAwait(false);

                    if (currentPosition != 0)
                    {
                        var sampleIndex = 0;

                        foreach (var pcm in samples)
                        {
                            var sample = (short)(pcm * Audio.Constants.RescaleFactor);
                            pcmData[sampleIndex++] = (byte)(sample >> 0);
                            pcmData[sampleIndex++] = (byte)(sample >> 8);
                        }

                        // ReSharper disable once MethodSupportsCancellation
                        await outStream.WriteAsync(pcmData, lastPosition, sampleIndex);

                        lastPosition = currentPosition;
                    }

                    await Awaiters.UnityMainThread;

                    if (RecordingManager.EnableDebug)
                    {
                        Debug.Log($"[{nameof(RecordingManager)}] State: {nameof(RecordingManager.IsRecording)}? {RecordingManager.IsRecording} | {currentPosition} | isCancelled? {cancellationToken.IsCancellationRequested}");
                    }

                    if (currentPosition == sampleCount ||
                        cancellationToken.IsCancellationRequested)
                    {
                        if (RecordingManager.IsRecording)
                        {
                            RecordingManager.IsRecording = false;

                            if (RecordingManager.EnableDebug)
                            {
                                Debug.Log($"[{nameof(RecordingManager)}] Finished recording...");
                            }
                        }

                        if (shouldStop)
                        {
                            if (RecordingManager.EnableDebug)
                            {
                                Debug.Log($"[{nameof(RecordingManager)}] Writing end of stream...");
                            }

                            // write header
                            outStream.Seek(0, SeekOrigin.Begin);
                            // ReSharper disable once MethodSupportsCancellation
                            // Marks the file as a riff file. Characters are each 1 byte long.
                            await outStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Constants.RIFF), 0, 4).ConfigureAwait(false);
                            // ReSharper disable once MethodSupportsCancellation
                            // Size of the overall file - 8 bytes, in bytes (32-bit integer). Typically, you’d fill this in after creation.
                            await outStream.WriteAsync(BitConverter.GetBytes(Constants.WavHeaderSize + outStream.Length - 8), 0, 4).ConfigureAwait(false);
                            // ReSharper disable once MethodSupportsCancellation
                            // File Type Header. For our purposes, it always equals “WAVE”.
                            await outStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Constants.WAVE), 0, 4).ConfigureAwait(false);
                            // ReSharper disable once MethodSupportsCancellation
                            // Format chunk marker. Includes trailing null
                            await outStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Constants.FMT), 0, 4).ConfigureAwait(false);
                            // ReSharper disable once MethodSupportsCancellation
                            // Length of format data as listed above
                            await outStream.WriteAsync(BitConverter.GetBytes(16u), 0, 4).ConfigureAwait(false);
                            // ReSharper disable once MethodSupportsCancellation
                            // Type of format (1 is PCM) - 2 byte integer
                            await outStream.WriteAsync(BitConverter.GetBytes((ushort)1), 0, 2).ConfigureAwait(false);
                            // ReSharper disable once MethodSupportsCancellation
                            // Number of Channels - 2 byte integer
                            await outStream.WriteAsync(BitConverter.GetBytes(channels), 0, 2).ConfigureAwait(false);
                            // ReSharper disable once MethodSupportsCancellation
                            // Sample Rate - 32 byte integer. Common values are 44100 (CD), 48000 (DAT). Sample Rate = Number of Samples per second, or Hertz.
                            await outStream.WriteAsync(BitConverter.GetBytes(frequency), 0, 4).ConfigureAwait(false);
                            // ReSharper disable once MethodSupportsCancellation
                            // (Sample Rate * BitsPerSample * Channels) / 8.
                            await outStream.WriteAsync(BitConverter.GetBytes(frequency * channels * 2), 0, 4).ConfigureAwait(false);
                            // ReSharper disable once MethodSupportsCancellation
                            // (BitsPerSample * Channels) / 8.1 - 8 bit mono2 - 8 bit stereo/16 bit mono4 - 16 bit stereo
                            await outStream.WriteAsync(BitConverter.GetBytes((ushort)(channels * 2)), 0, 2).ConfigureAwait(false);
                            // ReSharper disable once MethodSupportsCancellation
                            // Bits per sample
                            await outStream.WriteAsync(BitConverter.GetBytes((ushort)16), 0, 2).ConfigureAwait(false);
                            // ReSharper disable once MethodSupportsCancellation
                            // “data” chunk header. Marks the beginning of the data section.
                            await outStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Constants.DATA), 0, 4).ConfigureAwait(false);
                            // ReSharper disable once MethodSupportsCancellation
                            // Size of the data section.
                            await outStream.WriteAsync(BitConverter.GetBytes(sampleCount * channels * 2), 0, 4).ConfigureAwait(false);
                            break;
                        }

                        if (RecordingManager.EnableDebug)
                        {
                            Debug.Log($"[{nameof(RecordingManager)}] Stop stream requested...");
                        }

                        // delays stopping to make sure we process the last bits of the clip
                        shouldStop = true;
                    }
                }

                if (RecordingManager.EnableDebug)
                {
                    Debug.Log($"[{nameof(RecordingManager)}] Flush stream...");
                }

                // ReSharper disable once MethodSupportsCancellation
                await outStream.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Debug.LogError(e);
            }
            finally
            {
                await outStream.DisposeAsync().ConfigureAwait(false);
            }

            if (RecordingManager.EnableDebug)
            {
                Debug.Log($"[{nameof(RecordingManager)}] Copying recording data stream...");
            }

            var microphoneData = new float[lastPosition];
            Array.Copy(samples, microphoneData, microphoneData.Length - 1);

            await Awaiters.UnityMainThread;

            // Create a copy.
            var newClip = AudioClip.Create(clipName, microphoneData.Length, channels, frequency, false);
            newClip.SetData(microphoneData, 0);
            var result = new Tuple<string, AudioClip>(path, newClip);

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
