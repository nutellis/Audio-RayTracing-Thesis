using System.Collections.Generic;
using System.Runtime.InteropServices;
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

    [StructLayout(LayoutKind.Sequential)]
    public struct GPUNode
    {
        public Vector3 aabbMin;
        public Vector3 aabbMax;
        public int primitiveIndex;

        public int leftChild;
        public int rightChild;

        public int parent;
    }

    List<int> primitiveIds;
    List<Vector3> primitiveCentroids;
    List<Bounds> primitiveBounds;

    Bounds sceneBounds;

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

    private void LateUpdate()
    {
        // CollectBounds(); // this is a bottleneck. We need  to update it only if something changes. For now it will be called once on start.
        MortonGenerationBuffersSetup();
        RadixSort();
        BuildTree();
        RefitBVH();
    }

    void CollectBounds()
    {
        Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.InstanceID);

        if (renderers.Length == 0)
        {
            Debug.LogWarning("No renderers found in the scene.");
            return;
        }
        objectCount = renderers.Length;


        primitiveCentroids.Clear();
        primitiveIds.Clear();
        primitiveBounds.Clear();
        sceneBounds = renderers[0].bounds;

        foreach (Renderer renderer in renderers)
        {
            if (renderer.enabled)
            {
                PrimitiveInfo info = new PrimitiveInfo(renderer.GetInstanceID(), renderer.bounds);
                primitiveCentroids.Add(renderer.bounds.center);
                primitiveIds.Add(renderer.GetInstanceID());

                primitiveBounds.Add(renderer.bounds);


                //get scene bounds
                sceneBounds.Encapsulate(renderer.bounds);
            }
        }
    }

    //private void OnDrawGizmos()
    //{
    //    Gizmos.color = new Color(0f, 1f, 0f, 0.1f);
    //    Gizmos.DrawCube(sceneBounds.center, sceneBounds.size);

    //    Gizmos.color = new Color(0f, 1f, 0f, 1f);
    //    Gizmos.DrawWireCube(sceneBounds.center, sceneBounds.size);
    //}

    // https://medium.com/@Ksatese/advanced-ray-tracer-part-2-f5313530581c
    //void BuildTLAS(PrimitiveInfo[] primitives, int startIndex, int endIndex, BVHNode parent)
    //{

    //    if (endIndex - startIndex <= 1)
    //    {
    //        //create leaf node
    //        int primitiveIndex = primitives[startIndex].primitiveIndex;
    //        parent.primitiveIndex = primitiveIndex;
    //        parent.bounds = primitives[startIndex].bounds;
    //        //Debug.Log("Primitive leaf node added on tree");

    //        //var renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.InstanceID).ToList<Renderer>();

    //        //// find the instanceid and return the renderer, only debug
    //        //var renderer = renderers.Find(x => x.GetInstanceID() == primitiveIndex);
    //        //Debug.Log($"renderer's name is: {renderer.name}");
    //        return;
    //    }
    //    else
    //    {
    //        for (int i = startIndex; i < endIndex; i++)
    //        {
    //            parent.bounds.Encapsulate(primitives[i].bounds);
    //        }

    //        Vector3 extents = parent.bounds.max - parent.bounds.min;
    //        int splitAxis = 0;
    //        if (extents.y > extents.x) splitAxis = 1;
    //        if (extents.z > extents[splitAxis]) splitAxis = 2;

    //        //sort the primitives by their center along the axis we want to split so 
    //        // that we now that from start to mid are the left primitives and 
    //        // the rest are the right primitives
    //        Array.Sort(primitives, startIndex, endIndex - startIndex, Comparer<PrimitiveInfo>.Create((a, b) =>
    //        {
    //            float aCentroid = a.bounds.center[splitAxis];
    //            float bCentroid = b.bounds.center[splitAxis];
    //            return aCentroid.CompareTo(bCentroid);
    //        }));

    //        int mid = startIndex + (endIndex - startIndex) / 2;


    //        // run for the left side 
    //        BVHNode left = parent.leftChild = new BVHNode();
    //        // Debug.Log("Going left");
    //        BuildTLAS(primitives, startIndex, mid, left);


    //        // run for the right side
    //        BVHNode right = parent.rightChild = new BVHNode();
    //        //Debug.Log("Going right");
    //        BuildTLAS(primitives, mid, endIndex, right);
    //    }
    //}

    //public int FlattenTLAS(BVHNode node, List<GPUNode> nodeTree, int i)
    //{
    //    if (node == null) return i;

    //    // Get data from the current node
    //    GPUNode gpuNode = new GPUNode
    //    {
    //        aabbMin = node.bounds.min,
    //        aabbMax = node.bounds.max,
    //        primitiveIndex = node.IsLeaf ? node.primitiveIndex : 0,

    //        // siblingIndex = node.rightChild
    //    };
    //    nodeTree[i] = gpuNode;

    //    // Flatten left subtree
    //    i = FlattenTLAS(node.leftChild, nodeTree, i);


    //    // Flatten right subtree
    //    i = FlattenTLAS(node.rightChild, nodeTree, i + 1);
    //    return i;
    //}


    void InitializeBuffers()
    {
        centroidsBuffer = new ComputeBuffer(objectCount, sizeof(float) * 3);
        mortonCodesBuffer = new ComputeBuffer(objectCount, sizeof(uint));
        primitiveIndicesBuffer = new ComputeBuffer(objectCount, sizeof(uint));

        bvhGeneratorShader.SetBuffer(generateMortonCodeKernel, "centroidsBuffer", centroidsBuffer);
        bvhGeneratorShader.SetBuffer(generateMortonCodeKernel, "mortonCodesBuffer", mortonCodesBuffer);
        bvhGeneratorShader.SetBuffer(generateMortonCodeKernel, "primitiveIndicesBuffer", primitiveIndicesBuffer);

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
        primitiveIndicesBuffer.SetData(primitiveIds);

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
            ComputeBuffer tempM = mortonCodesBuffer;
            mortonCodesBuffer = outMortonBuffer;
            outMortonBuffer = tempM;

            ComputeBuffer tempI = primitiveIndicesBuffer;
            primitiveIndicesBuffer = outIndicesBuffer;
            outIndicesBuffer = tempI;
        }
    }

    void InitializeBVHBuffer()
    {
        // Internal nodes: 0 to N-2
        // Leaf nodes: N-1 to 2N-2
        int totalNodes = (objectCount * 2) - 1;
        bvhNodeBuffer = new ComputeBuffer(totalNodes, sizeof(float) * 6 + sizeof(int) * 4);
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

        // 1.Prepare leaf data(Min and Max for each renderer)
            Vector3[] rawBounds = new Vector3[objectCount * 2];

        for (int i = 0; i < objectCount; i++)
        {
            // Use your original primitive list/renderers here
            rawBounds[i * 2] = primitiveBounds[i].min;
            rawBounds[i * 2 + 1] = primitiveBounds[i].max;
        }

        leafBoundsBuffer.SetData(rawBounds);
    }

    void RefitBVH()
    {
       // 1. CLEAR FLAGS (Critical step)
        atomicFlagsBuffer.SetData(zeroFlags);

        // 3. Dispatch
        int groups = Mathf.CeilToInt(objectCount / 64.0f);
        bvhGeneratorShader.SetBuffer(fitKernel, "bvhNodes", bvhNodeBuffer);
        bvhGeneratorShader.SetBuffer(fitKernel, "leafBoundsBuffer", leafBoundsBuffer);
        bvhGeneratorShader.SetBuffer(fitKernel, "atomicFlags", atomicFlagsBuffer);
        bvhGeneratorShader.Dispatch(fitKernel, groups, 1, 1);
    }

    public int debugDepth = 5; // Use a slider to see different levels

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
    }
}