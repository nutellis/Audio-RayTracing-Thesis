using System;
using System.Runtime.InteropServices;
using UnityEngine;

public class ListenerController : AcousticBase
{

    public ComputeShader audioShader;

    ComputeBuffer rayBuffer;
    int initKernel;
    int traceKernel;

    // Start is called once before the first execution of Update after the MonoBehaviour is created

    protected override void Awake()
    {
        base.Awake();

    }
    
    void Start()
    {
       

    }

    // Update is called once per frame
    void Update()
    {

       // UpdateShaderData();

       // int groups = Mathf.CeilToInt(maxRays / 64f);


       // audioShader.Dispatch(traceKernel, groups, 1, 1);
    }

    private void OnDestroy()
    {
        rayBuffer?.Release();

    }

    public void InitializeRays(int initialRays)
    {
        initKernel = audioShader.FindKernel("InitRays");
        //  traceKernel = audioShader.FindKernel("TraceRays");

        SetupComputeBuffer(initialRays);

        SetupShader(initialRays);

        int groups = Mathf.CeilToInt(initialRays / 64f);
        audioShader.Dispatch(initKernel, groups, 1, 1);
    }


    void UpdateShaderData()
    {
        audioShader.SetVector("listenerPosition", transform.position);
        audioShader.SetVector("listenerForward", transform.forward);
        audioShader.SetVector("listenerRight", transform.right);
        audioShader.SetInt("listenerId", gameObject.GetInstanceID());
    }


    void SetupComputeBuffer(int initialRays)
    {

        int stride = Marshal.SizeOf(typeof(Ray));

        rayBuffer = new ComputeBuffer(initialRays, stride);

        audioShader.SetBuffer(initKernel, "rayBuffer", rayBuffer);
        audioShader.SetBuffer(traceKernel, "rayBuffer", rayBuffer);

    }

    private void SetupShader(int initialRays)
    {
        audioShader.SetInt("initialRays", initialRays);

        UpdateShaderData();
    }

#if UNITY_EDITOR
    /*private void OnDrawGizmos()
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
    }*/
#endif
}
