using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Profiling;

namespace ControlledDemolition.Geometry
{
    // Helper class to manage SliceJobResult disposal via reference counting
    public class SliceResultReference
    {
        public SliceMesh.SliceJobResult Result;
        private int _refCount;

        public SliceResultReference(SliceMesh.SliceJobResult result, int initialRefCount = 2)
        {
            Result = result;
            _refCount = initialRefCount;
        }

        // Returns true if disposal happened
        public bool DecrementAndDisposeIfZero()
        {
            // Thread-safe decrement and check
            if (Interlocked.Decrement(ref _refCount) == 0)
            {
                if (Result.IsValid) // Check IsValid before disposing
                {
                    Result.Dispose();
                }
                return true;
            }
            return false;
        }

        // Safety mechanism in case something goes wrong and ref count doesn't reach zero
        ~SliceResultReference()
        {
            // If this finalizer runs and the result is still valid, it means
            // DecrementAndDisposeIfZero was likely not called enough times.
            // Log an error and attempt disposal.
            if (Result.IsValid)
            {
                Debug.LogError("SliceResultReference finalizer called but Result was still valid. Potential leak! Forcing Dispose.");
                Result.Dispose();
            }
        }
    }


    [BurstCompile]
    public static class SliceMesh
    {
        // Profiling Markers
        private static readonly ProfilerMarker k_SliceAsyncMarker = new ProfilerMarker("SliceMesh.SliceAsync");
        private static readonly ProfilerMarker k_CreateSingleMeshHalfMarker = new ProfilerMarker("SliceMesh.CreateSingleMeshHalf");
        private static readonly ProfilerMarker k_ScheduleSliceJobMarker = new ProfilerMarker("SliceMesh.ScheduleSliceJob");
        private static readonly ProfilerMarker k_CreateMeshInternalMarker = new ProfilerMarker("SliceMesh.CreateMeshFromNativeListsInternal");

        private const float Epsilon = 1e-5f;

        // Public Structs

        // Holds the results of a SliceJob, including the handle and data lists
        public struct SliceJobResult : IDisposable
        {
            public JobHandle Handle;
            public bool IsValid;
            public bool HasUVs;
            public bool HasNormals;
            public string OriginalMeshName;

            // Output Data Lists (Owned by this struct)
            public NativeList<float3> PositiveVertices;
            public NativeList<int> PositiveTriangles;
            public NativeList<float2> PositiveUVs;
            public NativeList<float3> PositiveNormals;
            public NativeList<float3> NegativeVertices;
            public NativeList<int> NegativeTriangles;
            public NativeList<float2> NegativeUVs;
            public NativeList<float3> NegativeNormals;

            // Intermediate Data (Owned by this struct)
            public NativeList<float3> CutVerticesPositions;
            public NativeArray<int> PositiveIndexMap;
            public NativeArray<int> NegativeIndexMap;
            public NativeArray<float> VertexDistances;
            public NativeArray<byte> VertexAbovePlane;

            // Input Data Copies (Owned by this struct)
            public NativeArray<Vector3> InputVertices;
            public NativeArray<int> InputTriangles;
            public NativeArray<Vector2> InputUVs;     // Default if not present
            public NativeArray<Vector3> InputNormals;


            // Dispose ALL native collections associated with this result
            public void Dispose()
            {
                if (!IsValid) return;

                // Output
                if (PositiveVertices.IsCreated) PositiveVertices.Dispose();
                if (PositiveTriangles.IsCreated) PositiveTriangles.Dispose();
                if (HasUVs && PositiveUVs.IsCreated) PositiveUVs.Dispose();
                if (HasNormals && PositiveNormals.IsCreated) PositiveNormals.Dispose();
                if (NegativeVertices.IsCreated) NegativeVertices.Dispose();
                if (NegativeTriangles.IsCreated) NegativeTriangles.Dispose();
                if (HasUVs && NegativeUVs.IsCreated) NegativeUVs.Dispose();
                if (HasNormals && NegativeNormals.IsCreated) NegativeNormals.Dispose();

                // Intermediate
                if (CutVerticesPositions.IsCreated) CutVerticesPositions.Dispose();
                if (PositiveIndexMap.IsCreated) PositiveIndexMap.Dispose();
                if (NegativeIndexMap.IsCreated) NegativeIndexMap.Dispose();
                if (VertexDistances.IsCreated) VertexDistances.Dispose();
                if (VertexAbovePlane.IsCreated) VertexAbovePlane.Dispose();

                // Input Copies
                if (InputVertices.IsCreated) InputVertices.Dispose();
                if (InputTriangles.IsCreated) InputTriangles.Dispose();
                if (HasUVs && InputUVs.IsCreated) InputUVs.Dispose();
                if (HasNormals && InputNormals.IsCreated) InputNormals.Dispose();

                IsValid = false;
            }

