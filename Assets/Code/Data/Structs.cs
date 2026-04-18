using System.Runtime.InteropServices;
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
        public int primitiveIndex;

        public int leftChild;
        public int rightChild;

        public int parent;

        public int objectID;
        
        float padding;
    }
    
    public struct GPUBlas
    {
        public Vector3 aabbMin;
        public Vector3 aabbMax;
    }
}