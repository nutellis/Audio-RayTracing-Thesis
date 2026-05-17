// using System;
// using Code.Data;
// using Unity.Burst;
// using Unity.Collections;
// using Unity.Jobs;
// using Unity.Mathematics;
// using UnityEngine;
//
// using UnityEngine.Serialization;
//
// public class AcousticSource : MonoBehaviour
// {
//     public AudioSource audioSource;
//
//     [Tooltip("Faint = 0-40 average: ~20 \r\nNormal = 41-75 average: ~58\r\nLoud = 76-100 average: ~88  \r\nExtreme = 101-120 average: ~110")]
//     public AcousticProfile profile;
//     [FormerlySerializedAs("manualDb")] [Tooltip("Can be used to fine-tune the profile.\nCaution if the values are far from the profile you are effectively overriding the profile\nDefault is 0")]
//     public float sourceDb = 0f;
//
//     readonly float sortingGain;
//
//     private float baseAmplitude;
//     private float attenuation;
//     public float baseAmplitudeWeighted;
//
//     
//     
//     public float timeOfEmission = 0;
//
//     public float radius;
//     
//     
//     private float[] historyBuffer;
//     private int[] state; // [0] writeIndex, [1] wrapMask
//     
//     private int cachedSampleRate;
//     
//     public float frameGain;
//     public float frameDistance;
//     
//     public float maxAudibleDistance;
//     public float minAudibleDistance;
//     
//     public float directGain = 0;
//     // echo and reflections 
//     private const int MAX_REFLECTIONS_PER_SOURCE = 64; 
//     private Reflection[][] reflectionBuffers;
//     private volatile int activeBufferIndex = 0;
//     private int activeReflectionCount = 0;
//
//
//     // filter related
//     private FilterState[] filterStates;
//     private FilterCoefficients[] cachedCoefficients;
//     
//     
//     //getter for sortingGain
//     public float GetSortingGain()
//     {
//         return sortingGain;
//     }
//
//     private void OnEnable()
//     {
//         Initialize();
//     }
//
//     private void OnDisable()
//     {
//         Dispose();
//     }
//
//     private void OnDestroy()
//     {
//         Dispose();
//     }
//
//     private void Initialize()
//     {
//         cachedSampleRate = AudioSettings.outputSampleRate;
//         sourceDb = (sourceDb > 0) ? sourceDb : profile.dbLevel;
//
//         // Calculate once and store
//         baseAmplitude = math.pow(10f, (sourceDb - 60f) / 20f);
//         baseAmplitudeWeighted = baseAmplitude * profile.acousticWeight;
//         
//         CalculateDistances();
//
//         var collider = gameObject.GetComponent<SphereCollider>();
//         radius = collider ? collider.radius : 1f;
//
//         int historySize = 131072; // 2 seconds at 44.8khz
//         historyBuffer = new float[historySize];
//         state = new int [2];
//         state[0] = 0; // writeIndex
//         state[1] = historySize - 1; // wrapMask
//         
//         reflectionBuffers = new Reflection[2][];
//         reflectionBuffers[0] = new Reflection[MAX_REFLECTIONS_PER_SOURCE];
//         reflectionBuffers[1] = new Reflection[MAX_REFLECTIONS_PER_SOURCE];
//
//         //filter related
//         
//         // 64 reflections x 6 bands = 384 filter states
//         filterStates = new FilterState[384];
//     }
//     
//     private void Dispose()
//     {  
//             historyBuffer = null;
//             reflectionBuffers[0] = null;
//             reflectionBuffers[1] = null;
//             filterStates = null;
//     }
//     
//
//     public float GetScore(Vector3 listenerPos)
//     {
//         float d2 = (transform.position - listenerPos).sqrMagnitude;
//         return baseAmplitudeWeighted / Mathf.Max(1f, d2);
//     }
//     
//     private float DistanceAttenuation(float distance, float referenceDistance, float falloffFactor)
//     {
//         distance = Mathf.Max(distance, referenceDistance);
//         float gain = referenceDistance / (referenceDistance + falloffFactor * (distance - referenceDistance));
//         return gain;
//     }
//     
//     
//     private void CalculateDistances()
//     {
//         if (sourceDb <= 20f) 
//         {
//             minAudibleDistance = 0.1f;
//             maxAudibleDistance = 0.1f;
//             return;
//         }
//         
//         float dbDifferenceFromBaseline = sourceDb - 60f;
//     
//         minAudibleDistance = 1.0f * Mathf.Pow(10f, dbDifferenceFromBaseline / 20f);
//
//         float dbDropNeeded = sourceDb - 20f;
//         maxAudibleDistance = minAudibleDistance * Mathf.Pow(10f, dbDropNeeded / 20f);
//     }
//     
//
//     public void RegisterSound()
//     {
//         this.enabled = true;
//         this.audioSource.enabled = true;
//        
//         timeOfEmission = Time.time;
//         AudioManager manager =  FindAnyObjectByType(typeof(AudioManager)) as AudioManager;
//         if (manager != null)
//         {
//             manager.RegisterAudio(this);
//             cachedCoefficients = manager.GetFilterCoefficients();
//         }
//     }
//     
//     public void UnRegisterSound()
//     {
//         audioSource.Stop();
//         activeReflectionCount = 0;
//         this.audioSource.enabled = false;
//         this.enabled = false;
//     }
//     
//     public void UpdateReflections(PathData[] sourcePaths)
//     {
//         int writeIndex = (activeBufferIndex + 1) % 2;
//         int validReflections = 0;
//
//         float rayEnergyScalar = 1.0f; //(1.0f / 2048); //* 5000;
//         
//         // Consider moving these to Start() or class level variables (Haas Effect)
//         int mergeThresholdSamples = (int)(0.0025f * cachedSampleRate); 
//
//         float directGainAccumulator = 0f;
//         
//         for (int i = 0; i < sourcePaths.Length; i++)
//         {
//             var path = sourcePaths[i];
//             
//             if (path.state == 0) 
//             {
//                 float dE0 = path.energy0, dE1 = path.energy1, dE2 = path.energy2;
//                 float dE3 = path.energy3, dE4 = path.energy4, dE5 = path.energy5;
//
//                 AirAbsorption.ApplyAbsorption(ref dE0, ref dE1, ref dE2, ref dE3, ref dE4, ref dE5, path.distance);
//                 
//                 directGainAccumulator = ((dE0 + dE1 + dE2 + dE3 + dE4 + dE5) / 6f);
//                 continue; 
//             }
//             
//             
//             float deltaDistance = path.distance - frameDistance;
//             
//             // Skip direct sound
//             if (deltaDistance < 0.05f) continue; 
//             
//             //air absorption
//             float pE0 = path.energy0;
//             float pE1 = path.energy1;
//             float pE2 = path.energy2;
//             float pE3 = path.energy3;
//             float pE4 = path.energy4;
//             float pE5 = path.energy5;
//
//             AirAbsorption.ApplyAbsorption(ref pE0, ref pE1, ref pE2, ref pE3, ref pE4, ref pE5, path.distance);
//             
//             int delaySamples = (int)((deltaDistance / 343.0f) * cachedSampleRate);
//             bool merged = false;
//             
//             // Check for temporal overlap (binning of 2.5ms) - Haas effect
//             for (int r = 0; r < validReflections; r++)
//             {
//                 var existingRef = reflectionBuffers[writeIndex][r];
//                 
//                 if (Mathf.Abs(existingRef.delaySamples - delaySamples) < mergeThresholdSamples)
//                 {
//                     existingRef.energy0 += pE0 * rayEnergyScalar;
//                     existingRef.energy1 += pE1 * rayEnergyScalar;
//                     existingRef.energy2 += pE2 * rayEnergyScalar;
//                     existingRef.energy3 += pE3 * rayEnergyScalar;
//                     existingRef.energy4 += pE4 * rayEnergyScalar;
//                     existingRef.energy5 += pE5 * rayEnergyScalar;
//                     
//                     reflectionBuffers[writeIndex][r] = existingRef;
//                     merged = true;
//                     break;
//                 }
//             }
//
//             // Initialize new echo if no overlap
//             if (!merged && validReflections < MAX_REFLECTIONS_PER_SOURCE)
//             {
//                 reflectionBuffers[writeIndex][validReflections] = new Reflection()
//                 {
//                     delaySamples = delaySamples,
//                     energy0 = pE0 * rayEnergyScalar,
//                     energy1 = pE1 * rayEnergyScalar,
//                     energy2 = pE2 * rayEnergyScalar,
//                     energy3 = pE3 * rayEnergyScalar,
//                     energy4 = pE4 * rayEnergyScalar,
//                     energy5 = pE5 * rayEnergyScalar,
//                     arrivalAngles = path.arrivalAngles
//                 };
//                 validReflections++;
//             }
//         }
//     
//         if (validReflections > 0)
//         {
//             Array.Sort(reflectionBuffers[writeIndex], 0, validReflections);
//         }
//         
//         // clamp accumulated volumes to 1.0.
//         for(int r = 0; r < validReflections; r++)
//         {
//             var reflection = reflectionBuffers[writeIndex][r];
//             reflection.energy0 = math.min(reflection.energy0, 1.0f);
//             reflection.energy1 = math.min(reflection.energy1, 1.0f);
//             reflection.energy2 = math.min(reflection.energy2, 1.0f);
//             reflection.energy3 = math.min(reflection.energy3, 1.0f);
//             reflection.energy4 = math.min(reflection.energy4, 1.0f);
//             reflection.energy5 = math.min(reflection.energy5, 1.0f);
//             
//             reflectionBuffers[writeIndex][r] = reflection;
//         }
//         
//         this.directGain = math.min(directGainAccumulator, 1.0f);
//         activeReflectionCount = validReflections;
//         activeBufferIndex = writeIndex; 
//     }
//     
//     /*
//      *public void UpdateReflections(AudioManager.MacroBin[] sourceSlice)
// {
//     int writeIndex = (activeBufferIndex + 1) % 2;
//     int validReflections = 0;
//
//     float directGainAccumulator = 0f;
//
//     for (int i = 0; i < sourceSlice.Length; i++)
//     {
//         var bin = sourceSlice[i];
//         
//         // Skip empty bins (this saves the CPU from doing unnecessary math)
//         if (bin.energy0 == 0 && bin.energy1 == 0 && bin.energy2 == 0) continue;
//
//         // Convert the fixed-point GPU integers back into floats
//         float pE0 = (float)bin.energy0 / 100000.0f;
//         float pE1 = (float)bin.energy1 / 100000.0f;
//         float pE2 = (float)bin.energy2 / 100000.0f;
//         float pE3 = (float)bin.energy3 / 100000.0f;
//         float pE4 = (float)bin.energy4 / 100000.0f;
//         float pE5 = (float)bin.energy5 / 100000.0f;
//
//         // Calculate the physical distance of this time bin to apply air absorption
//         float binDistance = (i * 0.0025f) * 343.0f;
//         AirAbsorption.ApplyAbsorption(ref pE0, ref pE1, ref pE2, ref pE3, ref pE4, ref pE5, binDistance);
//
//         // If this is the very first bin (0 to 2.5ms), treat it as the Direct Sound
//         if (i == 0)
//         {
//             directGainAccumulator = (pE0 + pE1 + pE2 + pE3 + pE4 + pE5) / 6f;
//             // Depending on your setup, you might want to 'continue;' here so direct sound 
//             // isn't also processed as a reflection.
//         }
//
//         // Write directly to your existing reflection buffer structure
//         // (Assuming MAX_REFLECTIONS_PER_SOURCE is defined in your class, e.g., 64)
//         if (validReflections < MAX_REFLECTIONS_PER_SOURCE) 
//         {
//             reflectionBuffers[writeIndex][validReflections] = new Reflection()
//             {
//                 delaySamples = (int)(i * 0.0025f * cachedSampleRate), // i is the binIndex
//                 energy0 = math.min(pE0, 1.0f),
//                 energy1 = math.min(pE1, 1.0f),
//                 energy2 = math.min(pE2, 1.0f),
//                 energy3 = math.min(pE3, 1.0f),
//                 energy4 = math.min(pE4, 1.0f),
//                 energy5 = math.min(pE5, 1.0f)
//             };
//             validReflections++;
//         }
//     }
//
//     // Apply the final values to the AcousticSource state
//     this.directGain = math.min(directGainAccumulator, 1.0f);
//     activeReflectionCount = validReflections;
//     activeBufferIndex = writeIndex; 
// }
//      * 
//      */
//     public void UpdateReflections1(MacroBin[] sourceEchogram)
//     {
//         int writeIndex = (activeBufferIndex + 1) % 2;
//         int validReflections = 0;
//
//         float directGainAccumulator = 0f;
//
//         for (int i = 0; i < sourceEchogram.Length; i++)
//         {
//             var bin = sourceEchogram[i];
//         
//             // Skip empty bins (saves CPU time)
//             if (bin.energy0 == 0 && bin.energy1 == 0 && bin.energy2 == 0) continue;
//
//             // Convert the integers back to floats
//             float pE0 = (float)bin.energy0 / 100000.0f;
//             float pE1 = (float)bin.energy1 / 100000.0f;
//             float pE2 = (float)bin.energy2 / 100000.0f;
//             float pE3 = (float)bin.energy3 / 100000.0f;
//             float pE4 = (float)bin.energy4 / 100000.0f;
//             float pE5 = (float)bin.energy5 / 100000.0f;
//
//             // Calculate distance based on the bin index to apply air absorption
//             float binDistance = (i * 0.0025f) * 343.0f;
//             AirAbsorption.ApplyAbsorption(ref pE0, ref pE1, ref pE2, ref pE3, ref pE4, ref pE5, binDistance);
//
//             // If this is the very first bin (0-2.5ms), treat it as direct sound
//             if (i == 0)
//             {
//                 directGainAccumulator = (pE0 + pE1 + pE2 + pE3 + pE4 + pE5) / 6f;
//             }
//
//             // Write directly to your reflection buffer
//             if (validReflections < MAX_REFLECTIONS_PER_SOURCE)
//             {
//                 reflectionBuffers[writeIndex][validReflections] = new Reflection()
//                 {
//                     delaySamples = (int)(i * 0.0025f * cachedSampleRate),
//                     energy0 = math.min(pE0, 1.0f),
//                     energy1 = math.min(pE1, 1.0f),
//                     energy2 = math.min(pE2, 1.0f),
//                     energy3 = math.min(pE3, 1.0f),
//                     energy4 = math.min(pE4, 1.0f),
//                     energy5 = math.min(pE5, 1.0f)
//                 };
//                 validReflections++;
//             }
//         }
//
//         this.directGain = math.min(directGainAccumulator, 1.0f);
//         activeReflectionCount = validReflections;
//         activeBufferIndex = writeIndex; 
//     }
//     
//     void OnAudioFilterRead(float[] data, int channels)
//     {
//         // 1. Pass the dry audio buffer to the C++ Convolution Engine
//         if (nativeConvolver != null && hasBakedRIR)
//         {
//             nativeConvolver.Process(data, wetReverbBuffer);
//         }
//
//         // 2. Mix dry and wet together
//         for (int i = 0; i < data.Length; i++)
//         {
//             data[i] = (data[i] * directGain) + wetReverbBuffer[i];
//         }
//     }
//     
//     // void OnAudioFilterRead(float[] data, int channels)
//     // {
//     //     int writeIndex = state[0];
//     //     int wrapMask = state[1];
//     //     int frameCount = data.Length / channels;
//     //     
//     //     // data.Length is 1024 for Mono, 2048 for Stereo. 
//     //     // frameCount is always the true number of time slices (e.g., 1024).
//     //     for (int i = 0; i < frameCount; i++)
//     //     {
//     //         float monoInput = 0f;
//     //         for (int c = 0; c < channels; c++) {
//     //          monoInput += data[i * channels + c];
//     //         }
//     //         monoInput /= channels;
//     //     
//     //         historyBuffer[writeIndex] = monoInput;
//     //     
//     //         float reflectionAccumulator = 0f;
//     //         for (int r = 0; r < activeReflectionCount; r++)
//     //         {
//     //          var refData = reflectionBuffers[activeBufferIndex][r];
//     //          int readIndex = (writeIndex - refData.delaySamples + wrapMask + 1) & wrapMask;
//     //          float rawSample = historyBuffer[readIndex];
//     //     
//     //          float filteredSample = 0f;
//     //          
//     //          // // Process the 6 bands
//     //         for (int b = 0; b < 6; b++)
//     //         {
//     //              int stateIndex = (r * 6) + b;
//     //              FilterState fState = filterStates[stateIndex];
//     //              
//     //              float output = ProcessFilter(rawSample, ref fState, cachedCoefficients[b]);
//     //     
//     //              filterStates[stateIndex] = fState;
//     //     
//     //              float bandEnergy = refData.GetEnergy(b);
//     //              filteredSample += output * bandEnergy;
//     //         }
//     //     
//     //          reflectionAccumulator += (filteredSample * 0.166667f); 
//     //         }
//     //     
//     //         // limiter for the audio to avoid hard-clipping
//     //         float wetGain = 1.0f; // adjust this to taste
//     //         reflectionAccumulator *= wetGain;
//     //          
//     //         reflectionAccumulator /= (1.0f + math.abs(reflectionAccumulator));
//     //     
//     //         for (int c = 0; c < channels; c++)
//     //         {
//     //             float dry = data[i * channels + c] * directGain;
//     //     
//     //             float combined = dry + reflectionAccumulator; // +
//     //             if (combined > 1.0 || combined < -1.0)
//     //             {
//     //                 data[i * channels + c] = 0;
//     //             }
//     //             else
//     //             {
//     //                 data[i * channels + c] = math.clamp(combined, -1.0f, 1.0f);
//     //             }
//     //         }
//     //         writeIndex = (writeIndex + 1) & wrapMask;
//     //     }
//     //     state[0] = writeIndex;
//     // }
//
//
//     // https://ccrma.stanford.edu/~jos/fp/Transposed_Direct_Forms.html
//     public static float ProcessFilter(float input, ref FilterState state, FilterCoefficients coef)
//     {
//         float output = coef.a1 * input + state.x1;
//         state.x1 = coef.a2 * input - coef.a4 * output + state.x2;
//         state.x2 = coef.a3 * input - coef.a5 * output;
//         return output;
//     }
//
// #if UNITY_EDITOR
//     private void OnDrawGizmos()
//     {
//         if (profile == null) return;
//
//         float activeDb = (sourceDb > 0f) ? sourceDb : profile.dbLevel;
//         float relativeVolume = Mathf.InverseLerp(0f, 120f, activeDb);
//         Color baseColor = Color.Lerp(Color.cyan, Color.red, relativeVolume);
//
//         Gizmos.color = baseColor;
//         Gizmos.DrawSphere(transform.position, 0.3f);
//
//         Transform listener = null;
//         if (Camera.main != null) listener = Camera.main.transform;
//         if (listener == null) return;
//
//         Vector3 start = transform.position;
//         Vector3 end = listener.position;
//
//         float distance = Vector3.Distance(start, end);
//
//         float weightDb = 20f * Mathf.Log10(Mathf.Max(0.0001f, profile.acousticWeight));
//
//         float perceivedDb = activeDb - 20f * Mathf.Log10(Mathf.Max(1f, distance));
//
//         const float visualizationThresholdDb = 20f;
//         Gizmos.color = perceivedDb > visualizationThresholdDb ? Color.green : Color.red;
//         Gizmos.DrawLine(start, end);
//         
//         UnityEditor.Handles.Label(
//             transform.position + Vector3.up,
//             $"{activeDb:F1} dB | Weight: {profile.acousticWeight:F2}"
//         );
//
//         GUIStyle style = new() { fontSize = 12 };
//         style.normal.textColor = Gizmos.color;
//
//         Vector3 midPoint = Vector3.Lerp(start, end, 0.5f);
//         UnityEditor.Handles.Label(midPoint, $"Perceived: {perceivedDb:F1} dB", style);
//     }
//     #endif
// }
//
using System;
using Code.Data;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

