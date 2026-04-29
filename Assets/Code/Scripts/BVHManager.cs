using System.Collections.Generic;
using System.Runtime.InteropServices;
using Code.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;

public class BVHManager : MonoBehaviour
{
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

    //BLAS 
    ComputeBuffer blasNodesBuffer;
    ComputeBuffer trianglesBuffer;

    int objectCount = 0;
    private AcousticBase[] allObjects;
    
    public int bins = 8;
    private bool needsRefit = true;
    
    public bool showDebug;

    public List<GPUBlas> globalBlasNodes = new List<GPUBlas>();
    public List<Triangle> globalTriangleSoup = new List<Triangle>();

    Dictionary<int, BlasMetada> objectMetadata = new Dictionary<int, BlasMetada>();

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
    
    void UpdateBounds()
    {
        bvhGeneratorShader.SetBuffer(generateMortonCodeKernel, "centroidsBuffer", centroidsBuffer);
        bvhGeneratorShader.SetBuffer(generateMortonCodeKernel, "mortonCodesBuffer", mortonCodesBuffer);
        bvhGeneratorShader.SetBuffer(generateMortonCodeKernel, "primitiveIndicesBuffer", primitiveIndicesBuffer);
    }

    public void UpdateBVH()
    {
        if (needsRefit)
        {
            CollectBounds();
            UpdateBounds();
            MortonGenerationBuffersSetup();
            RadixSort();
            BuildTree();
            RefitBVH();

            needsRefit = false;
        }

        for (int i = 0; i < allObjects.Length; i++)
        {
            if (allObjects[i].transform.hasChanged)
            {
                needsRefit = true;
                break;
            }
        }
    }

    private void CollectBounds()
    {
        primitiveCentroids.Clear();
        primitiveIds.Clear();
        primitiveBounds.Clear();
        
        allObjects = ObjectRegistry<AcousticBase>.Instance.GetValues();
        objectCount = allObjects.Length;
        
        sceneBounds = allObjects[0].collider.bounds;

        foreach (AcousticBase obj in allObjects)
        {
            var bounds = obj.collider.bounds;
            primitiveCentroids.Add(bounds.center);
            primitiveIds.Add(obj.);

            primitiveBounds.Add(bounds);

            //get scene bounds
            sceneBounds.Encapsulate(bounds);
            
            obj.transform.hasChanged = false;
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
        if (!showDebug) return;
        if (bvhNodeBuffer == null || objectCount == 0) return;
    
        GPUNode[] nodes = new GPUNode[objectCount * 2 - 1];
        bvhNodeBuffer.GetData(nodes);

        // Recursive draw call
        DrawNode(nodes, 0, 0); // Start at root (index 0)
    }

    // Define a set of distinct colors for levels 0-10+
    private readonly Color[] levelColors = new Color[]
    {
        Color.green, // Level 0 (Root)
        Color.cyan, // Level 1
        Color.yellow, // Level 2
        Color.magenta, // Level 3
        new Color(1f, 0.5f, 0f), // Level 4 (Orange)
        Color.white, // Level 5
        new Color(0.5f, 1f, 0.5f), // Level 6
        new Color(0.5f, 0.5f, 1f), // Level 7
        Color.red // Level 8+ (Usually Leaves/Deepest)
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

            string label = $"ID: {id}";

            UnityEditor.Handles.Label(labelPos, label);
        }
    }
#endif

    public ComputeBuffer GetBVHBuffer()
    {
        return bvhNodeBuffer;
    }

    // public ComputeBuffer GetBLASMegaArray()
    // {
    // }
    
    
    
//-----------------------------------------------------------------------------------
// BLAS Funcitons
//-----------------------------------------------------------------------------------
    
    static readonly ProfilerMarker bvhBuildMarker = new ProfilerMarker("Acoustic.BuildBLAS");

