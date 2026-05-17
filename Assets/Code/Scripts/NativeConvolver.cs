using System;
using System.Runtime.InteropServices;

public class NativeConvolver : IDisposable
{
    // Matches the exact name of your compiled library (without the .dll extension)
    private const string DLL_NAME = "unityplugin";

    // --- Native C++ function bindings ---
    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)] 
    private static extern IntPtr Convolver_Create();

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)] 
    private static extern void Convolver_Destroy(IntPtr instance);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)] 
    private static extern void Convolver_SetImpulse(IntPtr instance, float[] impulse, int length);

    [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)] 
    private static extern void Convolver_Process(IntPtr instance, float[] input, float[] output, int numSamples);

    // Pointer to the unmanaged memory holding the C++ ConvolverWrapper struct
    private IntPtr nativeInstance;

    /// <summary>
    /// Initializes a new instance of the C++ Fast Convolution engine.
    /// </summary>
    public NativeConvolver()
    {
        nativeInstance = Convolver_Create();
    }

    /// <summary>
    /// Passes the baked Room Impulse Response (RIR) data into the C++ engine.
    /// </summary>
    /// <param name="irData">The raw float array representing the audio fingerprint.</param>
    public void LoadImpulseResponse(float[] irData)
    {
        if (nativeInstance != IntPtr.Zero && irData != null && irData.Length > 0)
        {
            Convolver_SetImpulse(nativeInstance, irData, irData.Length);
        }
    }

    /// <summary>
    /// Feeds dry audio samples to the C++ engine to produce the wet reverb wash.
    /// </summary>
    /// <param name="input">The dry input buffer from Unity's audio stream.</param>
    /// <param name="output">The pre-allocated target buffer where the wet reverb will be written.</param>
    public void Process(float[] input, float[] output)
    {
        if (nativeInstance != IntPtr.Zero && input != null && output != null)
        {
            Convolver_Process(nativeInstance, input, output, input.Length);
        }
    }

    /// <summary>
    /// Safely cleans up the unmanaged C++ instance to avoid catastrophic VRAM/RAM leaks.
    /// </summary>
    public void Dispose()
    {
        if (nativeInstance != IntPtr.Zero)
        {
            Convolver_Destroy(nativeInstance);
            nativeInstance = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }

    // Fallback finalizer if Dispose() was neglected
    ~NativeConvolver()
    {
        if (nativeInstance != IntPtr.Zero)
        {
            Convolver_Destroy(nativeInstance);
        }
    }
}