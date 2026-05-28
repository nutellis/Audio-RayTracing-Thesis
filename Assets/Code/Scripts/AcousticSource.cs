
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
        bool directSoundFound = false;
        
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

            float pE0 = ((float)sourceSlice[i].energy0 / 1000.0f) * rayNormalization;
            float pE1 = ((float)sourceSlice[i].energy1 / 1000.0f) * rayNormalization;
            float pE2 = ((float)sourceSlice[i].energy2 / 1000.0f) * rayNormalization;
            float pE3 = ((float)sourceSlice[i].energy3 / 1000.0f) * rayNormalization;
            float pE4 = ((float)sourceSlice[i].energy4 / 1000.0f) * rayNormalization;
            float pE5 = ((float)sourceSlice[i].energy5 / 1000.0f) * rayNormalization;

            float binDistance = (i * 0.0025f) * 343.0f;
            AirAbsorption.ApplyAbsorption(ref pE0, ref pE1, ref pE2, ref pE3, ref pE4, ref pE5, binDistance);
            
            if (!directSoundFound)
            {
                float totalDirectEnergy = pE0 + pE1 + pE2 + pE3 + pE4 + pE5;
    
                float totalAmplitude = math.sqrt(totalDirectEnergy) * profile.acousticWeight;
                this.directGain = math.min(totalAmplitude, 1.0f);
    
                directSoundFound = true;
            }
            
            //to amplitude
            float pA0 = math.sqrt(pE0);
            float pA1 = math.sqrt(pE1);
            float pA2 = math.sqrt(pE2);
            float pA3 = math.sqrt(pE3);
            float pA4 = math.sqrt(pE4);
            float pA5 = math.sqrt(pE5);
            

            sourceSlice[i].energy0 = (uint)(pA0 *  1000.0f);
            sourceSlice[i].energy1 = (uint)(pA1 *  1000.0f);
            sourceSlice[i].energy2 = (uint)(pA2 *  1000.0f);
            sourceSlice[i].energy3 = (uint)(pA3 *  1000.0f);
            sourceSlice[i].energy4 = (uint)(pA4 *  1000.0f);
            sourceSlice[i].energy5 = (uint)(pA5 *  1000.0f);
        }

        // Run the 6-band material response filter assembly asynchronously out-of-thread
        float[] bakedRIR = await RIRSynthesizer.BakeImpulseResponseAsync(sourceSlice, cachedCoefficients);

        if (nativeConvolver != null)
        {
            nativeConvolver.LoadImpulseResponse(bakedRIR);
            hasBakedRIR = true;
        }

        isBakingIR = false;
    }
    
    
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
        
        float wetGainControl = 1.0f; 

        for (int i = 0; i < frameCount; i++)
        {
            float wetSample = monoWetBuffer[i] * wetGainControl; 
            
            for (int c = 0; c < channels; c++)
            {
                float drySample = data[i * channels + c] * directGain;
                
                // Mix the wet room response onto every output speaker channel
                data[i * channels + c] = math.clamp(drySample + wetSample, -1.0f, 1.0f); //
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