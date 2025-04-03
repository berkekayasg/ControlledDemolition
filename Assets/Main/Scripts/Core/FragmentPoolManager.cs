using UnityEngine;
using UnityEngine.Pool;

namespace ControlledDemolition.Management
{
    /// <summary>
    /// Manages pools of fragment GameObjects using Unity's ObjectPool system. Singleton access.
    /// </summary>
    public class FragmentPoolManager : MonoBehaviour
    {
        public static FragmentPoolManager Instance { get; private set; }

        [Header("Pool Settings")]
        [Tooltip("The prefab for the fragment GameObject. Must have Rigidbody, MeshFilter, MeshRenderer, MeshCollider.")]
        [SerializeField] private GameObject fragmentPrefab;
        [SerializeField] private int defaultCapacity = 50;
        [SerializeField] private int maxPoolSize = 200;

        private IObjectPool<GameObject> _fragmentPool;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("Duplicate FragmentPoolManager instance found. Destroying self.", this);
                Destroy(gameObject);
                return;
            }
            Instance = this;
            InitializePool();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
                _fragmentPool?.Clear();
            }
        }

        private void InitializePool()
        {
            if (fragmentPrefab == null)
            {
                Debug.LogError("Fragment Prefab is not assigned in FragmentPoolManager!", this);
                enabled = false;
                return;
            }

            _fragmentPool = new ObjectPool<GameObject>(
                CreatePooledItem,
                OnTakeFromPool,
                OnReturnedToPool,
                OnDestroyPoolObject,
                true, // Collection check
                defaultCapacity,
                maxPoolSize
            );
        }

        private GameObject CreatePooledItem()
        {
            var go = Instantiate(fragmentPrefab);
            go.SetActive(false);
            return go;
        }

        private void OnTakeFromPool(GameObject go)
        {
            // DestructibleObject is responsible for setting mesh, material, mass, position, rotation.
            // We only reset physics state here.
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            go.SetActive(true);
        }

        private void OnReturnedToPool(GameObject go)
        {
            go.SetActive(false);
        }

        private void OnDestroyPoolObject(GameObject go)
        {
            Destroy(go);
        }

        public GameObject GetFragment()
        {
            if (_fragmentPool == null)
            {
                Debug.LogError("Fragment pool is not initialized!", this);
                return null;
            }
            return _fragmentPool.Get();
        }

        public void ReleaseFragment(GameObject fragment)
        {
            if (_fragmentPool == null)
            {
                Debug.LogError("Fragment pool is not initialized!", this);
                return;
            }
            _fragmentPool.Release(fragment);
        }
    }
}
