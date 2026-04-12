using System;
using System.Runtime.InteropServices;
using UnityEngine;

public class ListenerController : MonoBehaviour
{

    public ComputeShader audioShader;


    public uint maxRays = 1024;
    //[SerializeField]
    private BVHManager BVH;

    ComputeBuffer rayBuffer;
    int initKernel;
    int traceKernel;




    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
     //   initKernel = audioShader.FindKernel("InitRays");
        traceKernel = audioShader.FindKernel("TraceRays");

        SetupComputeBuffer();

        SetupShader();

        int groups = Mathf.CeilToInt(maxRays / 64f);
        audioShader.Dispatch(initKernel, groups, 1, 1);

    }

    // Update is called once per frame
    void Update()
    {
        UpdateShaderData();

        int groups = Mathf.CeilToInt(maxRays / 64f);


       // audioShader.Dispatch(traceKernel, groups, 1, 1);
    }

    private void OnDestroy()
    {
        rayBuffer?.Release();

    }


    void UpdateShaderData()
    {
        audioShader.SetVector("listenerPosition", transform.position);
        audioShader.SetVector("listenerForward", transform.forward);
        audioShader.SetVector("listenerRight", transform.right);

    }


    void SetupComputeBuffer()
    {

        int stride = Marshal.SizeOf(typeof(Ray));
       // Debug.Log("Stride: " + stride);
        rayBuffer = new ComputeBuffer(((int)maxRays), stride);

        audioShader.SetBuffer(initKernel, "rayBuffer", rayBuffer);
        audioShader.SetBuffer(traceKernel, "rayBuffer", rayBuffer);

    }

    private void SetupShader()
    {
        audioShader.SetInt("maxRays", ((int)maxRays));

        UpdateShaderData();
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if(rayBuffer != null)
        {

            Ray[] data = new Ray[rayBuffer.count];

            rayBuffer.GetData(data);

            if (data == null) return;

            for (int i = 0; i < data.Length; i++)
            {
                var ray = data[i];
                Gizmos.color = Color.red;// ray.hit ? Color.red : Color.green;

                 Gizmos.DrawLine(ray.position, ray.position + (ray.direction * 10));

                // Draw a small tip to show directionality
                Gizmos.DrawSphere(ray.position + (ray.direction * 10), 0.05f);
            }
        }
    }
#endif
}
