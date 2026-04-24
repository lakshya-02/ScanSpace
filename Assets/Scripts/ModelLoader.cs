using UnityEngine;
using GLTFast;
using System.Threading.Tasks;

public class ModelLoader : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Drag CenterEyeAnchor from OVRCameraRig > TrackingSpace here")]
    [SerializeField] private Transform centerEyeAnchor;

    [Header("Spawn Settings")]
    [Tooltip("How far in front of the player the model spawns (metres)")]
    [SerializeField] private float spawnDistance = 1.5f;

    private GameObject _currentModel;

    private void OnValidate()
    {
        if (spawnDistance < 0.1f)
            spawnDistance = 0.1f;

        if (centerEyeAnchor == null)
            centerEyeAnchor = Camera.main != null ? Camera.main.transform : centerEyeAnchor;
    }

    public async Task<bool> LoadModelAsync(byte[] glbBytes)
    {
        if (glbBytes == null || glbBytes.Length == 0)
        {
            Debug.LogError("[ModelLoader] Invalid .glb bytes.");
            return false;
        }

        if (centerEyeAnchor == null)
        {
            if (Camera.main != null) centerEyeAnchor = Camera.main.transform;
            else
            {
                Debug.LogError("[ModelLoader] centerEyeAnchor is not assigned.");
                return false;
            }
        }

        if (_currentModel != null)
            Destroy(_currentModel);

        // Calculate spawn position from player's current head position and direction
        Vector3 spawnPos = centerEyeAnchor.position + centerEyeAnchor.forward * Mathf.Max(0.1f, spawnDistance);
        // Face the model toward the player
        Quaternion spawnRot = Quaternion.LookRotation(centerEyeAnchor.position - spawnPos);

        var gltf = new GltfImport();
        bool success = await gltf.Load(glbBytes);

        if (!success)
        {
            Debug.LogError("[ModelLoader] Failed to load .glb data.");
            return false;
        }

        var parent = new GameObject("SpawnedModel");
        parent.transform.SetPositionAndRotation(spawnPos, spawnRot);

        bool instantiated = await gltf.InstantiateMainSceneAsync(parent.transform);

        if (!instantiated)
        {
            Debug.LogError("[ModelLoader] Failed to instantiate glTF scene.");
            Destroy(parent);
            return false;
        }

        var collider = parent.AddComponent<BoxCollider>();
        FitColliderToRenderers(parent, collider);

        var rb = parent.AddComponent<Rigidbody>();
        rb.isKinematic = true;

        _currentModel = parent;
        Debug.Log("[ModelLoader] Model spawned at player-relative position.");
        return true;
    }

    private static void FitColliderToRenderers(GameObject root, BoxCollider box)
    {
        var renderers = root.GetComponentsInChildren<Renderer>();
        if (renderers == null || renderers.Length == 0) return;

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);

        box.center = root.transform.InverseTransformPoint(bounds.center);
        box.size = bounds.size;
    }
}
