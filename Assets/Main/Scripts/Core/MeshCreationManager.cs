using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using ControlledDemolition.Geometry;
using ControlledDemolition.Utility;
using Unity.Profiling;

namespace ControlledDemolition.Management
{
    // Structure to hold data for mesh creation OR activation
    public struct MeshActivationRequest
    {
        // Data for Mesh CREATION (from Slice Job)
        public SliceResultReference SliceResultRef; // Null if activating an existing mesh
        public bool IsPositiveSide;                 // Which mesh half to extract from SliceResultRef

        // Data for Mesh ACTIVATION (Final Fragments)
        public Mesh FinalMesh; // Null if creating from SliceResultRef

        // Common Context
        public Material OriginalMaterial;
        public Vector3 OriginalPosition;
        public Quaternion OriginalRotation;
        public Vector3 OriginalScale;
        public Vector3 ImpactPoint;
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


    public class MeshCreationManager : MonoBehaviour
    {
        public static MeshCreationManager Instance { get; private set; }

        [Tooltip("Maximum number of activation requests (mesh creation + setup) to process per frame.")]
        [SerializeField] private int maxActivationsPerFrame = 256;

        private Queue<MeshActivationRequest> _activationQueue = new Queue<MeshActivationRequest>();
        private FragmentPoolManager _fragmentPoolManager;
        private List<(Rigidbody rb, MeshActivationRequest context)> _forceApplicationQueue = new List<(Rigidbody, MeshActivationRequest)>();

        // Profiling Markers
        private static readonly ProfilerMarker k_EnqueueRequestMarker = new ProfilerMarker("MeshCreationManager.EnqueueRequest");
        private static readonly ProfilerMarker k_UpdateMarker = new ProfilerMarker("MeshCreationManager.Update");
        private static readonly ProfilerMarker k_CreateMeshHalfMarker = new ProfilerMarker("MeshCreationManager.CreateMeshHalf");
        private static readonly ProfilerMarker k_ActivateAndQueueMarker = new ProfilerMarker("MeshCreationManager.ActivateAndPotentiallyQueueNextSlice");
        private static readonly ProfilerMarker k_ValidateMeshMarker = new ProfilerMarker("MeshCreationManager.ValidateFragmentMesh");
        private static readonly ProfilerMarker k_GetPooledMarker = new ProfilerMarker("MeshCreationManager.GetPooledFragment");
        private static readonly ProfilerMarker k_SetupTransformMarker = new ProfilerMarker("MeshCreationManager.SetupFragmentTransform");
        private static readonly ProfilerMarker k_SetupComponentsMarker = new ProfilerMarker("MeshCreationManager.SetupFragmentComponents");
        private static readonly ProfilerMarker k_SetupPhysicsMarker = new ProfilerMarker("MeshCreationManager.SetupFragmentPhysics");
        private static readonly ProfilerMarker k_InitLifetimeMarker = new ProfilerMarker("MeshCreationManager.InitializeFragmentLifetime");
        private static readonly ProfilerMarker k_TryQueueNextMarker = new ProfilerMarker("MeshCreationManager.TryQueueNextSliceTask");
        private static readonly ProfilerMarker k_ProcessForceQueueMarker = new ProfilerMarker("MeshCreationManager.ProcessForceQueue");

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
            // Find the pool manager instance in the scene
            _fragmentPoolManager = FindFirstObjectByType<FragmentPoolManager>();
            if (_fragmentPoolManager == null)
            {
                Debug.LogError("MeshCreationManager could not find FragmentPoolManager!", this);
            }
            // Start the coroutine responsible for applying forces after a physics update
            StartCoroutine(ProcessForceQueueCoroutine());
        }

