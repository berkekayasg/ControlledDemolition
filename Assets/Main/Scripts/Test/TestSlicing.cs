using UnityEngine;
using ControlledDemolition.Core;

namespace ControlledDemolition.Testing
{
    public class TestSlicing : MonoBehaviour
    {
        [Tooltip("The force magnitude applied at the click point.")]
        [SerializeField] private float clickForce = 500f;
        [Tooltip("The radius of the explosion force applied.")]
        [SerializeField] private float explosionRadius = 5f;

        private Camera _mainCamera;

        private void Awake()
        {
            _mainCamera = Camera.main;
            if (_mainCamera == null)
            {
                Debug.LogError("TestSlicing requires a main camera in the scene.", this);
                enabled = false;
            }
        }
        void Update()
        {
            if (Input.GetMouseButtonDown(0)) // Left mouse click
            {
                Slice();
            }
        }
        private void Slice()
        {
            var ray = _mainCamera.ScreenPointToRay(Input.mousePosition);
            if (!Physics.Raycast(ray, out var hit)) return;

            var destructible = hit.collider.GetComponent<DestructibleObject>();
            if (destructible == null) return;

            destructible.Fracture(hit.point, clickForce, explosionRadius);
        }
    }
}