            // Helper to create the final meshes after the job handle is completed
            // (Primarily for testing/old path, iterative approach uses CreateSingleMeshHalf)
            public (Mesh, Mesh) CreateMeshes()
            {
                if (!IsValid) return (null, null);

                // Call the internal static helper method from SliceMesh class
                Mesh positiveMesh = SliceMesh.CreateMeshFromNativeListsInternal(PositiveVertices, PositiveTriangles, PositiveUVs, PositiveNormals, OriginalMeshName + "_Positive", HasUVs, HasNormals);
                Mesh negativeMesh = SliceMesh.CreateMeshFromNativeListsInternal(NegativeVertices, NegativeTriangles, NegativeUVs, NegativeNormals, OriginalMeshName + "_Negative", HasUVs, HasNormals);
                return (positiveMesh, negativeMesh);
            }
        }


        // Private Structs

        [BurstCompile]
        private struct VertexData
        {
            public float3 Position;
            public float2 UV;
            public float3 Normal;
            public int OriginalIndex; // -1 for new vertices
        }

        // Creates a Burst-compatible plane representation (Ax + By + Cz + D = 0)
        public static float4 CreatePlane(float3 normal, float3 point)
        {
            normal = math.normalize(normal);
            float distance = -math.dot(normal, point);
            return new float4(normal, distance);
        }

        // Calculates distance from a point to the Burst-compatible plane
        public static float GetDistanceToPoint(float4 plane, float3 point)
        {
            return math.dot(plane.xyz, point) + plane.w;
        }

        // Public Methods

        // Schedules the job for async handling. Accepts LOCAL space plane.
        public static SliceJobResult SliceAsync(Mesh inputMesh, float4 cutPlaneLocal)
        {
            using (k_SliceAsyncMarker.Auto())
            {
                return ScheduleSliceJob(inputMesh, cutPlaneLocal);
            }
        }

        // Creates a single mesh half from a completed SliceJobResult.
        // Does NOT dispose the SliceJobResult's native collections.
        public static Mesh CreateSingleMeshHalf(SliceJobResult result, bool isPositiveSide)
        {
            using (k_CreateSingleMeshHalfMarker.Auto())
            {
                if (!result.IsValid)
                {
                    Debug.LogError("Cannot create mesh half from invalid SliceJobResult.");
                    return null;
                }

                // Select the correct data lists
                NativeList<float3> vertices = isPositiveSide ? result.PositiveVertices : result.NegativeVertices;
                NativeList<int> triangles = isPositiveSide ? result.PositiveTriangles : result.NegativeTriangles;
                NativeList<float2> uvs = isPositiveSide ? result.PositiveUVs : result.NegativeUVs;
                NativeList<float3> normals = isPositiveSide ? result.PositiveNormals : result.NegativeNormals;
                string nameSuffix = isPositiveSide ? "_Positive" : "_Negative";

                // Call the internal helper
                return CreateMeshFromNativeListsInternal(vertices, triangles, uvs, normals, result.OriginalMeshName + nameSuffix, result.HasUVs, result.HasNormals);
            }
        }

        // Private Methods

