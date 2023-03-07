using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Utilities.Async;

namespace Utilities.Encoding.Wav
{
    public static class RecordingManager
    {
        private static int maxRecordingLength = 300;

        private static readonly object recordingLock = new object();

        private static bool isRecording;

        private static bool isProcessing;

        private static CancellationTokenSource cancellationTokenSource;

        /// <summary>
        /// Max Recording length in seconds.
        /// The default value is 300 seconds (5 min)
        /// </summary>
        public static int MaxRecordingLength
        {
            get => maxRecordingLength;
            set
            {
                if (value != maxRecordingLength)
                {
                    if (value > 300)
                    {
                        maxRecordingLength = 300;
                    }
                    else if (value < 30)
                    {
                        maxRecordingLength = 30;
                    }
                    else
                    {
                        maxRecordingLength = value;
                    }
                }
            }
        }

        /// <summary>
        /// Is the recording manager currently recording?
        /// </summary>
        public static bool IsRecording
        {
            get
            {
                bool recording;

                lock (recordingLock)
                {
                    recording = isRecording;
                }

                return recording;
            }
        }

        /// <summary>
        /// Is the recording manager currently processing the last recording?
        /// </summary>
        public static bool IsProcessing
        {
            get
            {
                bool processing;

                lock (recordingLock)
                {
                    processing = isProcessing;
                }

                return processing;
            }
        }

        /// <summary>
        /// Indicates that the recording manager is either recording or processing the previous recording.
        /// </summary>
        public static bool IsBusy => IsProcessing || IsRecording;

        public static bool EnableDebug { get; set; }

        /// <summary>
        /// The event that is raised when an audio clip has finished recording and has been saved to disk.
        /// </summary>
        public static event Action<Tuple<string, AudioClip>> OnClipRecorded;

        private static string defaultSaveLocation;

        /// <summary>
        /// Defaults to /Assets/Resources/Recordings in editor.<br/>
        /// Defaults to /Application/TempCachePath/Recordings at runtime.
        /// </summary>
        public static string DefaultSaveLocation
        {
            get
            {
                if (string.IsNullOrWhiteSpace(defaultSaveLocation))
                {
#if UNITY_EDITOR
                    defaultSaveLocation = $"{Application.dataPath}/Resources/Recordings";
#else
                    defaultSaveLocation = $"{Application.temporaryCachePath}/Recordings";
#endif
                }

                return defaultSaveLocation;
            }
            set => defaultSaveLocation = value;
        }

        /// <summary>
        /// Starts the recording process.
        /// </summary>
        /// <param name="clipName">Optional, name for the clip.</param>
        /// <param name="saveDirectory">Optional, the directory to save the clip.</param>
        /// <param name="callback">Optional, callback when recording is complete.</param>
        /// <param name="cancellationToken">Optional, task cancellation token.</param>
        public static async void StartRecording(string clipName = null, string saveDirectory = null, Action<Tuple<string, AudioClip>> callback = null, CancellationToken cancellationToken = default)
        {
            var result = await StartRecordingAsync(clipName, saveDirectory, cancellationToken).ConfigureAwait(false);
            callback?.Invoke(result);
        }

