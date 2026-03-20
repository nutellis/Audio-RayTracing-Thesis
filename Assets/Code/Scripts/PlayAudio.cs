using UnityEngine;

public class PlayAudio : MonoBehaviour
{
    public AudioSource audioSource;
    public ComputeShader computeShader;

    private ComputeBuffer audioBuffer;
    private float[] audioData;
    private readonly int sampleRate = 1024; // Number of samples to capture

    void Start()
    {
        // Create the array to hold audio data
        audioData = new float[sampleRate];

        // Create the compute buffer (4 bytes per float)
        audioBuffer = new ComputeBuffer(sampleRate, sizeof(float));
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            audioSource.Play();
        }

        // Continuously capture audio data
        if (audioSource.isPlaying)
        {
            CaptureAudioData();
        }
    }

    void CaptureAudioData()
    {
        // Get output data from the audio source
        audioSource.GetOutputData(audioData, 0);

        // Upload to GPU buffer
        audioBuffer.SetData(audioData);

        // Pass to compute shader, commented out as we are using it because it's just to see if we can save the data
        // int kernelHandle = computeShader.FindKernel("CSMain");
        // computeShader.SetBuffer(kernelHandle, "audioBuffer", audioBuffer);
        // computeShader.Dispatch(kernelHandle, (sampleRate + 7) / 8, 1, 1);

        // Print the content of the first 10 samples for debugging
        for (int i = 0; i < 10; i++)
        {
            Debug.Log($"Sample {i}: {audioData[i]}");
        }
    }

    void OnDestroy()
    {
        audioBuffer?.Release();
    }
}
