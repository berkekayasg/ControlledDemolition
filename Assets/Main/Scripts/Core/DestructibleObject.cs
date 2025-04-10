using UnityEngine;
using ControlledDemolition.Management;
using System.Collections;

namespace ControlledDemolition.Core
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(Collider))]
    public class DestructibleObject : MonoBehaviour
    {
        private const float DestroyDelay = 0.1f; // Delay before destroying original object

        [Header("Fracturing")]
        [SerializeField] private int maxSliceDepth = 3;
        [SerializeField] private Material fragmentMaterial;

        [Header("Physics")]
        private float fragmentDensity = 1f;
        [Tooltip("Minimum collision impulse magnitude required to trigger impact fracturing.")]
        [SerializeField] private float minImpactForceToFracture = 10f; // Note: Represents impulse threshold
        [Tooltip("Time in seconds before fragments are returned to the pool.")]
        [SerializeField] private float fragmentLifetime = 5f;

        [Header("Recursive Destruction & Pooling")]
        [Tooltip("Fragments with volume smaller than this (relative to initial volume) will be pooled.")]
        [Range(0f, 1f)]
        [SerializeField] private float smallFragmentVolumeThreshold = 0.05f; // Default 5%
        [Tooltip("Fragments with volume larger than this (relative to initial volume) will become new DestructibleObjects.")]
        [Range(0f, 1f)]
        [SerializeField] private float recursiveFragmentVolumeRatio = 0.33f; // Default 33%

        private MeshFilter _meshFilter;
        private MeshRenderer _meshRenderer;
        private Collider _collider;
        private Rigidbody _rigidbody;
        private bool _isDestroyed = false;
        private float _initialVolume = 1f; // Default to 1 to avoid division by zero if calculation fails

        private void Awake()
        {
            _meshFilter = GetComponent<MeshFilter>();
            _meshRenderer = GetComponent<MeshRenderer>();
            _collider = GetComponent<Collider>();

            TryGetComponent<Rigidbody>(out _rigidbody);

            // Calculate initial volume first
            if (_meshFilter != null && _meshFilter.sharedMesh != null)
            {
                var bounds = _meshFilter.sharedMesh.bounds;
                // Use a small minimum to avoid zero volume
                _initialVolume = Mathf.Max(bounds.size.x, 0.001f) * Mathf.Max(bounds.size.y, 0.001f) * Mathf.Max(bounds.size.z, 0.001f);
            }
            else
            {
                 Debug.LogWarning($"Could not calculate initial volume for {gameObject.name}. MeshFilter or sharedMesh missing.", this);
                 _initialVolume = 1f; // Fallback
            }

            // Calculate density after volume
            if (_rigidbody != null)
            {
                fragmentDensity = _initialVolume > 0 ? _rigidbody.mass / _initialVolume : 1f; // Density = Mass / Volume
            }

            if (fragmentMaterial == null)
            {
                fragmentMaterial = _meshRenderer.material;
            }
        }

        public void Fracture(Vector3 impactPoint, float force, float explosionRadius)
        {
            if (_isDestroyed) return;

            // Create a new mesh instance to avoid modifying the original mesh
            Mesh originalMesh = new Mesh();
            originalMesh.vertices = _meshFilter.mesh.vertices;
            originalMesh.triangles = _meshFilter.mesh.triangles;
            originalMesh.normals = _meshFilter.mesh.normals;
            originalMesh.uv = _meshFilter.mesh.uv;


            if (originalMesh == null || originalMesh.vertexCount == 0)
            {
                Debug.LogError($"Cannot fracture {gameObject.name}: MeshFilter has no mesh or mesh is empty.", this);
                return;
            }

            Vector3 parentVelocity = _rigidbody != null ? _rigidbody.linearVelocity : Vector3.zero;

            if (SliceIterationManager.Instance != null)
            {
                // Create the initial slice task
                SliceIterationManager.Instance.EnqueueTask(new SliceTask {
                    MeshToSlice = originalMesh,
                    Material = fragmentMaterial,
                    Position = transform.position,
                    Rotation = transform.rotation,
                    Scale = transform.localScale,
                    SlicePlaneOrigin = impactPoint,
                    ImpactPoint = impactPoint,
                    ExplosionForce = force,
                    ExplosionRadius = explosionRadius,
                    FragmentDensity = fragmentDensity,
                    FragmentLifetime = fragmentLifetime,
                    ParentVelocity = parentVelocity,
                    CurrentDepth = 0,
                    MaxDepth = maxSliceDepth,
                    OriginalVolume = _initialVolume,
                    SmallFragmentVolumeThreshold = smallFragmentVolumeThreshold,
                    RecursiveFragmentVolumeRatio = recursiveFragmentVolumeRatio
                });
            }
            else
            {
                Debug.LogError("SliceIterationManager Instance is null. Cannot start slicing process.", this);
                return; // Exit early if manager not found
            }

            // Destroy the original object after queueing the first task.
            _isDestroyed = true;
            _collider.enabled = false; // Disable collider to prevent further interactions
            StartCoroutine(WaitBeforeDestroy());
        }

        IEnumerator WaitBeforeDestroy()
        {
            yield return new WaitForSeconds(DestroyDelay); // Use constant
            Destroy(gameObject);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (_isDestroyed) return;

            float impulseMagnitude = collision.impulse.magnitude / 6f; // Adjusted for better impact detection

            // Check if the impulse exceeds the threshold
            if (impulseMagnitude >= minImpactForceToFracture)
            {
                ContactPoint contact = collision.GetContact(0);
                Fracture(contact.point, impulseMagnitude, 0f);
            }
        }

        /// <summary>
        /// Initializes this DestructibleObject when it's added to a fragment during recursive slicing.
        /// </summary>
        public void InitializeRecursive(float initialVolume, float smallThreshold, float recursiveRatio, Material mat, float density, float impactThreshold, float lifetime, int currentDepth, int maxDepth)
        {
            _meshFilter = GetComponent<MeshFilter>();
            _meshRenderer = GetComponent<MeshRenderer>();
            _collider = GetComponent<Collider>();
            TryGetComponent<Rigidbody>(out _rigidbody);

            _initialVolume = initialVolume;
            smallFragmentVolumeThreshold = smallThreshold;
            recursiveFragmentVolumeRatio = recursiveRatio;
            fragmentMaterial = mat;
            fragmentDensity = density;
            minImpactForceToFracture = impactThreshold;
            fragmentLifetime = lifetime;
            maxSliceDepth = maxDepth;

            // Important: Set _isDestroyed false for the new fragment
            _isDestroyed = false;
        }
    }
} // End Namespace
