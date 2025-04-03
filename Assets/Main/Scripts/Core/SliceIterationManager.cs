using UnityEngine;
using Unity.Mathematics;
using System.Collections.Generic;
using Unity.Jobs;
using ControlledDemolition.Management;
using ControlledDemolition.Geometry;
using Unity.Profiling;

namespace ControlledDemolition.Management
{
    // Structure to hold data for a pending slice operation
    public struct SliceTask
    {
        public Mesh MeshToSlice;
        public Material Material;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
        public Vector3 SlicePlaneOrigin; // Point on the plane for slicing THIS mesh
        public Vector3 ImpactPoint;      // Original impact point for force application
        public float ExplosionForce;
        public float ExplosionRadius;
        public float FragmentDensity;
        public float FragmentLifetime;
        public Vector3 ParentVelocity;
        public int CurrentDepth;
        public int MaxDepth;
        // Volume Context for Recursive/Pooling
        public float OriginalVolume;
        public float SmallFragmentVolumeThreshold;
        public float RecursiveFragmentVolumeRatio;
    }

    public class SliceIterationManager : MonoBehaviour
    {
        public static SliceIterationManager Instance { get; private set; }

        [Tooltip("Maximum number of slice jobs to schedule per frame.")]
        public int MaxSlicesScheduledPerFrame = 1; // Adjust as needed for performance balancing
        [Tooltip("Minimum vertices a mesh must have to be sliced further.")]
        public int MinVertexCountForSlice = 10;

        private Queue<SliceTask> _sliceQueue = new Queue<SliceTask>();
        private MeshCreationManager _meshCreationManager;

