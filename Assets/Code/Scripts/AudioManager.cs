using System;
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
    List<int> sourcesToRemove = new List<int>(32);

    ListenerController listener;

    public int maxDb = 120;

    int initRaysKernel;
    int traceKernel;
    public ComputeShader audioShader;
    
    public int initialRays = 1024;
    ComputeBuffer rayBuffer;

    
    ComputeBuffer sourcesBuffer;
    ComputeBuffer pathBuffer;
    ComputeBuffer pathCounter;
    ComputeBuffer debugBuffer;
    ComputeBuffer instancesBuffer;

    readonly uint maxPaths = 1024;

    
    readonly List<int> removalBuffer = new();


    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        listener = FindFirstObjectByType <ListenerController> ();
        
        registeredAudioSources = new Dictionary<int, AcousticSource>();
                
        initRaysKernel = audioShader.FindKernel("InitRays");
        traceKernel = audioShader.FindKernel("TraceRays");
        
        // successful paths data
        pathBuffer = new ComputeBuffer((int)maxPaths, Marshal.SizeOf(typeof(PathData)));
        pathCounter = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Default);
        
        audioShader.SetBuffer(traceKernel, "pathBuffer", pathBuffer);
        audioShader.SetBuffer(traceKernel, "pathCounter", pathCounter);


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
        if (registeredAudioSources.Count != 0 )
        {
            pathCounter.SetData(new int[] { 0 });
        
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
        pathCounter?.Release();
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
        if (!pathCounter.IsValid()) return;
        uint[] counterArray =  new uint[1];
        
        
        pathCounter.GetData(counterArray);
        
        uint counter = counterArray[0] < maxPaths ? counterArray[0] : maxPaths;
        
        var data = request.GetData<PathData>().ToArray();


        ProcessAudioPaths(data, counter);
    }

    void ProcessAudioPaths(PathData[] paths, uint counter)
    {
        sourcesToRemove.Clear();
        
        //first we clean up this frames data
        foreach (var audioSource in registeredAudioSources.Values)
        {
            audioSource.frameGain = 0f;
            audioSource.frameDistance = float.MaxValue;
        }

        // we accumulate per path the gain and the relevant data for each source
        for (uint i = 0; i < counter; i++)
        {
            var path = paths[i];
            if (registeredAudioSources.TryGetValue(path.sourceId, out var source))
            {
                source.frameGain += path.gain;

                if (path.distance < source.frameDistance)
                {
                    source.frameDistance = path.distance;
                }
            }
        }

        // we apply this data to each source
        foreach (var source in registeredAudioSources.Values)
        {

            bool isAudible = source.frameGain > 0.1f;

            float delaySeconds = source.frameDistance / 343.0f;
            float timeOfArrival = source.timeOfEmission + delaySeconds;
            float elapsedTime = Time.time - timeOfArrival;
            
            if (isAudible)
            {
                source.CalculateFinalFrameVolume();

                if (Time.time >= timeOfArrival)
                {
                    //the sound played but we never heard it
                    if (!source.audioSource.loop && elapsedTime >= source.audioSource.clip.length)
                    {
                        sourcesToRemove.Add(source.gameObject.GetInstanceID());
                        continue; 
                    }
                    
                    if (!source.audioSource.isPlaying)
                    {
                    
                        source.audioSource.time = elapsedTime % source.audioSource.clip.length;
                        source.audioSource.Play();
                    }
                }
            }
            else
            {
                if (!source.audioSource.loop && elapsedTime >= source.audioSource.clip.length)
                {
                    sourcesToRemove.Add(source.gameObject.GetInstanceID());
                }
                
                if (source.audioSource.isPlaying)
                {
                    source.audioSource.Stop();
                }
            }
        }

    }

    // for(uint i = 0; i < counter; i++)
        // {
        //     var path = paths[i];
        //     
        //     if (path.distance <= 0) continue;
        //
        //     
        //     float pan = path.arrivalAngles.x; // -1 to 1
        //     if(path.gain <= 0.1) continue; // we cannot hear this. discard it. Maybe discard it inside the tracer?
        //     
        //     // find the source from audioSources and probably do something with it.
        //     if(registeredAudioSources.ContainsKey(path.sourceId))
        //     {
        //         var source = registeredAudioSources[path.sourceId];
        //         source.CalculateDistanceAttenuation(path.distance);
        //         
        //         // a sound with a delay > 0.1 will be added on the delayed sources dictionary and it will be triggered after the delay time has passed.
        //         if (delaySeconds >= 0.1f)
        //         {
        //             float delayTarget = source.timeOfEmission + delaySeconds;
        //
        //             // we are accounting for movement changes (does it work correctly though?)
        //             if (!Mathf.Approximately(delayTarget, source.delayTime))
        //             {
        //                 source.delayTime = delayTarget;
        //             }
        //             
        //             // avoid duplicate entries
        //             delayedAudioSources.TryAdd(path.sourceId, source);
        //         }
        //         else
        //         {
        //             source.audioSource.PlayOneShot(source.audioSource.clip, source.volume);
        //            // registeredAudioSources.Remove(path.sourceId);
        //         }
        //     }
        // }

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
                    sourceId = values[i].gameObject.GetInstanceID(),
                    radius = values[i].radius,
                    padding = Vector3.zero
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
        for (int i = 0; i < sourcesToRemove.Count; i++)
        {
            registeredAudioSources.Remove(sourcesToRemove[i]);
        }
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

