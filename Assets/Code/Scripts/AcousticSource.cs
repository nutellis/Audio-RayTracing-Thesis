using System;
using Code.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

using UnityEngine.Serialization;

public class AcousticSource : MonoBehaviour
{
    public AudioSource audioSource;

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
    
    
    private float[] historyBuffer;
    private int[] state; // [0] writeIndex, [1] wrapMask
    
    private int cachedSampleRate;
    
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
    
    
    //getter for sortingGain
    public float GetSortingGain()
    {
        return sortingGain;
    }

    private void OnEnable()
    {
        Initialize();
    }

    private void OnDisable()
    {
        Dispose();
    }

    private void OnDestroy()
    {
        Dispose();
    }

    private void Initialize()
    {
        cachedSampleRate = AudioSettings.outputSampleRate;
        sourceDb = (sourceDb > 0) ? sourceDb : profile.dbLevel;

        // Calculate once and store
        baseAmplitude = math.pow(10f, (sourceDb - 60f) / 20f);
        baseAmplitudeWeighted = baseAmplitude * profile.acousticWeight;
        
        CalculateDistances();

        var collider = gameObject.GetComponent<SphereCollider>();
        radius = collider ? collider.radius : 1f;

        int historySize = 131072; // 2 seconds at 44.8khz
        historyBuffer = new float[historySize];
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
            historyBuffer = null;
            reflectionBuffers[0] = null;
            reflectionBuffers[1] = null;
            filterStates = null;
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
        this.enabled = true;
        this.audioSource.enabled = true;
       
        timeOfEmission = Time.time;
        AudioManager manager =  FindAnyObjectByType(typeof(AudioManager)) as AudioManager;
        if (manager != null)
        {
            manager.RegisterAudio(this);
            cachedCoefficients = manager.GetFilterCoefficients();
        }
    }
    
    public void UnRegisterSound()
    {
        audioSource.Stop();
        activeReflectionCount = 0;
        this.audioSource.enabled = false;
        this.enabled = false;
    }
    
    public void UpdateReflections(PathData[] sourcePaths)
    {
        int writeIndex = (activeBufferIndex + 1) % 2;
        int validReflections = 0;

        float rayEnergyScalar = (1.0f / 2048); //* 5000;
        
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
                
                directGainAccumulator = ((dE0 + dE1 + dE2 + dE3 + dE4 + dE5) / 6f);
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
            
            int delaySamples = (int)((deltaDistance / 343.0f) * cachedSampleRate);
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
                    delaySamples = delaySamples,
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
    
    void OnAudioFilterRead(float[] data, int channels)
    {
        int writeIndex = state[0];
        int wrapMask = state[1];
        int frameCount = data.Length / channels;
        
        // data.Length is 1024 for Mono, 2048 for Stereo. 
        // frameCount is always the true number of time slices (e.g., 1024).
        for (int i = 0; i < frameCount; i++)
        {
            float monoInput = 0f;
            for (int c = 0; c < channels; c++) {
             monoInput += data[i * channels + c];
            }
            monoInput /= channels;
        
            historyBuffer[writeIndex] = monoInput;
        
            float reflectionAccumulator = 0f;
            for (int r = 0; r < activeReflectionCount; r++)
            {
             var refData = reflectionBuffers[activeBufferIndex][r];
             int readIndex = (writeIndex - refData.delaySamples + wrapMask + 1) & wrapMask;
             float rawSample = historyBuffer[readIndex];
        
             float filteredSample = 0f;
             
             // // Process the 6 bands
            for (int b = 0; b < 6; b++)
            {
                 int stateIndex = (r * 6) + b;
                 FilterState fState = filterStates[stateIndex];
                 
                 float output = ProcessFilter(rawSample, ref fState, cachedCoefficients[b]);
        
                 filterStates[stateIndex] = fState;
        
                 float bandEnergy = refData.GetEnergy(b);
                 filteredSample += output * bandEnergy;
            }
        
             reflectionAccumulator += (filteredSample * 0.166667f); 
            }
        
            // limiter for the audio to avoid hard-clipping
            float wetGain = 1.0f; // adjust this to taste
            reflectionAccumulator *= wetGain;
             
            // reflectionAccumulator /= (1.0f + math.abs(reflectionAccumulator));
        
            for (int c = 0; c < channels; c++)
            {
                float dry = data[i * channels + c] * directGain;
        
                float combined = reflectionAccumulator; // +
        
                data[i * channels + c] = math.clamp(combined, -1.0f, 1.0f);
            }
            writeIndex = (writeIndex + 1) & wrapMask;
        }
        state[0] = writeIndex;
    }


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