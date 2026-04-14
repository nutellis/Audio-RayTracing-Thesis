using System.Runtime.InteropServices;
using UnityEngine;

[StructLayout(LayoutKind.Sequential)]
public struct Ray
{
    public Vector3 position;
    //public float energy; // volume
    
    public Vector3 direction;
    //public uint bounceCount; //times reflected


   // public float distance; // distance traveled to calculate time delay

   public Vector2 padding; 
}