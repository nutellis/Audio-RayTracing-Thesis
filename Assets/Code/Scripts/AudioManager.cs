using System;
using System.Collections.Generic;
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

    Dictionary<int, AcousticSource> registeredAudioSources;
    List<int> sourcesToRemove = new List<int>(32);

    public ListenerController listener;
    CapsuleCollider listenerCollider;

    public int maxDb = 120;

    int initRaysKernel;
    int traceKernel;
    int traceDirectKernel;
    int convertToFloatKernel;
    public ComputeShader audioShader;

    public int initialRays = 64000;
    
    public int raysPerBatch = 16000;
    private int currentBatch = 0;
    private int totalBatches;
    private bool isAccumulating = false;

    ComputeBuffer rayBuffer;
    ComputeBuffer sourcesBuffer;
    ComputeBuffer debugBuffer;
    ComputeBuffer instancesBuffer;
    ComputeBuffer materialsBuffer;
    
    ComputeBuffer directAudioBuffer;
    private AsyncGPUReadbackRequest directDataRequest;
    private AsyncGPUReadbackRequest echogramRequest;

    private bool isTracing = false;

    // Filter
    FilterCoefficients[] filterCoefficients;
    public float[] centerFreqs = { 125f, 250f, 500f, 1000f, 2000f, 4000f };
    public float bandwidth = 100f;
    private const float thirdOctaveFactor = 0.23156333016903374f; 

    private const int MAX_BINS = 1600; 
    private const int MAX_SOURCES = 64;
    private int totalEchogramSize = MAX_SOURCES * MAX_BINS;
    GraphicsBuffer echogramBuffer;
    GraphicsBuffer finalFloatBuffer;

    NativeArray<SourceData> nativeSources;
    AcousticSource[] currentFrameSources; 
    private int activeSourceCount = 0;

    private bool sourcesDirty;
   
    void Start()
    {
        listener = FindFirstObjectByType<ListenerController>();
        listenerCollider = listener.GetComponent<CapsuleCollider>();
        
        registeredAudioSources = new Dictionary<int, AcousticSource>();
        currentFrameSources = new AcousticSource[MAX_SOURCES];
        nativeSources = new NativeArray<SourceData>(MAX_SOURCES, Allocator.Persistent);

        initRaysKernel = audioShader.FindKernel("InitRays");
        traceKernel = audioShader.FindKernel("TraceRays");
        traceDirectKernel = audioShader.FindKernel("TraceDirectSound");
        convertToFloatKernel = audioShader.FindKernel("ConvertToFloat");

        echogramBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource, totalEchogramSize, 24); 
        audioShader.SetBuffer(traceKernel, "echogramBuffer", echogramBuffer);

        sourcesBuffer = new ComputeBuffer(MAX_SOURCES, Marshal.SizeOf(typeof(SourceData)));
        audioShader.SetBuffer(traceKernel, "sources", sourcesBuffer);
        audioShader.SetBuffer(traceDirectKernel, "sources", sourcesBuffer);

        audioShader.SetBuffer(traceKernel, "triangleSoup", bvhManager.GetTrianglesBuffer());
        audioShader.SetBuffer(traceKernel, "blasTrees", bvhManager.GetBlasNodesBuffer());
        
        audioShader.SetBuffer(traceDirectKernel, "triangleSoup", bvhManager.GetTrianglesBuffer());
        audioShader.SetBuffer(traceDirectKernel, "blasTrees", bvhManager.GetBlasNodesBuffer());

        directAudioBuffer = new ComputeBuffer(MAX_SOURCES, Marshal.SizeOf(typeof(DirectAudioData)));
        audioShader.SetBuffer(traceDirectKernel, "directAudioOutput", directAudioBuffer);
        
        finalFloatBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalEchogramSize, 24); 
        audioShader.SetBuffer(convertToFloatKernel, "echogramBuffer", echogramBuffer);
        audioShader.SetBuffer(convertToFloatKernel, "finalFloatEchogram", finalFloatBuffer);

        InitializeStaticMaterials();

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
        if (nativeSources.IsCreated) nativeSources.Dispose();
        
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
        
        if (!isTracing && !isAccumulating && registeredAudioSources.Count > 0)
        {
            StartNewTraceCycle();
        }
        else if (isAccumulating)
        {
            ProcessBatch();
        }
        else if (isTracing && echogramRequest.done && directDataRequest.done)
        {
            FinishTraceCycle();
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

    void StartNewTraceCycle()
    {
        SetupSourceBuffer();
        
        totalBatches = Mathf.CeilToInt((float)initialRays / raysPerBatch);
        currentBatch = 0;
        isAccumulating = true;
    }

    void ProcessBatch()
    {
        int rayOffset = currentBatch * raysPerBatch;
        int raysThisBatch = Mathf.Min(raysPerBatch, initialRays - rayOffset);
        
        audioShader.SetInt("rayOffset", rayOffset);
        
        int threadGroups = Mathf.CeilToInt(raysThisBatch / 64f);
        audioShader.Dispatch(traceKernel, threadGroups, 1, 1);

        currentBatch++;

        if (currentBatch >= totalBatches)
        {
            isAccumulating = false;
            isTracing = true;
            
            int directGroups = Mathf.Max(1, Mathf.CeilToInt(activeSourceCount / 64f));
            audioShader.Dispatch(traceDirectKernel, directGroups, 1, 1);

            int convertGroups = Mathf.CeilToInt(totalEchogramSize / 64f);
            audioShader.Dispatch(convertToFloatKernel, convertGroups, 1, 1);
            
            echogramRequest = AsyncGPUReadback.Request(finalFloatBuffer);
            directDataRequest = AsyncGPUReadback.Request(directAudioBuffer);
        }
    }

    void FinishTraceCycle()
    {
        isTracing = false;
        if (!echogramRequest.hasError && !directDataRequest.hasError)
        {
            var echogramData = echogramRequest.GetData<AcousticData>();
            var directData = directDataRequest.GetData<DirectAudioData>();
            
            ProcessAudioPaths(echogramData, directData);
        }
    }
    
    void SetupSourceBuffer()
    {
        activeSourceCount = 0;
        
        foreach (var source in registeredAudioSources.Values)
        {
            if (activeSourceCount >= MAX_SOURCES) break;
            
            currentFrameSources[activeSourceCount] = source;
            nativeSources[activeSourceCount] = new SourceData()
            {
                origin = source.transform.position,
                sourceId = source.gameObject.GetInstanceID(),
                radius = source.radius,
                maxAudibleDistance = source.maxAudibleDistance,
                minAudibleDistance = source.minAudibleDistance,
                power = source.baseAmplitudeWeighted
            };
            activeSourceCount++;
        }

        sourcesBuffer.SetData(nativeSources, 0, 0, activeSourceCount);
        audioShader.SetInt("sourceCount", activeSourceCount);
    }

    static readonly ProfilerMarker processFrameMarker = new("Acoustic.ProcessAudioPaths");

    void ProcessAudioPaths(NativeArray<AcousticData> echogram, NativeArray<DirectAudioData> directDataArray)
    {
        using (processFrameMarker.Auto())
        {
            sourcesToRemove.Clear();
        
            for (int s = 0; s < activeSourceCount; s++)
            {
                var source = currentFrameSources[s];
            
                if (!source || !registeredAudioSources.ContainsKey(source.gameObject.GetInstanceID()))
                {
                    continue;
                }

                NativeSlice<AcousticData> sourceSlice = new NativeSlice<AcousticData>(echogram, s * MAX_BINS, MAX_BINS);
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

    public void InitializeRays(int initialRays)
    {
        UpdateListenerData();

        SetupComputeBuffers(initialRays);

        int groups = Mathf.CeilToInt(initialRays / 64f);
        audioShader.Dispatch(initRaysKernel, groups, 1, 1);
    }

    void UpdateListenerData()
    {
        float headOffset = listenerCollider ? (listenerCollider.center.y + (listenerCollider.height * 0.5f)) : 0.8f; 

        Vector3 listenerHeadPos = listener.transform.position + (listener.transform.up * headOffset);
        
        audioShader.SetVector("listenerPosition", listenerHeadPos);
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
    
    [Header("Debug Visualization")]
    public bool drawInitialRays = false;
    [Range(100, 5000)]
    public int debugRayCount = 1000;
    public float debugRayLength = 2f;

    private Ray[] debugRaysArray;

    private void OnDrawGizmos()
    {
        if (!drawInitialRays || rayBuffer == null || !Application.isPlaying) 
            return;

        if (debugRaysArray == null || debugRaysArray.Length != debugRayCount)
        {
            debugRaysArray = new Ray[debugRayCount];
        }

        rayBuffer.GetData(debugRaysArray, 0, 0, debugRayCount);

        Gizmos.color = Color.cyan;
        for (int i = 0; i < debugRayCount; i++)
        {
            Gizmos.DrawRay(debugRaysArray[i].position, debugRaysArray[i].direction * debugRayLength);
        }
    }
}