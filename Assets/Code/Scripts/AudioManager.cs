using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

[StructLayout(LayoutKind.Sequential)]
public struct PathData
{
    public float distance;
    public float gain;
    public Vector2 arrivalAngles;
    public int sourceId;
}

public class AudioManager : MonoBehaviour
{
    [SerializeField]
    BVHManager bvhManager;

    AcousticSource[] audioSources;

    AudioListener listener;

    public int maxDb = 120;

    public ComputeShader audioShader;

    public uint maxRays = 1024;

    ComputeBuffer rayBuffer;

    ComputeBuffer pathBuffer;
    ComputeBuffer countBuffer;
    int maxPaths = 10000;


    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        listener = FindFirstObjectByType<AudioListener>();
        //find all audio sources in the scene
        audioSources = FindObjectsByType<AcousticSource>(FindObjectsSortMode.InstanceID);

        pathBuffer = new ComputeBuffer(10,20, ComputeBufferType.Append);
        audioShader.SetBuffer(0, "pathBuffer", pathBuffer);
    }

    // Update is called once per frame
    void Update()
    {
        List<AcousticSource> sources = audioSources.Select(audioSource =>
        {
            float score = audioSource.GetScore(listenerPos: listener.transform.position);

            return (Target: audioSource, Score: score);

        })
        .OrderByDescending(x => x.Score)
        .Take(64)
        .Select(x => x.Target)
        .ToList();

        //Debug.Log("sorting done");

        pathBuffer.SetCounterValue(0);

        // 2. Set and Dispatch
        int kernel = audioShader.FindKernel("TraceRays");
        audioShader.SetBuffer(kernel, "pathBuffer", pathBuffer);
        audioShader.Dispatch(kernel, 1024 / 64, 1, 1);

        // 3. Request the data without stalling the CPU
        ComputeBuffer.CopyCount(pathBuffer, countBuffer, 0);
        AsyncGPUReadback.Request(pathBuffer, OnPathDataReadback);
    }

    void OnPathDataReadback(AsyncGPUReadbackRequest request)
    {
        if (request.hasError) return;

        var data = request.GetData<PathData>();

        // Note: Since this is async, you might want to read the countBuffer 
        // to know exactly how many elements in 'data' are valid.
        ProcessAudioPaths(data);
    }

    void ProcessAudioPaths(Unity.Collections.NativeArray<PathData> paths)
    {
        foreach (var path in paths)
        {
            if (path.distance <= 0) continue;

            float delaySeconds = path.distance / 343.0f;
            float volume = path.gain;
            float pan = path.arrivalAngles.x; // -1 to 1

            // SEND TO YOUR AUDIO ENGINE:
            //AudioEngine.AddTap(path.sourceId, delaySeconds, volume, pan);
        }
    }

    void InitBuffers()
    {
        // 20 bytes is the size of PathData (4+4+8+4)
        pathBuffer = new ComputeBuffer(maxPaths, 20, ComputeBufferType.Append);

        // An indirect arguments buffer or simple raw buffer to hold the count
        countBuffer = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Raw);
    }

    void DirectSound()
    {
        // set up a buffer with only relevant data

        // give it to the kernel

        //dispatch
    }
}

