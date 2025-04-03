using UnityEngine;
using System.Collections;
using ControlledDemolition.Management;

namespace ControlledDemolition.Utility
{
    /// <summary>
    /// Simple component that returns its GameObject to the FragmentPoolManager after a specified delay.
    /// </summary>
    public class ReturnToPoolAfterTime : MonoBehaviour
    {
        private float _delay;
        private Coroutine _returnCoroutine;

        /// <summary>
        /// Initializes the component with the delay. Starts the return timer.
        /// </summary>
        /// <param name="delay">The time in seconds before returning to the pool.</param>
        public void Initialize(float delay)
        {
            _delay = delay;

            // Ensure only one return coroutine runs at a time
            if (_returnCoroutine != null)
            {
                StopCoroutine(_returnCoroutine);
            }
            _returnCoroutine = StartCoroutine(ReturnAfterDelayCoroutine());
        }

        private IEnumerator ReturnAfterDelayCoroutine()
        {
            yield return new WaitForSeconds(_delay);

            var poolManager = FragmentPoolManager.Instance;

            // Check if the pool manager still exists before returning
            if (poolManager != null)
            {
                // Check if the component/GameObject is still active before returning
                if (gameObject.activeInHierarchy)
                {
                    poolManager.ReleaseFragment(gameObject);
                }
                // If not activeInHierarchy, it was likely already released or destroyed, so do nothing.
            }
            else
            {
                // If the pool manager is gone (e.g., scene change), just destroy the object
                Debug.LogWarning("FragmentPoolManager instance lost. Destroying fragment instead of returning.", this);
                // Only destroy if the object hasn't been destroyed already
                if (this != null && gameObject != null)
                {
                    Destroy(gameObject);
                }
            }
            _returnCoroutine = null;
        }

        /// <summary>
        /// Called when the object is disabled. Stop the return coroutine if it's running.
        /// This prevents attempting to release an already released object if it's manually returned.
        /// </summary>
        private void OnDisable()
        {
            if (_returnCoroutine != null)
            {
                StopCoroutine(_returnCoroutine);
                _returnCoroutine = null;
            }
            // Note: We don't release here because OnDisable is called *when* the pool releases the object.
            // Releasing again would cause an error.
        }
    }
}