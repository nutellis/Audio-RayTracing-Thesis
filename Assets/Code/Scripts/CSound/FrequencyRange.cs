public class FrequencyRange
{
    public float centerFreq;
    public float band;

    public FrequencyRange(float freqHz, float prevFreqHz)
    {
        centerFreq = (freqHz + prevFreqHz) / 2;
        band = (freqHz - prevFreqHz) / 2;
    }
}
