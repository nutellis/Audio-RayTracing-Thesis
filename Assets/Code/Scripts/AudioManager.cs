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

    Dictionary<int, AcousticSource> registeredAudioSources; //
    Dictionary<int, AcousticSource> delayedAudioSources;

    ListenerController listener;

    public int maxDb = 120;

    int initRaysKernel;
    int traceKernel;
    public ComputeShader audioShader;
    
    public int initialRays = 1024;
    ComputeBuffer rayBuffer;

    
    ComputeBuffer sourcesBuffer;
    ComputeBuffer pathBuffer;
    ComputeBuffer debugBuffer;
    ComputeBuffer instancesBuffer;
    
    int maxPaths = 1;

    
    readonly List<int> removalBuffer = new List<int>();


    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        listener = FindFirstObjectByType <ListenerController> ();
        
        registeredAudioSources = new Dictionary<int, AcousticSource>();
        delayedAudioSources = new Dictionary<int, AcousticSource>();
        
        initRaysKernel = audioShader.FindKernel("InitRays");
        traceKernel = audioShader.FindKernel("TraceRays");
        
        // successful paths data
        pathBuffer = new ComputeBuffer(maxPaths, Marshal.SizeOf(typeof(PathData)), ComputeBufferType.Append);
        
        audioShader.SetBuffer(traceKernel, "pathBuffer", pathBuffer);

        //sources buffer
        sourcesBuffer = new ComputeBuffer(64, Marshal.SizeOf(typeof(SourceData)));

        audioShader.SetBuffer(traceKernel, "sources", sourcesBuffer);
        
        debugBuffer = new ComputeBuffer(1, Marshal.SizeOf(typeof(DebugInfo)));
        audioShader.SetBuffer(traceKernel, "debugInfo", debugBuffer);
        
        audioShader.SetBuffer(traceKernel, "triangleSoup", bvhManager.GetTrianglesBuffer());
        audioShader.SetBuffer(traceKernel, "blasTrees", bvhManager.GetBlasNodesBuffer());

        InitializeRays(initialRays);
    }

    // Update is called once per frame
    void Update()
    {
        if (registeredAudioSources.Count > 0)
        {
            SetupSourceBuffer(); 
        }

        foreach (var delayedAudio in delayedAudioSources.Values)
        {
            if (Time.time >= delayedAudio.delayTime)
            {
                delayedAudio.audioSource.PlayOneShot(delayedAudio.audioSource.clip, delayedAudio.volume);
                
                removalBuffer.Add(delayedAudio.gameObject.GetInstanceID());
            }
        }
        
        //use async rather than getdata
        AsyncGPUReadback.Request(pathBuffer, OnPathDataReadback);
    }

    private void LateUpdate()
    {
        UpdateInstances();
        
        //update the tree
        bvhManager.UpdateBVH();

        audioShader.SetBuffer(traceKernel, "tlasTree", bvhManager.GetBVHBuffer());
        
        //trace rays if there are audio sources
        if (registeredAudioSources.Count != 0 || delayedAudioSources.Count != 0)
        {
            pathBuffer.SetCounterValue(0);
        
            audioShader.SetBuffer(traceKernel, "pathBuffer", pathBuffer);
        
            audioShader.Dispatch(traceKernel, initialRays / 64, 1, 1);
        }
        
        // "garbage collect" delayed sounds
        CleanSounds();
    }

    private void OnDestroy()
    {
        pathBuffer?.Release();
        sourcesBuffer?.Release();
        debugBuffer?.Release();
        instancesBuffer?.Release();
        rayBuffer?.Release();
    }
    
    void UpdateInstances()
    {
        var instances = ObjectRegistry<Instance>.Instance.GetValues();
        
        // Reallocate if count changes
        if (instancesBuffer == null || instancesBuffer.count != instances.Length)
        {
            instancesBuffer?.Release();
            instancesBuffer = new ComputeBuffer(instances.Length, Marshal.SizeOf(typeof(Instance)));
            
            audioShader.SetBuffer(traceKernel, "objectInstances", instancesBuffer);
        }

        instancesBuffer.SetData(instances);
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
            if(path.gain <= 0.1) continue; // we cannot hear this. discard it. Maybe discard it inside the tracer?
            
            // find the source from audioSources and probably do something with it.
            if(registeredAudioSources.ContainsKey(path.sourceId))
            {
                var source = registeredAudioSources[path.sourceId];
                source.CalculateDistanceAttenuation(path.distance);
                
                // a sound with a delay > 0.1 will be added on the delayed sources dictionary and it will be triggered after the delay time has passed.
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
                    registeredAudioSources.Remove(path.sourceId);
                }
            }
        }
    }

    //fix this ugly mess. 
    void SetupSourceBuffer()
    {
            // it can be done better...
            SourceData[] sourceDataArray = new SourceData[registeredAudioSources.Count];
            var values = registeredAudioSources.Values.ToArray();
            for(int i = 0; i < values.Length; i++)
            {
                sourceDataArray[i] = new SourceData()
                {
                    origin = values[i].transform.position,
                    sourceId = values[i].gameObject.GetInstanceID()
                };
            }

            sourcesBuffer.SetData(sourceDataArray);
            audioShader.SetInt("sourceCount", sourceDataArray.Length);
    }
    
    //this is to know if a source needs to taken into consideration
    public void RegisterAudio(AcousticSource acousticSource)
    {
        registeredAudioSources.TryAdd(acousticSource.gameObject.GetInstanceID(), acousticSource);
    }

    private void CleanSounds()
    {
        for (int i = 0; i < removalBuffer.Count; i++)
        {
            delayedAudioSources.Remove(removalBuffer[i]);
        }
        removalBuffer.Clear();

    }
    
    
    // RAY INITIALIZATION
    public void InitializeRays(int initialRays)
    {
        SetupComputeBuffers(initialRays);

        UpdateShaderData();

        int groups = Mathf.CeilToInt(initialRays / 64f);
        audioShader.Dispatch(initRaysKernel, groups, 1, 1);
    }


    void UpdateShaderData()
    {
        audioShader.SetVector("listenerPosition", transform.position);
        audioShader.SetVector("listenerForward", transform.forward);
        audioShader.SetVector("listenerRight", transform.right);
        audioShader.SetInt("listenerId", gameObject.GetInstanceID());
    }


    void SetupComputeBuffers(int initialRays)
    {
        rayBuffer = new ComputeBuffer(initialRays, Marshal.SizeOf(typeof(Ray)));

        audioShader.SetInt("initialRays", initialRays);
        audioShader.SetBuffer(initRaysKernel, "rayBuffer", rayBuffer);
        audioShader.SetBuffer(traceKernel, "rayBuffer", rayBuffer);
    }

}

