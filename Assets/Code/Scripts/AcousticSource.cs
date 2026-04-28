using System;
using UnityEngine;
using UnityEngine.Serialization;

public class AcousticSource : MonoBehaviour
{
    public AudioClip audioClip;
    public AudioSource audioSource;

    [Tooltip("Faint = 0-40 average: ~20 \r\nNormal = 41-75 average: ~58\r\nLoud = 76-100 average: ~88  \r\nExtreme = 101-120 average: ~110")]
    public AcousticProfile profile;
    [Tooltip("Can be used to fine-tune the profile.\nCaution if the values are far from the profile you are effectively overriding the profile\nDefault is 0")]
    public float manualDb = 0f;
    
    float sortingGain;

    private float baseAmplitude;
    private float finalGain;
    private float baseAmplitudeWeighted;

    public float volume = 0;
    
    public bool isDelayed = false;
    public float delayTime = 0;
    public float timeOfEmission = 0;

    //getter for sortingGain
    public float GetSortingGain()
    {
        return sortingGain;
    }

    // protected override void Awake()
    // {
    //     base.Awake();
    // }

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        float finalDb = (manualDb > 0) ? manualDb : profile.dbLevel;

        // Calculate once and store
        baseAmplitude = Mathf.Pow(10f, (finalDb - 60f) / 20f);
        baseAmplitudeWeighted = baseAmplitude * profile.acousticWeight;
        
        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.clip = audioClip;
        audioSource.volume = volume;
        audioSource.playOnAwake = false;
        
    }

    public float GetScore(Vector3 listenerPos)
    {
        float d2 = (transform.position - listenerPos).sqrMagnitude;
        return baseAmplitudeWeighted / Mathf.Max(1f, d2);
    }

    public void Update()
    {
        if (!audioSource) return;
        audioSource.volume = finalGain;
    }

    private float DistanceAttenuation(float distance, float referenceDistance, float falloffFactor)
    {
        distance = Mathf.Max(distance, referenceDistance);
        float gain = referenceDistance / (referenceDistance + falloffFactor * (distance - referenceDistance));
        return gain;
    }

    public void CalculateFinalGain(float distance)
    {
        finalGain = baseAmplitude * 1f / (1f + distance);// * DistanceAttenuation(distance, 1f, 1f);
        volume = finalGain;
    }

    public void RegisterSound()
    {
        timeOfEmission = Time.time;
        AudioManager manager =  FindAnyObjectByType(typeof(AudioManager)) as AudioManager;
        if (manager != null)
        {
            manager.RegisterAudio(this);
        }
    }


#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (profile == null) return;

        float activeDb = (manualDb > 0f) ? manualDb : profile.dbLevel;
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

        GUIStyle style = new GUIStyle { fontSize = 12 };
        style.normal.textColor = Gizmos.color;

        Vector3 midPoint = Vector3.Lerp(start, end, 0.5f);
        UnityEditor.Handles.Label(midPoint, $"Perceived: {perceivedDb:F1} dB", style);
    }
}
#endif

