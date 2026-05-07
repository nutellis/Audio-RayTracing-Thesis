using UnityEngine;

public class FilterController : MonoBehaviour
{
    [Header("Test Absorption Coefficients (0..1)")]
    [Range(0.0f, 1.0f)] public float abs125HzCoeff = 0.0f;
    [Range(0.0f, 1.0f)] public float abs250HzCoeff = 0.0f;
    [Range(0.0f, 1.0f)] public float abs500HzCoeff = 0.0f;
    [Range(0.0f, 1.0f)] public float abs1000HzCoeff = 0.0f;
    [Range(0.0f, 1.0f)] public float abs2000HzCoeff = 0.0f;
    [Range(0.0f, 1.0f)] public float abs4000HzCoeff = 0.0f;

    readonly FrequencyRange freq125HzRange = new(Frequency.freq125Hz);
    readonly FrequencyRange freq250HzRange = new(Frequency.freq250Hz);
    readonly FrequencyRange freq500HzRange = new(Frequency.freq500Hz);
    readonly FrequencyRange freq1000HzRange = new(Frequency.freq1000Hz);
    readonly FrequencyRange freq2000HzRange = new(Frequency.freq2000Hz);
    readonly FrequencyRange freq4000HzRange = new(Frequency.freq4000Hz);

    readonly ButterBandPassFilter[] leftBands = new ButterBandPassFilter[6];
    readonly ButterBandPassFilter[] rightBands = new ButterBandPassFilter[6];

    void Start()
    {
        // Initialize all band-pass filters
        for (int i = 0; i < 6; i++)
        {
            leftBands[i] = new ButterBandPassFilter();
            rightBands[i] = new ButterBandPassFilter();
        }
    }

    void OnAudioFilterRead(float[] data, int channels)
    {
        for (int i = 0; i < data.Length; i += channels)
        {
            float inputL = data[i];
            float inputR = data[i + 1];

            float outL = 0f;
            float outR = 0f;

            outL += leftBands[0].Process(inputL, freq125HzRange.centerFreq, freq125HzRange.bandWidth) * AbsCoeffToAmplitude(abs125HzCoeff);
            outR += rightBands[0].Process(inputR, freq125HzRange.centerFreq, freq125HzRange.bandWidth) * AbsCoeffToAmplitude(abs125HzCoeff);

            outL += leftBands[1].Process(inputL, freq250HzRange.centerFreq, freq250HzRange.bandWidth) * AbsCoeffToAmplitude(abs250HzCoeff);
            outR += rightBands[1].Process(inputR, freq250HzRange.centerFreq, freq250HzRange.bandWidth) * AbsCoeffToAmplitude(abs250HzCoeff);

            outL += leftBands[2].Process(inputL, freq500HzRange.centerFreq, freq500HzRange.bandWidth) * AbsCoeffToAmplitude(abs500HzCoeff);
            outR += rightBands[2].Process(inputR, freq500HzRange.centerFreq, freq500HzRange.bandWidth) * AbsCoeffToAmplitude(abs500HzCoeff);

            outL += leftBands[3].Process(inputL, freq1000HzRange.centerFreq, freq1000HzRange.bandWidth) * AbsCoeffToAmplitude(abs1000HzCoeff);
            outR += rightBands[3].Process(inputR, freq1000HzRange.centerFreq, freq1000HzRange.bandWidth) * AbsCoeffToAmplitude(abs1000HzCoeff);

            outL += leftBands[4].Process(inputL, freq2000HzRange.centerFreq, freq2000HzRange.bandWidth) * AbsCoeffToAmplitude(abs2000HzCoeff);
            outR += rightBands[4].Process(inputR, freq2000HzRange.centerFreq, freq2000HzRange.bandWidth) * AbsCoeffToAmplitude(abs2000HzCoeff);

            outL += leftBands[5].Process(inputL, freq4000HzRange.centerFreq, freq4000HzRange.bandWidth) * AbsCoeffToAmplitude(abs4000HzCoeff);
            outR += rightBands[5].Process(inputR, freq4000HzRange.centerFreq, freq4000HzRange.bandWidth) * AbsCoeffToAmplitude(abs4000HzCoeff);

            data[i] = outL;
            data[i + 1] = outR;
        }
    }

    // Physically accurate one is sqrt(1 - coeff) because the absorption coefficient represents the fraction of energy absorbed
    private static float AbsCoeffToAmplitude(float coeff)
    {
        return Mathf.Sqrt(1f - coeff);
    }
}
