using UnityEngine;
using Unity.Mathematics;
using System.Collections.Generic;
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
        [Tooltip("Maximum number of completed slice jobs to process per frame.")]
        public int MaxCompletedJobsProcessedPerFrame = 5; // Optional limit

        private Queue<SliceTask> _sliceQueue = new Queue<SliceTask>();
        private MeshCreationManager _meshCreationManager;

        private struct PendingSliceJob
        {
            public SliceMesh.SliceJobResult SliceResult;
            public SliceTask OriginalTaskContext;
        }
        private List<PendingSliceJob> _pendingJobs = new List<PendingSliceJob>();


        // Profiling Markers
        private static readonly ProfilerMarker k_UpdateMarker = new ProfilerMarker("SliceIterationManager.Update");
        private static readonly ProfilerMarker k_ProcessCompletedMarker = new ProfilerMarker("SliceIterationManager.ProcessCompletedJobs");
        private static readonly ProfilerMarker k_TryProcessTaskMarker = new ProfilerMarker("SliceIterationManager.TryProcessNextSliceTask");
        private static readonly ProfilerMarker k_DefinePlaneMarker = new ProfilerMarker("SliceIterationManager.DefineWorldSlicePlane");
        private static readonly ProfilerMarker k_ScheduleJobMarker = new ProfilerMarker("SliceIterationManager.ScheduleSliceJob"); // Updated marker name to match method
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
                        // If manager doesn't exist or mesh is null, destroy the mesh safely
                        if (task.MeshToSlice != null) Destroy(task.MeshToSlice);
                        if (_meshCreationManager == null) Debug.LogError("Cannot handle final fragment activation: MeshCreationManager not found.");
                    }
                } // End using
                return; // Exit after handling the final fragment case
            } // End if (validation check)

            // If the task is valid, enqueue it for slicing
            _sliceQueue.Enqueue(task);
        }


        void Update()
        {
            using (k_UpdateMarker.Auto())
            {
                if (_meshCreationManager == null) return;

                // Schedule new jobs from the queue
                int scheduledThisFrame = 0;
                while (_sliceQueue.Count > 0 && scheduledThisFrame < MaxSlicesScheduledPerFrame)
                {
                    if (TryProcessNextSliceTask())
                    {
                        scheduledThisFrame++;
                    }
                    else
                    {
                        // Break if scheduling failed to avoid potential persistent issues this frame
                        break;
                    }
                }

                // Process any jobs that completed since the last frame
                ProcessCompletedJobs();
            }
        }

        private void ProcessCompletedJobs()
        {
            using (k_ProcessCompletedMarker.Auto())
            {
                int processedThisFrame = 0;
                // Iterate backwards for safe removal
                for (int i = _pendingJobs.Count - 1; i >= 0 && processedThisFrame < MaxCompletedJobsProcessedPerFrame; i--)
                {
                    PendingSliceJob pendingJob = _pendingJobs[i];
                    if (pendingJob.SliceResult.Handle.IsCompleted)
                    {
                        // Complete the job formally (releases scheduler resources)
                        pendingJob.SliceResult.Handle.Complete();

                        HandleSuccessfulSlice(pendingJob.SliceResult, pendingJob.OriginalTaskContext);

                        _pendingJobs.RemoveAt(i);
                        processedThisFrame++;
                    }
                }
            }
        }

        private bool TryProcessNextSliceTask()
        {
            using (k_TryProcessTaskMarker.Auto())
            {
                SliceTask currentTask = _sliceQueue.Dequeue();

                if (currentTask.MeshToSlice == null)
                {
                    Debug.LogWarning("Dequeued SliceTask with null mesh. Skipping.");
                    return false;
                }

                float4 worldCutPlane = DefineWorldSlicePlane(currentTask);
                SliceMesh.SliceJobResult sliceResult = ScheduleSliceJob(currentTask, worldCutPlane);

                if (sliceResult.IsValid)
                {
                    _pendingJobs.Add(new PendingSliceJob
                    {
                        SliceResult = sliceResult,
                        OriginalTaskContext = currentTask
                    });

                    // Destroy the input mesh now; job system owns the data.
                    Destroy(currentTask.MeshToSlice);
                    return true;
                }
                else
                {
                    HandleFailedSlice(currentTask);
                    // Destroy mesh if scheduling failed
                    Destroy(currentTask.MeshToSlice);
                    return false;
                }
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

    // Transforms the plane to local space and schedules the job asynchronously
    private SliceMesh.SliceJobResult ScheduleSliceJob(SliceTask task, float4 cutPlaneWorld)
    {
        using (k_ScheduleJobMarker.Auto())
        {
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

            SliceMesh.SliceJobResult sliceResult = SliceMesh.SliceAsync(task.MeshToSlice, cutPlaneLocal);

            // Return the result containing the JobHandle (job runs asynchronously)
            return sliceResult;
        }
    }

    private void HandleSuccessfulSlice(SliceMesh.SliceJobResult sliceResult, SliceTask originalTask)
    {
        using (k_HandleSuccessMarker.Auto())
        {
            // Create a reference-counted wrapper for the slice result data.
            // MeshCreationManager will dispose of the NativeArrays via this reference.
            // Initial count is 2 (one for each activation request).
            var resultRef = new SliceResultReference(sliceResult, 2);

            int nextDepth = originalTask.CurrentDepth + 1;

            // Enqueue activation requests for both halves
            EnqueueActivationRequest(resultRef, true, nextDepth, originalTask);
            EnqueueActivationRequest(resultRef, false, nextDepth, originalTask);
        }
    }

    private void HandleFailedSlice(SliceTask originalTask)
    {
        // Mesh is destroyed in TryProcessNextSliceTask after failure
        Debug.LogWarning($"Slicing job scheduling failed for mesh '{originalTask.MeshToSlice?.name}' at depth {originalTask.CurrentDepth}. Discarding.", this);
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
