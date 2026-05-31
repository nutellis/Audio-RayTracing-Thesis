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

    Dictionary<int, AcousticSource> registeredAudioSources;
    List<int> sourcesToRemove = new List<int>(32);

    public ListenerController listener;

    public int maxDb = 120;

    int initRaysKernel;
    int extendRaysKernel;
    int shadePathsKernel;
    int connectShadowsKernel;
    int traceDirectKernel;
    int echogramResetKernel;
    int prepareArgsKernel;
    int convertToFloatKernel;
    public ComputeShader audioShader;

    public int initialRays = 64000;

    ComputeBuffer pathQueueA;
    ComputeBuffer pathQueueB;
    ComputeBuffer intersectionBuffer;
    ComputeBuffer shadowQueue;

    ComputeBuffer indirectArgsBuffer;
    ComputeBuffer activeCountBuffer;
    uint[] countData = new uint[1];

    ComputeBuffer sourcesBuffer;
    ComputeBuffer debugBuffer;
    ComputeBuffer instancesBuffer;
    ComputeBuffer materialsBuffer;
    
    ComputeBuffer directAudioBuffer;
    DirectAudioData[] readbackDirectData;
    private AsyncGPUReadbackRequest directDataRequest;
    
    private AsyncGPUReadbackRequest echogramRequest;
    private bool isTracing = false;
    
    GraphicsBuffer finalFloatBuffer;
    AcousticData[] readbackFloatData;

    // filter
    FilterCoefficients[] filterCoefficients;
    public float[] centerFreqs = { 125f, 250f, 500f, 1000f, 2000f, 4000f };
    public float bandwidth = 100f;
    private const float thirdOctaveFactor = 0.23156333016903374f; 

    private const int MAX_BINS = 1600; 
    private const int MAX_SOURCES = 64;
    private int totalEchogramSize = MAX_SOURCES * MAX_BINS;
    GraphicsBuffer echogramBuffer;
    GraphicsBuffer echogramStagingBuffer;
    MacroBin[] readbackEchogramData;
    AcousticSource[] currentFrameSources; 

    private bool sourcesDirty;
   
    void Start()
    {
        listener = FindFirstObjectByType<ListenerController>();
        registeredAudioSources = new Dictionary<int, AcousticSource>();

        initRaysKernel = audioShader.FindKernel("InitRays");
        extendRaysKernel = audioShader.FindKernel("ExtendRays");
        shadePathsKernel = audioShader.FindKernel("ShadePaths");
        connectShadowsKernel = audioShader.FindKernel("ConnectShadows");
        traceDirectKernel = audioShader.FindKernel("TraceDirectSound");
        echogramResetKernel = audioShader.FindKernel("ResetEchogram");

        echogramBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource, totalEchogramSize, 24); 
        echogramStagingBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopyDestination, totalEchogramSize, 24);
        readbackEchogramData = new MacroBin[totalEchogramSize];
        
        audioShader.SetBuffer(shadePathsKernel, "echogramBuffer", echogramBuffer);
        audioShader.SetBuffer(connectShadowsKernel, "echogramBuffer", echogramBuffer);
        audioShader.SetBuffer(echogramResetKernel, "echogramBuffer", echogramBuffer);

        sourcesBuffer = new ComputeBuffer(MAX_SOURCES, Marshal.SizeOf(typeof(SourceData)));
        BindBufferToKernels("sources", sourcesBuffer, extendRaysKernel, shadePathsKernel, connectShadowsKernel, traceDirectKernel);

        BindBufferToKernels("triangleSoup", bvhManager.GetTrianglesBuffer(), extendRaysKernel, connectShadowsKernel, traceDirectKernel);
        BindBufferToKernels("blasTrees", bvhManager.GetBlasNodesBuffer(), extendRaysKernel, connectShadowsKernel, traceDirectKernel);

        directAudioBuffer = new ComputeBuffer(MAX_SOURCES, Marshal.SizeOf(typeof(DirectAudioData)));
        readbackDirectData = new DirectAudioData[MAX_SOURCES];
        audioShader.SetBuffer(traceDirectKernel, "directAudioOutput", directAudioBuffer);
        
        prepareArgsKernel = audioShader.FindKernel("PrepareIndirectArgs");
        
        indirectArgsBuffer = new ComputeBuffer(3, sizeof(uint), ComputeBufferType.IndirectArguments); 
        activeCountBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Raw);
        
        convertToFloatKernel = audioShader.FindKernel("ConvertToFloat");

        finalFloatBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalEchogramSize, 24); 
        readbackFloatData = new AcousticData[totalEchogramSize];

        audioShader.SetBuffer(convertToFloatKernel, "echogramBuffer", echogramBuffer);
        audioShader.SetBuffer(convertToFloatKernel, "finalFloatEchogram", finalFloatBuffer);
        
        InitializeStaticMaterials();

        filterCoefficients = new FilterCoefficients[6];
        for (int i = 0; i < 6; i++)
        {
            bandwidth = centerFreqs[i] * thirdOctaveFactor;
            filterCoefficients[i] = CreateBandPass(centerFreqs[i], bandwidth, AudioSettings.outputSampleRate);
        }
        
        SetupComputeBuffers(initialRays);
    }
    
    private void OnDestroy()
    {
        sourcesBuffer?.Release();
        instancesBuffer?.Release();
        materialsBuffer?.Release();
        pathQueueA?.Release();
        pathQueueB?.Release();
        intersectionBuffer?.Release();
        shadowQueue?.Release();
        indirectArgsBuffer?.Release();
        activeCountBuffer?.Release();
        echogramBuffer?.Release();
        debugBuffer?.Release();
        directAudioBuffer?.Release();
        echogramStagingBuffer?.Release();
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
            BindBufferToKernels("tlasTree", bvhManager.GetBVHBuffer(), extendRaysKernel, connectShadowsKernel, traceDirectKernel);
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

        if (registeredAudioSources.Count > 0 && isTracing == false)
        {
            isTracing = true;
            SetupSourceBuffer();
            
            int clearThreads = Mathf.CeilToInt(totalEchogramSize / 64f);
            audioShader.Dispatch(echogramResetKernel, clearThreads, 1, 1);

            ExecuteWavefrontTracer();

            int directGroups = Mathf.Max(1, Mathf.CeilToInt(currentFrameSources.Length / 64f));
            audioShader.Dispatch(traceDirectKernel, directGroups, 1, 1);
            
            int convertGroups = Mathf.CeilToInt(totalEchogramSize / 64f);
            audioShader.Dispatch(convertToFloatKernel, convertGroups, 1, 1);
            
            echogramRequest = AsyncGPUReadback.Request(finalFloatBuffer);
            directDataRequest = AsyncGPUReadback.Request(directAudioBuffer);
        }
        CleanSounds();
    }
    
    void ExecuteWavefrontTracer()
{
    pathQueueA.SetCounterValue(0);
    audioShader.SetBuffer(initRaysKernel, "pathQueueOut", pathQueueA);
    audioShader.Dispatch(initRaysKernel, Mathf.CeilToInt(initialRays / 64f), 1, 1);

    ComputeBuffer currentQueue = pathQueueA;
    ComputeBuffer nextQueue = pathQueueB;

    for (int bounce = 0; bounce < 128; bounce++)
    {
        ComputeBuffer.CopyCount(currentQueue, activeCountBuffer, 0);
        audioShader.SetBuffer(prepareArgsKernel, "activeCount", activeCountBuffer);
        audioShader.SetBuffer(prepareArgsKernel, "indirectArgs", indirectArgsBuffer);
        audioShader.Dispatch(prepareArgsKernel, 1, 1, 1);

        audioShader.SetBuffer(extendRaysKernel, "activeCount", activeCountBuffer);
        audioShader.SetBuffer(extendRaysKernel, "pathQueueIn", currentQueue);
        audioShader.SetBuffer(extendRaysKernel, "intersectionBuffer", intersectionBuffer);
        audioShader.DispatchIndirect(extendRaysKernel, indirectArgsBuffer);

        nextQueue.SetCounterValue(0);
        shadowQueue.SetCounterValue(0);

        audioShader.SetBuffer(shadePathsKernel, "activeCount", activeCountBuffer);
        audioShader.SetBuffer(shadePathsKernel, "pathQueueIn", currentQueue);
        audioShader.SetBuffer(shadePathsKernel, "intersectionBuffer", intersectionBuffer);
        audioShader.SetBuffer(shadePathsKernel, "pathQueueOut", nextQueue);
        audioShader.SetBuffer(shadePathsKernel, "shadowQueueOut", shadowQueue);
        audioShader.DispatchIndirect(shadePathsKernel, indirectArgsBuffer);

        ComputeBuffer.CopyCount(shadowQueue, activeCountBuffer, 0);
        audioShader.SetBuffer(prepareArgsKernel, "activeCount", activeCountBuffer);
        audioShader.SetBuffer(prepareArgsKernel, "indirectArgs", indirectArgsBuffer);
        audioShader.Dispatch(prepareArgsKernel, 1, 1, 1);

        audioShader.SetBuffer(connectShadowsKernel, "activeCount", activeCountBuffer);
        audioShader.SetBuffer(connectShadowsKernel, "shadowQueueIn", shadowQueue);
        audioShader.DispatchIndirect(connectShadowsKernel, indirectArgsBuffer);

        (currentQueue, nextQueue) = (nextQueue, currentQueue);
    }
}

    void SetupComputeBuffers(int initialRays)
    {
        pathQueueA = new ComputeBuffer(initialRays, 60, ComputeBufferType.Append);
        pathQueueB = new ComputeBuffer(initialRays, 60, ComputeBufferType.Append);
        
        intersectionBuffer = new ComputeBuffer(initialRays, 32);
        
        shadowQueue = new ComputeBuffer(initialRays * MAX_SOURCES, 64, ComputeBufferType.Append);
    }
    
    void BindBufferToKernels(string bufferName, ComputeBuffer buffer, params int[] kernels)
    {
        foreach (int kernel in kernels)
        {
            audioShader.SetBuffer(kernel, bufferName, buffer);
        }
    }

    void InitializeStaticMaterials()
    {
        var materials = ObjectRegistry<MaterialData>.Instance.GetValues();
        materialsBuffer?.Release();
        materialsBuffer = new ComputeBuffer(materials.Length, Marshal.SizeOf(typeof(MaterialData)));
        materialsBuffer.SetData(materials);
        
        BindBufferToKernels("materials", materialsBuffer, extendRaysKernel, shadePathsKernel, connectShadowsKernel, traceDirectKernel);
    }

    void UpdateInstances(bool forceUpdate)
    {
        if (!forceUpdate) return;

        var instances = ObjectRegistry<Instance>.Instance.GetValues();
        if (instancesBuffer == null || instancesBuffer.count != instances.Length)
        {
            instancesBuffer?.Release();
            instancesBuffer = new ComputeBuffer(instances.Length, Marshal.SizeOf(typeof(Instance)));
            BindBufferToKernels("objectInstances", instancesBuffer, extendRaysKernel, connectShadowsKernel, traceDirectKernel);
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
            
                if (!source || !registeredAudioSources.ContainsKey(source.gameObject.GetInstanceID()))
                {
                    continue;
                }

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