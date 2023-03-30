// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Utilities.Async;
using Utilities.Audio;
using Microphone = Utilities.Audio.Microphone;

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

            int lastPosition;
            var clipName = clip.name;
            var channels = clip.channels;
            var frequency = clip.frequency;
            var sampleCount = clip.samples;
            var samples = new float[sampleCount * channels];
            var readBuffer = new byte[samples.Length * sizeof(short)];

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
            var writer = new BinaryWriter(outStream);

            try
            {
                // Marks the file as a riff file. Characters are each 1 byte long.
                writer.Write(Constants.RIFF.ToCharArray());
                // Size of the overall file - 8 bytes, in bytes (32-bit integer).
                writer.Write(0); // temp data
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
                writer.Write(0); // temp data

                lastPosition = Constants.WavHeaderSize;

                var shouldStop = false;

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
                        var length = currentPosition - lastPosition;

                        foreach (var pcm in samples)
                        {
                            var sample = (short)(pcm * short.MaxValue);
                            readBuffer[sampleIndex++] = (byte)(sample >> 0);
                            readBuffer[sampleIndex++] = (byte)(sample >> 8);
                        }

                        writer.Write(new ReadOnlySpan<byte>(readBuffer, lastPosition * sizeof(short), length * sizeof(short)));
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

                            var fileSize = outStream.Position;
                            // rewind and write header file size
                            writer.Seek(4, SeekOrigin.Begin);
                            // Size of the overall file - 8 bytes, in bytes (32-bit integer).
                            writer.Write((int)(fileSize - 8));
                            // rewind and write data size
                            writer.Seek(40, SeekOrigin.Begin);
                            // Size of the data section.
                            writer.Write(lastPosition * sizeof(short));
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

                writer.Flush();
                // ReSharper disable once MethodSupportsCancellation
                await outStream.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Debug.LogError(e);
                return null;
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
