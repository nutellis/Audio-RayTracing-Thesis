using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

[StructLayout(LayoutKind.Sequential)]
public struct PathData
{
    public Vector2 arrivalAngles;
    public float distance;
    public float gain;
    
    public int sourceId;
}
public struct SourceData
{
    public Vector3 origin;
    
    public int sourceId;
};


public class AudioManager : MonoBehaviour
{
    [SerializeField]
    BVHManager bvhManager;

    AcousticSource[] audioSources;

    ListenerController listener;

    public int maxDb = 120;

    public ComputeShader audioShader;

    public int maxRays = 1024;

    ComputeBuffer sourcesBuffer;

    ComputeBuffer pathBuffer;
    ComputeBuffer countBuffer;
    int maxPaths = 10000;

    int traceKernel;



    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        listener = FindFirstObjectByType <ListenerController> ();

        listener.InitializeRays(maxRays);

        //find all audio sources in the scene
        audioSources = FindObjectsByType<AcousticSource>(FindObjectsSortMode.InstanceID);

        traceKernel = audioShader.FindKernel("TraceRays");
        
        // successful paths data
        pathBuffer = new ComputeBuffer(1, Marshal.SizeOf(typeof(PathData))); // , ComputeBufferType.Append);

        audioShader.SetBuffer(traceKernel, "pathBuffer", pathBuffer);

        //sources buffer
        sourcesBuffer = new ComputeBuffer(audioSources.Length < 64 ? audioSources.Length : 64, Marshal.SizeOf(typeof(SourceData)));

        audioShader.SetBuffer(traceKernel, "sources", sourcesBuffer);
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

        SetupSourceBuffer(sources);
    }

    private void LateUpdate()
    {
        //update the tree
        bvhManager.UpdateBVH();

        audioShader.SetBuffer(traceKernel, "tree", bvhManager.GetBVHBuffer());

        //trace rays
        pathBuffer.SetCounterValue(0);

        audioShader.SetBuffer(traceKernel, "pathBuffer", pathBuffer);
        audioShader.Dispatch(traceKernel, maxRays / 64, 1, 1);


        //debug
        PathData[] paths = new PathData[1];
        pathBuffer.GetData(paths);

        for(int i = 0; i < paths.Length; i++)
        {
            if (paths[i].distance > 0)
            {
                Debug.Log($"Path {i}: Distance={paths[i].distance}, Gain={paths[i].gain}, ArrivalAngles={paths[i].arrivalAngles}, SourceId={paths[i].sourceId}");
            }
        }
        Debug.Log($"Total valid paths: {paths.Count(p => p.distance > 0)}");
        // ComputeBuffer.CopyCount(pathBuffer, countBuffer, 0);
        // AsyncGPUReadback.Request(pathBuffer, OnPathDataReadback);
    }

    private void OnDestroy()
    {
        pathBuffer?.Release();
        countBuffer?.Release();
        sourcesBuffer?.Release();
    }
    void OnPathDataReadback(AsyncGPUReadbackRequest request)
    {
        if (request.hasError) return;

        var data = request.GetData<PathData>().ToArray();

        // Note: Since this is async, you might want to read the countBuffer 
        // to know exactly how many elements in 'data' are valid.
        ProcessAudioPaths(data);
    }

    void ProcessAudioPaths(PathData[] paths)
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

    void SetupSourceBuffer(List<AcousticSource> sources)
    {
            SourceData[] sourceDataArray = sources.Select((source, index) => new SourceData
            {
                origin = source.transform.position,
                sourceId = source.GetInstanceID()
            }).ToArray();

            sourcesBuffer.SetData(sourceDataArray);
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

