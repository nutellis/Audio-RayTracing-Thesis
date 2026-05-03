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
                worldToLocal = transform.worldToLocalMatrix,
                localToWorld = transform.localToWorldMatrix,
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
    private GPUBlasNode[] blasArray;
    private Triangle[] triangles;
    private void OnDrawGizmosSelected()
    {
        if (!showDebug) return;
        var manager = FindAnyObjectByType(typeof(BVHManager)) as BVHManager;
        if (manager != null)
        {
            var blasOffset = objectInstance.blasOffset;
            var blasCount = objectInstance.blasCount;
            blasArray = manager.globalBlasNodes.GetRange(blasOffset, blasCount).ToArray();
            
            triangles = manager.globalTriangleSoup.GetRange(objectInstance.trianglesOffset, objectInstance.trianglesCount).ToArray();
            DrawNode(0, 0);
            //Debug.Log("End of Tree");
        }
    }
    
    private void DrawNode(int nodeIdx, int depth)
    {
        if (depth > maxDepth || nodeIdx >= blasArray.Length) return;
    
        GPUBlasNode node = blasArray[nodeIdx];
        bool isVisible = depth >= minDepth;
    
        if (isVisible)
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = Color.Lerp(Color.green, Color.red, (float)depth / Mathf.Max(1, maxDepth));
    
            float3 center = (node.aabbMin + node.aabbMax) * 0.5f;
            float3 size = node.aabbMax - node.aabbMin;
            Gizmos.DrawWireCube(center, size);
            
            //Debug.Log($"AABB center {center} and size {size}");
            
    
            //If triCount > 0, this is a leaf node
            if (node.triCount > 0)
            {
                Gizmos.color = Color.yellow;
                for (int i = 0; i < node.triCount; i++)
                {
                    Triangle tri = triangles[node.leftFirst + i];
                    
                    Gizmos.DrawLine(tri.vertexA, tri.vertexB);
                    Gizmos.DrawLine(tri.vertexB, tri.vertexC);
                    Gizmos.DrawLine(tri.vertexC, tri.vertexA);
                    
                    //Debug.Log($"Drawing Triangle: A({tri.vertexA}), B({tri.vertexB}), C({tri.vertexC})");
                }
            }
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