        public void EnqueueRequest(MeshActivationRequest request)
        {
            using (k_EnqueueRequestMarker.Auto())
            {
                // Validate based on whether it's a creation or activation request
                if (request.SliceResultRef != null) // Creation request
                {
                    // Check if the SliceResultRef or its Result is valid before queueing
                    if (request.SliceResultRef == null || !request.SliceResultRef.Result.IsValid)
                    {
                        Debug.LogWarning("Attempted to enqueue MeshActivationRequest (Creation) with null or invalid SliceResultRef. Request ignored.");
                        // Ensure disposal if the ref exists but result is invalid
                        if (request.SliceResultRef != null && request.SliceResultRef.Result.IsValid)
                        {
                            request.SliceResultRef.DecrementAndDisposeIfZero(); // Decrement ref count even if ignored
                        }
                        return;
                    }
                }
                else if (request.FinalMesh == null) // Activation request but mesh is null
                {
                    Debug.LogWarning("Attempted to enqueue MeshActivationRequest (Activation) with null FinalMesh. Request ignored.");
                    return;
                }

                _activationQueue.Enqueue(request);
            }
        }

        void Update()
        {
            using (k_UpdateMarker.Auto())
            {
                if (_fragmentPoolManager == null) return;

                int activationsThisFrame = 0;
                while (_activationQueue.Count > 0 && activationsThisFrame < maxActivationsPerFrame)
                {
                    MeshActivationRequest request = _activationQueue.Dequeue();
                    Mesh fragmentMesh = null;
                    bool disposeResult = false; // Flag to track if SliceResultRef needs disposal

                    if (request.SliceResultRef != null) // Process CREATION request
                    {
                        using (k_CreateMeshHalfMarker.Auto())
                        {
                            fragmentMesh = SliceMesh.CreateSingleMeshHalf(request.SliceResultRef.Result, request.IsPositiveSide);
                        }

                        // Decrement ref count AFTER mesh creation attempt.
                        disposeResult = request.SliceResultRef.DecrementAndDisposeIfZero();
                    }
                    else // Process ACTIVATION request
                    {
                        fragmentMesh = request.FinalMesh;
                    }

                    // Activate Fragment & Queue Next Slice (if mesh is valid)
                    if (fragmentMesh != null && fragmentMesh.vertexCount >= 4)
                    {
                        ActivateAndPotentiallyQueueNextSlice(fragmentMesh, request);
                    }
                    else // Mesh is null, empty, or too simple for convex hull
                    {
                        if (fragmentMesh != null)
                        {
                            if (fragmentMesh.vertexCount > 0)
                            {
                                Debug.Log($"Discarding fragment mesh with {fragmentMesh.vertexCount} vertices (less than 4 required for convex hull).");
                            }
                            Destroy(fragmentMesh);
                        }

                        // Log if mesh creation failed but wasn't disposed
                        if (request.SliceResultRef != null && fragmentMesh == null && !disposeResult && request.SliceResultRef.Result.IsValid)
                        {
                            Debug.LogError("Mesh creation failed but SliceResult was not disposed!");
                        }
                    }

                    activationsThisFrame++;
                }
            }
        }

        private void ActivateAndPotentiallyQueueNextSlice(Mesh fragmentMesh, MeshActivationRequest requestContext)
        {
            using (k_ActivateAndQueueMarker.Auto())
            {
                if (!ValidateFragmentMesh(fragmentMesh)) return;

                GameObject fragmentGO = GetPooledFragment(fragmentMesh);
                if (fragmentGO == null) return;

                SetupFragmentTransform(fragmentGO, requestContext);
                SetupFragmentComponents(fragmentGO, fragmentMesh, requestContext.OriginalMaterial);
                SetupFragmentPhysics(fragmentGO, fragmentMesh, requestContext); // Handles recursive/pooling logic

                fragmentGO.SetActive(true);

                TryQueueNextSliceTask(fragmentGO, fragmentMesh, requestContext);
            }
        }

        private bool ValidateFragmentMesh(Mesh fragmentMesh)
        {
            using (k_ValidateMeshMarker.Auto())
            {
                if (fragmentMesh == null || fragmentMesh.vertexCount == 0)
                {
                    if (fragmentMesh != null) Destroy(fragmentMesh);
                    return false;
                }
                return true;
            }
        }

