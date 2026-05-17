using System;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;

namespace Code.Data
{
    [StructLayout(LayoutKind.Sequential)]
    public struct PathData 
    {
        public float2 arrivalAngles;
        public float distance;
        
        //it hurts but i want to avoid unsafe
        public float energy0;
        public float energy1;
        public float energy2;
        public float energy3;
        public float energy4;
        public float energy5;
        
        public float padding;
        
        public int sourceId;
        public int state; // 0 = direct, 1 = reflection, 2 = ??

        
        public float totalGain()
        {
            return (energy0 + energy1 + energy2 + energy3 + energy4 + energy5) / 6f;
        }
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct MacroBin
    {
        public uint energy0;
        public uint energy1;
        public uint energy2;
        public uint energy3;
        public uint energy4;
        public uint energy5;
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
        public int counter;
        public int counter2;

        public Vector2 padding;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct GPUTlasNode
    {
        public Vector3 aabbMin;
        public Vector3 aabbMax;
        
        public int leftChild;
        public int rightChild;

        public int parent;
        
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
        public float absorption0;
        public float absorption1;
        public float absorption2;
        public float absorption3;
        public float absorption4;
        public float absorption5;

        public float transmission0;
        public float transmission1;
        public float transmission2;
        
        public float scattering;

        public float2 padding;
        
        
        public float GetAbsorption(int bandIndex)
        {
            return bandIndex switch
            {
                0 => absorption0,
                1 => absorption1,
                2 => absorption2,
                3 => absorption3,
                4 => absorption4,
                5 => absorption5,
                _ => 0f
            };
        }
    }
    
    //this one keeps track of the early reflections
    public struct Reflection : IComparable<Reflection>
    {
        public int delaySamples;
        
        public float energy0;
        public float energy1;
        public float energy2;
        public float energy3;
        public float energy4;
        public float energy5;
        
        public float2 arrivalAngles;
        
        public int CompareTo(Reflection other)
        {
            return delaySamples.CompareTo(other.delaySamples);
        }
        
        public float GetEnergy(int bandIndex)
        {
            return bandIndex switch
            {
                0 => energy0,
                1 => energy1,
                2 => energy2,
                3 => energy3,
                4 => energy4,
                5 => energy5,
                _ => 0f
            };
        }
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