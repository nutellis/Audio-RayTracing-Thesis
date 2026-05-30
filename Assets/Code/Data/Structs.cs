using System;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;

namespace Code.Data
{
    [StructLayout(LayoutKind.Sequential)]
    public struct AcousticData 
    {
        //it hurts but i want to avoid unsafe
        public float energy0;
        public float energy1;
        public float energy2;
        public float energy3;
        public float energy4;
        public float energy5;

    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct MacroBin
    {
        public float energy0; 
        public float energy1;
        public float energy2;
        public float energy3;
        public float energy4;
        public float energy5;
    };
    
    public struct DirectAudioData 
    {
        public float transmissionMultiplier;
        public float delayMs;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct SourceData
    {
        public Vector3 origin;
        public float radius;
        
        public float maxAudibleDistance;
        public float minAudibleDistance;

        public float power;
        
        public int sourceId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DebugInfo
    {
        public float3 o;
        public float3 d;

        public float2 padding;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct GPUTlasNode
    {
        public Vector3 aabbMin;
        public Vector3 aabbMax;
        
        public int leftChild;
        public int rightChild;
        
        public int primitiveIndex;
        
        float2 padding;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct GPUBlasNode
    {
        public Vector3 aabbMin;
        
        public Vector3 aabbMax;
        
        public int leftFirst;

        public int triCount;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct Triangle
    {
        public float3 vertexA;
        public float3 vertexB;
        public float3 vertexC;

        public float3 padding;
    }

    public struct BlasMetada
    {
        public int blasOffset;
        public int blasCount;
        
        public int trianglesOffset;
        public int trianglesCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Instance
    {
        public Matrix4x4 worldToLocal;
        public Matrix4x4 localToWorld;

        public int objectId;
        public int materialId;

        public int blasOffset;
        public int blasCount;
        
        public int trianglesOffset;
        public int trianglesCount;
        
        float2 padding;
    }
    
    //i realize that i use many very similar structs but i cannot focus on managing this.
    //This struct is only to pass the material data into the tracer.
    [StructLayout(LayoutKind.Sequential)]
    public struct MaterialData
    {
        public float3 absorptionLowMid;
        public float3 absorptionMidHigh;

        public float3 transmission;

        public float scattering;
        public float2 padding;
    }
    
    public struct FilterCoefficients
    {
        public float a1; 
        public float a2;
        public float a3; 
        public float a4; 
        public float a5; 
    }
    
    public struct FilterState
    {
        public float x1;
        public float x2;
    }
    
}