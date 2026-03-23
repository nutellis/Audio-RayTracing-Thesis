using UnityEngine;

public class CsoundController : MonoBehaviour
{
    public CsoundUnity csound;

    [Header("Test Absorption Coefficients (0..1)")]
    [Range(0f, 1f)] public float absLowCoeff = 0.2f;
    [Range(0f, 1f)] public float absMidCoeff = 0.5f;
    [Range(0f, 1f)] public float absHighCoeff = 0.8f;

    [SerializeField] private bool updateEveryFrame = false;

    void Start()
    {
        if (csound == null)
        {
            Debug.LogError("CsoundController: CsoundUnity component not assigned!");
            return;
        }

        PushAbsorption();
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