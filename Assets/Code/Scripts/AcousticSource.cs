using System;
using System.Runtime.InteropServices;
using Code.Data;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using FMODUnity;
using FMOD.Studio;

using UnityEngine.Serialization;

public class AcousticSource : MonoBehaviour
{
    public struct BakedReflection
    {
        public int delaySamples;
        public float energy0, energy1, energy2, energy3, energy4, energy5;
    }
    
    // In AcousticSource class variables:
    private const int MAX_BAKED_REFLECTIONS = 128; // Slightly larger to accommodate the splits
    private BakedReflection[][] bakedBuffers;

    
    private void Initialize1()
    {
        RuntimeManager.CoreSystem.getSoftwareFormat(out cachedSampleRate, out FMOD.SPEAKERMODE speakerMode, out int numRawSpeakers);
        
        sourceDb = (sourceDb > 0) ? sourceDb : profile.dbLevel;

        baseAmplitude = math.pow(10f, (sourceDb - 60f) / 20f);
        baseAmplitudeWeighted = baseAmplitude * profile.acousticWeight;
    
        CalculateDistances();

        var collider = gameObject.GetComponent<SphereCollider>();
        radius = collider ? collider.radius : 1f;
    
        historyBuffer = new NativeArray<float>(historySize * 6, Allocator.Persistent);
        unsafe 
        {
            historyBufferPtr = (IntPtr)NativeArrayUnsafeUtility.GetUnsafePtr(historyBuffer);
        }
    
        state = new int [2];
        state[0] = 0; // writeIndex
        state[1] = historySize - 1; // wrapMask
    
        // NEW: Initialize the baked integer buffers
        bakedBuffers = new BakedReflection[2][];
        bakedBuffers[0] = new BakedReflection[MAX_BAKED_REFLECTIONS];
        bakedBuffers[1] = new BakedReflection[MAX_BAKED_REFLECTIONS];

        // 64 reflections x 6 bands = 384 filter states
        filterStates = new FilterState[384];
    }
    
    private void Dispose1()
    {  
        if (historyBuffer.IsCreated)
        {
            historyBuffer.Dispose();
        }
    
        if (bakedBuffers != null)
        {
            bakedBuffers[0] = null;
            bakedBuffers[1] = null;
            bakedBuffers = null;
        }
    
        filterStates = null;
    }
    
