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
    int resetCounterKernel;
    public ComputeShader audioShader;

    public int initialRays = 1024000;

    ComputeBuffer rayBuffer;
    ComputeBuffer sourcesBuffer;
    ComputeBuffer pathBuffer;
    ComputeBuffer pathCounterBuffer;
    ComputeBuffer debugBuffer;
    ComputeBuffer instancesBuffer;
    ComputeBuffer materialsBuffer;

    readonly uint maxPaths = 20480;

    private AsyncGPUReadbackRequest pathCounterRequest;
    private AsyncGPUReadbackRequest pathDataRequest;
    private bool isTracing = false;

    // filter
    FilterCoefficients[] filterCoefficients;
    public float[] centerFreqs = { 125f, 250f, 500f, 1000f, 2000f, 4000f };
    public float bandwidth = 100f;
    private const float thirdOctaveFactor = 0.23156333016903374f; // Precomputed value for (2^(1/6) - 2^(-1/6))

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        listener = FindFirstObjectByType<ListenerController>();

        registeredAudioSources = new Dictionary<int, AcousticSource>();

        initRaysKernel = audioShader.FindKernel("InitRays");
        traceKernel = audioShader.FindKernel("TraceRays");
        resetCounterKernel = audioShader.FindKernel("ResetCounter");

        // successful paths data
        pathBuffer = new ComputeBuffer((int)maxPaths, Marshal.SizeOf(typeof(PathData)));
        pathCounterBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Default);

        audioShader.SetBuffer(traceKernel, "pathBuffer", pathBuffer);
        audioShader.SetBuffer(traceKernel, "pathCounter", pathCounterBuffer);

        audioShader.SetBuffer(resetCounterKernel, "pathCounter", pathCounterBuffer);


        //sources buffer
        sourcesBuffer = new ComputeBuffer(64, Marshal.SizeOf(typeof(SourceData)));

        audioShader.SetBuffer(traceKernel, "sources", sourcesBuffer);

        debugBuffer = new ComputeBuffer(1, Marshal.SizeOf(typeof(DebugInfo)));
        audioShader.SetBuffer(traceKernel, "debugInfo", debugBuffer);

        audioShader.SetBuffer(traceKernel, "triangleSoup", bvhManager.GetTrianglesBuffer());
        audioShader.SetBuffer(traceKernel, "blasTrees", bvhManager.GetBlasNodesBuffer());

        UpdateMaterialsAndInstances();

        //filter init
        filterCoefficients = new FilterCoefficients[6];

        for (int i = 0; i < 6; i++)
        {
            bandwidth = centerFreqs[i] * thirdOctaveFactor;
            filterCoefficients[i] = CreateBandPass(centerFreqs[i], bandwidth, AudioSettings.outputSampleRate);
        }

        InitializeRays(initialRays);
    }

    private void LateUpdate()
    {
        UpdateListenerData();

        //maybe skip calling it every frame if we are not adding or removing objects
        UpdateMaterialsAndInstances();

        //update the tree
        bvhManager.UpdateBVH();
        audioShader.SetBuffer(traceKernel, "tlasTree", bvhManager.GetBVHBuffer());

        if (isTracing && pathCounterRequest.done && pathDataRequest.done)
        {
            isTracing = false;
            if (!pathCounterRequest.hasError && !pathDataRequest.hasError)
            {
                var counterArray = pathCounterRequest.GetData<uint>();
                uint count = counterArray[0] < maxPaths ? counterArray[0] : maxPaths;

                PathData[] paths = new PathData[maxPaths];
                pathDataRequest.GetData<PathData>().CopyTo(paths);

                ProcessAudioPaths(paths, count);
            }
        }

        //trace rays if there are audio sources
        if (registeredAudioSources.Count > 0 && isTracing == false)
        {
            isTracing = true;

            SetupSourceBuffer();

            audioShader.Dispatch(resetCounterKernel, 1, 1, 1);

            audioShader.Dispatch(traceKernel, initialRays / 64, 1, 1);

            pathCounterRequest = AsyncGPUReadback.Request(pathCounterBuffer);
            pathDataRequest = AsyncGPUReadback.Request(pathBuffer);
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
        materialsBuffer?.Release();
        rayBuffer?.Release();
        pathCounterBuffer?.Release();
    }

    void UpdateMaterialsAndInstances()
    {
        //materials
        var materials = ObjectRegistry<MaterialData>.Instance.GetValues();

        if (materialsBuffer == null || materialsBuffer.count != materials.Length)
        {
            materialsBuffer?.Release();
            materialsBuffer = new ComputeBuffer(materials.Length, Marshal.SizeOf(typeof(MaterialData)));
            audioShader.SetBuffer(traceKernel, "materials", materialsBuffer);
        }

        materialsBuffer.SetData(materials);

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

    static readonly ProfilerMarker processFrameMarker = new("Acoustic.ProcessAudioPaths");


    void ProcessAudioPaths(PathData[] paths, uint counter)
    {
        using (processFrameMarker.Auto())
        {
            sourcesToRemove.Clear();
            //first we clean up this frame's data
            foreach (var audioSource in registeredAudioSources.Values)
            {
                audioSource.frameGain = 0f;
                audioSource.frameDistance = float.MaxValue;
            }

            Dictionary<int, List<PathData>> pathsBySource = new Dictionary<int, List<PathData>>();

            for (int i = 0; i < counter; i++)
            {
                var path = paths[i];
                if (!pathsBySource.TryGetValue(path.sourceId, out var list))
                {
                    list = new List<PathData>();
                    pathsBySource[path.sourceId] = list;
                }

                list.Add(path);

                // we accumulate per path the gain and the relevant data for each source
                if (registeredAudioSources.TryGetValue(path.sourceId, out var source))
                {
                    source.frameGain += path.totalGain();
                    if (path.distance < source.frameDistance)
                    {
                        source.frameDistance = path.distance;
                    }
                }
            }

            // we apply this data to each source
            foreach (var source in registeredAudioSources.Values)
            {
                float delaySeconds = source.frameDistance / 343.0f;
                float timeOfArrival = source.timeOfEmission + delaySeconds;
                float elapsedTime = Time.time - timeOfArrival;
                
                if (pathsBySource.TryGetValue(source.gameObject.GetInstanceID(), out var sourcePaths))
                {
                    PathData[] finalPaths = new PathData[sourcePaths.Count];
                    sourcePaths.CopyTo(finalPaths);
                    source.UpdateReflections(finalPaths);
                }
                else
                {
                    var emptyList = Array.Empty<PathData>();
                    source.UpdateReflections(emptyList);
                }

                if (Time.time >= timeOfArrival)
                {
                    //the sound played
                    if (!source.audioSource.loop && elapsedTime >= source.audioSource.clip.length)
                    {
                        sourcesToRemove.Add(source.gameObject.GetInstanceID());
                        continue;
                    }

                    if (!source.audioSource.isPlaying)
                    {
                        source.audioSource.time = elapsedTime % source.audioSource.clip.length;
                        source.audioSource.Play(); //.PlayOneShot(source.audioSource.clip, source.volume);
                    }
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
        for (int i = 0; i < values.Length; i++)
        {
            sourceDataArray[i] = new SourceData()
            {
                origin = values[i].transform.position,
                sourceId = values[i].gameObject.GetInstanceID(),
                radius = values[i].radius,
                maxAudibleDistance = values[i].maxAudibleDistance,
                minAudibleDistance = values[i].minAudibleDistance,
                power = values[i].baseAmplitudeWeighted
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
            registeredAudioSources[sourcesToRemove[i]].UnRegisterSound();
            registeredAudioSources.Remove(sourcesToRemove[i]);
        }

        sourcesToRemove.Clear();
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