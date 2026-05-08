using System;
using UnityEngine;

public class ButterBandPassFilter
{
    private float a1, a2, a3, a4, a5;
    private float x1 = 0.0f, x2 = 0.0f;   // Delay states
    private float lastCenterFreq = -1.0f;
    private float lastBandWidth = -1.0f;

    private readonly float sampleRate = AudioSettings.outputSampleRate;

    // Process a single sample using a 2-pole Butterworth band-pass filter.
    public float Process(float input, float centerFreq, float bandWidth)
    {
        if (bandWidth <= 0.0f)
            return 0f;

        // Recalculate coefficients only when freq or BW changes.
        if (Math.Abs(centerFreq - lastCenterFreq) > 0.001f || Math.Abs(bandWidth - lastBandWidth) > 0.001f)
        {
            UpdateCoefficients(centerFreq, bandWidth);
            lastCenterFreq = centerFreq;
            lastBandWidth = bandWidth;
        }

        // Biquad band-pass difference equation
        float t = input - a4 * x1 - a5 * x2;
        float y = t * a1 + a2 * x1 + a3 * x2;  // a2 is always 0 for this design

        // Update history
        x2 = x1;
        x1 = t;

        return y;
    }

    private void UpdateCoefficients(float freq, float bw)
    {
        float pid_sr = Mathf.PI / sampleRate;
        float tpid_sr = 2.0f * Mathf.PI / sampleRate;

        float c = 1.0f / Mathf.Tan(pid_sr * bw);
        float d = 2.0f * Mathf.Cos(tpid_sr * freq);

        a1 = 1.0f / (1.0f + c);
        a2 = 0.0f;
        a3 = -a1;
        a4 = -c * d * a1;
        a5 = (c - 1.0f) * a1;
    }
}