        private GameObject GetPooledFragment(Mesh fragmentMesh)
        {
            using (k_GetPooledMarker.Auto())
            {
                GameObject fragmentGO = _fragmentPoolManager.GetFragment();
                if (fragmentGO == null)
                {
                    Debug.LogError("Failed to get fragment from pool.", this);
                    Destroy(fragmentMesh); // Destroy mesh if pool failed
                }
                return fragmentGO;
            }
        }

        private void SetupFragmentTransform(GameObject fragmentGO, MeshActivationRequest requestContext)
        {
            using (k_SetupTransformMarker.Auto())
            {
                fragmentGO.transform.position = requestContext.OriginalPosition;
                fragmentGO.transform.rotation = requestContext.OriginalRotation;
                fragmentGO.transform.localScale = requestContext.OriginalScale;
            }
        }

        private void SetupFragmentComponents(GameObject fragmentGO, Mesh fragmentMesh, Material material)
        {
            using (k_SetupComponentsMarker.Auto())
            {
                MeshFilter mf = fragmentGO.GetComponent<MeshFilter>();
                MeshRenderer mr = fragmentGO.GetComponent<MeshRenderer>();
                MeshCollider mc = fragmentGO.GetComponent<MeshCollider>();

                if (mf) mf.sharedMesh = fragmentMesh;
                if (mr) mr.sharedMaterial = material;

                if (mc == null)
                {
                    mc = fragmentGO.AddComponent<MeshCollider>();
                }
                mc.sharedMesh = fragmentMesh;
                mc.convex = true;
            }
        }

        private void SetupFragmentPhysics(GameObject fragmentGO, Mesh fragmentMesh, MeshActivationRequest requestContext)
        {
            using (k_SetupPhysicsMarker.Auto())
            {
                Rigidbody rb = fragmentGO.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    var bounds = fragmentMesh.bounds;
                    var volume = Mathf.Max(bounds.size.x, 0.001f) * Mathf.Max(bounds.size.y, 0.001f) * Mathf.Max(bounds.size.z, 0.001f);
                    rb.mass = Mathf.Max(0.01f, volume / requestContext.OriginalVolume * requestContext.FragmentDensity);

                    // Apply parent velocity BEFORE queuing explosion force
                    rb.linearVelocity = requestContext.ParentVelocity;

                    // Queue the force application
                    _forceApplicationQueue.Add((rb, requestContext));

                    // Recursive Destruction / Pooling Logic
                    float volumeRatio = requestContext.OriginalVolume > 0.0001f ? volume / requestContext.OriginalVolume : 0f;

                    if (volumeRatio > requestContext.RecursiveFragmentVolumeRatio)
                    {
                        if (requestContext.MaxDepth > 0)
                        {
                            // Large Fragment: Make it destructible again
                            var newDestructible = fragmentGO.AddComponent<Core.DestructibleObject>();
                            newDestructible.InitializeRecursive(
                                volume, // This fragment's volume is the new 'initial' volume
                                requestContext.SmallFragmentVolumeThreshold,
                                requestContext.RecursiveFragmentVolumeRatio,
                                requestContext.OriginalMaterial,
                                requestContext.FragmentDensity,
                                10f, // Use default impact threshold for recursive fragments
                                requestContext.FragmentLifetime,
                                requestContext.CurrentDepth,
                                requestContext.MaxDepth - 1
                            );
                            // Ensure ReturnToPoolAfterTime is disabled
                            var returnToPool = fragmentGO.GetComponent<ReturnToPoolAfterTime>();
                            if (returnToPool != null) returnToPool.enabled = false;
                        }
                    }
                    else if (volumeRatio <= requestContext.SmallFragmentVolumeThreshold)
                    {
                        // Small Fragment: Initialize for pooling
                        InitializeFragmentLifetime(fragmentGO, requestContext.FragmentLifetime);
                        // Ensure DestructibleObject is removed
                        var existingDestructible = fragmentGO.GetComponent<Core.DestructibleObject>();
                        if (existingDestructible != null) Destroy(existingDestructible);
                    }
                    else
                    {
                        // Medium Fragment: Persist - Ensure neither pooling nor recursive component is active
                        var returnToPool = fragmentGO.GetComponent<ReturnToPoolAfterTime>();
                        if (returnToPool != null) returnToPool.enabled = false;
                        var existingDestructible = fragmentGO.GetComponent<Core.DestructibleObject>();
                        if (existingDestructible != null) Destroy(existingDestructible);
                    }
                }
                else
                {
                    Debug.LogWarning($"Fragment {fragmentGO.name} is missing a Rigidbody component.", fragmentGO);
                }
            }
        }