public class AcousticSource : MonoBehaviour
{
    public AudioSource audioSource;

    [Tooltip("Faint = 0-40 average: ~20 \r\nNormal = 41-75 average: ~58\r\nLoud = 76-100 average: ~88  \r\nExtreme = 101-120 average: ~110")]
    public AcousticProfile profile;
    
    [FormerlySerializedAs("manualDb")] 
    [Tooltip("Can be used to fine-tune the profile.\nCaution if the values are far from the profile you are effectively overriding the profile\nDefault is 0")]
    public float sourceDb = 0f;

    private float baseAmplitude;
    public float baseAmplitudeWeighted;
    
    [HideInInspector] public float timeOfEmission = 0;
    [HideInInspector] public float radius;
    [HideInInspector] public float frameDistance;
    [HideInInspector] public float maxAudibleDistance;
    [HideInInspector] public float minAudibleDistance;
    [HideInInspector] public float directGain = 0;

    private FilterCoefficients[] cachedCoefficients;

    // --- NEW NATIVE CONVOLUTION DATA STRUCTS ---
    private NativeConvolver nativeConvolver;
    private float[] monoInputBuffer;
    private float[] monoWetBuffer;
    private bool hasBakedRIR = false;
    private bool isBakingIR = false;

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
        sourceDb = (sourceDb > 0) ? sourceDb : profile.dbLevel;

