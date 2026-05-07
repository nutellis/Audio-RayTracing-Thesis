using UnityEngine;
using System;

[RequireComponent(typeof(AudioSource))]
public class SineMultiTonePlayer : MonoBehaviour
{
    public float gain = 1f; // overall amplitude (0..1)
    public float[] frequencies = new float[] { 125f, 250f, 500f, 1000f, 2000f, 4000f };

    private double[] phases;
    private double sampleRate;

    void Awake()
    {
        sampleRate = AudioSettings.outputSampleRate;
        phases = new double[frequencies.Length];
    }

    void OnAudioFilterRead(float[] data, int channels)
    {
        int nTones = frequencies.Length;
        float perToneAmp = gain / nTones;
        for (int i = 0; i < data.Length; i += channels)
        {
            double sample = 0.0;
            for (int t = 0; t < nTones; t++)
            {
                double inc = 2.0 * Math.PI * frequencies[t] / sampleRate;
                sample += perToneAmp * Math.Sin(phases[t]);
                phases[t] += inc;
                if (phases[t] > Math.PI * 2) phases[t] -= Math.PI * 2;
            }
            float outSample = (float)sample;
            for (int ch = 0; ch < channels; ch++) data[i + ch] = outSample;
        }
    }
}