using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;

namespace Code.Data
{
    [StructLayout(LayoutKind.Sequential)]
    public struct PathData
    {
        public Vector2 arrivalAngles;
        public float distance;
        public float gain;
    
        public int sourceId;

        public Vector3 padding;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct SourceData
    {
        public Vector3 origin;
    
        public int sourceId;
    };

    [StructLayout(LayoutKind.Sequential)]
    public struct DebugInfo
    {
        public int counter;
        public int counter2;

        public Vector2 padding;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct GPUNode
    {
        public Vector3 aabbMin;
        public Vector3 aabbMax;
        
        public int leftChild;
        public int rightChild;

        public int parent;
        
        public int primitiveIndex;
        
        float2 padding;
    }
    
    public struct GPUBlas
    {
        public Vector3 aabbMin;
        public Vector3 aabbMax;
        
        public int leftFirst;

        public int triCount;

    }
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

    public struct Instance
    {
        public Matrix4x4 transformMatrix;

        public int objectId; // unique object id (InstanceID)

        public int blasOffset;
        public int blasCount;
        
        public int trianglesOffset;
        public int trianglesCount;
    }
}