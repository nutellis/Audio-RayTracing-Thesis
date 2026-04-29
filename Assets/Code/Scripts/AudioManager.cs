using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Code.Data;
using UnityEngine;
using UnityEngine.Rendering;


public class AudioManager : MonoBehaviour
{
    [SerializeField]
    BVHManager bvhManager;

    Dictionary<int, AcousticSource> audioSources;
    Dictionary<int, AcousticSource> delayedAudioSources;

    ListenerController listener;

    public int maxDb = 120;

    int traceKernel;
    public ComputeShader audioShader;
    
    public int initialRays = 1024;

    ComputeBuffer sourcesBuffer;
    ComputeBuffer pathBuffer;
    ComputeBuffer debugBuffer;
    
    int maxPaths = 3;

   

    readonly List<int> removalBuffer = new List<int>();


    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        listener = FindFirstObjectByType <ListenerController> ();

        listener.InitializeRays(initialRays);

        audioSources = new Dictionary<int, AcousticSource>();
        delayedAudioSources = new Dictionary<int, AcousticSource>();
        
        traceKernel = audioShader.FindKernel("TraceRays");
        
        // successful paths data
        pathBuffer = new ComputeBuffer(maxPaths, Marshal.SizeOf(typeof(PathData)), ComputeBufferType.Append);
        
        audioShader.SetBuffer(traceKernel, "pathBuffer", pathBuffer);

        //sources buffer
        sourcesBuffer = new ComputeBuffer(64, Marshal.SizeOf(typeof(SourceData)));

        audioShader.SetBuffer(traceKernel, "sources", sourcesBuffer);
        
        
        debugBuffer = new ComputeBuffer(1, Marshal.SizeOf(typeof(DebugInfo)));
        audioShader.SetBuffer(traceKernel, "debugInfo", debugBuffer);

    }

    // Update is called once per frame
    void Update()
    {
        // this is not used yet but i refuse to delete it cause we might need it
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

        
        foreach (var delayedAudio in delayedAudioSources.Values)
        {
            if (Time.time >= delayedAudio.delayTime)
            {
                delayedAudio.audioSource.PlayOneShot(delayedAudio.audioSource.clip, delayedAudio.volume);
                
                removalBuffer.Add(delayedAudio.gameObject.GetInstanceID());
                audioSources.Remove(delayedAudio.gameObject.GetInstanceID());
            }
        }
        
        //use async rather than getdata
        AsyncGPUReadback.Request(pathBuffer, OnPathDataReadback);
    }

    private void LateUpdate()
    {
        //update the tree
        bvhManager.UpdateBVH();

        audioShader.SetBuffer(traceKernel, "tree", bvhManager.GetBVHBuffer());

        audioShader.SetBuffer(traceKernel, "triangles", bvhManager.GetTrianglesBuffer());
        audioShader.SetBuffer(traceKernel, "blasNodes", bvhManager.GetBlasNodesBuffer());
        //audioShader.SetBuffer(traceKernel, "instances", );
        
        //trace rays
        pathBuffer.SetCounterValue(0);

        audioShader.SetBuffer(traceKernel, "pathBuffer", pathBuffer);

        audioShader.Dispatch(traceKernel, initialRays / 64, 1, 1);

        // "garbage collect" delayed sounds
        CleanSounds();
    }


    private void OnDestroy()
    {
        pathBuffer?.Release();
        sourcesBuffer?.Release();
        debugBuffer?.Release();
    }
    void OnPathDataReadback(AsyncGPUReadbackRequest request)
    {
        if (request.hasError) return;

        var data = request.GetData<PathData>().ToArray();

        ProcessAudioPaths(data);
    }

    void ProcessAudioPaths(PathData[] paths)
    {
        foreach (var path in paths)
        {
            if (path.distance <= 0) continue;

            float delaySeconds = path.distance / 343.0f;
            float pan = path.arrivalAngles.x; // -1 to 1
            
            // find the source from audioSources and probably do something with it.
            if(audioSources.ContainsKey(path.sourceId))
            {
                var source = audioSources[path.sourceId];
                source.CalculateFinalGain(path.distance);
                
                // a sound with a delay > 0.5 will be added on the delayed sources dictionary and it will be triggered after the delay time has passed.
                if (delaySeconds >= 0.1f)
                {
                    float delayTarget = source.timeOfEmission + delaySeconds;

                    // we are accounting for movement changes (does it work correctly though?)
                    if (!Mathf.Approximately(delayTarget, source.delayTime))
                    {
                        source.delayTime = delayTarget;
                    }
                    
                    // avoid duplicate entries
                    delayedAudioSources.TryAdd(path.sourceId, source);
                }
                else
                {
                    source.audioSource.PlayOneShot(source.audioSource.clip, source.volume);
                    audioSources.Remove(path.sourceId);
                }
            }
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
    
    //this limits a specific sound to only one instance... for now i suppose
    public void RegisterAudio(AcousticSource acousticSource)
    {
        audioSources.TryAdd(acousticSource.gameObject.GetInstanceID(), acousticSource);
    }

    private void CleanSounds()
    {
        for (int i = 0; i < removalBuffer.Count; i++)
        {
            delayedAudioSources.Remove(removalBuffer[i]);
        }
        removalBuffer.Clear();

    }

}

