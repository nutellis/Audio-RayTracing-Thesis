using UnityEngine;
using UnityEngine.UI;

public class CsoundController : MonoBehaviour
{
    public CsoundUnity csound;

    [Header("Test Absorption Coefficients (0..1)")]
    [Range(0.0f, 1.0f)] public float abs125HzCoeff = 0.0f;
    [Range(0.0f, 1.0f)] public float abs250HzCoeff = 0.0f;
    [Range(0.0f, 1.0f)] public float abs500HzCoeff = 0.0f;
    [Range(0.0f, 1.0f)] public float abs1000HzCoeff = 0.0f;
    [Range(0.0f, 1.0f)] public float abs2000HzCoeff = 0.0f;
    [Range(0.0f, 1.0f)] public float abs4000HzCoeff = 0.0f;

    [SerializeField] private readonly bool updateEveryFrame = true;

    readonly FrequencyRange freq125HzRange = new(Frequency.freq125Hz, Frequency.freq0Hz);
    readonly FrequencyRange freq250HzRange = new(Frequency.freq250Hz, Frequency.freq125Hz);
    readonly FrequencyRange freq500HzRange = new(Frequency.freq500Hz, Frequency.freq250Hz);
    readonly FrequencyRange freq1000HzRange = new(Frequency.freq1000Hz, Frequency.freq500Hz);
    readonly FrequencyRange freq2000HzRange = new(Frequency.freq2000Hz, Frequency.freq1000Hz);
    readonly FrequencyRange freq4000HzRange = new(Frequency.freq4000Hz, Frequency.freq2000Hz);

    readonly ButterBandPassFilter[] leftBands = new ButterBandPassFilter[6];
    readonly ButterBandPassFilter[] rightBands = new ButterBandPassFilter[6];

    // Probably what will do in another class
    void OnAudioFilterRead(float[] data, int channels)
    {
        for (int i = 0; i < data.Length; i += channels)
        {
            float inputL = data[i];
            float inputR = data[i + 1];

            float outL = 0f;
            float outR = 0f;

            outL += leftBands[0].Process(inputL, freq125HzRange.centerFreq, freq125HzRange.bandWidth);
            outR += rightBands[0].Process(inputR, freq125HzRange.centerFreq, freq125HzRange.bandWidth);

            outL += leftBands[1].Process(inputL, freq250HzRange.centerFreq, freq250HzRange.bandWidth);
            outR += rightBands[1].Process(inputR, freq250HzRange.centerFreq, freq250HzRange.bandWidth);

            outL += leftBands[2].Process(inputL, freq500HzRange.centerFreq, freq500HzRange.bandWidth);
            outR += rightBands[2].Process(inputR, freq500HzRange.centerFreq, freq500HzRange.bandWidth);

            outL += leftBands[3].Process(inputL, freq1000HzRange.centerFreq, freq1000HzRange.bandWidth);
            outR += rightBands[3].Process(inputR, freq1000HzRange.centerFreq, freq1000HzRange.bandWidth);

            outL += leftBands[4].Process(inputL, freq2000HzRange.centerFreq, freq2000HzRange.bandWidth);
            outR += rightBands[4].Process(inputR, freq2000HzRange.centerFreq, freq2000HzRange.bandWidth);

            outL += leftBands[5].Process(inputL, freq4000HzRange.centerFreq, freq4000HzRange.bandWidth);
            outR += rightBands[5].Process(inputR, freq4000HzRange.centerFreq, freq4000HzRange.bandWidth);

            // outL *= 1f / 6f; // Normalize to prevent clipping (adjust as needed)
            // outR *= 1f / 6f;

            data[i] = outL;
            data[i + 1] = outR;
        }
    }

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

    void PlaySound()
    {
        //initializes and plays the audio source

    }

    void ModifyPlayingSound() // push variables to csound
    {

    }

    void PushAbsorption()
    {
        if (csound == null)
        {
            return;
        }

        csound.SetChannel("abs125HzCoeff", abs125HzCoeff);
        csound.SetChannel("abs250HzCoeff", abs250HzCoeff);
        csound.SetChannel("abs500HzCoeff", abs500HzCoeff);
        csound.SetChannel("abs1000HzCoeff", abs1000HzCoeff);
        csound.SetChannel("abs2000HzCoeff", abs2000HzCoeff);
        csound.SetChannel("abs4000HzCoeff", abs4000HzCoeff);

        csound.SetChannel("kCenterFreq125Hz", freq125HzRange.centerFreq);
        csound.SetChannel("kCenterFreq250Hz", freq250HzRange.centerFreq);
        csound.SetChannel("kCenterFreq500Hz", freq500HzRange.centerFreq);
        csound.SetChannel("kCenterFreq1000Hz", freq1000HzRange.centerFreq);
        csound.SetChannel("kCenterFreq2000Hz", freq2000HzRange.centerFreq);
        csound.SetChannel("kCenterFreq4000Hz", freq4000HzRange.centerFreq);

        csound.SetChannel("kBand125Hz", freq125HzRange.bandWidth);
        csound.SetChannel("kBand250Hz", freq250HzRange.bandWidth);
        csound.SetChannel("kBand500Hz", freq500HzRange.bandWidth);
        csound.SetChannel("kBand1000Hz", freq1000HzRange.bandWidth);
        csound.SetChannel("kBand2000Hz", freq2000HzRange.bandWidth);
        csound.SetChannel("kBand4000Hz", freq4000HzRange.bandWidth);
    }
}
