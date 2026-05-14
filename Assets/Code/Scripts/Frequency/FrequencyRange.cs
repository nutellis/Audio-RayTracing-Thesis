using UnityEngine;

public class FrequencyRange
{
    public float centerFreq;
    public float bandWidth;

    private const float thirdOctaveFactor = 0.23156333016903374f; // Precomputed value for (2^(1/6) - 2^(-1/6))

    public FrequencyRange(float freq)
    {
        centerFreq = freq;

        // Third-octave bandwidth calculation: BW = f * (2^(1/6) - 2^(-1/6))
        bandWidth = freq * thirdOctaveFactor;

        // Debug.Log("FrequencyRange created for " + freq + " Hz: Center = " + centerFreq + " Hz, Bandwidth = " + bandWidth + " Hz");
    }
}
