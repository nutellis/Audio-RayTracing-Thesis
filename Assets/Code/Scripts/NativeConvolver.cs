using System;
using System.Runtime.InteropServices;

public class NativeConvolver : IDisposable
{
    private const string DLL_NAME = "unityplugin";

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)] 
    private static extern IntPtr HybridReverb_Create();

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)] 
    private static extern void HybridReverb_Destroy(IntPtr instance);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)] 
    private static extern void HybridReverb_SetEarlyImpulse(IntPtr instance, float[] impulse, int length);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)] 
    private static extern void HybridReverb_SetLateParams(IntPtr instance, double mappedRT60, double absorption);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)] 
    private static extern void HybridReverb_Process(IntPtr instance, float[] input, float[] output, int numSamples);

    private IntPtr nativeInstance;

    public NativeConvolver()
    {
        nativeInstance = HybridReverb_Create();
    }

    public void LoadEarlyImpulseResponse(float[] irData)
    {
        if (nativeInstance != IntPtr.Zero && irData != null && irData.Length > 0)
        {
            HybridReverb_SetEarlyImpulse(nativeInstance, irData, irData.Length);
        }
    }

    public void SetLateParams(double mappedRT60, double absorption)
    {
        if (nativeInstance != IntPtr.Zero)
        {
            HybridReverb_SetLateParams(nativeInstance, mappedRT60, absorption);
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

    ~NativeConvolver()
    {
        if (nativeInstance != IntPtr.Zero)
        {
            HybridReverb_Destroy(nativeInstance);
        }
    }
}