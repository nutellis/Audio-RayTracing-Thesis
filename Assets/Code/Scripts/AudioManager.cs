using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Code.Data;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;


public class AudioManager : MonoBehaviour
{
    [SerializeField] BVHManager bvhManager;

    Dictionary<int, AcousticSource> registeredAudioSources; //
    List<int> sourcesToRemove = new List<int>(32);

    ListenerController listener;

    public int maxDb = 120;

    int initRaysKernel;
    int traceKernel;
    int echogramResetKernel;
    public ComputeShader audioShader;

    public int initialRays = 64000;

    ComputeBuffer rayBuffer;
    ComputeBuffer sourcesBuffer;
    ComputeBuffer pathBuffer;
    ComputeBuffer pathCounterBuffer;
    ComputeBuffer debugBuffer;
    ComputeBuffer instancesBuffer;
    ComputeBuffer materialsBuffer;
    
    private AsyncGPUReadbackRequest pathCounterRequest;
    private AsyncGPUReadbackRequest pathDataRequest;
    private bool isTracing = false;

    // filter
    FilterCoefficients[] filterCoefficients;
    public float[] centerFreqs = { 125f, 250f, 500f, 1000f, 2000f, 4000f };
    public float bandwidth = 100f;
    private const float thirdOctaveFactor = 0.23156333016903374f; // Precomputed value for (2^(1/6) - 2^(-1/6))

    private const int MAX_BINS = 800; // 2.0s at 2.5ms resolution
    private const int MAX_SOURCES = 64; // Matches your sourcesBuffer size
    private int totalEchogramSize = MAX_SOURCES * MAX_BINS;
    GraphicsBuffer echogramBuffer;
    GraphicsBuffer echogramStagingBuffer;
    MacroBin[] readbackEchogramData;
    AcousticSource[] currentFrameSources; // Caches the exact order sent to GPU
    private AsyncGPUReadbackRequest echogramRequest;

    private bool sourcesDirty;
    // --------------------------
   
    void Start()
    {
        listener = FindFirstObjectByType<ListenerController>();
        registeredAudioSources = new Dictionary<int, AcousticSource>();

        initRaysKernel = audioShader.FindKernel("InitRays");
        traceKernel = audioShader.FindKernel("TraceRays");
        echogramResetKernel = audioShader.FindKernel("ResetEchogram");

        // --- NEW BUFFER INIT ---
        // 24 bytes = 6 uints * 4 bytes each
        echogramBuffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource, 
            totalEchogramSize, 
            24
        ); 

        echogramStagingBuffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopyDestination, 
            totalEchogramSize, 
            24
        );
        readbackEchogramData = new MacroBin[totalEchogramSize];
        
        audioShader.SetBuffer(traceKernel, "echogramBuffer", echogramBuffer);
        // -----------------------

        sourcesBuffer = new ComputeBuffer(MAX_SOURCES, Marshal.SizeOf(typeof(SourceData)));
        audioShader.SetBuffer(traceKernel, "sources", sourcesBuffer);

        audioShader.SetBuffer(traceKernel, "triangleSoup", bvhManager.GetTrianglesBuffer());
        audioShader.SetBuffer(traceKernel, "blasTrees", bvhManager.GetBlasNodesBuffer());

        audioShader.SetBuffer(echogramResetKernel, "echogramBuffer", echogramBuffer);
        
        InitializeStaticMaterials();

        //filter init
        filterCoefficients = new FilterCoefficients[6];
        for (int i = 0; i < 6; i++)
        {
            bandwidth = centerFreqs[i] * thirdOctaveFactor;
            filterCoefficients[i] = CreateBandPass(centerFreqs[i], bandwidth, AudioSettings.outputSampleRate);
        }
        
        debugBuffer = new ComputeBuffer(1, Marshal.SizeOf(typeof(DebugInfo)));
        audioShader.SetBuffer(traceKernel, "debugInfo", debugBuffer);

        InitializeRays(initialRays);
    }
    
    private void OnDestroy()
    {
        sourcesBuffer?.Release();
        instancesBuffer?.Release();
        materialsBuffer?.Release();
        rayBuffer?.Release();
        echogramBuffer?.Release();
        debugBuffer?.Release();
        echogramStagingBuffer?.Release();
    }
    
    private void LateUpdate()
    {
        UpdateListenerData();

        bool shouldUpdateTree = bvhManager.needsRefit;
        
        bvhManager.UpdateBVH();

        if (shouldUpdateTree)
        {
            UpdateInstances(true);
            audioShader.SetBuffer(traceKernel, "tlasTree", bvhManager.GetBVHBuffer());

        }
  
        // 1. Process Finished Traces
        if (isTracing && echogramRequest.done)
        {
            isTracing = false;
            if (!echogramRequest.hasError)
            {
                // Copy straight into our readback array
                echogramRequest.GetData<MacroBin>().CopyTo(readbackEchogramData);
                ProcessAudioPaths(readbackEchogramData);
            }
        }

        // 2. Start New Trace
        if (registeredAudioSources.Count > 0 && isTracing == false)
        {
            isTracing = true;

            // ONLY upload the buffer if something changed
            if (sourcesDirty)
            {
                SetupSourceBuffer();
                sourcesDirty = false; // Clear the flag after upload
            }

            // Wipe the VRAM buffer clean via GPU kernel
            int clearThreads = Mathf.CeilToInt(totalEchogramSize / 64f);
            audioShader.Dispatch(echogramResetKernel, clearThreads, 1, 1);

            // Trace rays
            int threadGroups = Mathf.CeilToInt(initialRays / 64f);
            audioShader.Dispatch(traceKernel, threadGroups, 1, 1);
            
            Graphics.CopyBuffer(echogramBuffer, echogramStagingBuffer);

            // Request readback
            echogramRequest = AsyncGPUReadback.Request(echogramStagingBuffer);
        }
        CleanSounds();
    }
    
    void InitializeStaticMaterials()
    {
        var materials = ObjectRegistry<MaterialData>.Instance.GetValues();
        materialsBuffer?.Release();
        materialsBuffer = new ComputeBuffer(materials.Length, Marshal.SizeOf(typeof(MaterialData)));
        materialsBuffer.SetData(materials);
        audioShader.SetBuffer(traceKernel, "materials", materialsBuffer);
    }

    void UpdateInstances(bool forceUpdate)
    {
        if (!forceUpdate) return;

        var instances = ObjectRegistry<Instance>.Instance.GetValues();
        if (instancesBuffer == null || instancesBuffer.count != instances.Length)
        {
            instancesBuffer?.Release();
            instancesBuffer = new ComputeBuffer(instances.Length, Marshal.SizeOf(typeof(Instance)));
            audioShader.SetBuffer(traceKernel, "objectInstances", instancesBuffer);
        }

        instancesBuffer.SetData(instances);
    }

    static readonly ProfilerMarker processFrameMarker = new("Acoustic.ProcessAudioPaths");


    void ProcessAudioPaths(MacroBin[] echogram)
    {
        using (processFrameMarker.Auto())
        {
            sourcesToRemove.Clear();
        
            for (int s = 0; s < currentFrameSources.Length; s++)
            {
                var source = currentFrameSources[s];
            
                // --- ASYNC LATENCY GUARD ---
                // If the source was destroyed, disabled, or removed while the GPU was busy, skip it!
                if (!source || !registeredAudioSources.ContainsKey(source.gameObject.GetInstanceID()))
                {
                    continue;
                }
                // ----------------------------

                // 1. Extract the 800-bin slice belonging to THIS source
                MacroBin[] sourceSlice = new MacroBin[MAX_BINS];
                Array.Copy(echogram, s * MAX_BINS, sourceSlice, 0, MAX_BINS);
                
                // 2. Pass the pre-binned slice to the AcousticSource script
                source.UpdateReflections(sourceSlice);

                // 3. Find the first valid bin to calculate frameDistance for audio delay
                source.frameDistance = float.MaxValue;
                for(int b = 0; b < MAX_BINS; b++) 
                {
                    if (sourceSlice[b].energy0 > 0 || sourceSlice[b].energy1 > 0 || sourceSlice[b].energy2 > 0) 
                    {
                        // 0.0025s (2.5ms) * 343m/s speed of sound = 0.8575 meters per bin
                        source.frameDistance = b * 0.8575f; 
                        break;
                    }
                }

                // 4. Playback Logic
                float delaySeconds = source.frameDistance == float.MaxValue ? 0f : source.frameDistance / 343.0f;
                float timeOfArrival = source.timeOfEmission + delaySeconds;
                float elapsedTime = Time.time - timeOfArrival;

                if (Time.time >= timeOfArrival && source.frameDistance != float.MaxValue)
                {
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
        }
    }
    
    void SetupSourceBuffer()
    {
        // Cache the array order for this specific frame
        currentFrameSources = registeredAudioSources.Values.ToArray();
        SourceData[] sourceDataArray = new SourceData[currentFrameSources.Length];
        
        for (int i = 0; i < currentFrameSources.Length; i++)
        {
            sourceDataArray[i] = new SourceData()
            {
                origin = currentFrameSources[i].transform.position,
                sourceId = currentFrameSources[i].gameObject.GetInstanceID(),
                radius = currentFrameSources[i].radius,
                maxAudibleDistance = currentFrameSources[i].maxAudibleDistance,
                minAudibleDistance = currentFrameSources[i].minAudibleDistance,
                power = currentFrameSources[i].baseAmplitudeWeighted
            };
        }

        sourcesBuffer.SetData(sourceDataArray);
        audioShader.SetInt("sourceCount", sourceDataArray.Length);
    }
    
    
    //this is to know if a source needs to taken into consideration
    public void RegisterAudio(AcousticSource acousticSource)
    {
        if (registeredAudioSources.TryAdd(acousticSource.gameObject.GetInstanceID(), acousticSource))
        {
            sourcesDirty = true;
        }
    }

    private void CleanSounds()
    {
        bool removedAny = false;
        for (int i = 0; i < sourcesToRemove.Count; i++)
        {
            int id = sourcesToRemove[i];
            if (registeredAudioSources.TryGetValue(id, out var source))
            {
                if (source) source.UnRegisterSound();
                registeredAudioSources.Remove(id);
                removedAny = true;
            }
        }
        sourcesToRemove.Clear();
    
        if (removedAny) 
        {
            sourcesDirty = true;
        }
    }


    // RAY INITIALIZATION
    public void InitializeRays(int initialRays)
    {
        UpdateListenerData();

        SetupComputeBuffers(initialRays);

        int groups = Mathf.CeilToInt(initialRays / 64f);
        audioShader.Dispatch(initRaysKernel, groups, 1, 1);
    }


    void UpdateListenerData()
    {
        audioShader.SetVector("listenerPosition", listener.transform.position);
        audioShader.SetVector("listenerForward", listener.transform.forward);
        audioShader.SetVector("listenerRight", listener.transform.right);
        audioShader.SetInt("listenerId", listener.gameObject.GetInstanceID());
        audioShader.SetInt("initialRays", initialRays);
        audioShader.SetFloats("padding", 0.0f, 0.0f);
    }


    void SetupComputeBuffers(int initialRays)
    {
        rayBuffer = new ComputeBuffer(initialRays, Marshal.SizeOf(typeof(Ray)));

        audioShader.SetBuffer(initRaysKernel, "rayBuffer", rayBuffer);
        audioShader.SetBuffer(traceKernel, "rayBuffer", rayBuffer);
    }

    public static FilterCoefficients CreateBandPass(float freq, float bw, float sampleRate)
    {
        double pid_sr = math.PI / sampleRate;
        double tpid_sr = 2.0 * math.PI / sampleRate;

        double c = 1.0 / math.tan(pid_sr * (double)bw);
        double d = 2.0 * math.cos(tpid_sr * (double)freq);

        float b0 = (float)(1.0 / (1.0 + c));

        return new FilterCoefficients
        {
            a1 = b0,
            a2 = 0.0f,
            a3 = -b0,
            a4 = (float)(-c * d * b0),
            a5 = (float)((c - 1.0) * b0)
        };
    }

    public FilterCoefficients[] GetFilterCoefficients()
    {
        return filterCoefficients;
    }
}