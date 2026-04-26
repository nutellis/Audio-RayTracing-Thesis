using System;
using Code.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;

public class AcousticObject : AcousticBase
{
    public AcousticMaterial acousticMaterial;
    public int bins = 8;
    private NativeArray<GPUBlas> blas;
    private NativeArray<Triangle> triangles;
    private NativeArray<float3> centroids;
    private NativeArray<int> triangleIndices;
    private NativeArray<int> nodesUsed;

    public GPUBlas[] blasArray;

    protected override void Awake()
    {
        base.Awake();
        var meshFilter = gameObject.GetComponent<MeshFilter>();

        if (!meshFilter) return;
        
       var key = meshFilter.sharedMesh.name.GetHashCode();
 
       GPUBlas[] existingBlas = ObjectRegistry<GPUBlas[]>.Instance.GetObject(key);
        if (existingBlas != null)
        {
            Debug.LogWarning($"BLAS for object {name} already exists in registry.");
            blasArray = existingBlas;
        }
        else
        {
            BuildBlas(meshFilter);
            ObjectRegistry<GPUBlas[]>.Instance.RegisterObject(key, blasArray);
            
            Debug.LogWarning($"BLAS for object {name} added in registry.");
        }

    }


    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    { 
    }

    // Update is called once per frame
    void Update()
    {

    }

    private void OnDestroy()
    {
        if (triangles.IsCreated) triangles.Dispose();
        if (centroids.IsCreated) centroids.Dispose();
        if (blas.IsCreated) blas.Dispose();
        if (triangleIndices.IsCreated) triangleIndices.Dispose();
        if (nodesUsed.IsCreated) nodesUsed.Dispose();
    }

//-----------------------------------------------------------------------------------
// BLAS Funcitons
//-----------------------------------------------------------------------------------
    [BurstCompile]
    struct BuildBlasJob : IJob
    {
        public NativeArray<GPUBlas> blas;
        public NativeArray<AcousticObject.Triangle> triangles;
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
            triangles[i] = new Triangle { vertexA = v0, vertexB = v1, vertexC = v2 };
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
    
    
    public struct Triangle
    {
        public float3 vertexA;
        public float3 vertexB;
        public float3 vertexC;
    }
    
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

    static readonly ProfilerMarker bvhBuildMarker = new ProfilerMarker("Acoustic.BuildBLAS");
    static readonly ProfilerMarker dataPrepMarker = new ProfilerMarker("Acoustic.DataPrep");
    public void BuildBlas(MeshFilter meshFilter)
    {
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

            triangles = new NativeArray<Triangle>(triangleCount, Allocator.Persistent);
            centroids = new NativeArray<float3>(triangleCount, Allocator.Persistent);
            triangleIndices = new NativeArray<int>(triangleCount, Allocator.Persistent);
            for (int i = 0; i < triangleCount; i++) triangleIndices[i] = i;

            blas = new NativeArray<GPUBlas>(triangleCount * 2 - 1, Allocator.Persistent);
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

            meshDataArray.Dispose();
            tempNodesUsed.Dispose();
        }
        
        //sort the blas

        blasArray = blas.ToArray();
    }
    
    
    
    [Range(0, 32)] public int minDepth = 0;
    [Range(0, 32)] public int maxDepth = 20;
    public bool showDebug;
    private void OnDrawGizmosSelected()
    {
        if (!showDebug) return;
        if (blasArray == null || blasArray.Length == 0) return;
        DrawNode(0, 0);
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
