using System.Collections.Generic;
using System.Runtime.InteropServices;
using Code.Data;
using UnityEngine;

public class BVHManager : MonoBehaviour
{
    List<int> primitiveIds;
    List<Vector3> primitiveCentroids;
    List<Bounds> primitiveBounds;

    Bounds sceneBounds;
    
    Dictionary<int, GPUBlas> blas; // holds a unique record of each blas created

    public ComputeShader bvhGeneratorShader;
    
    //bvh tree related kernels
    int generateMortonCodeKernel;
    int histogramPassKernel;
    int prefixPassKernel;
    int scatterPassKernel;
    int hierarchyKernel;
    int fitKernel;

    // buffers
    ComputeBuffer centroidsBuffer;
    ComputeBuffer mortonCodesBuffer;
    ComputeBuffer primitiveIndicesBuffer;
    ComputeBuffer primitiveObjectIds;
    ComputeBuffer deltas;

    ComputeBuffer outMortonBuffer;
    ComputeBuffer outIndicesBuffer;

    ComputeBuffer blockHistogramsBuffer;
    ComputeBuffer globalOffsetsBuffer;

    ComputeBuffer bvhNodeBuffer;

    ComputeBuffer leafBoundsBuffer;
    ComputeBuffer atomicFlagsBuffer;
    int[] zeroFlags; // Cached array of zeros


    int objectCount = 0;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        primitiveIds = new List<int>();
        primitiveCentroids = new List<Vector3>();
        primitiveBounds = new List<Bounds>();

        generateMortonCodeKernel = bvhGeneratorShader.FindKernel("GenerateMortonCodes");
        histogramPassKernel = bvhGeneratorShader.FindKernel("LocalHistogramPass");
        prefixPassKernel = bvhGeneratorShader.FindKernel("GlobalScanPass");
        scatterPassKernel = bvhGeneratorShader.FindKernel("ScatterPass");

        hierarchyKernel = bvhGeneratorShader.FindKernel("BuildHierarchy");
        fitKernel = bvhGeneratorShader.FindKernel("FitAABBs");

        CollectBounds();

        InitializeBuffers();
        InitializeBVHBuffer();
        InitializeRefitBuffers();

        UpdateBounds();
        MortonGenerationBuffersSetup();
        RadixSort();

        BuildTree();

        uint[] mortonDebug = new uint[objectCount];
        mortonCodesBuffer.GetData(mortonDebug);

