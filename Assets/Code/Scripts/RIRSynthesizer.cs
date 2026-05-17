using System;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Mathematics;
using Code.Data;

public static class RIRSynthesizer
{
    private const int SAMPLE_RATE = 48000; 
    private const float BIN_RESOLUTION_MS = 2.5f;
    private const int MAX_IR_LENGTH_SAMPLES = SAMPLE_RATE * 2; // 2 seconds of reverb

    public static async Task<float[]> BakeImpulseResponseAsync(MacroBin[] echogram, FilterCoefficients[] coeffs)
    {
        return await Task.Run(() =>
        {
            float[] irBuffer = new float[MAX_IR_LENGTH_SAMPLES];
            int filterTailSamples = 256; 
            
            // Allocate filter states for our 6 bands
            FilterState[] synthFilters = new FilterState[6];
            System.Random rand = new System.Random();

            for (int binIndex = 0; binIndex < echogram.Length; binIndex++)
            {
                var bin = echogram[binIndex];
    
                // 1. Unpack fixed-point integers back to raw energy floats
                float e0 = (float)bin.energy0 / 100000.0f;
                float e1 = (float)bin.energy1 / 100000.0f;
                float e2 = (float)bin.energy2 / 100000.0f;
                float e3 = (float)bin.energy3 / 100000.0f;
                float e4 = (float)bin.energy4 / 100000.0f;
                float e5 = (float)bin.energy5 / 100000.0f;

                // 2. CRITICAL FIX: Convert Acoustic Energy to Sound Pressure Amplitude
                float a0 = math.sqrt(e0);
                float a1 = math.sqrt(e1);
                float a2 = math.sqrt(e2);
                float a3 = math.sqrt(e3);
                float a4 = math.sqrt(e4);
                float a5 = math.sqrt(e5);

                float maxAmplitude = math.max(a0, math.max(a1, math.max(a2, math.max(a3, math.max(a4, a5)))));
                if (maxAmplitude < 0.0001f) continue; 

                float delayMs = binIndex * BIN_RESOLUTION_MS;
                int startSample = (int)((delayMs / 1000.0f) * SAMPLE_RATE);

                Array.Clear(synthFilters, 0, synthFilters.Length);

                for (int i = 0; i < filterTailSamples; i++)
                {
                    int writePos = startSample + i;
                    if (writePos >= MAX_IR_LENGTH_SAMPLES) break;

                    float inputSample = (i == 0) ? 1.0f : 0.0f;

                    // 3. Multiply by the Amplitudes (a0-a5) instead of the raw energies (e0-e5)
                    float filteredSample = 0f;
                    filteredSample += ProcessFilter(inputSample, ref synthFilters[0], coeffs[0]) * a0;
                    filteredSample += ProcessFilter(inputSample, ref synthFilters[1], coeffs[1]) * a1;
                    filteredSample += ProcessFilter(inputSample, ref synthFilters[2], coeffs[2]) * a2;
                    filteredSample += ProcessFilter(inputSample, ref synthFilters[3], coeffs[3]) * a3;
                    filteredSample += ProcessFilter(inputSample, ref synthFilters[4], coeffs[4]) * a4;
                    filteredSample += ProcessFilter(inputSample, ref synthFilters[5], coeffs[5]) * a5;

                    irBuffer[writePos] += filteredSample;
                }
            }
            return irBuffer;
        });
    }

    private static float ProcessFilter(float input, ref FilterState state, FilterCoefficients coef)
    {
        float output = coef.a1 * input + state.x1;
        state.x1 = coef.a2 * input - coef.a4 * output + state.x2;
        state.x2 = coef.a3 * input - coef.a5 * output;
        return output;
    }
}