        // Profiling Markers
        private static readonly ProfilerMarker k_UpdateMarker = new ProfilerMarker("SliceIterationManager.Update");
        private static readonly ProfilerMarker k_TryProcessTaskMarker = new ProfilerMarker("SliceIterationManager.TryProcessNextSliceTask");
        private static readonly ProfilerMarker k_DefinePlaneMarker = new ProfilerMarker("SliceIterationManager.DefineWorldSlicePlane");
        private static readonly ProfilerMarker k_ScheduleJobMarker = new ProfilerMarker("SliceIterationManager.ScheduleAndCompleteSliceJob");
        private static readonly ProfilerMarker k_HandleSuccessMarker = new ProfilerMarker("SliceIterationManager.HandleSuccessfulSlice");
        private static readonly ProfilerMarker k_EnqueueActivationMarker = new ProfilerMarker("SliceIterationManager.EnqueueActivationRequest");
        private static readonly ProfilerMarker k_EnqueueFinalMarker = new ProfilerMarker("SliceIterationManager.EnqueueFinalFragment");

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
            }
            else
            {
                Instance = this;
            }
        }

        void Start()
        {
            _meshCreationManager = MeshCreationManager.Instance;
            if (_meshCreationManager == null)
            {
                Debug.LogError("SliceIterationManager could not find MeshCreationManager Instance!", this);
            }
        }

        public void EnqueueTask(SliceTask task)
        {
            // Basic validation
            if (task.MeshToSlice == null || task.MeshToSlice.vertexCount < MinVertexCountForSlice || task.CurrentDepth >= task.MaxDepth)
            {
                // If the task is invalid for slicing (too small or max depth reached),
                // treat it as a final fragment and send it directly for activation.
                using (k_EnqueueFinalMarker.Auto())
                {
                    if (_meshCreationManager != null && task.MeshToSlice != null)
                    {
                        // Create a MeshActivationRequest for this "final" mesh.
                        _meshCreationManager.EnqueueRequest(new MeshActivationRequest {
                            SliceResultRef = null, // Indicate this is NOT from a slice job
                            IsPositiveSide = false, // Not relevant
                            FinalMesh = task.MeshToSlice, // Provide the mesh directly
                            OriginalMaterial = task.Material,
                            OriginalPosition = task.Position,
                            OriginalRotation = task.Rotation,
                            OriginalScale = task.Scale,
                            ImpactPoint = task.ImpactPoint,
                            ExplosionForce = task.ExplosionForce,
                            ExplosionRadius = task.ExplosionRadius,
                            FragmentDensity = task.FragmentDensity,
                            FragmentLifetime = task.FragmentLifetime,
                            ParentVelocity = task.ParentVelocity,
                            CurrentDepth = task.CurrentDepth,
                            MaxDepth = task.MaxDepth,
                            // Pass Volume Context for Final Fragments
                            OriginalVolume = task.OriginalVolume,
                            SmallFragmentVolumeThreshold = task.SmallFragmentVolumeThreshold,
                            RecursiveFragmentVolumeRatio = task.RecursiveFragmentVolumeRatio
                        });
                        // Mesh ownership is transferred to MeshCreationManager
                    }
                    else
                    {
                        // If manager doesn't exist or mesh is null, destroy the mesh
                        if (task.MeshToSlice != null) Destroy(task.MeshToSlice);
                        if (_meshCreationManager == null) Debug.LogError("Cannot handle final fragment activation: MeshCreationManager not found.");
                    }
                }
                return;
            }
            _sliceQueue.Enqueue(task);
        }

        void Update()
        {
            using (k_UpdateMarker.Auto())
            {
                if (_meshCreationManager == null) return;

                int scheduledThisFrame = 0;
                while (_sliceQueue.Count > 0 && scheduledThisFrame < MaxSlicesScheduledPerFrame)
                {
                    if (TryProcessNextSliceTask())
                    {
                        scheduledThisFrame++;
                    }
                }
            }
        }

        private bool TryProcessNextSliceTask()
        {
            using (k_TryProcessTaskMarker.Auto())
            {
                SliceTask currentTask = _sliceQueue.Dequeue();

                // Validate mesh
                if (currentTask.MeshToSlice == null)
                {
                    Debug.LogWarning("Dequeued SliceTask with null mesh. Skipping.");
                    return false; // Task not processed
                }

                // Define the plane in world space
                float4 worldCutPlane = DefineWorldSlicePlane(currentTask);
                // Schedule the job
                SliceMesh.SliceJobResult sliceResult = ScheduleAndCompleteSliceJob(currentTask, worldCutPlane);

                if (sliceResult.IsValid)
                {
                    HandleSuccessfulSlice(sliceResult, currentTask);
                }
                else
                {
                    HandleFailedSlice(currentTask);
                }

                // Destroy the original mesh that was sliced or failed to slice
                // Its data is either in sliceResult or it's being discarded.
                Destroy(currentTask.MeshToSlice);

                return true; // Task processed (successfully or not)
            }
    }

    // Returns a WORLD space plane
    private float4 DefineWorldSlicePlane(SliceTask task)
    {
        using (k_DefinePlaneMarker.Auto())
        {
            Vector3 worldOrigin = task.SlicePlaneOrigin + UnityEngine.Random.insideUnitSphere * 0.1f; // Add a small random offset to the origin
            Vector3 planeNormal;
            float strategyChoice = UnityEngine.Random.value; // Random float between 0.0 and 1.0

            // --- 60% Chance: Axis-Aligned ---
            if (strategyChoice < 0.6f)
            {
                int axis = UnityEngine.Random.Range(0, 3); // 0=X, 1=Y, 2=Z
                if (axis == 0) planeNormal = Vector3.right;
                else if (axis == 1) planeNormal = Vector3.up;
                else planeNormal = Vector3.forward;
            }
            // --- 40% Chance: Impact-Oriented ---
            else
            {
                Vector3 impactDirection = task.ImpactPoint - worldOrigin;

                // If impact is too close or direction is zero, fall back to random
                if (impactDirection.sqrMagnitude < 0.01f)
                {
                    planeNormal = UnityEngine.Random.onUnitSphere;
                }
                else
                {
                    // Normalize the direction
                    impactDirection.Normalize();

                    // Find a vector perpendicular to the impact direction
                    Vector3 randomVec = UnityEngine.Random.onUnitSphere;
                    planeNormal = Vector3.Cross(impactDirection, randomVec);

                    // Fallback if cross product is zero
                    if (planeNormal.sqrMagnitude < 0.001f)
                    {
                        if (Mathf.Abs(Vector3.Dot(impactDirection, Vector3.up)) < 0.99f)
                        {
                            planeNormal = Vector3.Cross(impactDirection, Vector3.up);
                        }
                        else
                        {
                            planeNormal = Vector3.Cross(impactDirection, Vector3.right);
                        }
                    }
                    // Ensure the final normal is unit length if derived from cross product
                    if (planeNormal.sqrMagnitude > 0.001f) // Avoid normalizing zero vector
                    {
                         planeNormal.Normalize();
                    }
                    else // Ultimate fallback if all cross products failed
                    {
                        planeNormal = UnityEngine.Random.onUnitSphere;
                    }
                }
            }
            planeNormal = (planeNormal + UnityEngine.Random.onUnitSphere).normalized;

            return SliceMesh.CreatePlane(planeNormal, worldOrigin);
        }
    }

    // Transforms the plane to local space and schedules the job
    private SliceMesh.SliceJobResult ScheduleAndCompleteSliceJob(SliceTask task, float4 cutPlaneWorld)
    {
        using (k_ScheduleJobMarker.Auto())
        {
            // Construct matrices
            float4x4 localToWorldMatrix = float4x4.TRS(task.Position, task.Rotation, task.Scale);
            float4x4 worldToLocalMatrix = math.inverse(localToWorldMatrix);

            // Inline Plane Transformation (World to Local)
            float3 originalNormal_world = cutPlaneWorld.xyz;
            float originalDistance_world = cutPlaneWorld.w;
            float3 pointOnPlane_world = -originalNormal_world * originalDistance_world;
            float3 transformedPoint_local = math.mul(worldToLocalMatrix, new float4(pointOnPlane_world, 1.0f)).xyz;
            float3 transformedNormal_local = math.normalize(math.mul(math.transpose(math.inverse(worldToLocalMatrix)), new float4(originalNormal_world, 0.0f)).xyz);
            float newDistance_local = -math.dot(transformedNormal_local, transformedPoint_local);
            float4 cutPlaneLocal = new float4(transformedNormal_local, newDistance_local);

            // Call SliceAsync with the mesh and the LOCAL plane
            SliceMesh.SliceJobResult sliceResult = SliceMesh.SliceAsync(task.MeshToSlice, cutPlaneLocal);

            // Complete the Job Immediately
            if (sliceResult.IsValid)
            {
                sliceResult.Handle.Complete();
            }
            return sliceResult;
        }
    }

    private void HandleSuccessfulSlice(SliceMesh.SliceJobResult sliceResult, SliceTask originalTask)
    {
        using (k_HandleSuccessMarker.Auto())
        {
            // Create a reference wrapper for the result
            var resultRef = new SliceResultReference(sliceResult, 2); // Initial ref count of 2

            int nextDepth = originalTask.CurrentDepth + 1;

            // Enqueue activation requests for both halves
            EnqueueActivationRequest(resultRef, true, nextDepth, originalTask);
            EnqueueActivationRequest(resultRef, false, nextDepth, originalTask);
        }
    }

    private void HandleFailedSlice(SliceTask originalTask)
    {
        Debug.LogWarning($"Slicing job scheduling failed for mesh at depth {originalTask.CurrentDepth}. Discarding mesh.", this);
        // Mesh destruction happens in TryProcessNextSliceTask
    }

    private void EnqueueActivationRequest(SliceResultReference resultRef, bool isPositiveSide, int depth, SliceTask context)
    {
        using (k_EnqueueActivationMarker.Auto())
        {
            _meshCreationManager.EnqueueRequest(new MeshActivationRequest {
                SliceResultRef = resultRef,
                IsPositiveSide = isPositiveSide,
                OriginalMaterial = context.Material,
                OriginalPosition = context.Position,
                OriginalRotation = context.Rotation,
                OriginalScale = context.Scale,
                ImpactPoint = context.ImpactPoint,
                ExplosionForce = context.ExplosionForce,
                ExplosionRadius = context.ExplosionRadius,
                FragmentDensity = context.FragmentDensity,
                FragmentLifetime = context.FragmentLifetime,
                ParentVelocity = context.ParentVelocity,
                CurrentDepth = depth,
                MaxDepth = context.MaxDepth,
                // Pass Volume Context
                    OriginalVolume = context.OriginalVolume,
                    SmallFragmentVolumeThreshold = context.SmallFragmentVolumeThreshold,
                    RecursiveFragmentVolumeRatio = context.RecursiveFragmentVolumeRatio
                });
            }
        }
    }
}