    public void UpdateReflections1(PathData[] sourcePaths)
{
    int writeIndex = (activeBufferIndex + 1) % 2;
    int bakedCount = 0;

    float rayEnergyScalar = 1.0f / 2048f; // Ensure your ray count is correct
    float directGainAccumulator = 0f;

    // Clear the active buffer for writing
    Array.Clear(bakedBuffers[writeIndex], 0, MAX_BAKED_REFLECTIONS);

    for (int i = 0; i < sourcePaths.Length; i++)
    {
        var path = sourcePaths[i];

        if (path.state == 0 || path.state == 10) // direct or "specular" hits at the zero bounce" <- that means that the ray is in direct view of the audio sphere.
        {
            float dE0 = path.energy0, dE1 = path.energy1, dE2 = path.energy2;
            float dE3 = path.energy3, dE4 = path.energy4, dE5 = path.energy5;

            AirAbsorption.ApplyAbsorption(ref dE0, ref dE1, ref dE2, ref dE3, ref dE4, ref dE5, path.distance);
            directGainAccumulator += ((dE0 + dE1 + dE2 + dE3 + dE4 + dE5) / 6f);
            continue;
        }

        float deltaDistance = path.distance - frameDistance;
        if (deltaDistance < 0.05f) continue;

        float pE0 = path.energy0, pE1 = path.energy1, pE2 = path.energy2;
        float pE3 = path.energy3, pE4 = path.energy4, pE5 = path.energy5;

        AirAbsorption.ApplyAbsorption(ref pE0, ref pE1, ref pE2, ref pE3, ref pE4, ref pE5, path.distance);

        float delaySamples = (deltaDistance / 343.0f) * cachedSampleRate;
        int sampleFloor = (int)math.floor(delaySamples);
        float fraction = delaySamples - sampleFloor;
        
        float invFraction = 1.0f - fraction;

        // Add the base integer delay
        bakedCount = MergeOrAddBakedReflection(writeIndex, bakedCount, sampleFloor, 
            pE0 * rayEnergyScalar * invFraction, 
            pE1 * rayEnergyScalar * invFraction, 
            pE2 * rayEnergyScalar * invFraction, 
            pE3 * rayEnergyScalar * invFraction, 
            pE4 * rayEnergyScalar * invFraction, 
            pE5 * rayEnergyScalar * invFraction);

        // Add the +1 integer delay for the fractional remainder
        if (fraction > 0.001f)
        {
            bakedCount = MergeOrAddBakedReflection(writeIndex, bakedCount, sampleFloor + 1, 
                pE0 * rayEnergyScalar * fraction, 
                pE1 * rayEnergyScalar * fraction, 
                pE2 * rayEnergyScalar * fraction, 
                pE3 * rayEnergyScalar * fraction, 
                pE4 * rayEnergyScalar * fraction, 
                pE5 * rayEnergyScalar * fraction);
        }
    }

    // Clamp total energies
    for (int r = 0; r < bakedCount; r++)
    {
        bakedBuffers[writeIndex][r].energy0 = math.min(bakedBuffers[writeIndex][r].energy0, 1.0f);
        bakedBuffers[writeIndex][r].energy1 = math.min(bakedBuffers[writeIndex][r].energy1, 1.0f);
        bakedBuffers[writeIndex][r].energy2 = math.min(bakedBuffers[writeIndex][r].energy2, 1.0f);
        bakedBuffers[writeIndex][r].energy3 = math.min(bakedBuffers[writeIndex][r].energy3, 1.0f);
        bakedBuffers[writeIndex][r].energy4 = math.min(bakedBuffers[writeIndex][r].energy4, 1.0f);
        bakedBuffers[writeIndex][r].energy5 = math.min(bakedBuffers[writeIndex][r].energy5, 1.0f);
    }

    this.directGain = math.min(directGainAccumulator, 1.0f);
    activeReflectionCount = bakedCount;
    activeBufferIndex = writeIndex;
}

// Helper method to merge delays that land on the exact same integer sample
private int MergeOrAddBakedReflection(int bufferIndex, int currentCount, int delay, float e0, float e1, float e2, float e3, float e4, float e5)
{
    var buffer = bakedBuffers[bufferIndex];

    for (int i = 0; i < currentCount; i++)
    {
        if (buffer[i].delaySamples == delay)
        {
            buffer[i].energy0 += e0;
            buffer[i].energy1 += e1;
            buffer[i].energy2 += e2;
            buffer[i].energy3 += e3;
            buffer[i].energy4 += e4;
            buffer[i].energy5 += e5;
            return currentCount;
        }
    }

    if (currentCount < MAX_BAKED_REFLECTIONS)
    {
        buffer[currentCount] = new BakedReflection
        {
            delaySamples = delay,
            energy0 = e0, energy1 = e1, energy2 = e2, energy3 = e3, energy4 = e4, energy5 = e5
        };
        return currentCount + 1;
    }

    return currentCount;
}
    
    private bool isRegistered = false;
    private bool hasStartedPlaying = false;
    
    public EventReference fmodEvent;
    private EventInstance m_EventInstance;

    private FMOD.DSP m_CustomDSP;
    private FMOD.DSP_READ_CALLBACK m_ReadCallback;
    private GCHandle m_ObjHandle;

    [Tooltip("Faint = 0-40 average: ~20 \r\nNormal = 41-75 average: ~58\r\nLoud = 76-100 average: ~88  \r\nExtreme = 101-120 average: ~110")]
    public AcousticProfile profile;
    [FormerlySerializedAs("manualDb")] [Tooltip("Can be used to fine-tune the profile.\nCaution if the values are far from the profile you are effectively overriding the profile\nDefault is 0")]
    public float sourceDb = 0f;

    readonly float sortingGain;

    private float baseAmplitude;
    private float attenuation;
    public float baseAmplitudeWeighted;

    
    
