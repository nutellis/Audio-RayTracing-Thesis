using System;
using System.IO;
using System.Text;
using System.Collections.Concurrent;
using UnityEngine;
using System.Globalization;

public class AudioRecorderToCSV : MonoBehaviour
{
    [Tooltip("Minimum absolute amplitude to consider as sound activity")]
    public float amplitudeThreshold = 0f;

    [Tooltip("Frames of silence needed before auto-stopping the recording (48000 frames = 1 second at 48kHz)")]
    public int silenceFramesToStop = 48000;

    [Tooltip("CSV file name (will be saved to Application.persistentDataPath)")]
    public string fileName = "audio_recording.csv";

    [Tooltip("Receiver name to write into CSV. Defaults to GameObject name.")]
    public string receiverName = "";

    [Tooltip("Downsample factor: write every Nth frame (1 = every frame)")]
    public int sampleStride = 1;

    [Tooltip("If true, average across channels to one amplitude value")]
    public bool averageChannels = true;

    [Tooltip("Start recording automatically on Play")]
    public bool autoStart = true;

    [Tooltip("If true, save CSV into the project folder under Assets/Audio/. Otherwise uses persistentDataPath.")]
    public bool saveToAssetsFolder = true;

    [Tooltip("If true, write time as integer sample index instead of seconds")]
    public bool writeTimeAsSampleIndex = true;

    private StreamWriter writer;
    private readonly ConcurrentQueue<string> queue = new();

    private long totalFramesRecorded = 0; // frame = one sample frame across channels
    private int sampleRate;
    private string fullPath;
    private bool recording = false;
    private bool headerWritten = false;
    private long recordingStartFrame = 0;
    private long silenceFrameCount = 0;

    void Awake()
    {
        if (string.IsNullOrEmpty(receiverName)) receiverName = gameObject.name;
        sampleRate = AudioSettings.outputSampleRate;
        if (saveToAssetsFolder)
        {
            // Application.dataPath points to the project's Assets folder in the Editor
            string assetsAudio = Path.Combine(Application.dataPath, "Code/Scripts/CSV");
            Directory.CreateDirectory(assetsAudio);
            fullPath = Path.Combine(assetsAudio, fileName);
        }
        else
        {
            fullPath = Path.Combine(Application.persistentDataPath, fileName);
        }
    }

    void OnDisable()
    {
        StopRecording();
    }

    public void StartRecording()
    {
        if (recording) return;
        try
        {
            bool exists = File.Exists(fullPath);
            writer = new StreamWriter(fullPath, append: true, encoding: Encoding.UTF8);
            if (!exists || !headerWritten)
            {
                writer.WriteLine("receiver,time,amplitude");
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture, ",{0:F1},", (double)sampleRate));
                writer.Flush();
                headerWritten = true;
            }
            // Reset recording frame offset so time starts from 0 for each sound burst
            recordingStartFrame = totalFramesRecorded;
            silenceFrameCount = 0;
            recording = true;
        }
        catch (Exception e)
        {
            Debug.LogError("Failed to open CSV for writing: " + e);
            recording = false;
        }
    }

    public void StopRecording()
    {
        recording = false;
        // flush remaining queue
        FlushQueueToWriter();
        if (writer != null)
        {
            writer.Flush();
            writer.Close();
            writer = null;
        }
    }

    void OnDestroy()
    {
        StopRecording();
    }

    // Audio thread callback
    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (data == null || data.Length == 0) return;

        int frames = data.Length / channels;

        for (int f = 0; f < frames; f++)
        {
            float amp = ReadFrameAmplitude(data, channels, f);

            bool hasSoundActivity = Mathf.Abs(amp) > amplitudeThreshold;

            if (!recording && autoStart && hasSoundActivity)
            {
                StartRecording();
            }

            if (recording)
            {
                if (hasSoundActivity)
                {
                    silenceFrameCount = 0;
                    QueueSampleIfNeeded(amp);
                }
                else
                {
                    silenceFrameCount++;

                    // Queue trailing silence frames only (up to silenceFramesToStop)
                    if (silenceFrameCount <= silenceFramesToStop)
                    {
                        QueueSampleIfNeeded(amp);
                    }
                    else
                    {
                        // Stop after trailing silence window is complete
                        StopRecording();
                        totalFramesRecorded++;
                        continue;
                    }
                }
            }

            totalFramesRecorded++;
        }
    }

    private float ReadFrameAmplitude(float[] data, int channels, int frameIndex)
    {
        if (averageChannels)
        {
            float sum = 0f;
            int baseIndex = frameIndex * channels;
            for (int c = 0; c < channels; c++) sum += data[baseIndex + c];
            return sum / channels;
        }

        return data[frameIndex * channels];
    }

    private void QueueSampleIfNeeded(float amp)
    {
        if (!recording || (totalFramesRecorded % sampleStride) != 0) return;

        // Calculate time relative to recording start; will be ~0 for first frame of each burst
        long timeInRecording = totalFramesRecorded - recordingStartFrame;
        string timeValue = writeTimeAsSampleIndex
            ? timeInRecording.ToString(CultureInfo.InvariantCulture)
            : (timeInRecording / sampleRate).ToString("F6", CultureInfo.InvariantCulture);

        // format: receiver,time,amplitude
        // amplitude uses high-precision formatting to preserve tiny values like 6.18497640734293e-17
        string line = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2:G17}", receiverName, timeValue, (double)amp);
        queue.Enqueue(line);
    }

    void Update()
    {
        if (!recording) return;
        FlushQueueToWriter();
    }

    private void FlushQueueToWriter()
    {
        if (writer == null) return;
        while (queue.TryDequeue(out var line))
        {
            writer.WriteLine(line);
        }
        writer.Flush();
    }

    // Small helper for quick testing via Inspector
    [ContextMenu("Log Output Path")]
    public void LogOutputPath()
    {
        Debug.Log("CSV will be saved to: " + fullPath);
    }
}
