using UnityEngine;
using ControlledDemolition.Core;

namespace ControlledDemolition.Gameplay
{
    /// <summary>
    /// A simple explosive device that detonates after a delay, applying force
    /// and triggering fragmentation on nearby objects.
    /// </summary>
    public class BasicExplosive : MonoBehaviour
    {
        [Header("Explosion Settings")]
        [SerializeField]
        [Min(0.1f)]
        [Tooltip("Radius of the explosion effect.")]
        private float explosionRadius = 5f;

        [SerializeField]
        [Min(0f)]
        [Tooltip("Force applied to rigidbodies caught in the blast.")]
        private float explosionForce = 700f;

        [SerializeField]
        [Min(0f)]
        [Tooltip("Delay in seconds before the explosive detonates.")]
        private float detonationDelay = 3f;

        private bool _hasDetonated = false;

        private void Start()
        {
            Invoke(nameof(Detonate), detonationDelay);
        }

        private void Detonate()
        {
            if (_hasDetonated) return;
            _hasDetonated = true;

            Vector3 explosionPosition = transform.position;

            // Find all colliders within the radius
            Collider[] colliders = Physics.OverlapSphere(explosionPosition, explosionRadius);

            foreach (Collider hitCollider in colliders)
            {
                Rigidbody rb = hitCollider.attachedRigidbody;

                // Apply force to rigidbodies
                if (rb != null)
                {
                    rb.AddExplosionForce(explosionForce, explosionPosition, explosionRadius);
                }

                // Trigger fragmentation on DestructibleObjects
                // Try getting component from collider first, then from attached Rigidbody if collider doesn't have it.
                DestructibleObject destructible = hitCollider.GetComponent<DestructibleObject>() ?? rb?.GetComponent<DestructibleObject>();

                if (destructible != null)
                {
                    destructible.Fracture(explosionPosition, explosionForce, explosionRadius);
                }
            }

            Destroy(gameObject);
        }

        // Draw gizmos in the editor for visualization
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(transform.position, explosionRadius);
        }
    }
}
