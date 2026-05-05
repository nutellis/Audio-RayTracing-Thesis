public class FrequencyRange
{
    public float centerFreq;
    public float bandWidth;

    public FrequencyRange(float freqHz, float prevFreqHz)
    {
        centerFreq = (freqHz + prevFreqHz) * 0.5f; // Try Math.Sqrt(freqHz * prevFreqHz)
        bandWidth = freqHz - prevFreqHz;
    }
}