    public float timeOfEmission = 0;

    public float radius;
    
    NativeArray<float> historyBuffer;
    public IntPtr historyBufferPtr; 
    int historySize = 65536; // 131072; // 2 seconds at 44.8khz
    
    private int[] state; // [0] writeIndex, [1] wrapMask
    
    private int cachedSampleRate = 48000;
    
    public float frameGain;
    public float frameDistance;
    
    public float maxAudibleDistance;
    public float minAudibleDistance;
    
    public float directGain = 0;
    // echo and reflections 
    private const int MAX_REFLECTIONS_PER_SOURCE = 64; 
    private Reflection[][] reflectionBuffers;
    private volatile int activeBufferIndex = 0;
    private int activeReflectionCount = 0;


    // filter related
    private FilterState[] filterStates;
    private FilterCoefficients[] cachedCoefficients;
    
    
    //testing
    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            TogglePlayback();
        }
    }

    public void TogglePlayback()
    {
        if (isRegistered)
        {
            UnRegisterSound();
            Debug.Log("FMOD Sound Stopped.");
        }
        else
        {
            RegisterSound();
            Debug.Log("FMOD Sound Started.");
        }
    }

    private void OnEnable()
    {
        Initialize1();
    }

    private void OnDisable()
    {
        Dispose1();
    }

    private void OnDestroy()
    {
        Dispose1();
    }

    private void Initialize()
    {
        RuntimeManager.CoreSystem.getSoftwareFormat(out cachedSampleRate, out FMOD.SPEAKERMODE speakerMode, out int numRawSpeakers);

        sourceDb = (sourceDb > 0) ? sourceDb : profile.dbLevel;

        // Calculate once and store
        baseAmplitude = math.pow(10f, (sourceDb - 60f) / 20f);
        baseAmplitudeWeighted = baseAmplitude * profile.acousticWeight;
        
        CalculateDistances();

        var collider = gameObject.GetComponent<SphereCollider>();
        radius = collider ? collider.radius : 1f;
        
        historyBuffer = new NativeArray<float>(historySize * 6, Allocator.Persistent);
        unsafe 
        {
            // Extract the raw pointer once on the main thread
            historyBufferPtr = (IntPtr)NativeArrayUnsafeUtility.GetUnsafePtr(historyBuffer);
        }
        state = new int [2];
        state[0] = 0; // writeIndex
        state[1] = historySize - 1; // wrapMask
        
        reflectionBuffers = new Reflection[2][];
        reflectionBuffers[0] = new Reflection[MAX_REFLECTIONS_PER_SOURCE];
        reflectionBuffers[1] = new Reflection[MAX_REFLECTIONS_PER_SOURCE];

        //filter related
        
        // 64 reflections x 6 bands = 384 filter states
        filterStates = new FilterState[384];
    }
    
    private void Dispose()
    {  
        if (historyBuffer.IsCreated)
        {
            historyBuffer.Dispose();
        }
        reflectionBuffers[0] = null;
        reflectionBuffers[1] = null;
        filterStates = null;
    }
    
    public bool IsFinished()
    {
        if (!hasStartedPlaying) return false;

        // If FMOD already destroyed the one-shot, it's done.
        if (!m_EventInstance.isValid()) return true;

        // Safely poll the async state
        FMOD.RESULT result = m_EventInstance.getPlaybackState(out FMOD.Studio.PLAYBACK_STATE state);
    
        if (result != FMOD.RESULT.OK) return true;

        return state == FMOD.Studio.PLAYBACK_STATE.STOPPING || 
               state == FMOD.Studio.PLAYBACK_STATE.STOPPED;
    }

    public bool IsPlaying()
    {
        return hasStartedPlaying && !IsFinished();
    }
    
    public void PlayWithOffset(float elapsedTimeSeconds)
    {
        if (!m_EventInstance.isValid()) return;

        int offsetMs = (int)(elapsedTimeSeconds * 1000f);

        RuntimeManager.StudioSystem.getEventByID(fmodEvent.Guid, out FMOD.Studio.EventDescription desc);
        desc.getLength(out int lengthMs);
        desc.is3D(out bool is3D); // Optional: check if you accidentally left spatialization on
    
        // Identify if this specific event is set to loop in FMOD Studio
        desc.isOneshot(out bool isOneshot); 
        bool isLooping = isOneshot; // Simplistic check; true loops rely on region setups in FMOD
    
        if (lengthMs > 0)
        {
            if (offsetMs >= lengthMs)
            {
                if (isLooping)
                {
                    offsetMs %= lengthMs;
                }
                else
                {
                    // The sound finished its entire duration while traveling through the air.
                    // Do not play it. Flag it to be cleaned up next frame.
                    hasStartedPlaying = true; 
                    m_EventInstance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
                    return;
                }
            }
        }

        m_EventInstance.setTimelinePosition(offsetMs);
        m_EventInstance.setPaused(false);
        hasStartedPlaying = true;
    }

    public float GetScore(Vector3 listenerPos)
    {
        float d2 = (transform.position - listenerPos).sqrMagnitude;
        return baseAmplitudeWeighted / Mathf.Max(1f, d2);
    }
    
    private float DistanceAttenuation(float distance, float referenceDistance, float falloffFactor)
    {
        distance = Mathf.Max(distance, referenceDistance);
        float gain = referenceDistance / (referenceDistance + falloffFactor * (distance - referenceDistance));
        return gain;
    }
    
    
    private void CalculateDistances()
    {
        if (sourceDb <= 20f) 
        {
            minAudibleDistance = 0.1f;
            maxAudibleDistance = 0.1f;
            return;
        }
        
        float dbDifferenceFromBaseline = sourceDb - 60f;
    
        minAudibleDistance = 1.0f * Mathf.Pow(10f, dbDifferenceFromBaseline / 20f);

        float dbDropNeeded = sourceDb - 20f;
        maxAudibleDistance = minAudibleDistance * Mathf.Pow(10f, dbDropNeeded / 20f);
    }
    

    public void RegisterSound()
    {
        if (isRegistered) return;
        
        timeOfEmission = Time.time;
        AudioManager manager =  FindAnyObjectByType(typeof(AudioManager)) as AudioManager;
        if (manager != null)
        {
            manager.RegisterAudio(this);
            cachedCoefficients = manager.GetFilterCoefficients();
        }
        m_ObjHandle = GCHandle.Alloc(this);
        m_ReadCallback = new FMOD.DSP_READ_CALLBACK(DSPReadCallback);

        // 2. Define the DSP
        FMOD.DSP_DESCRIPTION desc = new FMOD.DSP_DESCRIPTION
        {
            pluginsdkversion = FMOD.VERSION.number,
            numinputbuffers = 1,
            numoutputbuffers = 1,
            read = m_ReadCallback,
            userdata = GCHandle.ToIntPtr(m_ObjHandle)
        };

        // 3. Create the DSP in FMOD Core
        RuntimeManager.CoreSystem.createDSP(ref desc, out m_CustomDSP);

        // 4. Instantiate and play the FMOD Event
        m_EventInstance = RuntimeManager.CreateInstance(fmodEvent);
    
        // Attach 3D position tracking if your event uses spatialization
        RuntimeManager.AttachInstanceToGameObject(m_EventInstance, gameObject, GetComponent<Rigidbody>());
    
        m_EventInstance.start();
        m_EventInstance.setPaused(true); 
        RuntimeManager.StudioSystem.flushCommands();
        
        // 6. Attach DSP to the Event's ChannelGroup
        m_EventInstance.getChannelGroup(out FMOD.ChannelGroup channelGroup);
        if (channelGroup.hasHandle())
        {
            channelGroup.addDSP(FMOD.CHANNELCONTROL_DSP_INDEX.HEAD, m_CustomDSP);
        }
    
        hasStartedPlaying = false;
        isRegistered = true;
    }
    
    public void UnRegisterSound()
    {
        if (!isRegistered) return;
        activeReflectionCount = 0;

        // 1. Stop Audio
        if (m_EventInstance.isValid())
        {
            m_EventInstance.stop(FMOD.Studio.STOP_MODE.ALLOWFADEOUT);
            m_EventInstance.release();
        }

        // 2. Detach and release DSP
        if (m_CustomDSP.hasHandle())
        {
            m_EventInstance.getChannelGroup(out FMOD.ChannelGroup channelGroup);
            if (channelGroup.hasHandle())
            {
                channelGroup.removeDSP(m_CustomDSP);
            }
            m_CustomDSP.release();
            m_CustomDSP.clearHandle();
        }

        // 3. Free the pinned memory handle
        if (m_ObjHandle.IsAllocated)
        {
            m_ObjHandle.Free();
        }
        
        isRegistered = false;
        hasStartedPlaying = false;
    }
    
    public void UpdateReflections(PathData[] sourcePaths)
    {
        int writeIndex = (activeBufferIndex + 1) % 2;
        int validReflections = 0;

        float rayEnergyScalar = (1.0f / 64000); //* 5000;
        
        // Consider moving these to Start() or class level variables (Haas Effect)
        int mergeThresholdSamples = (int)(0.0025f * cachedSampleRate); 

        float directGainAccumulator = 0f;
        
        for (int i = 0; i < sourcePaths.Length; i++)
        {
            var path = sourcePaths[i];
            
            if (path.state == 0) 
            {
                float dE0 = path.energy0, dE1 = path.energy1, dE2 = path.energy2;
                float dE3 = path.energy3, dE4 = path.energy4, dE5 = path.energy5;

                AirAbsorption.ApplyAbsorption(ref dE0, ref dE1, ref dE2, ref dE3, ref dE4, ref dE5, path.distance);
                
                directGainAccumulator += ((dE0 + dE1 + dE2 + dE3 + dE4 + dE5) / 6f);
                continue; 
            }
            
            
            float deltaDistance = path.distance - frameDistance;
            
            // Skip direct sound
            if (deltaDistance < 0.05f) continue; 
            
            //air absorption
            float pE0 = path.energy0;
            float pE1 = path.energy1;
            float pE2 = path.energy2;
            float pE3 = path.energy3;
            float pE4 = path.energy4;
            float pE5 = path.energy5;

            AirAbsorption.ApplyAbsorption(ref pE0, ref pE1, ref pE2, ref pE3, ref pE4, ref pE5, path.distance);
            
            float delaySamples = (deltaDistance / 343.0f) * cachedSampleRate;
            int sampleFloor = (int)math.floor(delaySamples);
            float fraction = delaySamples - sampleFloor;
            bool merged = false;
            
            // Check for temporal overlap (binning of 2.5ms) - Haas effect
            for (int r = 0; r < validReflections; r++)
            {
                var existingRef = reflectionBuffers[writeIndex][r];
                
                if (Mathf.Abs(existingRef.delaySamples - delaySamples) < mergeThresholdSamples)
                {
                    existingRef.energy0 += pE0 * rayEnergyScalar;
                    existingRef.energy1 += pE1 * rayEnergyScalar;
                    existingRef.energy2 += pE2 * rayEnergyScalar;
                    existingRef.energy3 += pE3 * rayEnergyScalar;
                    existingRef.energy4 += pE4 * rayEnergyScalar;
                    existingRef.energy5 += pE5 * rayEnergyScalar;
                    
                    reflectionBuffers[writeIndex][r] = existingRef;
                    merged = true;
                    break;
                }
            }

            // Initialize new echo if no overlap
            if (!merged && validReflections < MAX_REFLECTIONS_PER_SOURCE)
            {
                reflectionBuffers[writeIndex][validReflections] = new Reflection()
                {
                    delaySamples = sampleFloor,
                    fraction = fraction,
                    energy0 = pE0 * rayEnergyScalar,
                    energy1 = pE1 * rayEnergyScalar,
                    energy2 = pE2 * rayEnergyScalar,
                    energy3 = pE3 * rayEnergyScalar,
                    energy4 = pE4 * rayEnergyScalar,
                    energy5 = pE5 * rayEnergyScalar,
                    arrivalAngles = path.arrivalAngles
                };
                validReflections++;
            }
        }
    
        if (validReflections > 0)
        {
            Array.Sort(reflectionBuffers[writeIndex], 0, validReflections);
        }
        
        // clamp accumulated volumes to 1.0.
        for(int r = 0; r < validReflections; r++)
        {
            var reflection = reflectionBuffers[writeIndex][r];
            reflection.energy0 = math.min(reflection.energy0, 1.0f);
            reflection.energy1 = math.min(reflection.energy1, 1.0f);
            reflection.energy2 = math.min(reflection.energy2, 1.0f);
            reflection.energy3 = math.min(reflection.energy3, 1.0f);
            reflection.energy4 = math.min(reflection.energy4, 1.0f);
            reflection.energy5 = math.min(reflection.energy5, 1.0f);
            
            reflectionBuffers[writeIndex][r] = reflection;
        }
        
        this.directGain = math.min(directGainAccumulator, 1.0f);
        activeReflectionCount = validReflections;
        activeBufferIndex = writeIndex; 
    }
    
    [AOT.MonoPInvokeCallback(typeof(FMOD.DSP_READ_CALLBACK))]
    static FMOD.RESULT DSPReadCallback(ref FMOD.DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        // Retrieve the pinned instance
        dsp_state.functions.getuserdata(ref dsp_state, out IntPtr userData);
        GCHandle handle = GCHandle.FromIntPtr(userData);
        AcousticSource src = handle.Target as AcousticSource;

        if (src == null || !src.historyBuffer.IsCreated) return FMOD.RESULT.OK;

        unsafe
        {
            float* inData = (float*)inbuffer.ToPointer();
            float* outData = (float*)outbuffer.ToPointer();

            // Pin the jagged history arrays. 
            float* hPtr = (float*)src.historyBufferPtr;
            int size = src.historySize;

            // 2. Define the starting address for each band via pointer math
            float* h0 = hPtr;
            float* h1 = hPtr + size;
            float* h2 = hPtr + 2 * size;
            float* h3 = hPtr + 3 * size;
            float* h4 = hPtr + 4 * size;
            float* h5 = hPtr + 5 * size;
            
            // Cache state locally to prevent main-thread race conditions during the audio loop
            int writeIndex = src.state[0];
            int wrapMask = src.state[1];
            int activeIndex = src.activeBufferIndex;
            int refCount = src.activeReflectionCount;
            float directG = src.directGain;
            
            // Reference to the active reflection buffer
            var currentReflections = src.bakedBuffers[activeIndex];

            for (int i = 0; i < length; i++)
            {
                float monoInput = 0f;

                for (int c = 0; c < inchannels; c++)
                    monoInput += inData[i * inchannels + c];
                
                monoInput /= inchannels;

                // 1. Process Filters
                h0[writeIndex] = ProcessFilter(monoInput, ref src.filterStates[0], src.cachedCoefficients[0]);
                h1[writeIndex] = ProcessFilter(monoInput, ref src.filterStates[1], src.cachedCoefficients[1]);
                h2[writeIndex] = ProcessFilter(monoInput, ref src.filterStates[2], src.cachedCoefficients[2]);
                h3[writeIndex] = ProcessFilter(monoInput, ref src.filterStates[3], src.cachedCoefficients[3]);
                h4[writeIndex] = ProcessFilter(monoInput, ref src.filterStates[4], src.cachedCoefficients[4]);
                h5[writeIndex] = ProcessFilter(monoInput, ref src.filterStates[5], src.cachedCoefficients[5]);

                float reflectionAccumulator = 0f;

                for (int r = 0; r < refCount; r++)
                {
                    var refData = currentReflections[r];
    
                    // Only one index calculation needed per reflection
                    int index = (writeIndex - refData.delaySamples) & wrapMask;

                    // Direct read and multiply. No fractions. No NextIndex.
                    float filteredReflection = 
                        (h0[index] * refData.energy0) +
                        (h1[index] * refData.energy1) +
                        (h2[index] * refData.energy2) +
                        (h3[index] * refData.energy3) +
                        (h4[index] * refData.energy4) +
                        (h5[index] * refData.energy5);

                    reflectionAccumulator += (filteredReflection / 6f);
                }

                // Limiter
                float wetGain = 1.0f;
                reflectionAccumulator *= wetGain;
                reflectionAccumulator /= (1.0f + math.abs(reflectionAccumulator));

                float dry = monoInput * directG;
                float combined = dry + reflectionAccumulator;
                combined = combined / (1.0f + math.abs(combined));
                // Output to FMOD channels
                for (int c = 0; c < outchannels; c++)
                {
                    outData[i * outchannels + c] = combined; //math.clamp(combined, -1f, 1f);
                }

                writeIndex = (writeIndex + 1) & wrapMask;
            }
            
            src.state[0] = writeIndex;
        }
        return FMOD.RESULT.OK;
    }
    
    
    
    // void OnAudioFilterRead(float[] data, int channels)
    // {
    //     int writeIndex = state[0];
    //     int wrapMask = state[1];
    //     int frameCount = data.Length / channels;
    //     
    //     // data.Length is 1024 for Mono, 2048 for Stereo. 
    //     // frameCount is always the true number of time slices (e.g., 1024).
    //     for (int i = 0; i < frameCount; i++)
    //     {
    //         float monoInput = 0f;
    //
    //         for (int c = 0; c < channels; c++)
    //             monoInput += data[i * channels + c];
    //
    //         monoInput /= channels;
    //     
    //         // 1. Process the filters FIRST
    //         float band0 = ProcessFilter(monoInput, ref filterStates[0], cachedCoefficients[0]);
    //         float band1 = ProcessFilter(monoInput, ref filterStates[1], cachedCoefficients[1]);
    //         float band2 = ProcessFilter(monoInput, ref filterStates[2], cachedCoefficients[2]);
    //         float band3 = ProcessFilter(monoInput, ref filterStates[3], cachedCoefficients[3]);
    //         float band4 = ProcessFilter(monoInput, ref filterStates[4], cachedCoefficients[4]);
    //         float band5 = ProcessFilter(monoInput, ref filterStates[5], cachedCoefficients[5]);
    //
    //         historyBuffer[0][writeIndex] = band0;
    //         historyBuffer[1][writeIndex] = band1;
    //         historyBuffer[2][writeIndex] = band2;
    //         historyBuffer[3][writeIndex] = band3;
    //         historyBuffer[4][writeIndex] = band4;
    //         historyBuffer[5][writeIndex] = band5;
    //
    //         float reflectionAccumulator = 0f;
    //         for (int r = 0; r < activeReflectionCount; r++)
    //         {
    //             var refData = reflectionBuffers[activeBufferIndex][r];
    //             int baseIndex = writeIndex - refData.delaySamples;
    //             int nextIndex = baseIndex - 1;
    //
    //             baseIndex &= wrapMask;
    //             nextIndex &= wrapMask;
    //      
    //             float a0 = historyBuffer[0][baseIndex]; float b0 = historyBuffer[0][nextIndex];
    //             float del0 = a0 * (1.0f - refData.fraction) + b0 * refData.fraction;
    //
    //             float a1 = historyBuffer[1][baseIndex]; float b1 = historyBuffer[1][nextIndex];
    //             float del1 = a1 * (1.0f - refData.fraction) + b1 * refData.fraction;
    //
    //             float a2 = historyBuffer[2][baseIndex]; float b2 = historyBuffer[2][nextIndex];
    //             float del2 = a2 * (1.0f - refData.fraction) + b2 * refData.fraction;
    //
    //             float a3 = historyBuffer[3][baseIndex]; float b3 = historyBuffer[3][nextIndex];
    //             float del3 = a3 * (1.0f - refData.fraction) + b3 * refData.fraction;
    //
    //             float a4 = historyBuffer[4][baseIndex]; float b4 = historyBuffer[4][nextIndex];
    //             float del4 = a4 * (1.0f - refData.fraction) + b4 * refData.fraction;
    //
    //             float a5 = historyBuffer[5][baseIndex]; float b5 = historyBuffer[5][nextIndex];
    //             float del5 = a5 * (1.0f - refData.fraction) + b5 * refData.fraction;
    //
    //             float filteredReflection = 
    //                 (del0 * refData.energy0) +
    //                 (del1 * refData.energy1) +
    //                 (del2 * refData.energy2) +
    //                 (del3 * refData.energy3) +
    //                 (del4 * refData.energy4) +
    //                 (del5 * refData.energy5);
    //
    //             reflectionAccumulator += filteredReflection; 
    //         }
    //     
    //         // limiter for the audio to avoid hard-clipping
    //         float wetGain = 1.0f; // adjust this to taste
    //         reflectionAccumulator *= wetGain;
    //          
    //         reflectionAccumulator /= (1.0f + math.abs(reflectionAccumulator));
    //     
    //         for (int c = 0; c < channels; c++)
    //         {
    //             float dry = monoInput * directGain;
    //
    //             float combined = dry + reflectionAccumulator;
    //
    //             data[i * channels + c] = math.clamp(combined, -1f, 1f);
    //         }
    //         writeIndex = (writeIndex + 1) & wrapMask;
    //     }
    //     state[0] = writeIndex;
    // }


    // https://ccrma.stanford.edu/~jos/fp/Transposed_Direct_Forms.html
    public static float ProcessFilter(float input, ref FilterState state, FilterCoefficients coef)
    {
        float output = coef.a1 * input + state.x1;
        state.x1 = coef.a2 * input - coef.a4 * output + state.x2;
        state.x2 = coef.a3 * input - coef.a5 * output;
        return output;
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (profile == null) return;

        float activeDb = (sourceDb > 0f) ? sourceDb : profile.dbLevel;
        float relativeVolume = Mathf.InverseLerp(0f, 120f, activeDb);
        Color baseColor = Color.Lerp(Color.cyan, Color.red, relativeVolume);

        Gizmos.color = baseColor;
        Gizmos.DrawSphere(transform.position, 0.3f);

        Transform listener = null;
        if (Camera.main != null) listener = Camera.main.transform;
        if (listener == null) return;

        Vector3 start = transform.position;
        Vector3 end = listener.position;

        float distance = Vector3.Distance(start, end);

        float weightDb = 20f * Mathf.Log10(Mathf.Max(0.0001f, profile.acousticWeight));

        float perceivedDb = activeDb - 20f * Mathf.Log10(Mathf.Max(1f, distance));

        const float visualizationThresholdDb = 20f;
        Gizmos.color = perceivedDb > visualizationThresholdDb ? Color.green : Color.red;
        Gizmos.DrawLine(start, end);
        
        UnityEditor.Handles.Label(
            transform.position + Vector3.up,
            $"{activeDb:F1} dB | Weight: {profile.acousticWeight:F2}"
        );

        GUIStyle style = new() { fontSize = 12 };
        style.normal.textColor = Gizmos.color;

        Vector3 midPoint = Vector3.Lerp(start, end, 0.5f);
        UnityEditor.Handles.Label(midPoint, $"Perceived: {perceivedDb:F1} dB", style);
    }
    #endif
}

//THIS SHOULD NOT BE HERE!!!
public static class AirAbsorption
{
    // Precomputed -alpha / 20 for each band up to 4k
    private const float c_125 = -0.001f / 20f;
    private const float c_250 = -0.002f / 20f;
    private const float c_500 = -0.005f / 20f;
    private const float c_1k  = -0.010f / 20f;
    private const float c_2k  = -0.025f / 20f;
    private const float c_4k  = -0.070f / 20f;

    public static void ApplyAbsorption(ref float e0, ref float e1, ref float e2, ref float e3, ref float e4, ref float e5, float distance)
    {
        // 10^(c * distance)
        e0 *= math.pow(10f, c_125 * distance);
        e1 *= math.pow(10f, c_250 * distance);
        e2 *= math.pow(10f, c_500 * distance);
        e3 *= math.pow(10f, c_1k  * distance);
        e4 *= math.pow(10f, c_2k  * distance);
        e5 *= math.pow(10f, c_4k  * distance);
    }
}