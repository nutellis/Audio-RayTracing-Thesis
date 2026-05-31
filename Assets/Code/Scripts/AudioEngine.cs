using System;
using System.Runtime.InteropServices;
using Code.Data;

public class AudioEngine : IDisposable
{
    private const string DLL_NAME = "unityplugin";
    
    private const int SAMPLE_RATE = 48000; 
    private const float BIN_RESOLUTION_MS = 2.5f;

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)] 
    private static extern IntPtr HybridReverb_Create();

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)] 
    private static extern void HybridReverb_Destroy(IntPtr instance);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)] 
    private static extern void HybridReverb_SetEarlyImpulse(IntPtr instance, float[] impulse, int length);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)] 
    private static extern void HybridReverb_SetLateParams(IntPtr instance, double mappedRT60, double absorption, float reverbSwitch);
    
    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)] 
    private static extern void HybridReverb_SetPreDelay(IntPtr instance, float preDelayMs);
    
    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)] 
    private static extern void HybridReverb_Process(IntPtr instance, float[] input, float[] output, int numSamples);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern void BakeImpulseResponse(
        AcousticData[] echogram, int echogramLength,
        FilterCoefficients[] coeffs,
        float[] outIrBuffer, int outIrLength,
        int sampleRate, float binResolutionMs, int startIndex);
    
    
    private IntPtr nativeInstance;

    public AudioEngine()
    {
        nativeInstance = HybridReverb_Create();
    }
    
    public static float[] BakeImpulseResponse(
        AcousticData[] echogram, FilterCoefficients[] coeffs, int startIndex = 0)
    {
        int validBins = echogram.Length - startIndex;
        if (validBins <= 0) return new float[0];

        float totalMs = validBins * BIN_RESOLUTION_MS;
        int requiredSamples = (int)((totalMs / 1000.0f) * SAMPLE_RATE) + 256; 

        float[] irBuffer = new float[requiredSamples];
        
        // Execute the stateless C++ function directly on the current thread
        BakeImpulseResponse(
            echogram, echogram.Length,
            coeffs,
            irBuffer, irBuffer.Length,
            SAMPLE_RATE, BIN_RESOLUTION_MS, startIndex);

        return irBuffer;
    }
    
    public void LoadEarlyImpulseResponse(float[] irData)
    {
        if (nativeInstance != IntPtr.Zero && irData != null && irData.Length > 0)
        {
            HybridReverb_SetEarlyImpulse(nativeInstance, irData, irData.Length);
        }
    }

    public void SetLateParams(double mappedRT60, double absorption, float reverbSwitch)
    {
        if (nativeInstance != IntPtr.Zero)
        {
            HybridReverb_SetLateParams(nativeInstance, mappedRT60, absorption, reverbSwitch);
        }
    }

    public void SetPreDelay(float preDelayMs) 
    {
        if (nativeInstance != IntPtr.Zero) 
        {
            HybridReverb_SetPreDelay(nativeInstance, preDelayMs);
        }
    }

    public void Process(float[] input, float[] output)
    {
        if (nativeInstance != IntPtr.Zero && input != null && output != null)
        {
            HybridReverb_Process(nativeInstance, input, output, input.Length);
        }
    }

    public void Dispose()
    {
        if (nativeInstance != IntPtr.Zero)
        {
            HybridReverb_Destroy(nativeInstance);
            nativeInstance = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }

    ~AudioEngine()
    {
        if (nativeInstance != IntPtr.Zero)
        {
            HybridReverb_Destroy(nativeInstance);
        }
    }
}