        /// <summary>
        /// Starts the recording process.
        /// </summary>
        /// <param name="clipName">Optional, name for the clip.</param>
        /// <param name="saveDirectory">Optional, the directory to save the clip.</param>
        /// <param name="cancellationToken">Optional, task cancellation token.</param>
        public static async Task<Tuple<string, AudioClip>> StartRecordingAsync(string clipName = null, string saveDirectory = null, CancellationToken cancellationToken = default)
        {
            if (IsBusy)
            {
                Debug.LogWarning($"[{nameof(RecordingManager)}] Recording already in progress!");
                return null;
            }

            lock (recordingLock)
            {
                isRecording = true;
            }

            if (string.IsNullOrWhiteSpace(saveDirectory))
            {
                saveDirectory = DefaultSaveLocation;
            }

            var clip = Microphone.Start(null, false, MaxRecordingLength, 44100);

            if (EnableDebug)
            {
                Microphone.GetDeviceCaps(null, out var minFreq, out var maxFreq);
                Debug.Log($"[{nameof(RecordingManager)}] Recording devices: {string.Join(", ", Microphone.devices)} | minFreq: {minFreq} | maxFreq {maxFreq} | clip freq: {clip.frequency} | samples: {clip.samples}");
            }

            clip.name = (string.IsNullOrWhiteSpace(clipName) ? Guid.NewGuid().ToString() : clipName)!;

            lock (recordingLock)
            {
                cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            }

#if UNITY_EDITOR
            if (EnableDebug)
            {
                Debug.Log($"[{nameof(RecordingManager)}] <>Disable auto refresh<>");
            }

            UnityEditor.AssetDatabase.DisallowAutoRefresh();
#endif

            try
            {
                return await StreamSaveToDiskAsync(clip, saveDirectory, cancellationTokenSource.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Debug.LogError($"[{nameof(RecordingManager)}] Failed to record {clipName}!\n{e}");
            }
            finally
            {
                lock (recordingLock)
                {
                    isRecording = false;
                    isProcessing = false;
                }
#if UNITY_EDITOR
                await Awaiters.UnityMainThread;

                if (EnableDebug)
                {
                    Debug.Log($"[{nameof(RecordingManager)}] <>Enable auto refresh<>");
                }

                UnityEditor.AssetDatabase.AllowAutoRefresh();
#endif
            }

            return null;
        }

        private static async Task<Tuple<string, AudioClip>> StreamSaveToDiskAsync(AudioClip clip, string saveDirectory, CancellationToken cancellationToken)
        {
            if (EnableDebug)
            {
                Debug.Log($"[{nameof(RecordingManager)}] Recording started...");
            }

            lock (recordingLock)
            {
                isProcessing = true;
            }

            var lastPosition = 0;
            var clipName = clip.name;
            var channels = clip.channels;
            var frequency = clip.frequency;
            var sampleCount = clip.samples;
            var samples = new float[sampleCount * channels];

            // ReSharper disable once MethodSupportsCancellation
            await Task.Delay(1).ConfigureAwait(false);

            if (!Directory.Exists(saveDirectory))
            {
                Directory.CreateDirectory(saveDirectory);
            }

            var path = $"{saveDirectory}/{clipName}.ogg";

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
                outStream.Write(new byte[Constants.WavHeaderSize]);

                while (true)
                {
                    await Awaiters.UnityMainThread;
                    var currentPosition = Microphone.GetPosition(null);
                    clip.GetData(samples, 0);

                    if (shouldStop)
                    {
                        Microphone.End(null);
                    }

                    // ReSharper disable once MethodSupportsCancellation
                    await Task.Delay(1).ConfigureAwait(false);

                    if (currentPosition != 0)
                    {
                        foreach (var value in samples)
                        {
                            // ReSharper disable once MethodSupportsCancellation
                            await outStream.WriteAsync(BitConverter.GetBytes((short)(value * Constants.RescaleFactor)));
                        }

                        lastPosition = currentPosition;
                    }

                    await Awaiters.UnityMainThread;

                    if (EnableDebug)
                    {
                        Debug.Log($"[{nameof(RecordingManager)}] State: {nameof(IsRecording)}? {IsRecording} | {currentPosition} | isCancelled? {cancellationToken.IsCancellationRequested}");
                    }

                    if (currentPosition == sampleCount ||
                        cancellationToken.IsCancellationRequested)
                    {
                        if (IsRecording)
                        {
                            lock (recordingLock)
                            {
                                isRecording = false;
                            }

                            if (EnableDebug)
                            {
                                Debug.Log($"[{nameof(RecordingManager)}] Finished recording...");
                            }
                        }

                        if (shouldStop)
                        {
                            if (EnableDebug)
                            {
                                Debug.Log($"[{nameof(RecordingManager)}] Writing end of stream...");
                            }

                            // write header
                            outStream.Seek(0, SeekOrigin.Begin);
                            // Marks the file as a riff file. Characters are each 1 byte long.
                            // ReSharper disable once MethodSupportsCancellation
                            await outStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Constants.RIFF));
                            // Size of the overall file - 8 bytes, in bytes (32-bit integer). Typically, you’d fill this in after creation.
                            // ReSharper disable once MethodSupportsCancellation
                            await outStream.WriteAsync(BitConverter.GetBytes(outStream.Length - 8));
                            // File Type Header. For our purposes, it always equals “WAVE”.
                            // ReSharper disable once MethodSupportsCancellation
                            await outStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Constants.WAVE));
                            // Format chunk marker. Includes trailing null
                            // ReSharper disable once MethodSupportsCancellation
                            await outStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Constants.FMT));
                            // Length of format data as listed above
                            // ReSharper disable once MethodSupportsCancellation
                            await outStream.WriteAsync(BitConverter.GetBytes(16u));
                            // Type of format (1 is PCM) - 2 byte integer
                            // ReSharper disable once MethodSupportsCancellation
                            await outStream.WriteAsync(BitConverter.GetBytes((ushort)1));
                            // Number of Channels - 2 byte integer
                            // ReSharper disable once MethodSupportsCancellation
                            await outStream.WriteAsync(BitConverter.GetBytes(channels));
                            // Sample Rate - 32 byte integer. Common values are 44100 (CD), 48000 (DAT). Sample Rate = Number of Samples per second, or Hertz.
                            // ReSharper disable once MethodSupportsCancellation
                            await outStream.WriteAsync(BitConverter.GetBytes(frequency));
                            // (Sample Rate * BitsPerSample * Channels) / 8.
                            // ReSharper disable once MethodSupportsCancellation
                            await outStream.WriteAsync(BitConverter.GetBytes(frequency * channels * 2));
                            // (BitsPerSample * Channels) / 8.1 - 8 bit mono2 - 8 bit stereo/16 bit mono4 - 16 bit stereo
                            // ReSharper disable once MethodSupportsCancellation
                            await outStream.WriteAsync(BitConverter.GetBytes((ushort)(channels * 2)));
                            // Bits per sample
                            // ReSharper disable once MethodSupportsCancellation
                            await outStream.WriteAsync(BitConverter.GetBytes((ushort)16));
                            // “data” chunk header. Marks the beginning of the data section.
                            // ReSharper disable once MethodSupportsCancellation
                            await outStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Constants.DATA));
                            // Size of the data section.
                            // ReSharper disable once MethodSupportsCancellation
                            await outStream.WriteAsync(BitConverter.GetBytes(sampleCount * channels * 2));
                            break;
                        }

                        if (EnableDebug)
                        {
                            Debug.Log($"[{nameof(RecordingManager)}] Stop stream requested...");
                        }

                        // delays stopping to make sure we process the last bits of the clip
                        shouldStop = true;
                    }
                }

                if (EnableDebug)
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

            if (EnableDebug)
            {
                Debug.Log($"[{nameof(RecordingManager)}] Copying recording data stream...");
            }

            var microphoneData = new float[lastPosition];
            Array.Copy(samples, microphoneData, microphoneData.Length - 1);

            await Awaiters.UnityMainThread;

            // Create a copy.
            var newClip = AudioClip.Create(clipName, microphoneData.Length, 1, 44100, false);
            newClip.SetData(microphoneData, 0);
            var result = new Tuple<string, AudioClip>(path, newClip);

            lock (recordingLock)
            {
                isProcessing = false;
            }

            if (EnableDebug)
            {
                Debug.Log($"[{nameof(RecordingManager)}] Finished processing...");
            }

            OnClipRecorded?.Invoke(result);
            return result;
        }

        /// <summary>
        /// Ends the recording process if in progress.
        /// </summary>
        public static void EndRecording()
        {
            if (!IsRecording) { return; }

            lock (recordingLock)
            {
                if (cancellationTokenSource is { IsCancellationRequested: false })
                {
                    cancellationTokenSource.Cancel();

                    if (EnableDebug)
                    {
                        Debug.Log($"[{nameof(RecordingManager)}] End Recording requested...");
                    }
                }
            }
        }
    }
}
