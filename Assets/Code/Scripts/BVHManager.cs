using NUnit.Framework;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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

    public class BVHNode
    {
        public Bounds bounds;
        public BVHNode leftChild;
        public BVHNode rightChild;

        public int primitiveIndex; // Index of the primitive if this is a leaf node
        public bool IsLeaf => primitiveIndex != 0;

        public int CreateLeafNode(PrimitiveInfo primitive)
        {

            return 0;

        }

        public void CreateInternalNode(BVHNode child, int side)
        {
            if (side == 0)
            {
                leftChild = child;
            }
            else
            {
                rightChild = child;
            }
        }
    }


    List<PrimitiveInfo> primitiveInfo;

    public BVHNode tlas;

    Bounds sceneBounds;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        primitiveInfo = new List<PrimitiveInfo>();
        tlas = new BVHNode();
        CollectBounds();

        BuildTLAS(primitiveInfo.ToArray(), 0, primitiveInfo.Count, tlas);

        List<BVHNode> flatTree = new List<BVHNode>();
        FlattenTLAS(tlas, flatTree, 0);

        Debug.Log("flattened");
    }

    // Update is called once per frame
    void Update()
    {   
    }

    private void LateUpdate()
    {
        CollectBounds();
        BuildTLAS(primitiveInfo.ToArray(),0, primitiveInfo.Count, tlas);
    }

    void CollectBounds()
    {
        Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.InstanceID);

        if (renderers.Length == 0)
        {
            Debug.LogWarning("No renderers found in the scene.");
            return;
        }
        primitiveInfo.Clear();
        sceneBounds = renderers[0].bounds;

        foreach (Renderer renderer in renderers)
        {
            if (renderer.enabled)
            {
                PrimitiveInfo info = new PrimitiveInfo(renderer.GetInstanceID(), renderer.bounds);
                primitiveInfo.Add(info);

                //get scene bounds
                sceneBounds.Encapsulate(renderer.bounds);
            }
        }

        tlas = new()
        {
            bounds = sceneBounds
        };
    }

    // https://medium.com/@Ksatese/advanced-ray-tracer-part-2-f5313530581c
    void BuildTLAS(PrimitiveInfo[] primitives, int startIndex, int endIndex, BVHNode parent)
    {

        if (endIndex - startIndex <= 1)
        {
            //create leaf node
            int primitiveIndex = primitives[startIndex].primitiveIndex;
            parent.primitiveIndex = primitiveIndex;
            parent.bounds = primitives[startIndex].bounds;
            //Debug.Log("Primitive leaf node added on tree");

            //var renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.InstanceID).ToList<Renderer>();

            //// find the instanceid and return the renderer, only debug
            //var renderer = renderers.Find(x => x.GetInstanceID() == primitiveIndex);
            //Debug.Log($"renderer's name is: {renderer.name}");
            return;
        }
        else
        {
            for(int i = startIndex; i < endIndex; i++)
            {
                parent.bounds.Encapsulate(primitives[i].bounds);
            }

            Vector3 extents = parent.bounds.max - parent.bounds.min;
            int splitAxis = 0;
            if (extents.y > extents.x) splitAxis = 1;
            if (extents.z > extents[splitAxis]) splitAxis = 2;

            //sort the primitives by their center along the axis we want to split so 
            // that we now that from start to mid are the left primitives and 
            // the rest are the right primitives
            Array.Sort(primitives, startIndex, endIndex - startIndex, Comparer<PrimitiveInfo>.Create((a, b) =>
            {
                float aCentroid = a.bounds.center[splitAxis];
                float bCentroid = b.bounds.center[splitAxis];
                return aCentroid.CompareTo(bCentroid);
            }));

            int mid = startIndex + (endIndex - startIndex) / 2;


            // run for the left side 
            BVHNode left = parent.leftChild = new BVHNode();
           // Debug.Log("Going left");
            BuildTLAS(primitives, startIndex, mid, left);


            // run for the right side
            BVHNode right = parent.rightChild = new BVHNode();
            //Debug.Log("Going right");
            BuildTLAS(primitives, mid, endIndex, right);
        }
    }

    public int FlattenTLAS(BVHNode node, List<BVHNode> nodeTree, int i)
    {
    
        //as this is a bvh we need to flatten it with intersections in mind.

        //the first item in the array should be the root. If the ray doesnt hit the root, we cooked.

        // next should be the left right of the root. but then??

        return 0;

    }
}
