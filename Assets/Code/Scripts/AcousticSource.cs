
using System;
using Code.Data;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;
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
    [HideInInspector] public float dynamicTailLength { get; private set; } = 0f;

    private FilterCoefficients[] cachedCoefficients;

    private AudioEngine nativeConvolver;
    private float[] monoInputBuffer;
    private float[] monoWetBuffer;
    private bool hasBakedRIR = false;
    private bool isBakingIR = false;
    public volatile bool isTailPhase = false;
    
    private float[] schroederCurve = new float[1600];
    private float previousDistance = 0f;
    private readonly object dspLock = new object();

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

    private void Update()
    {
        sourceDb = (sourceDb > 0) ? sourceDb : profile.dbLevel;

        baseAmplitude = math.pow(10f, (sourceDb - 60f) / 20f);
        baseAmplitudeWeighted = baseAmplitude * profile.acousticWeight;
        
        CalculateDistances();
    }
    
    private void Initialize()
    {
        sourceDb = (sourceDb > 0) ? sourceDb : profile.dbLevel;

        baseAmplitude = math.pow(10f, (sourceDb - 60f) / 20f);
        baseAmplitudeWeighted = baseAmplitude * profile.acousticWeight;
        
        CalculateDistances();

        var collider = gameObject.GetComponent<SphereCollider>();
        radius = collider ? collider.radius : 1f;

        nativeConvolver = new AudioEngine();
        hasBakedRIR = false;
        isBakingIR = false;
    }
    
    private void Dispose()
    {  
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
    
    static readonly ProfilerMarker processFrameMarker = new("AcousticSource.UpdateFrame");

public void UpdateFrameData(NativeSlice<AcousticData> sourceSlice, DirectAudioData directData)
{
    using (processFrameMarker.Auto())
    {
        if (isBakingIR || nativeConvolver == null) return;

        var audioManager = FindFirstObjectByType<AudioManager>();
        if (!audioManager) return;

        isBakingIR = true;

        float distance = Vector3.Distance(transform.position, audioManager.listener.transform.position);
        float distanceGain = 1.0f / math.max(distance, 1.0f);

        directGain = distanceGain * directData.transmissionMultiplier * profile.acousticWeight;
        audioSource.volume = math.clamp(this.directGain, 0f, 1f);

        float radialVelocity = (distance - previousDistance) / Time.deltaTime;
        previousDistance = distance;

        float pitchShift = 343.0f / (343.0f + (radialVelocity * 0.5f));
        audioSource.pitch = math.clamp(pitchShift, 0.5f, 2.0f);

        int totalBins = sourceSlice.Length;
        int dynamicEarlyBins = CalculateDynamicMixingTime(sourceSlice);
        int earlyBinCount = math.min(dynamicEarlyBins, totalBins);

        AcousticData[] earlyEnergies = new AcousticData[earlyBinCount];
        float rayNormalization = 1.0f / audioManager.initialRays;

        int firstBin = -1;
        for (int i = 0; i < earlyBinCount; i++)
        {
            if (sourceSlice[i].energy0 == 0 && sourceSlice[i].energy1 == 0 && sourceSlice[i].energy2 == 0) continue;
            if (firstBin == -1)
            {
                firstBin = i;
            }

            int shiftedIndex = i - firstBin;

            if (shiftedIndex <= 2) continue;

            float pE0 = sourceSlice[i].energy0 * rayNormalization;
            float pE1 = sourceSlice[i].energy1 * rayNormalization;
            float pE2 = sourceSlice[i].energy2 * rayNormalization;
            float pE3 = sourceSlice[i].energy3 * rayNormalization;
            float pE4 = sourceSlice[i].energy4 * rayNormalization;
            float pE5 = sourceSlice[i].energy5 * rayNormalization;

            float binDistance = (i * 0.0025f) * 343.0f;
            AirAbsorption.ApplyAbsorption(ref pE0, ref pE1, ref pE2, ref pE3, ref pE4, ref pE5, binDistance);
            
            earlyEnergies[shiftedIndex] = new AcousticData
            {
                energy0 = pE0, energy1 = pE1, energy2 = pE2,
                energy3 = pE3, energy4 = pE4, energy5 = pE5
            };
        }

        var (estimatedRT60, estimatedDamping) = ExtractSchroederParameters(
            sourceSlice,
            schroederCurve,
            rayNormalization,
            earlyBinCount
        );

        double normalizedRT60 = math.clamp((estimatedRT60 - 0.1f) / 7.0f, 0.0f, 1.0f);
        double mappedRoomSize = 0.3 + (normalizedRT60 * 0.68f);

        float reverbKillSwitch = 1.0f;
        if (estimatedRT60 <= 0.15f)
        {
            reverbKillSwitch = 0.0f;
        }
        
        dynamicTailLength = estimatedRT60;
        
        if (nativeConvolver != null)
        {
            float[] bakedEarlyRIR = AudioEngine.BakeImpulseResponse(earlyEnergies, cachedCoefficients);
            float preDelayMs = (earlyBinCount - firstBin) * 2.5f;
            
            lock (dspLock)
            {
                nativeConvolver.LoadEarlyImpulseResponse(bakedEarlyRIR);
                nativeConvolver.SetLateParams(mappedRoomSize, estimatedDamping, reverbKillSwitch);
                nativeConvolver.SetPreDelay(preDelayMs);
                hasBakedRIR = true;
            }
        }

        isBakingIR = false;
    }
}

private int CalculateDynamicMixingTime(NativeSlice<AcousticData> sourceSlice)
{
    int totalBins = sourceSlice.Length;
    
    int windowSize = 20;  // 12
    int densityThreshold = 18; // 9

    int firstHitBin = -1;
    for (int i = 0; i < totalBins; i++)
    {
        if (sourceSlice[i].energy0 > 0 || sourceSlice[i].energy1 > 0)
        {
            firstHitBin = i;
            break;
        }
    }

    if (firstHitBin == -1) return 32; 

    for (int i = firstHitBin; i < totalBins - windowSize; i++)
    {
        int activeBinsInWindow = 0;
        
        for (int j = 0; j < windowSize; j++)
        {
            if (sourceSlice[i + j].energy0 > 0 || sourceSlice[i + j].energy1 > 0)
            {
                activeBinsInWindow++;
            }
        }

        if (activeBinsInWindow >= densityThreshold)
        {
            int mixingBin = i + (windowSize / 2);
            
            return math.clamp(mixingBin, 16, 160); 
        }
    }

    return 32; 
}

private (float rt60, float damping) ExtractSchroederParameters(
    NativeSlice<AcousticData> sourceSlice, 
    float[] schroederCurve, 
    float rayNormalization, 
    int earlyBinCount)
{
    int totalBins = sourceSlice.Length;
    float currentIntegral = 0f;
    float hfIntegral = 0f;
    int lastValidBin = earlyBinCount;
    bool foundTailEnd = false;
    
    for (int i = totalBins - 1; i >= earlyBinCount; i--)
    {
        float pE0 = sourceSlice[i].energy0 * rayNormalization;
        float pE1 = sourceSlice[i].energy1 * rayNormalization;
        float pE2 = sourceSlice[i].energy2 * rayNormalization;
        float pE3 = sourceSlice[i].energy3 * rayNormalization;
        float pE4 = sourceSlice[i].energy4 * rayNormalization;
        float pE5 = sourceSlice[i].energy5 * rayNormalization;

        float binDistance = (i * 0.0025f) * 343.0f;
        AirAbsorption.ApplyAbsorption(ref pE0, ref pE1, ref pE2, ref pE3, ref pE4, ref pE5, binDistance);

        float binEnergy = pE0 + pE1 + pE2 + pE3 + pE4 + pE5;
        float hfEnergy = pE4 + pE5;

        currentIntegral += binEnergy;
        hfIntegral += hfEnergy;

        schroederCurve[i] = currentIntegral;

        if (!foundTailEnd && currentIntegral > 1e-24f)
        {
            lastValidBin = i;
            foundTailEnd = true;
        }
    }

    float estimatedRT60 = 0.0f;
    float estimatedDamping = 1.0f;

    float maxEnergy = schroederCurve[earlyBinCount];
    
    if (maxEnergy > 1e-12f && lastValidBin > earlyBinCount + 10)
    {
        float maxDb = 10f * math.log10(maxEnergy);
        
        float targetStartDb = maxDb - 5f; 
        
        float targetEndDb = maxDb - 25f;  

        int tStartIdx = -1;
        int tEndIdx = -1;

        for (int i = earlyBinCount; i <= lastValidBin; i++)
        {
            float currentDb = 10f * math.log10(schroederCurve[i]);
            
            if (tStartIdx == -1 && currentDb <= targetStartDb) tStartIdx = i;
            
            if (tEndIdx == -1 && currentDb <= targetEndDb)
            {
                tEndIdx = i;
                break;
            }
        }

        if (tEndIdx == -1) tEndIdx = lastValidBin;

        if (tStartIdx != -1 && tEndIdx > tStartIdx)
        {
            float actualStartDb = 10f * math.log10(schroederCurve[tStartIdx]);
            float actualEndDb = 10f * math.log10(schroederCurve[tEndIdx]);
            
            float timeDelta = (tEndIdx - tStartIdx) * 0.0025f;

            if (timeDelta > 0.025f && actualStartDb > actualEndDb)
            {
                float slope = (actualEndDb - actualStartDb) / timeDelta;
                estimatedRT60 = -60f / slope;
            }
        }

        float hfRatio = hfIntegral / currentIntegral;
        estimatedDamping = math.clamp(1.0f - (hfRatio * 2.0f), 0.0f, 1.0f);
    }

    return (estimatedRT60, estimatedDamping);
}
    
void OnAudioFilterRead(float[] data, int channels)
{
    if (isTailPhase)
    {
        Array.Clear(data, 0, data.Length);
    }
    
    int frameCount = data.Length / channels;

    if (monoInputBuffer == null || monoInputBuffer.Length != frameCount)
    {
        monoInputBuffer = new float[frameCount];
        monoWetBuffer = new float[frameCount];
    }
    
    for (int i = 0; i < frameCount; i++)
    {
        float monoInput = 0f;
        for (int c = 0; c < channels; c++) 
        {
            monoInput += data[i * channels + c];
        }
        monoInputBuffer[i] = monoInput / channels; 
    }
    
    lock (dspLock)
    {
        if (nativeConvolver != null && hasBakedRIR)
        {
            nativeConvolver.Process(monoInputBuffer, monoWetBuffer);
        }
        else
        {
            Array.Clear(monoWetBuffer, 0, monoWetBuffer.Length);
        }
    }
    
    float wetGainControl = 1.0f; 

    for (int i = 0; i < frameCount; i++)
    {
        float wetSample = monoWetBuffer[i] * wetGainControl; 
        
        for (int c = 0; c < channels; c++)
        {
            data[i * channels + c] += wetSample; 
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