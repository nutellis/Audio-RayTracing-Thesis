using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Code.Data;
using Unity.Mathematics;
using UnityEngine;
using System.Runtime.InteropServices;

public class AcousticObject : AcousticBase
{
    public AcousticMaterial acousticMaterial;
    Instance objectInstance;

    protected override void Awake()
    {
        base.Awake();
        var meshFilter = gameObject.GetComponent<MeshFilter>();

        if (!meshFilter) return;
        
       var key = meshFilter.sharedMesh.name.GetHashCode();
 
       var bvhManager = FindAnyObjectByType(typeof(BVHManager)) as BVHManager;
       if (bvhManager)
       {
           var metadata = bvhManager.BuildBlas(meshFilter);

           objectInstance = new Instance
           {
                objectId = InstanceID,
                transformMatrix = transform.localToWorldMatrix,
                blasOffset = metadata.blasOffset,
                blasCount = metadata.blasCount,
                trianglesOffset = metadata.trianglesOffset,
                trianglesCount = metadata.trianglesCount
           };

           ObjectRegistry<Instance>.Instance.RegisterObject(InstanceID, objectInstance);
       }
       else
       {
           Debug.LogError("No BVHManager found in the scene. Please add one to build BLAS for acoustic objects.");
       }
    }
    
    [Range(0, 32)] public int minDepth = 0;
    [Range(0, 32)] public int maxDepth = 20;
    public bool showDebug;
    private GPUBlas[] blasArray;
    private void OnDrawGizmosSelected()
    {
        if (!showDebug) return;
        var manager = FindAnyObjectByType(typeof(BVHManager)) as BVHManager;
        if (manager != null)
        {
            var blasOffset = objectInstance.blasOffset;
            var blasCount = objectInstance.blasCount;
            blasArray = manager.globalBlasNodes.GetRange(blasOffset, blasCount).ToArray();
            DrawNode(0, 0);
        }
    }
    
    private void DrawNode(int nodeIdx, int depth)
    {
        if (depth > maxDepth || nodeIdx >= blasArray.Length) return;
    
        GPUBlas node = blasArray[nodeIdx];
        bool isVisible = depth >= minDepth;
    
        if (isVisible)
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = Color.Lerp(Color.green, Color.red, (float)depth / Mathf.Max(1, maxDepth));
    
            float3 center = (node.aabbMin + node.aabbMax) * 0.5f;
            float3 size = node.aabbMax - node.aabbMin;
            Gizmos.DrawWireCube(center, size);
    
            // If triCount > 0, this is a leaf node
            // if (node.triCount > 0)
            // {
            //     Gizmos.color = Color.yellow;
            //     for (int i = 0; i < node.triCount; i++)
            //     {
            //         int triIdx = triangleIndices[node.leftFirst + i];
            //         Triangle tri = triangles[triIdx];
            //         Gizmos.DrawLine(tri.vertexA, tri.vertexB);
            //         Gizmos.DrawLine(tri.vertexB, tri.vertexC);
            //         Gizmos.DrawLine(tri.vertexC, tri.vertexA);
            //     }
            // }
        }
    
        // If triCount == 0, this is an inner node. 
        // leftFirst points to the index of the first child.
        // The second child is always at leftFirst + 1.
        if (node.triCount == 0 && node.leftFirst > 0)
        {
            DrawNode(node.leftFirst, depth + 1);
            DrawNode(node.leftFirst + 1, depth + 1);
        }
    }

}
