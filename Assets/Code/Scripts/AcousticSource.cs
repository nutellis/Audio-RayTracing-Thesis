using UnityEngine;

public class AcousticSource : MonoBehaviour
{
    public AudioClip audioClip;
    public AudioSource audioSource;

    [Tooltip("Faint = 0-40 average: ~20 \r\nNormal = 41-75 average: ~58\r\nLoud = 76-100 average: ~88  \r\nExtreme = 101-120 average: ~110")]
    public AcousticProfile profile;
    [Tooltip("Can be used to finetune the profile.\nCaution if the values are far from the profile you are effectively overriding the profile\nDefault is 0")]
    public float manualDb = 0f;
    private float cachedNumerator;

    float sortingGain;

    public float energy;

    //getter for sortingGain
    public float GetSortingGain()
    {
        return sortingGain;
    }



    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        float finalDb = (manualDb > 0) ? manualDb : profile.dbLevel;

        // Calculate once and store
        float gain = Mathf.Pow(10f, (finalDb - 120f) / 20f);
        cachedNumerator = gain * profile.acousticWeight;
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    public float GetScore(Vector3 listenerPos)
    {
        float d2 = (transform.position - listenerPos).sqrMagnitude;
        return cachedNumerator / Mathf.Max(1f, d2);
    }


#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (profile == null) return;

        // 1. Setup Values
        float activeDb = (manualDb > 0) ? manualDb : profile.dbLevel;
        float gain = Mathf.Pow(10f, (activeDb - 120f) / 20f);
        float vizNumerator = gain * profile.acousticWeight;
        const float NOISE_FLOOR = 0.00001f;

        // 2. Visualize Hearability Radius
        float relativeVolume = Mathf.InverseLerp(-100f, 0f, activeDb - 120f);
        Color baseColor = Color.Lerp(Color.cyan, Color.red, relativeVolume);

        Gizmos.color = baseColor;
        Gizmos.DrawSphere(transform.position, 0.3f);

        float maxAudibleDistance = Mathf.Sqrt(vizNumerator / NOISE_FLOOR);
        Gizmos.color = new Color(baseColor.r, baseColor.g, baseColor.b, 0.2f);
        Gizmos.DrawWireSphere(transform.position, maxAudibleDistance);

        // 3. Listener Trace Logic
        Transform listener = null;
        if (Camera.main != null) listener = Camera.main.transform;
        if (listener == null) return;

        Vector3 start = transform.position;
        Vector3 end = listener.position;
        float d2 = (start - end).sqrMagnitude;
        float currentScore = vizNumerator / Mathf.Max(1f, d2);

        // 4. Draw Trace Line
        const float visualizationThreshold = 0.001f;
        Gizmos.color = currentScore > visualizationThreshold ? Color.red : Color.green;
        Gizmos.DrawLine(start, end);

        // 5. Labels
        UnityEditor.Handles.Label(transform.position + Vector3.up,
            $"{activeDb}dB | Weight: {profile.acousticWeight}\nMax: {maxAudibleDistance:F1}m");

        GUIStyle style = new GUIStyle { fontSize = 12 };
        style.normal.textColor = Gizmos.color;

        Vector3 midPoint = Vector3.Lerp(start, end, 0.5f);
        UnityEditor.Handles.Label(midPoint, $"Score: {currentScore:F6}", style);
    }
}
#endif

