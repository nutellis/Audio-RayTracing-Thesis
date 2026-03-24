using UnityEngine;

public class CsoundController : MonoBehaviour
{
    public CsoundUnity csound;

    [Header("Test Absorption Coefficients (0..0.99)")]
    [Range(0.0f, 0.99f)] public float absLowCoeff = 0.2f;
    [Range(0.0f, 0.99f)] public float absMidCoeff = 0.5f;
    [Range(0.0f, 0.99f)] public float absHighCoeff = 0.8f;

    [SerializeField] private bool updateEveryFrame = true;

    void Start()
    {
        if (csound == null)
        {
            Debug.LogError("CsoundController: CsoundUnity component not assigned!");
            return;
        }

        csound.processClipAudio = true;
        PushAbsorption();

        AudioSource source = csound.GetComponent<AudioSource>();
        if (source == null)
        {
            Debug.LogWarning("CsoundController: No AudioSource found on the CsoundUnity object.");
        }
        else
        {
            if (source.clip == null)
            {
                Debug.LogWarning("CsoundController: AudioSource has no clip assigned. Assign a clip to feed Csound.");
            }
            else if (!source.isPlaying)
            {
                source.Play();
                Debug.Log($"CsoundController: Started AudioSource clip '{source.clip.name}' for Csound input.");
            }
            else
            {
                Debug.Log($"CsoundController: AudioSource already playing clip '{source.clip.name}'.");
            }
        }
    }

    void Update()
    {
        if (updateEveryFrame)
        {
            PushAbsorption();
        }
    }

    void PushAbsorption()
    {
        if (csound == null)
        {
            return;
        }

        csound.SetChannel("absLowCoeff", absLowCoeff);
        csound.SetChannel("absMidCoeff", absMidCoeff);
        csound.SetChannel("absHighCoeff", absHighCoeff);
    }
}