    //REMINDER THAT THIS RUNS ON AWAKE() WHICH IS BEFORE START()!
    public BlasMetada BuildBlas(MeshFilter meshFilter)
    {
        // we check if blas already exists, otherwise we build it
        var key = meshFilter.sharedMesh.name.GetHashCode();
        BlasMetada metadata = new BlasMetada();
        if (objectMetadata.TryGetValue(key, out metadata))
        {
            return metadata;
        }
        
        using (bvhBuildMarker.Auto())
        {
            var sharedMesh = meshFilter.sharedMesh;
            int totalIndices = 0;
            int subMeshCount = sharedMesh.subMeshCount;
            for (int i = 0; i < subMeshCount; i++)
            {
                totalIndices += (int)sharedMesh.GetIndexCount(i);
            }

            int triangleCount = totalIndices / 3;

            var triangles = new NativeArray<Triangle>(triangleCount, Allocator.Persistent);
            var centroids = new NativeArray<float3>(triangleCount, Allocator.Persistent);
            var triangleIndices = new NativeArray<int>(triangleCount, Allocator.Persistent);
            for (int i = 0; i < triangleCount; i++) triangleIndices[i] = i;

            var blas = new NativeArray<GPUBlas>(triangleCount * 2 - 1, Allocator.Persistent);
            NativeArray<int> tempNodesUsed = new NativeArray<int>(1, Allocator.TempJob);
            var meshDataArray = Mesh.AcquireReadOnlyMeshData(sharedMesh);
            var job = new BuildBlasJob
            {
                triangles = triangles,
                centroids = centroids,
                triangleIndices = triangleIndices,
                blas = blas,
                nodesUsed = tempNodesUsed,
                bins = this.bins,
                mesh = meshDataArray[0]
            };

            JobHandle handle = job.Schedule();
            handle.Complete();
            
            metadata = AddToMegaArrays(key, blas.ToArray(), triangles.ToArray());
            
            triangles.Dispose();
            centroids.Dispose();
            blas.Dispose();
            triangleIndices.Dispose();
            meshDataArray.Dispose();
            tempNodesUsed.Dispose();
            
            return metadata;
        }
    }
    
    private BlasMetada AddToMegaArrays(int key, GPUBlas[] blas, Triangle[] triangles)
    {
        int nodeStartOffset = globalBlasNodes.Count;
        int triStartOffset = globalTriangleSoup.Count;
        
        globalTriangleSoup.AddRange(triangles);
        globalBlasNodes.AddRange(blas);

        var metadata = new BlasMetada();
        
        metadata.trianglesOffset = triStartOffset;
        metadata.trianglesCount = triangles.Length;
        
        metadata.blasOffset = nodeStartOffset;
        metadata.blasCount = blas.Length;
        
        //register with the dictionary to guarantee unique values
        objectMetadata.Add(key, metadata);
        
        return metadata;
    }
        //         // IMPORTANT: If 'leftFirst' in your TriangleNode points to a triangle index,
        //         // you MUST add triStartOffset to it so it finds the right triangle in the soup.
        //         AdjustTriangleIndices(nodeStartOffset, obj.blasArray.Length, triStartOffset);
        
    [BurstCompile]
    struct BuildBlasJob : IJob
    {
        public NativeArray<GPUBlas> blas;
        public NativeArray<Triangle> triangles;
        public NativeArray<float3> centroids;
        public NativeArray<int> triangleIndices;
        
        public NativeArray<int> nodesUsed;

        public Mesh.MeshData mesh;
        public int bins;
        
        public void Execute()
        {
            GetTriangles();
            GPUBlas rootNode = blas[0];
            
            rootNode.leftFirst = 0;
            rootNode.triCount = triangles.Length;
            
            blas[0] = rootNode;
            
            nodesUsed[0] = 1;
            
            var cache = new BlasBuildCache();
            cache.Allocate(bins);
            
            UpdateBlasBounds(0);
            SubdivideBlas(0, ref cache);
            
            var sortedTriangles = new NativeArray<Triangle>(triangles.Length, Allocator.Temp);

            for (int i = 0; i < triangleIndices.Length; i++)
            {
                sortedTriangles[i] = triangles[triangleIndices[i]];
            }

            triangles.CopyFrom(sortedTriangles);

            sortedTriangles.Dispose();
            cache.Dispose();
        }
        
