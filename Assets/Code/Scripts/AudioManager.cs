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

    public ListenerController listener;

    public int maxDb = 120;

    int initRaysKernel;
    int traceKernel;
    int traceDirectKernel;
    int echogramResetKernel;
    int convertToFloatKernel;
    public ComputeShader audioShader;

    public int initialRays = 64000;

    ComputeBuffer rayBuffer;
    ComputeBuffer sourcesBuffer;
    ComputeBuffer pathBuffer;
    ComputeBuffer pathCounterBuffer;
    ComputeBuffer debugBuffer;
    ComputeBuffer instancesBuffer;
    ComputeBuffer materialsBuffer;
    
    ComputeBuffer directAudioBuffer;
    DirectAudioData[] readbackDirectData;
    private AsyncGPUReadbackRequest directDataRequest;
    
    private AsyncGPUReadbackRequest pathCounterRequest;
    private AsyncGPUReadbackRequest pathDataRequest;

    private bool isTracing = false;

    // filter
    FilterCoefficients[] filterCoefficients;
    public float[] centerFreqs = { 125f, 250f, 500f, 1000f, 2000f, 4000f };
    public float bandwidth = 100f;
    private const float thirdOctaveFactor = 0.23156333016903374f; // Precomputed value for (2^(1/6) - 2^(-1/6))

    private const int MAX_BINS = 1600; // 4.0s at 2.5ms resolution
    private const int MAX_SOURCES = 64;
    private int totalEchogramSize = MAX_SOURCES * MAX_BINS;
    GraphicsBuffer echogramBuffer;
    
    GraphicsBuffer finalFloatBuffer;
    AcousticData[] readbackFloatData;
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
        traceDirectKernel = audioShader.FindKernel("TraceDirectSound");
        echogramResetKernel = audioShader.FindKernel("ResetEchogram");
        convertToFloatKernel = audioShader.FindKernel("ConvertToFloat");


        // 24 bytes = 6 uints * 4 bytes each
        echogramBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource, totalEchogramSize, 24); 

        
        readbackEchogramData = new MacroBin[totalEchogramSize];
        
        audioShader.SetBuffer(traceKernel, "echogramBuffer", echogramBuffer);
        audioShader.SetBuffer(echogramResetKernel, "echogramBuffer", echogramBuffer);


        sourcesBuffer = new ComputeBuffer(MAX_SOURCES, Marshal.SizeOf(typeof(SourceData)));
        audioShader.SetBuffer(traceKernel, "sources", sourcesBuffer);
        audioShader.SetBuffer(traceDirectKernel, "sources", sourcesBuffer);

        audioShader.SetBuffer(traceKernel, "triangleSoup", bvhManager.GetTrianglesBuffer());
        audioShader.SetBuffer(traceKernel, "blasTrees", bvhManager.GetBlasNodesBuffer());
        
        audioShader.SetBuffer(traceDirectKernel, "triangleSoup", bvhManager.GetTrianglesBuffer());
        audioShader.SetBuffer(traceDirectKernel, "blasTrees", bvhManager.GetBlasNodesBuffer());

        directAudioBuffer = new ComputeBuffer(MAX_SOURCES, Marshal.SizeOf(typeof(DirectAudioData)));
        readbackDirectData = new DirectAudioData[MAX_SOURCES];
        audioShader.SetBuffer(traceDirectKernel, "directAudioOutput", directAudioBuffer);
        
        finalFloatBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalEchogramSize, 24); 
        readbackFloatData = new AcousticData[totalEchogramSize];
        
        audioShader.SetBuffer(convertToFloatKernel, "echogramBuffer", echogramBuffer);
        audioShader.SetBuffer(convertToFloatKernel, "finalFloatEchogram", finalFloatBuffer);

        InitializeStaticMaterials();

        //filter init
        filterCoefficients = new FilterCoefficients[6];
        for (int i = 0; i < 6; i++)
        {
            bandwidth = centerFreqs[i] * thirdOctaveFactor;
            filterCoefficients[i] = CreateBandPass(centerFreqs[i], bandwidth, AudioSettings.outputSampleRate);
        }

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
        directAudioBuffer?.Release();
        finalFloatBuffer?.Release();
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
            audioShader.SetBuffer(traceDirectKernel, "tlasTree", bvhManager.GetBVHBuffer());

        }
        
        if (isTracing && echogramRequest.done && directDataRequest.done)
        {
            isTracing = false;
            if (!echogramRequest.hasError && !directDataRequest.hasError)
            {
                echogramRequest.GetData<AcousticData>().CopyTo(readbackFloatData);
                directDataRequest.GetData<DirectAudioData>().CopyTo(readbackDirectData);
                
                ProcessAudioPaths(readbackFloatData, readbackDirectData);
            }
        }

        // 2. Start New Trace
        if (registeredAudioSources.Count > 0 && isTracing == false)
        {
            isTracing = true;

            SetupSourceBuffer();
            
            // Clear echogram
            int clearThreads = Mathf.CeilToInt(totalEchogramSize / 64f);
            audioShader.Dispatch(echogramResetKernel, clearThreads, 1, 1);

            //run tracer
            int threadGroups = Mathf.CeilToInt(initialRays / 64f);
            audioShader.Dispatch(traceKernel, threadGroups, 1, 1);
            
            // run direct sound tracing
            int directGroups = Mathf.Max(1, Mathf.CeilToInt(currentFrameSources.Length / 64f));
            audioShader.Dispatch(traceDirectKernel, directGroups, 1, 1);

            int convertGroups = Mathf.CeilToInt(totalEchogramSize / 64f);
            audioShader.Dispatch(convertToFloatKernel, convertGroups, 1, 1);
            
            echogramRequest = AsyncGPUReadback.Request(finalFloatBuffer);
            directDataRequest = AsyncGPUReadback.Request(directAudioBuffer);
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
        audioShader.SetBuffer(traceDirectKernel, "materials", materialsBuffer);
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
            audioShader.SetBuffer(traceDirectKernel, "objectInstances", instancesBuffer);
        }

        instancesBuffer.SetData(instances);
    }

    static readonly ProfilerMarker processFrameMarker = new("Acoustic.ProcessAudioPaths");


    void ProcessAudioPaths(AcousticData[] echogram, DirectAudioData[] directDataArray)
    {
        using (processFrameMarker.Auto())
        {
            sourcesToRemove.Clear();
        
            for (int s = 0; s < currentFrameSources.Length; s++)
            {
                var source = currentFrameSources[s];
            
                // If the source was destroyed, disabled, or removed while the GPU was busy, skip it!
                if (!source || !registeredAudioSources.ContainsKey(source.gameObject.GetInstanceID()))
                {
                    continue;
                }
                // ----------------------------

                //extract the slice belonging to THIS source
                AcousticData[] sourceSlice = new AcousticData[MAX_BINS];
                Array.Copy(echogram, s * MAX_BINS, sourceSlice, 0, MAX_BINS);
                
                DirectAudioData directData = directDataArray[s];
                
                source.UpdateFrameData(sourceSlice, directData);

                float delaySeconds = directData.delayMs / 1000.0f;
                float timeOfArrival = source.timeOfEmission + delaySeconds;
                float elapsedTime = Time.time - timeOfArrival;

                if (Time.time >= timeOfArrival)
                {
                    source.isTailPhase = !source.audioSource.loop && (elapsedTime >= source.audioSource.clip.length);
                    
                    if (!source.audioSource.loop && elapsedTime >= source.audioSource.clip.length + source.dynamicTailLength)
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
        audioShader.SetInt("frameCount", Time.frameCount);
        audioShader.SetFloat("padding", 0.0f);
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