using System.Collections.Generic;
using UnityEngine;

public class BVHManager : MonoBehaviour
{
    public struct PrimitiveInfo
    {
        public int primitiveIndex;
        public Bounds bounds;

        public PrimitiveInfo(int primitiveIndex, Bounds bounds)
        {
            this.primitiveIndex = primitiveIndex;
            this.bounds = bounds;
        }
    }

    public struct BVHNode
    {
        public Bounds bounds;
        public int leftChild;
        public int rightChild;
        public int primitiveIndex; // Index of the primitive if this is a leaf node
        public bool IsLeaf => primitiveIndex >= 0;

        public int CreateLeafNode(PrimitiveInfo primitive)
        {


        }
    }


    List<PrimitiveInfo> primitiveInfo;

    List<BVHNode> nodes;


    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        CollectBounds();
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    void CollectBounds()
    {
        Renderer[] renderers = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.InstanceID);

        foreach (Renderer renderer in renderers)
        {
            if (renderer.enabled)
            {
                PrimitiveInfo info = new PrimitiveInfo(renderer.GetInstanceID(), renderer.bounds);
                primitiveInfo.Add(info);
            }
        }
    }

    void BuildTLAS()
    {

    }
}