        void GetTriangles()
        {
            var vertexData = new NativeArray<float3>(mesh.vertexCount, Allocator.Temp);
            mesh.GetVertices(vertexData.Reinterpret<Vector3>());
            
            if (mesh.indexFormat == UnityEngine.Rendering.IndexFormat.UInt16)
            {
                var indexData = mesh.GetIndexData<ushort>();
                for (int i = 0; i < triangles.Length; i++)
                {
                    ushort i0 = indexData[i * 3];
                    ushort i1 = indexData[i * 3 + 1];
                    ushort i2 = indexData[i * 3 + 2];
            
                    ProcessTriangle(i, i0, i1, i2, vertexData);
                }
            }
            else
            {
                var indexData = mesh.GetIndexData<int>();
                for (int i = 0; i < triangles.Length; i++)
                {
                    int i0 = indexData[i * 3];
                    int i1 = indexData[i * 3 + 1];
                    int i2 = indexData[i * 3 + 2];
            
                    ProcessTriangle(i, i0, i1, i2, vertexData);
                }
            }
            vertexData.Dispose();
        }
        
        void ProcessTriangle(int i, int i0, int i1, int i2, NativeArray<float3> vData)
        {
            float3 v0 = vData[i0];
            float3 v1 = vData[i1];
            float3 v2 = vData[i2];
            triangles[i] = new Triangle { vertexA = v0, vertexB = v1, vertexC = v2, padding = float3.zero };
            centroids[i] = (v0 + v1 + v2) * 0.333333f;
        }

        
        void UpdateBlasBounds(int nodeIdx)
        {
            GPUBlas node = blas[nodeIdx];
            float3 bmin = new float3(1e30f, 1e30f, 1e30f);
            float3 bmax = new float3(-1e30f, -1e30f, -1e30f);
            
            for (int first = node.leftFirst, i = 0; i < node.triCount; i++)
            {
                Triangle leafTri = triangles[triangleIndices[first + i]];
                
                bmin = math.min(bmin, math.min(leafTri.vertexA, math.min(leafTri.vertexB, leafTri.vertexC)));
                bmax = math.max(bmax, math.max(leafTri.vertexA, math.max(leafTri.vertexB, leafTri.vertexC)));
            }
            
            node.aabbMin = bmin;
            node.aabbMax = bmax;
            blas[nodeIdx] = node;
        }

        float FindBestSplitPlane( ref GPUBlas node, ref int axis, ref float splitPos, ref BlasBuildCache cache)
        {
            float bestCost = 1e30f;
            
            for (int a = 0; a < 3; a++)
            {
                float boundsMin = 1e30f, boundsMax = -1e30f;
                for (int i = 0; i < node.triCount; i++)
                {
                    int index = node.leftFirst + i;
                    float centroid = centroids[triangleIndices[index]][a];
                    boundsMin = math.min(boundsMin, centroid);
                    boundsMax = math.max( boundsMax, centroid);
                }
                if (boundsMin == boundsMax) continue;
                
                for (int i = 0; i < bins; i++) 
                {
                    var b = cache.bins[i];
                    b.triCount = 0;
                    b.bounds = aabb.Empty();
                    cache.bins[i] = b;
                }
                
                float scale = bins / (boundsMax - boundsMin);
                for (int i = 0; i < node.triCount; i++)
                {
                    int index = triangleIndices[node.leftFirst + i];
                    Triangle triangle = triangles[index];
                    int binIdx = math.min( bins - 1, (int)((centroids[index][a] - boundsMin) * scale) );

                    var cacheBin = cache.bins[binIdx];
                    cacheBin.triCount++;
                    cacheBin.bounds.Grow( triangle.vertexA );
                    cacheBin.bounds.Grow( triangle.vertexB );
                    cacheBin.bounds.Grow( triangle.vertexC );

                    cache.bins[binIdx] = cacheBin;
                }
                aabb leftBox = aabb.Empty();
                aabb rightBox = aabb.Empty();
                int leftSum = 0, rightSum = 0;
                for (int i = 0; i < bins - 1; i++)
                {
                    leftSum += cache.bins[i].triCount;
                    cache.leftCount[i] = leftSum;
                    leftBox.Grow(cache.bins[i].bounds);
                    cache.leftArea[i] = leftBox.Area();
                    rightSum += cache.bins[bins - 1 - i].triCount;
                    cache.rightCount[bins - 2 - i] = rightSum;
                    rightBox.Grow( cache.bins[bins - 1 - i].bounds );
                    cache.rightArea[bins - 2 - i] = rightBox.Area();
                }
                
                scale = (boundsMax - boundsMin) / bins;
                for (int i = 0; i < bins - 1; i++)
                {
                    float planeCost = cache.leftCount[i] * cache.leftArea[i] + cache.rightCount[i] * cache.rightArea[i];
                    if (planeCost < bestCost)
                    {
                        axis = a;
                        splitPos = boundsMin + scale * (i + 1);
                        bestCost = planeCost;
                    }
                        
                }
            }
            return bestCost;
        }

