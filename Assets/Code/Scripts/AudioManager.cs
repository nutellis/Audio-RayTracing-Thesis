using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

[StructLayout(LayoutKind.Sequential)]
public struct PathData
{
    public Vector2 arrivalAngles;
    public float distance;
    public float gain;
    
    public int sourceId;

    public Vector3 padding;
}
public struct SourceData
{
    public Vector3 origin;
    
    public int sourceId;
};


public struct DebugInfo
{
    public int counter;
    public int counter2;

    public Vector2 padding;
}


public class AudioManager : MonoBehaviour
{
    [SerializeField]
    BVHManager bvhManager;

    Dictionary<int, AcousticSource> audioSources;

    ListenerController listener;

    public int maxDb = 120;

    public ComputeShader audioShader;
    
    public int initialRays = 1024;

    ComputeBuffer sourcesBuffer;

    ComputeBuffer pathBuffer;
    ComputeBuffer countBuffer;
    int maxPaths = 3;

    int traceKernel;
    
    
    ComputeBuffer debugBuffer;




    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        listener = FindFirstObjectByType <ListenerController> ();

        listener.InitializeRays(initialRays);

        audioSources = new Dictionary<int, AcousticSource>();
        //find all audio sources in the scene
        var audioSourcesArray = FindObjectsByType<AcousticSource>(FindObjectsSortMode.InstanceID);
        foreach (var audioSource in audioSourcesArray)
        {
            audioSources.Add(audioSource.gameObject.GetInstanceID(), audioSource);
        }

        traceKernel = audioShader.FindKernel("TraceRays");
        
        // successful paths data
        pathBuffer = new ComputeBuffer(maxPaths, Marshal.SizeOf(typeof(PathData)), ComputeBufferType.Append);
        
        audioShader.SetBuffer(traceKernel, "pathBuffer", pathBuffer);

        //sources buffer
        sourcesBuffer = new ComputeBuffer(audioSourcesArray.Length < 64 ? audioSourcesArray.Length : 64, Marshal.SizeOf(typeof(SourceData)));

        audioShader.SetBuffer(traceKernel, "sources", sourcesBuffer);
        
        
        debugBuffer = new ComputeBuffer(1, Marshal.SizeOf(typeof(DebugInfo)));
        audioShader.SetBuffer(traceKernel, "debugInfo", debugBuffer);

    }

    // Update is called once per frame
    void Update()
    {
        List<AcousticSource> sources = audioSources.Values.Select(audioSource =>
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

        audioShader.Dispatch(traceKernel, initialRays / 64, 1, 1);


        //debug
    /*    PathData[] paths = new PathData[3];
        pathBuffer.GetData(paths);

        for(int i = 0; i < paths.Length; i++)
        {
            if (paths[i].distance > 0)
            {
                Debug.Log($"Path {i}: Distance={paths[i].distance}, Gain={paths[i].gain}, ArrivalAngles={paths[i].arrivalAngles}, SourceId={paths[i].sourceId}");
            }
        }
        Debug.Log($"Total valid paths: {paths.Count(p => p.distance > 0)}"); */

        //use async rather than getdata
        AsyncGPUReadback.Request(pathBuffer, OnPathDataReadback);
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
            
            // find the source from audioSources and probably do something with it.
            // audioSources[path.sourceId]
           
            
            // Debug.Log($"playing a sound after {delaySeconds} seconds, volume: {volume}, pan: {pan}");
        }
    }

    void SetupSourceBuffer(List<AcousticSource> sources)
    {
            SourceData[] sourceDataArray = sources.Select((source, index) => new SourceData
            {
                origin = source.transform.position,
                sourceId = source.gameObject.GetInstanceID()
            }).ToArray();

            sourcesBuffer.SetData(sourceDataArray);
            audioShader.SetInt("sourceCount", sourceDataArray.Length);
    }
}