        // Schedules the SliceJob. Accepts LOCAL space plane.
        private static SliceJobResult ScheduleSliceJob(Mesh inputMesh, float4 cutPlaneLocal)
        {
            using (k_ScheduleSliceJobMarker.Auto())
            {
                var result = new SliceJobResult { IsValid = false, OriginalMeshName = inputMesh != null ? inputMesh.name : "NullMesh" };

                if (inputMesh == null)
                {
                    Debug.LogError("Input mesh cannot be null.");
                    return result;
                }

                // Use Allocator.Persistent for job data managed by SliceJobResult.

                // Get Original Mesh Data using Mesh.AcquireReadOnlyMeshData (avoids GC)
                Mesh.MeshDataArray meshDataArray;
                try
                {
                    meshDataArray = Mesh.AcquireReadOnlyMeshData(inputMesh);
                }
                catch (Exception e)
                {
                    Debug.LogError($"Failed to acquire mesh data for {inputMesh.name}: {e.Message}");
                    return result;
                }

                Mesh.MeshData meshData = meshDataArray[0];

                int vertexCount = meshData.vertexCount;
                int triangleIndexCount = (int)meshData.GetSubMesh(0).indexCount;

                if (vertexCount == 0 || triangleIndexCount == 0)
                {
                    Debug.LogWarning($"Input mesh {inputMesh.name} has no vertices or triangles.");
                    meshDataArray.Dispose();
                    return result;
                }

                // Allocate Persistent NativeArrays for the job (using UnityEngine types for MeshData compatibility)
                var originalVertices = new NativeArray<Vector3>(vertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                var originalTriangles = new NativeArray<int>(triangleIndexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

                bool hasUVs = meshData.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0);
                var originalUVs = hasUVs
                    ? new NativeArray<Vector2>(vertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory)
                    : default;

                bool hasNormals = meshData.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Normal);
                var originalNormals = hasNormals
                    ? new NativeArray<Vector3>(vertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory)
                    : default;

                // Copy data from MeshData to Persistent NativeArrays
                meshData.GetVertices(originalVertices);
                // Handle 16-bit vs 32-bit indices
                if (meshData.indexFormat == UnityEngine.Rendering.IndexFormat.UInt16)
                {
                    NativeArray<ushort> tempIndices16 = meshData.GetIndexData<ushort>();
                    for (int i = 0; i < triangleIndexCount; ++i) originalTriangles[i] = tempIndices16[i];
                }
                else
                {
                    meshData.GetIndices(originalTriangles, 0);
                }

                if (hasUVs) meshData.GetUVs(0, originalUVs);
                if (hasNormals) meshData.GetNormals(originalNormals);

                // Dispose the acquired mesh data
                meshDataArray.Dispose();


                // Initialize Output Data Structures (Allocator.Persistent)
                int initialCapacityVerts = vertexCount / 2 + 10;
                int initialCapacityTris = triangleIndexCount / 2 + 10;

                result.PositiveVertices = new NativeList<float3>(initialCapacityVerts, Allocator.Persistent);
                result.PositiveTriangles = new NativeList<int>(initialCapacityTris, Allocator.Persistent);
                result.PositiveUVs = hasUVs ? new NativeList<float2>(initialCapacityVerts, Allocator.Persistent) : default;
                result.PositiveNormals = hasNormals ? new NativeList<float3>(initialCapacityVerts, Allocator.Persistent) : default;

                result.NegativeVertices = new NativeList<float3>(initialCapacityVerts, Allocator.Persistent);
                result.NegativeTriangles = new NativeList<int>(initialCapacityTris, Allocator.Persistent);
                result.NegativeUVs = hasUVs ? new NativeList<float2>(initialCapacityVerts, Allocator.Persistent) : default;
                result.NegativeNormals = hasNormals ? new NativeList<float3>(initialCapacityVerts, Allocator.Persistent) : default;

                // Intermediate data (Allocator.Persistent)
                result.CutVerticesPositions = new NativeList<float3>(64, Allocator.Persistent);
                result.PositiveIndexMap = new NativeArray<int>(originalVertices.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                result.NegativeIndexMap = new NativeArray<int>(originalVertices.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                result.VertexDistances = new NativeArray<float>(originalVertices.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                result.VertexAbovePlane = new NativeArray<byte>(originalVertices.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

                // Setup Job
                var sliceJob = new SliceJob
                {
                    // Input (Read Only - copies owned by result)
                    CutPlaneLocal = cutPlaneLocal,
                    HasUVs = hasUVs,
                    HasNormals = hasNormals,
                    // Burst handles reinterpret from UnityEngine types to Unity.Mathematics types
                    OriginalVertices = originalVertices.Reinterpret<float3>(),
                    OriginalTriangles = originalTriangles,
                    OriginalUVs = hasUVs ? originalUVs.Reinterpret<float2>() : default,
                    OriginalNormals = hasNormals ? originalNormals.Reinterpret<float3>() : default,
                    // Writable Input/Output (Owned by result)
                    VertexAbovePlane = result.VertexAbovePlane,
                    VertexDistances = result.VertexDistances,
                    PositiveIndexMap = result.PositiveIndexMap,
                    NegativeIndexMap = result.NegativeIndexMap,
                    // Output Lists (Owned by result)
                    PositiveVertices = result.PositiveVertices,
                    PositiveTriangles = result.PositiveTriangles,
                    PositiveUVs = result.PositiveUVs,
                    PositiveNormals = result.PositiveNormals,
                    NegativeVertices = result.NegativeVertices,
                    NegativeTriangles = result.NegativeTriangles,
                    NegativeUVs = result.NegativeUVs,
                    NegativeNormals = result.NegativeNormals,
                    CutVerticesPositions = result.CutVerticesPositions
                };

                // Assign input arrays to result for later disposal
                result.InputVertices = originalVertices;
                result.InputTriangles = originalTriangles;
                result.InputUVs = originalUVs;
                result.InputNormals = originalNormals;

                // Schedule
                result.Handle = sliceJob.Schedule();
                result.IsValid = true;
                result.HasUVs = hasUVs;
                result.HasNormals = hasNormals;

                return result;
            }
        }


        // Private Structs

        [BurstCompile]
        private struct SliceJob : IJob
        {
            // Input Data (Read Only)
            [ReadOnly] public float4 CutPlaneLocal;
            [ReadOnly] public bool HasUVs;
            [ReadOnly] public bool HasNormals;
            // Input arrays owned by SliceJobResult
            [ReadOnly] public NativeArray<float3> OriginalVertices;
            [ReadOnly] public NativeArray<int> OriginalTriangles;
            [ReadOnly] public NativeArray<float2> OriginalUVs;
            [ReadOnly] public NativeArray<float3> OriginalNormals;

            // Writable Input/Output Data (Owned by SliceJobResult)
            public NativeArray<float> VertexDistances;
            public NativeArray<byte> VertexAbovePlane;
            public NativeArray<int> PositiveIndexMap;
            public NativeArray<int> NegativeIndexMap;

            // Output Data (Owned by SliceJobResult, disposed there)
            public NativeList<float3> PositiveVertices;
            public NativeList<int> PositiveTriangles;
            public NativeList<float2> PositiveUVs;
            public NativeList<float3> PositiveNormals;
            public NativeList<float3> NegativeVertices;
            public NativeList<int> NegativeTriangles;
            public NativeList<float2> NegativeUVs;
            public NativeList<float3> NegativeNormals;
            public NativeList<float3> CutVerticesPositions;

            public void Execute()
            {
                if (!OriginalVertices.IsCreated || !OriginalTriangles.IsCreated) return;

                // 1. Classify Vertices relative to the LOCAL space plane
                for (int i = 0; i < OriginalVertices.Length; i++)
                {
                    VertexDistances[i] = GetDistanceToPointInternal(CutPlaneLocal, OriginalVertices[i]);
                    VertexAbovePlane[i] = (VertexDistances[i] >= -Epsilon) ? (byte)1 : (byte)0;
                    PositiveIndexMap[i] = -1;
                    NegativeIndexMap[i] = -1;
                }

                // 2. Process Triangles
                for (int i = 0; i < OriginalTriangles.Length; i += 3)
                {
                    int index0 = OriginalTriangles[i];
                    int index1 = OriginalTriangles[i + 1];
                    int index2 = OriginalTriangles[i + 2];

                    // Get original LOCAL space vertex data
                    VertexData v0_local = new VertexData { Position = OriginalVertices[index0], UV = HasUVs ? OriginalUVs[index0] : float2.zero, Normal = HasNormals ? OriginalNormals[index0] : float3.zero, OriginalIndex = index0 };
                    VertexData v1_local = new VertexData { Position = OriginalVertices[index1], UV = HasUVs ? OriginalUVs[index1] : float2.zero, Normal = HasNormals ? OriginalNormals[index1] : float3.zero, OriginalIndex = index1 };
                    VertexData v2_local = new VertexData { Position = OriginalVertices[index2], UV = HasUVs ? OriginalUVs[index2] : float2.zero, Normal = HasNormals ? OriginalNormals[index2] : float3.zero, OriginalIndex = index2 };

                    byte side0 = VertexAbovePlane[index0];
                    byte side1 = VertexAbovePlane[index1];
                    byte side2 = VertexAbovePlane[index2];

                    // Case 1: All positive
                    if (side0 == 1 && side1 == 1 && side2 == 1)
                    {
                        AddTriangleJob(PositiveVertices, PositiveTriangles, PositiveUVs, PositiveNormals, PositiveIndexMap, v0_local, v1_local, v2_local);
                    }
                    // Case 2: All negative
                    else if (side0 == 0 && side1 == 0 && side2 == 0)
                    {
                        AddTriangleJob(NegativeVertices, NegativeTriangles, NegativeUVs, NegativeNormals, NegativeIndexMap, v0_local, v1_local, v2_local);
                    }
                    // Case 3: Intersects plane
                    else
                    {
                        SliceTriangleJob(v0_local, v1_local, v2_local, side0, side1, side2);
                    }
                }

                // 3. Create Cap Faces
                if (CutVerticesPositions.Length > 0)
                {
                    CreateCapFacesJob();
                }
            }

            // Job-internal Helper Functions

            private static float GetDistanceToPointInternal(float4 plane, float3 point)
            {
                return math.dot(plane.xyz, point) + plane.w;
            }

            // Adds a vertex to the specified mesh side if it doesn't exist, returning its index.
            private int AddVertexJob(
                NativeList<float3> vertices, NativeList<float2> uvs, NativeList<float3> normals,
                NativeArray<int> indexMap, VertexData v)
            {
                // If it's an original vertex and already added, return existing index
                if (v.OriginalIndex != -1 && indexMap[v.OriginalIndex] != -1)
                {
                    return indexMap[v.OriginalIndex];
                }

                // Otherwise, add as a new vertex
                int newIndex = vertices.Length;
                vertices.Add(v.Position);
                if (HasUVs) uvs.Add(v.UV);
                if (HasNormals) normals.Add(v.Normal); // Add local normal

                // If it was an original vertex, store its new index
                if (v.OriginalIndex != -1)
                {
                    indexMap[v.OriginalIndex] = newIndex;
                }
                return newIndex;
            }

            // Adds a triangle using AddVertexJob for vertex reuse/mapping.
            private void AddTriangleJob(
                NativeList<float3> vertices, NativeList<int> triangles, NativeList<float2> uvs, NativeList<float3> normals,
                NativeArray<int> indexMap, VertexData v0, VertexData v1, VertexData v2)
            {
                int newIndex0 = AddVertexJob(vertices, uvs, normals, indexMap, v0);
                int newIndex1 = AddVertexJob(vertices, uvs, normals, indexMap, v1);
                int newIndex2 = AddVertexJob(vertices, uvs, normals, indexMap, v2);
                triangles.Add(newIndex0);
                triangles.Add(newIndex1);
                triangles.Add(newIndex2);
            }

            // Slices a triangle intersecting the plane. Accepts LOCAL space vertices/sides.
            private void SliceTriangleJob(VertexData v0_local, VertexData v1_local, VertexData v2_local, byte side0, byte side1, byte side2)
            {
                // Identify the vertex that is alone on one side
                VertexData loneVertex_local;
                VertexData edgeVertex1_local, edgeVertex2_local;
                bool loneVertexIsPositive = false;

                if (side0 != side1 && side0 != side2) { loneVertex_local = v0_local; edgeVertex1_local = v1_local; edgeVertex2_local = v2_local; loneVertexIsPositive = (side0 == 1); }
                else if (side1 != side0 && side1 != side2) { loneVertex_local = v1_local; edgeVertex1_local = v0_local; edgeVertex2_local = v2_local; loneVertexIsPositive = (side1 == 1); }
                else { loneVertex_local = v2_local; edgeVertex1_local = v0_local; edgeVertex2_local = v1_local; loneVertexIsPositive = (side2 == 1); }

                // Calculate intersection points in LOCAL space
                VertexData intersection1_local = GetIntersectionVertex(CutPlaneLocal, loneVertex_local, edgeVertex1_local);
                VertexData intersection2_local = GetIntersectionVertex(CutPlaneLocal, loneVertex_local, edgeVertex2_local);

                // Add LOCAL intersection positions for cap generation
                if (loneVertexIsPositive)
                {
                    CutVerticesPositions.Add(intersection1_local.Position);
                    CutVerticesPositions.Add(intersection2_local.Position);
                }
                else
                {
                    CutVerticesPositions.Add(intersection2_local.Position);
                    CutVerticesPositions.Add(intersection1_local.Position);
                }

                // Add new triangles using LOCAL space vertices
                if (loneVertexIsPositive) // 1 pos, 2 neg
                {
                    AddTriangleJob(PositiveVertices, PositiveTriangles, PositiveUVs, PositiveNormals, PositiveIndexMap,
                                   loneVertex_local, intersection1_local, intersection2_local);

                    AddTriangleJob(NegativeVertices, NegativeTriangles, NegativeUVs, NegativeNormals, NegativeIndexMap,
                                   edgeVertex1_local, intersection1_local, edgeVertex2_local);
                    AddTriangleJob(NegativeVertices, NegativeTriangles, NegativeUVs, NegativeNormals, NegativeIndexMap,
                                   intersection1_local, intersection2_local, edgeVertex2_local);
                }
                else // 1 neg, 2 pos
                {
                    AddTriangleJob(NegativeVertices, NegativeTriangles, NegativeUVs, NegativeNormals, NegativeIndexMap,
                                   loneVertex_local, intersection2_local, intersection1_local); // Winding

                    AddTriangleJob(PositiveVertices, PositiveTriangles, PositiveUVs, PositiveNormals, PositiveIndexMap,
                                   edgeVertex1_local, edgeVertex2_local, intersection1_local); // Winding
                    AddTriangleJob(PositiveVertices, PositiveTriangles, PositiveUVs, PositiveNormals, PositiveIndexMap,
                                   intersection1_local, edgeVertex2_local, intersection2_local);
                }
            }

            // Creates cap faces using collected LOCAL intersection points.
            private void CreateCapFacesJob()
            {
                if (CutVerticesPositions.Length < 3) return;

                // Simple center point calculation (LOCAL space) for fan triangulation
                // Assumes somewhat convex cut polygon. Robust triangulation is complex.
                float3 center_local = float3.zero;
                for (int i = 0; i < CutVerticesPositions.Length; ++i) center_local += CutVerticesPositions[i];
                if (CutVerticesPositions.Length > 0) center_local /= CutVerticesPositions.Length;

                // Cap face normals are derived from the LOCAL plane normal
                float3 planeNormal_local = CutPlaneLocal.xyz;
                float3 capNormalPos_local = -planeNormal_local;
                float3 capNormalNeg_local = planeNormal_local;

                // Calculate UV projection axes based on LOCAL plane normal
                float3 uAxis_local, vAxis_local;
                if (math.abs(planeNormal_local.x) > math.abs(planeNormal_local.y)) uAxis_local = math.normalize(new float3(-planeNormal_local.z, 0, planeNormal_local.x));
                else uAxis_local = math.normalize(new float3(0, planeNormal_local.z, -planeNormal_local.y));
                vAxis_local = math.cross(planeNormal_local, uAxis_local);
                float uvScale = 1.0f;

                int firstPosIndex = -1, firstNegIndex = -1;
                // Temp arrays for cap vertex indices
                NativeArray<int> capIndicesPos = new NativeArray<int>(CutVerticesPositions.Length, Allocator.Temp);
                NativeArray<int> capIndicesNeg = new NativeArray<int>(CutVerticesPositions.Length, Allocator.Temp);

                // Add distinct vertices (LOCAL space) for the cap face to both sides
                for (int i = 0; i < CutVerticesPositions.Length; i++)
                {
                    float3 vertPos_local = CutVerticesPositions[i];
                    // Calculate UVs based on LOCAL space projection
                    float2 uv = new float2(math.dot(vertPos_local, uAxis_local), math.dot(vertPos_local, vAxis_local)) * uvScale;

                    // Create NEW VertexData for cap vertices (OriginalIndex = -1) using LOCAL space data
                    VertexData capVertexPosData = new VertexData { Position = vertPos_local, UV = uv, Normal = capNormalPos_local, OriginalIndex = -1 };
                    VertexData capVertexNegData = new VertexData { Position = vertPos_local, UV = uv, Normal = capNormalNeg_local, OriginalIndex = -1 };

                    // AddVertexJob uses LOCAL space data
                    int posIndex = AddVertexJob(PositiveVertices, PositiveUVs, PositiveNormals, PositiveIndexMap, capVertexPosData);
                    int negIndex = AddVertexJob(NegativeVertices, NegativeUVs, NegativeNormals, NegativeIndexMap, capVertexNegData);

                    capIndicesPos[i] = posIndex;
                    capIndicesNeg[i] = negIndex;

                    if (i == 0) { firstPosIndex = posIndex; firstNegIndex = negIndex; }
                }

                // Create triangles (simple fan triangulation)
                for (int i = 1; i < CutVerticesPositions.Length - 1; i++)
                {
                    // Positive Cap Face
                    PositiveTriangles.Add(firstPosIndex);
                    PositiveTriangles.Add(capIndicesPos[i + 1]);
                    PositiveTriangles.Add(capIndicesPos[i]);

                    // Negative Cap Face
                    NegativeTriangles.Add(firstNegIndex);
                    NegativeTriangles.Add(capIndicesNeg[i]);
                    NegativeTriangles.Add(capIndicesNeg[i + 1]);
                }

                capIndicesPos.Dispose();
                capIndicesNeg.Dispose();
            }
        }

        // Calculates intersection of an edge with the LOCAL space plane.
        // Returns new VertexData with interpolated attributes in LOCAL space.
        private static VertexData GetIntersectionVertex(float4 plane_local, VertexData vStart_local, VertexData vEnd_local)
        {
            // Calculations are entirely in local space
            float3 edgeDirection_local = vEnd_local.Position - vStart_local.Position;
            float edgeLength_local = math.length(edgeDirection_local);
            if (edgeLength_local < Epsilon) return vStart_local;

            float3 edgeDirNormalized_local = edgeDirection_local / edgeLength_local;
            float denominator = math.dot(plane_local.xyz, edgeDirNormalized_local);

            // Check if edge is parallel to plane
            if (math.abs(denominator) < Epsilon)
            {
                // Return closer vertex
                float distStart_local = math.abs(GetDistanceToPoint(plane_local, vStart_local.Position));
                float distEnd_local = math.abs(GetDistanceToPoint(plane_local, vEnd_local.Position));
                return distStart_local < distEnd_local ? vStart_local : vEnd_local;
            }

            // Calculate intersection factor 't'
            float t_local = -(GetDistanceToPoint(plane_local, vStart_local.Position)) / denominator;
            t_local = math.clamp(t_local, 0f, edgeLength_local);
            float interpolationFactor = edgeLength_local > Epsilon ? t_local / edgeLength_local : 0f;

            // Interpolate attributes in LOCAL space
            float3 intersectionPos_local = math.lerp(vStart_local.Position, vEnd_local.Position, interpolationFactor);
            float2 intersectionUV = math.lerp(vStart_local.UV, vEnd_local.UV, interpolationFactor);
            // Interpolate local normals, then normalize. TODO: Revisit if lighting is wrong.
            float3 intersectionNormal_local = math.normalize(math.lerp(vStart_local.Normal, vEnd_local.Normal, interpolationFactor));

            return new VertexData
            {
                Position = intersectionPos_local,
                UV = intersectionUV,
                Normal = intersectionNormal_local,
                OriginalIndex = -1 // Intersection vertices are new
            };
        }

        // Internal helper to create a Mesh object from NativeLists using MeshData API.
        // Takes HasUVs/HasNormals flags explicitly.
        private static Mesh CreateMeshFromNativeListsInternal(
            NativeList<float3> vertices, NativeList<int> triangles,
            NativeList<float2> uvs, NativeList<float3> normals, string name,
            bool sourceHasUVs, bool sourceHasNormals)
        {
            using (k_CreateMeshInternalMarker.Auto())
            {
                Mesh mesh = new Mesh();
                mesh.name = name;

                // Check for valid data
                if (!vertices.IsCreated || !triangles.IsCreated || vertices.Length == 0 || triangles.Length == 0)
                {
                    return mesh; // Return empty mesh
                }

                int vertexCount = vertices.Length;
                int triangleIndexCount = triangles.Length;

                UnityEngine.Rendering.IndexFormat indexFormat = vertexCount > 65534
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16;

                // Define Vertex Attributes
                bool hasValidUVs = sourceHasUVs && uvs.IsCreated && uvs.Length == vertexCount;
                bool hasValidNormals = sourceHasNormals && normals.IsCreated && normals.Length == vertexCount;

                var attributeList = new List<UnityEngine.Rendering.VertexAttributeDescriptor>();
                int currentStream = 0;

                // Position (Stream 0)
                attributeList.Add(new UnityEngine.Rendering.VertexAttributeDescriptor(
                    UnityEngine.Rendering.VertexAttribute.Position, UnityEngine.Rendering.VertexAttributeFormat.Float32, 3, stream: currentStream));
                int positionStream = currentStream++;

                // Normals (Stream 1 if valid)
                int normalStream = -1;
                if (hasValidNormals)
                {
                    attributeList.Add(new UnityEngine.Rendering.VertexAttributeDescriptor(
                        UnityEngine.Rendering.VertexAttribute.Normal, UnityEngine.Rendering.VertexAttributeFormat.Float32, 3, stream: currentStream));
                    normalStream = currentStream++;
                }

                // UVs (Stream 1 or 2 if valid)
                int uvStream = -1;
                if (hasValidUVs)
                {
                    attributeList.Add(new UnityEngine.Rendering.VertexAttributeDescriptor(
                       UnityEngine.Rendering.VertexAttribute.TexCoord0, UnityEngine.Rendering.VertexAttributeFormat.Float32, 2, stream: currentStream));
                    uvStream = currentStream++;
                }

                var attributes = new NativeArray<UnityEngine.Rendering.VertexAttributeDescriptor>(
                    attributeList.ToArray(), Allocator.Temp
                );

                try
                {
                    Mesh.MeshDataArray meshDataArray = Mesh.AllocateWritableMeshData(1);
                    Mesh.MeshData meshData = meshDataArray[0];

                    meshData.SetVertexBufferParams(vertexCount, attributes);
                    meshData.SetIndexBufferParams(triangleIndexCount, indexFormat);

                    // Copy vertex data
                    NativeArray<float3> meshVertices = meshData.GetVertexData<float3>(positionStream);
                    vertices.AsArray().CopyTo(meshVertices);

                    // Copy index data (handling format)
                    if (indexFormat == UnityEngine.Rendering.IndexFormat.UInt16)
                    {
                        NativeArray<ushort> meshTrianglesIndices16 = meshData.GetIndexData<ushort>();
                        if (meshTrianglesIndices16.Length == triangles.Length)
                        {
                            for (int i = 0; i < triangles.Length; ++i) meshTrianglesIndices16[i] = (ushort)triangles[i];
                        }
                        else { Debug.LogError($"[{name}] Triangle index (UInt16) length mismatch!"); }
                    }
                    else // IndexFormat.UInt32
                    {
                        NativeArray<int> meshTrianglesIndices32 = meshData.GetIndexData<int>();
                        if (meshTrianglesIndices32.Length == triangles.Length)
                        {
                            triangles.AsArray().CopyTo(meshTrianglesIndices32);
                        }
                        else { Debug.LogError($"[{name}] Triangle index (UInt32) length mismatch!"); }
                    }

                    // Copy normals if valid
                    if (hasValidNormals)
                    {
                        NativeArray<float3> meshNormals = meshData.GetVertexData<float3>(normalStream);
                        if (normals.Length == meshNormals.Length) normals.AsArray().CopyTo(meshNormals);
                        else Debug.LogError($"[{name}] Normal length mismatch!");
                    }

                    // Copy UVs if valid
                    if (hasValidUVs)
                    {
                        NativeArray<float2> meshUVs = meshData.GetVertexData<float2>(uvStream);
                        if (uvs.Length == meshUVs.Length) uvs.AsArray().CopyTo(meshUVs);
                        else Debug.LogError($"[{name}] UV length mismatch!");
                    }

                    // Set submesh and apply data
                    meshData.subMeshCount = 1;
                    meshData.SetSubMesh(0, new UnityEngine.Rendering.SubMeshDescriptor(0, triangleIndexCount),
                        UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds | UnityEngine.Rendering.MeshUpdateFlags.DontValidateIndices);

                    Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, mesh,
                         UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds | UnityEngine.Rendering.MeshUpdateFlags.DontValidateIndices);

                    // Recalculate normals if they weren't valid or provided
                    if (!hasValidNormals)
                    {
                        if (sourceHasNormals && normals.IsCreated && normals.Length != vertexCount)
                        {
                            Debug.LogWarning($"Mesh \"{name}\" Normal count ({normals.Length}) mismatch vertex count ({vertexCount}). Recalculating normals.");
                        }
                        mesh.RecalculateNormals();
                    }
                    if (!hasValidUVs && sourceHasUVs && uvs.IsCreated && uvs.Length != vertexCount)
                    {
                        Debug.LogWarning($"Mesh \"{name}\" UV count ({uvs.Length}) mismatch vertex count ({vertexCount}). Skipping UVs.");
                    }

                    mesh.RecalculateBounds();
                    return mesh;
                }
                finally
                {
                    if (attributes.IsCreated) attributes.Dispose();
                }
            }
        }
    }
}