        baseAmplitude = math.pow(10f, (sourceDb - 60f) / 20f);
        baseAmplitudeWeighted = baseAmplitude * profile.acousticWeight;
        
        CalculateDistances();

        var collider = gameObject.GetComponent<SphereCollider>();
        radius = collider ? collider.radius : 1f;

        // Initialize our fast unmanaged C++ plugin engine instance
        nativeConvolver = new NativeConvolver();
        hasBakedRIR = false;
        isBakingIR = false;
    }
    
    private void Dispose()
    {  
        // Clean up the native unmanaged plugin block to avoid severe memory leaks
        if (nativeConvolver != null)
        {
            nativeConvolver.Dispose();
            nativeConvolver = null;
        }
        hasBakedRIR = false;
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
        AudioManager manager = FindAnyObjectByType(typeof(AudioManager)) as AudioManager;
        if (manager != null)
        {
            manager.RegisterAudio(this);
            cachedCoefficients = manager.GetFilterCoefficients();
        }
    }
    
    public void UnRegisterSound()
    {
        audioSource.Stop();
        this.audioSource.enabled = false;
        this.enabled = false;
    }
    
    // Receives the GPU pre-sorted 2.5ms slice from AudioManager
    public async void UpdateReflections(MacroBin[] sourceSlice)
    {
        // Drop the update frame if the background thread is still busy cooking the last one
        if (isBakingIR || nativeConvolver == null) return;

        // 1. Fetch the total ray count from the AudioManager for normalization
        var audioManager = FindFirstObjectByType<AudioManager>();
        if (!audioManager) return;
    
        int initialRays = audioManager.initialRays;
        // Each ray carries a 1/N fraction of the total acoustic energy
        float rayNormalization = 1.0f / initialRays; 

        isBakingIR = true;

        // Apply physical air absorption attenuation directly across our 800 buckets
        for (int i = 0; i < sourceSlice.Length; i++)
        {
            if (sourceSlice[i].energy0 == 0 && sourceSlice[i].energy1 == 0 && sourceSlice[i].energy2 == 0) continue;

            float pE0 = ((float)sourceSlice[i].energy0 / 100000.0f) * rayNormalization;
            float pE1 = ((float)sourceSlice[i].energy1 / 100000.0f) * rayNormalization;
            float pE2 = ((float)sourceSlice[i].energy2 / 100000.0f) * rayNormalization;
            float pE3 = ((float)sourceSlice[i].energy3 / 100000.0f) * rayNormalization;
            float pE4 = ((float)sourceSlice[i].energy4 / 100000.0f) * rayNormalization;
            float pE5 = ((float)sourceSlice[i].energy5 / 100000.0f) * rayNormalization;

            float binDistance = (i * 0.0025f) * 343.0f;
            AirAbsorption.ApplyAbsorption(ref pE0, ref pE1, ref pE2, ref pE3, ref pE4, ref pE5, binDistance);

            // Assign the direct sound parameter via our first bucket (0-2.5ms arrival)
            if (i == 0)
            {
                this.directGain = math.min((pE0 + pE1 + pE2 + pE3 + pE4 + pE5) / 6f, 1.0f);
            }

            sourceSlice[i].energy0 = (uint)(pE0 * 100000.0f);
            sourceSlice[i].energy1 = (uint)(pE1 * 100000.0f);
            sourceSlice[i].energy2 = (uint)(pE2 * 100000.0f);
            sourceSlice[i].energy3 = (uint)(pE3 * 100000.0f);
            sourceSlice[i].energy4 = (uint)(pE4 * 100000.0f);
            sourceSlice[i].energy5 = (uint)(pE5 * 100000.0f);
        }

        // Run the 6-band material response filter assembly asynchronously out-of-thread
        float[] bakedRIR = await RIRSynthesizer.BakeImpulseResponseAsync(sourceSlice, cachedCoefficients);

        // Upload the processed acoustic fingerprint straight to unmanaged C++ execution memory
        if (nativeConvolver != null)
        {
            nativeConvolver.LoadImpulseResponse(bakedRIR);
            hasBakedRIR = true;
        }

        isBakingIR = false;
    }
    
    // --- O(1) ULTRA HIGH PERFORMANCE AUDIO THREAD DSP ---
    void OnAudioFilterRead(float[] data, int channels)
    {
        int frameCount = data.Length / channels;

        // Ensure our processing array match Unity's runtime chunk configurations
        if (monoInputBuffer == null || monoInputBuffer.Length != frameCount)
        {
            monoInputBuffer = new float[frameCount];
            monoWetBuffer = new float[frameCount];
        }
        
        // 1. Accumulate multi-channel output data into a mono mixdown array for the convolver
        for (int i = 0; i < frameCount; i++)
        {
            float monoInput = 0f;
            for (int c = 0; c < channels; c++) 
            {
                monoInput += data[i * channels + c];
            }
            monoInputBuffer[i] = monoInput / channels;
        }
        
        // 2. Perform the fast frequency-domain multiplication in C++ via WDL
        if (nativeConvolver != null && hasBakedRIR)
        {
            nativeConvolver.Process(monoInputBuffer, monoWetBuffer);
        }
        else
        {
            Array.Clear(monoWetBuffer, 0, monoWetBuffer.Length);
        }
        
        // 3. Inject our wet room acoustics wash back into the output stream
        float wetGainControl = 1.0f; // <-- Add this variable here to tweak your reverb volume!

        for (int i = 0; i < frameCount; i++)
        {
            // Replace the old hardcoded 0.4f with your new control variable
            float wetSample = monoWetBuffer[i] * wetGainControl; 
            
            for (int c = 0; c < channels; c++)
            {
                float drySample = data[i * channels + c] * directGain;
                
                // Mix the wet room response onto every output speaker channel
                data[i * channels + c] = math.clamp(drySample + wetSample, -1.0f, 1.0f);
            }
        }
    }
}
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