        //for (int i = 0; i < objectCount; i++)
        //    Debug.Log(mortonDebug[i]);
    }

    private void OnDestroy()
    {
        centroidsBuffer?.Release();
        mortonCodesBuffer?.Release();
        primitiveIndicesBuffer?.Release();

        outMortonBuffer?.Release();
        outIndicesBuffer?.Release();

        blockHistogramsBuffer?.Release();
        globalOffsetsBuffer?.Release();

        bvhNodeBuffer?.Release();

        leafBoundsBuffer?.Release();
        atomicFlagsBuffer?.Release();

        deltas?.Release();
    }

    public void RegisterBLAS(MeshFilter meshFilter)
    {
        //meshFilter.mesh.tr
    }
    void UpdateBounds()
    {
        bvhGeneratorShader.SetBuffer(generateMortonCodeKernel, "centroidsBuffer", centroidsBuffer);
        bvhGeneratorShader.SetBuffer(generateMortonCodeKernel, "mortonCodesBuffer", mortonCodesBuffer);
        bvhGeneratorShader.SetBuffer(generateMortonCodeKernel, "primitiveIndicesBuffer", primitiveIndicesBuffer);
    }
    
    public void UpdateBVH()
    {
        CollectBounds();
        UpdateBounds();
        MortonGenerationBuffersSetup();
        RadixSort();
        BuildTree();
        RefitBVH();
    }

    void CollectBounds()
    {
        // get all objects with a collider
        Renderer[] colliders = FindObjectsByType<Renderer>(FindObjectsSortMode.InstanceID);

        if (colliders.Length == 0)
        {
            return;
        }
        objectCount = colliders.Length;


        primitiveCentroids.Clear();
        primitiveIds.Clear();
        primitiveBounds.Clear();
        sceneBounds = colliders[0].bounds;

        foreach (Renderer collider in colliders)
        {
            if (collider.enabled)
            {
                primitiveCentroids.Add(collider.bounds.center);
                primitiveIds.Add(collider.gameObject.GetInstanceID());

                primitiveBounds.Add(collider.bounds);

                //get scene bounds
                sceneBounds.Encapsulate(collider.bounds);
            }
        }
    }

    void InitializeBuffers()
    {
        centroidsBuffer = new ComputeBuffer(objectCount, sizeof(float) * 3);
        mortonCodesBuffer = new ComputeBuffer(objectCount, sizeof(uint));
        primitiveIndicesBuffer = new ComputeBuffer(objectCount, sizeof(uint));
        primitiveObjectIds = new ComputeBuffer(objectCount, sizeof(int));

        outMortonBuffer = new ComputeBuffer(objectCount, sizeof(uint));
        outIndicesBuffer = new ComputeBuffer(objectCount, sizeof(uint));

        int groups = Mathf.CeilToInt(objectCount / 64.0f);
        blockHistogramsBuffer = new ComputeBuffer(groups * 16, sizeof(uint));
        globalOffsetsBuffer = new ComputeBuffer(groups * 16, sizeof(uint));
    }

    private void MortonGenerationBuffersSetup()
    {
        bvhGeneratorShader.SetInt("objectCount", objectCount);

        bvhGeneratorShader.SetVector("sceneMin", sceneBounds.min);
        bvhGeneratorShader.SetVector("sceneMax", sceneBounds.max);

        centroidsBuffer.SetData(primitiveCentroids);
        primitiveObjectIds.SetData(primitiveIds);

        bvhGeneratorShader.SetBuffer(generateMortonCodeKernel, "centroidsBuffer", centroidsBuffer);
        bvhGeneratorShader.SetBuffer(generateMortonCodeKernel, "primitiveIndicesBuffer", primitiveIndicesBuffer);

        int groups = Mathf.CeilToInt(objectCount / 64.0f);
        bvhGeneratorShader.Dispatch(generateMortonCodeKernel, groups, 1, 1);
    }

    void RadixSort()
    {
        int groups = Mathf.CeilToInt(objectCount / 64.0f);

        for (int pass = 0; pass < 8; pass++)
        {
            int shift = pass * 4;
            bvhGeneratorShader.SetInt("shift", shift);
            bvhGeneratorShader.SetInt("objectCount", objectCount);

            // local histograms
            bvhGeneratorShader.SetBuffer(histogramPassKernel, "mortonCodesBuffer", mortonCodesBuffer);
            bvhGeneratorShader.SetBuffer(histogramPassKernel, "blockHistograms", blockHistogramsBuffer);
            bvhGeneratorShader.Dispatch(histogramPassKernel, groups, 1, 1);

            // prefix pass
            bvhGeneratorShader.SetBuffer(prefixPassKernel, "blockHistograms", blockHistogramsBuffer);
            bvhGeneratorShader.SetBuffer(prefixPassKernel, "globalOffsets", globalOffsetsBuffer);
            bvhGeneratorShader.Dispatch(prefixPassKernel, 1, 1, 1);

            // scatter pass
            bvhGeneratorShader.SetBuffer(scatterPassKernel, "mortonCodesBuffer", mortonCodesBuffer);
            bvhGeneratorShader.SetBuffer(scatterPassKernel, "primitiveIndicesBuffer", primitiveIndicesBuffer);
            bvhGeneratorShader.SetBuffer(scatterPassKernel, "globalOffsets", globalOffsetsBuffer);
            bvhGeneratorShader.SetBuffer(scatterPassKernel, "outputMorton", outMortonBuffer);
            bvhGeneratorShader.SetBuffer(scatterPassKernel, "outputObjectIDs", outIndicesBuffer);
            bvhGeneratorShader.Dispatch(scatterPassKernel, groups, 1, 1);

            // swap old with new ordered buffer.
            (mortonCodesBuffer, outMortonBuffer) = (outMortonBuffer, mortonCodesBuffer);

            (primitiveIndicesBuffer, outIndicesBuffer) = (outIndicesBuffer, primitiveIndicesBuffer);
        }
    }

    void InitializeBVHBuffer()
    {
        // Internal nodes: 0 to N-2
        // Leaf nodes: N-1 to 2N-2
        int totalNodes = (objectCount * 2) - 1;
        bvhNodeBuffer = new ComputeBuffer(totalNodes, Marshal.SizeOf(typeof(GPUNode)));
        GPUNode[] clearNodes = new GPUNode[totalNodes];
        for (int i = 0; i < totalNodes; i++)
        {
            clearNodes[i].parent = -1;
            clearNodes[i].leftChild = -1;
            clearNodes[i].rightChild = -1;
            clearNodes[i].primitiveIndex = -1;
        }
        bvhNodeBuffer.SetData(clearNodes);

        deltas = new ComputeBuffer((objectCount * 2) - 1, sizeof(int) * 4);
    }

    void BuildTree()
    {
        int groups = Mathf.CeilToInt((objectCount - 1) / 64.0f);

        bvhGeneratorShader.SetInt("objectCount", objectCount);
        bvhGeneratorShader.SetBuffer(hierarchyKernel, "bvhNodes", bvhNodeBuffer);
        bvhGeneratorShader.SetBuffer(hierarchyKernel, "sortedMortonCodes", mortonCodesBuffer);
        bvhGeneratorShader.SetBuffer(hierarchyKernel, "sortedIndices", primitiveIndicesBuffer);
        bvhGeneratorShader.SetBuffer(hierarchyKernel, "primitiveObjectIds", primitiveObjectIds);

        bvhGeneratorShader.SetBuffer(hierarchyKernel, "deltas", deltas);

        bvhGeneratorShader.Dispatch(hierarchyKernel, groups, 1, 1);
    }

    void InitializeRefitBuffers()
    {
        // 2 Vector3s (Min/Max) per object
        leafBoundsBuffer = new ComputeBuffer(objectCount * 2, sizeof(float) * 3);

        // One flag per internal node
        atomicFlagsBuffer = new ComputeBuffer(objectCount - 1, sizeof(int));
        zeroFlags = new int[objectCount - 1];

        // repare leaf data(Min and Max for each renderer)
            Vector3[] rawBounds = new Vector3[objectCount * 2];

        for (int i = 0; i < objectCount; i++)
        {
            rawBounds[i * 2] = primitiveBounds[i].min;
            rawBounds[i * 2 + 1] = primitiveBounds[i].max;
        }

        leafBoundsBuffer.SetData(rawBounds);
    }

    void RefitBVH()
    {
        atomicFlagsBuffer.SetData(zeroFlags);
        
        // repare leaf data(Min and Max for each renderer)
        Vector3[] rawBounds = new Vector3[objectCount * 2];

        for (int i = 0; i < objectCount; i++)
        {
            rawBounds[i * 2] = primitiveBounds[i].min;
            rawBounds[i * 2 + 1] = primitiveBounds[i].max;
        }

        leafBoundsBuffer.SetData(rawBounds);
        

        int groups = Mathf.CeilToInt(objectCount / 64.0f);
        bvhGeneratorShader.SetBuffer(fitKernel, "bvhNodes", bvhNodeBuffer);
        bvhGeneratorShader.SetBuffer(fitKernel, "leafBoundsBuffer", leafBoundsBuffer);
        bvhGeneratorShader.SetBuffer(fitKernel, "atomicFlags", atomicFlagsBuffer);
        bvhGeneratorShader.Dispatch(fitKernel, groups, 1, 1);
    }

    public int debugDepth = 5;

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (bvhNodeBuffer == null || objectCount == 0) return;

        GPUNode[] nodes = new GPUNode[objectCount * 2 - 1];
        bvhNodeBuffer.GetData(nodes);
        //for (int i = 0; i < nodes.Length; i++)
        //    Debug.Log($"Node {i}: min={nodes[i].aabbMin}, max={nodes[i].aabbMax}, primIdx={nodes[i].primitiveIndex}, left={nodes[i].leftChild}, right={nodes[i].rightChild}, parent={nodes[i].parent}");

        // Recursive draw call
        DrawNode(nodes, 0, 0); // Start at root (index 0)
    }
    // Define a set of distinct colors for levels 0-10+
    private readonly Color[] levelColors = new Color[]
    {
        Color.green,       // Level 0 (Root)
        Color.cyan,        // Level 1
        Color.yellow,      // Level 2
        Color.magenta,     // Level 3
        new Color(1f, 0.5f, 0f), // Level 4 (Orange)
        Color.white,       // Level 5
        new Color(0.5f, 1f, 0.5f), // Level 6
        new Color(0.5f, 0.5f, 1f), // Level 7
        Color.red          // Level 8+ (Usually Leaves/Deepest)
    };

    void DrawNode(GPUNode[] nodes, int nodeIdx, int currentDepth)
    {
        if (nodeIdx < 0 || nodeIdx >= nodes.Length || currentDepth > debugDepth)
            return;

        GPUNode node = nodes[nodeIdx];

        // 1. Pick color based on depth
        // Uses modulo to cycle colors if the tree is deeper than the array
        Gizmos.color = levelColors[currentDepth % levelColors.Length];

        // 2. Draw the box
        Vector3 center = (node.aabbMin + node.aabbMax) * 0.5f;
        Vector3 size = node.aabbMax - node.aabbMin;

        if (size.sqrMagnitude > 0.0001f)
        {
            Gizmos.DrawWireCube(center, size);
        }

        // 3. Recurse if internal
        if (nodeIdx < (objectCount - 1))
        {
            DrawNode(nodes, node.leftChild, currentDepth + 1);
            DrawNode(nodes, node.rightChild, currentDepth + 1);
        }
        if (node.primitiveIndex != -1)
        {
            int id = node.objectID;
            Vector3 labelPos = center + Vector3.up * 0.05f;
            
            string label =  $"ID: {id}";

            UnityEditor.Handles.Label(labelPos, label);
        }
    }
#endif

    public ComputeBuffer GetBVHBuffer()
    {
        return bvhNodeBuffer;
    }
}