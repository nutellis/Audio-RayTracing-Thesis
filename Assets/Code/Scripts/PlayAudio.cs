using System.Collections.Generic;
using UnityEngine;

public class PlayAudio : MonoBehaviour
{
    public AudioSource audioSource;
    public ComputeShader computeShader;

    [SerializeField] private int sampleCount = 1024;
    [SerializeField] private float pitchFactor = 1.2f;
    [SerializeField] private bool debugReadback = true;
    [SerializeField] private int debugEveryNFrames = 30;
    [SerializeField] private int maxBlocksPerFrame = 4;
    [SerializeField] private int maxBufferedSamples = 32768;
    [SerializeField] private string kernelName = "PitchUp";
    [SerializeField] private string inputBufferName = "inputSamples";
    [SerializeField] private string outputBufferName = "outputSamples";

    private float[] inputSamples;
    private float[] outputSamples;
    private ComputeBuffer inputBuffer;
    private ComputeBuffer outputBuffer;
    private int kernelHandle;

    private readonly Queue<float> pendingInputSamples = new Queue<float>();
    private readonly Queue<float> pendingOutputSamples = new Queue<float>();
    private readonly object queueLock = new object();

    void Start()
    {
        if (audioSource == null || computeShader == null)
        {
            Debug.LogWarning("PlayAudio needs both AudioSource and ComputeShader assigned.");
            enabled = false;
            return;
        }

        sampleCount = Mathf.NextPowerOfTwo(Mathf.Max(64, sampleCount));
        inputSamples = new float[sampleCount];
        outputSamples = new float[sampleCount];

        inputBuffer = new ComputeBuffer(sampleCount, sizeof(float));
        outputBuffer = new ComputeBuffer(sampleCount, sizeof(float));

        kernelHandle = computeShader.FindKernel(kernelName);
        computeShader.SetBuffer(kernelHandle, inputBufferName, inputBuffer);
        computeShader.SetBuffer(kernelHandle, outputBufferName, outputBuffer);
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            audioSource.Play();
        }

        ProcessAvailableBlocks();
    }

    void ProcessAvailableBlocks()
    {
        int blocksProcessed = 0;
        while (blocksProcessed < Mathf.Max(1, maxBlocksPerFrame))
        {
            lock (queueLock)
            {
                if (pendingInputSamples.Count < sampleCount)
                {
                    break;
                }

                for (int i = 0; i < sampleCount; i++)
                {
                    inputSamples[i] = pendingInputSamples.Dequeue();
                }
            }

            inputBuffer.SetData(inputSamples);
            computeShader.SetInt("sampleCount", sampleCount);
            computeShader.SetFloat("pitchFactor", Mathf.Max(0.0001f, pitchFactor));

            int groupsX = Mathf.CeilToInt(sampleCount / 256.0f);
            computeShader.Dispatch(kernelHandle, groupsX, 1, 1);

            outputBuffer.GetData(outputSamples);

            lock (queueLock)
            {
                for (int i = 0; i < outputSamples.Length; i++)
                {
                    pendingOutputSamples.Enqueue(outputSamples[i]);
                }

                while (pendingOutputSamples.Count > maxBufferedSamples)
                {
                    pendingOutputSamples.Dequeue();
                }
            }

            if (debugReadback && debugEveryNFrames > 0 && Time.frameCount % debugEveryNFrames == 0)
            {
                float inRms = ComputeRms(inputSamples);
                float outRms = ComputeRms(outputSamples);
                int inQueued;
                int outQueued;
                lock (queueLock)
                {
                    inQueued = pendingInputSamples.Count;
                    outQueued = pendingOutputSamples.Count;
                }

                Debug.Log($"PitchUp GPU frame={Time.frameCount} inRms={inRms:F6} outRms={outRms:F6} inQ={inQueued} outQ={outQueued}");
            }

            blocksProcessed++;
        }
    }

    void OnAudioFilterRead(float[] data, int channels)
    {
        if (channels <= 0 || data == null)
        {
            return;
        }

        int frameCount = data.Length / channels;
        lock (queueLock)
        {
            // Capture source audio as mono for the compute path.
            for (int frame = 0; frame < frameCount; frame++)
            {
                int baseIndex = frame * channels;
                float mono = 0f;
                for (int c = 0; c < channels; c++)
                {
                    mono += data[baseIndex + c];
                }

                mono /= channels;
                pendingInputSamples.Enqueue(mono);

                while (pendingInputSamples.Count > maxBufferedSamples)
                {
                    pendingInputSamples.Dequeue();
                }
            }

            // Replace output with processed mono when available.
            for (int frame = 0; frame < frameCount; frame++)
            {
                if (pendingOutputSamples.Count == 0)
                {
                    continue;
                }

                float processedSample = pendingOutputSamples.Dequeue();
                int baseIndex = frame * channels;
                for (int c = 0; c < channels; c++)
                {
                    data[baseIndex + c] = processedSample;
                }
            }
        }
    }

    float ComputeRms(float[] data)
    {
        float sumSquares = 0f;
        for (int i = 0; i < data.Length; i++)
        {
            sumSquares += data[i] * data[i];
        }

        return Mathf.Sqrt(sumSquares / Mathf.Max(data.Length, 1));
    }

    void OnDestroy()
    {
        inputBuffer?.Release();
        outputBuffer?.Release();
    }
}