        float CalculateNodeCost( ref GPUBlas node )
        {
            float3 e = node.aabbMax - node.aabbMin;
            float surfaceArea = e.x * e.y + e.y * e.z + e.z * e.x;
            return node.triCount * surfaceArea;
        }

        void SubdivideBlas(int nodeIdx, ref BlasBuildCache cache)
        {
            GPUBlas node = blas[nodeIdx];
            int axis = 0;
            float splitPos = 0;
            float splitCost = FindBestSplitPlane( ref node, ref axis, ref splitPos, ref cache);
            float nosplitCost = CalculateNodeCost( ref node );
            
            if (splitCost >= nosplitCost) return;
            
            // in-place partition
            int i = blas[nodeIdx].leftFirst;
            int j = i + blas[nodeIdx].triCount - 1;
            while (i <= j)
            {
                if (centroids[triangleIndices[i]][axis] < splitPos)
                    i++;
                else
                {
                    (triangleIndices[i], triangleIndices[j]) = (triangleIndices[j], triangleIndices[i]);
                    j--;
                }
            }
            
            // abort split if one of the sides is empty
            int leftCount = i - blas[nodeIdx].leftFirst;
            if (leftCount == 0 || leftCount == blas[nodeIdx].triCount) return;
            
            // create child nodes
            int leftChildIdx = nodesUsed[0]++;
            int rightChildIdx = nodesUsed[0]++;
            
            GPUBlas leftChild = new GPUBlas {
                leftFirst = node.leftFirst,
                triCount = leftCount
            };

            GPUBlas rightChild = new GPUBlas {
                leftFirst = i,
                triCount = node.triCount - leftCount
            };
            
            node.leftFirst = leftChildIdx;
            node.triCount = 0;
            
            blas[nodeIdx] = node;
            blas[leftChildIdx] = leftChild;
            blas[rightChildIdx] = rightChild;
            
            UpdateBlasBounds( leftChildIdx);
            UpdateBlasBounds( rightChildIdx);
            // recurse
            SubdivideBlas( leftChildIdx, ref cache);
            SubdivideBlas( rightChildIdx, ref cache);
        }
    };
    
    struct aabb
    {
        public static aabb Empty()
        {
            return new aabb
            {
                bmin = new float3(1e30f, 1e30f, 1e30f),
                bmax = new float3(-1e30f, -1e30f, -1e30f)
            };
        }

        private float3 bmin;
        private float3 bmax;
        public void Grow( float3 p ) { bmin = math.min( bmin, p ); bmax = math.max( bmax, p ); }
        public void Grow( in aabb b ) { if (b.bmin.x != 1e30f) { Grow( b.bmin ); Grow( b.bmax ); } }
        public float Area()
        {
            float3 e = bmax - bmin; // box extent
            return e.x * e.y + e.y * e.z + e.z * e.x;
        }
    };
    
    struct Bin { 
        public aabb bounds;
        public int triCount;
    };
    
    struct BlasBuildCache
    {
        public NativeArray<Bin> bins;
        public NativeArray<float> leftArea;
        public NativeArray<float> rightArea;
        public NativeArray<int> leftCount;
        public NativeArray<int> rightCount;

        public void Allocate(int binCount)
        {
            bins = new NativeArray<Bin>(binCount, Allocator.Temp);
            leftArea = new NativeArray<float>(binCount - 1, Allocator.Temp);
            rightArea = new NativeArray<float>(binCount - 1, Allocator.Temp);
            leftCount = new NativeArray<int>(binCount - 1, Allocator.Temp);
            rightCount = new NativeArray<int>(binCount - 1, Allocator.Temp);
        }

        public void Dispose()
        {
            bins.Dispose();
            leftArea.Dispose();
            rightArea.Dispose();
            leftCount.Dispose();
            rightCount.Dispose();
        }
    }

    public ComputeBuffer GetTrianglesBuffer()
    {
        throw new System.NotImplementedException();
    }

    public ComputeBuffer GetBlasNodesBuffer()
    {
        throw new System.NotImplementedException();
    }
}