        private void InitializeFragmentLifetime(GameObject fragmentGO, float lifetime)
        {
            using (k_InitLifetimeMarker.Auto())
            {
                var returnToPool = fragmentGO.GetComponent<ReturnToPoolAfterTime>();
                if (returnToPool != null)
                {
                    returnToPool.Initialize(lifetime);
                }
                else
                {
                    Debug.LogWarning($"Fragment {fragmentGO.name} is missing ReturnToPoolAfterTime component. Consider adding it to the prefab.", fragmentGO);
                }
            }
        }

        private void TryQueueNextSliceTask(GameObject fragmentGO, Mesh fragmentMesh, MeshActivationRequest requestContext)
        {
            using (k_TryQueueNextMarker.Auto())
            {
                // Only queue if this was a creation request and depth/size allow.
                if (requestContext.SliceResultRef != null &&
                    requestContext.CurrentDepth < requestContext.MaxDepth &&
                    SliceIterationManager.Instance != null &&
                    fragmentMesh.vertexCount >= SliceIterationManager.Instance.MinVertexCountForSlice)
                {
                    SliceIterationManager.Instance.EnqueueTask(new SliceTask
                    {
                        MeshToSlice = fragmentMesh,
                        Material = requestContext.OriginalMaterial,
                        Position = fragmentGO.transform.position,
                        Rotation = fragmentGO.transform.rotation,
                        Scale = fragmentGO.transform.localScale,
                        // Calculate the WORLD space center of the fragment's bounds for the next slice origin
                        SlicePlaneOrigin = fragmentGO.transform.TransformPoint(fragmentMesh.bounds.center),
                        ImpactPoint = requestContext.ImpactPoint,
                        ExplosionForce = requestContext.ExplosionForce,
                        ExplosionRadius = requestContext.ExplosionRadius,
                        FragmentDensity = requestContext.FragmentDensity,
                        FragmentLifetime = requestContext.FragmentLifetime,
                        ParentVelocity = fragmentGO.GetComponent<Rigidbody>()?.linearVelocity ?? Vector3.zero,
                        CurrentDepth = requestContext.CurrentDepth,
                        MaxDepth = requestContext.MaxDepth,
                        // Pass Volume Context for Next Slice
                        OriginalVolume = requestContext.OriginalVolume,
                        SmallFragmentVolumeThreshold = requestContext.SmallFragmentVolumeThreshold,
                        RecursiveFragmentVolumeRatio = requestContext.RecursiveFragmentVolumeRatio
                    });
                }
            }
        }

        private IEnumerator ProcessForceQueueCoroutine()
        {
            var waitForFixedUpdate = new WaitForFixedUpdate();

            while (true)
            {
                if (_forceApplicationQueue.Count > 0)
                {
                    yield return waitForFixedUpdate;

                    using (k_ProcessForceQueueMarker.Auto())
                    {
                        foreach (var item in _forceApplicationQueue)
                        {
                            // Ensure Rigidbody still exists and is active
                            if (item.rb != null && item.rb.gameObject.activeInHierarchy)
                            {
                                item.rb.AddExplosionForce(item.context.ExplosionForce, item.context.ImpactPoint, item.context.ExplosionRadius);
                            }
                        }
                        _forceApplicationQueue.Clear();
                    }

                    yield return null; // Wait for next frame after processing
                }
                else
                {
                    yield return null; // Wait for next frame if queue is empty
                }
            }
        }
    }
}
