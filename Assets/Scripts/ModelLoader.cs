using UnityEngine;
using GLTFast;
using System;
using System.Threading.Tasks;
using UnityEngine.Rendering;

public class ModelLoader : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Drag CenterEyeAnchor from OVRCameraRig > TrackingSpace here")]
    [SerializeField] private Transform centerEyeAnchor;

    [Header("Spawn Settings")]
    [Tooltip("How far in front of the player the model spawns (metres)")]
    [SerializeField] private float spawnDistance = 1.5f;
    [SerializeField] private float placedModelMaxSize = 0.35f;
    [SerializeField] private float modelYawOffsetDegrees = 180f;

    [Header("Quest Material Fix")]
    [SerializeField] private bool convertLoadedMaterialsForQuest = true;
    [SerializeField] private bool renderGeneratedModelsUnlit = true;

    private GameObject _currentModel;
    public bool HasCurrentModel => _currentModel != null;
    public Transform CurrentModelTransform => _currentModel != null ? _currentModel.transform : null;

    private void OnValidate()
    {
        if (spawnDistance < 0.1f)
            spawnDistance = 0.1f;

        if (placedModelMaxSize < 0.05f)
            placedModelMaxSize = 0.05f;

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
        Quaternion spawnRot = ApplyYawOffset(Quaternion.LookRotation(centerEyeAnchor.position - spawnPos));
        Debug.Log($"[ModelLoader][Trace] Load start | glbBytes={glbBytes.Length} | spawnPos={spawnPos} | spawnDistance={spawnDistance}");

        var gltf = new GltfImport();
        bool success;
        try
        {
            success = await gltf.Load(glbBytes);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ModelLoader] glTF load exception: {ex}");
            return false;
        }

        if (!success)
        {
            Debug.LogError("[ModelLoader] Failed to load .glb data.");
            return false;
        }

        var parent = new GameObject("SpawnedModel");
        parent.transform.SetPositionAndRotation(spawnPos, spawnRot);

        bool instantiated;
        try
        {
            instantiated = await gltf.InstantiateMainSceneAsync(parent.transform);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ModelLoader] Instantiate exception: {ex}");
            Destroy(parent);
            return false;
        }

        if (!instantiated)
        {
            Debug.LogError("[ModelLoader] Failed to instantiate glTF scene.");
            Destroy(parent);
            return false;
        }

        if (convertLoadedMaterialsForQuest)
            ConvertMaterialsForQuest(parent);

        var collider = parent.AddComponent<BoxCollider>();
        FitColliderToRenderers(parent, collider);

        var rb = parent.AddComponent<Rigidbody>();
        rb.isKinematic = true;

        _currentModel = parent;
        Debug.Log($"[ModelLoader] Model spawned at player-relative position. childCount={parent.transform.childCount}");
        return true;
    }

    public bool MoveCurrentModelTo(Vector3 position, Quaternion rotation, float targetMaxSize = -1f)
    {
        if (_currentModel == null)
        {
            Debug.LogWarning("[ModelLoader] No generated model to move.");
            return false;
        }

        if (targetMaxSize > 0f)
            FitModelToMaxSize(_currentModel, targetMaxSize);

        _currentModel.transform.SetPositionAndRotation(position, rotation);

        if (_currentModel.TryGetComponent(out BoxCollider box))
            FitColliderToRenderers(_currentModel, box);

        if (targetMaxSize > 0f)
            Debug.Log($"[ModelLoader] Model moved to preview pose. position={position}, targetMaxSize={targetMaxSize:0.00}");

        return true;
    }

    public bool MoveCurrentModelToPreview(Transform previewTransform)
    {
        if (previewTransform == null)
        {
            Debug.LogWarning("[ModelLoader] Preview transform is missing.");
            return false;
        }

        Quaternion rotation = previewTransform.rotation;
        if (centerEyeAnchor != null)
        {
            Vector3 lookDirection = centerEyeAnchor.position - previewTransform.position;
            if (lookDirection.sqrMagnitude > 0.0001f)
                rotation = Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
        }

        return MoveCurrentModelTo(previewTransform.position, ApplyYawOffset(rotation), placedModelMaxSize);
    }

    public bool RemoveCurrentModel()
    {
        if (_currentModel == null)
        {
            Debug.Log("[ModelLoader] No generated model to remove.");
            return false;
        }

        Destroy(_currentModel);
        _currentModel = null;
        Debug.Log("[ModelLoader] Generated model removed.");
        return true;
    }

    public bool IsPartOfCurrentModel(Transform candidate)
    {
        return _currentModel != null
               && candidate != null
               && (candidate == _currentModel.transform || candidate.IsChildOf(_currentModel.transform));
    }

    public bool NudgeCurrentModel(Vector3 worldDelta)
    {
        if (_currentModel == null)
            return false;

        _currentModel.transform.position += worldDelta;
        return true;
    }

    public bool RotateCurrentModel(float yawDegrees)
    {
        if (_currentModel == null || Mathf.Abs(yawDegrees) < 0.001f)
            return false;

        _currentModel.transform.Rotate(Vector3.up, yawDegrees, Space.World);
        return true;
    }

    public bool RotateCurrentModelByStep(float direction)
    {
        if (Mathf.Abs(direction) < 0.001f)
            return false;

        return RotateCurrentModel(Mathf.Sign(direction) * 15f);
    }

    public bool ScaleCurrentModel(float scaleMultiplier)
    {
        if (_currentModel == null || Mathf.Abs(scaleMultiplier - 1f) < 0.001f)
            return false;

        Vector3 nextScale = _currentModel.transform.localScale * scaleMultiplier;
        float maxAxis = Mathf.Max(nextScale.x, nextScale.y, nextScale.z);
        float minAxis = Mathf.Min(nextScale.x, nextScale.y, nextScale.z);
        if (minAxis < 0.02f || maxAxis > 4f)
            return false;

        _currentModel.transform.localScale = nextScale;

        if (_currentModel.TryGetComponent(out BoxCollider box))
            FitColliderToRenderers(_currentModel, box);

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

    private void ConvertMaterialsForQuest(GameObject root)
    {
        Shader targetShader = FindQuestSafeShader();
        if (targetShader == null)
        {
            Debug.LogWarning("[ModelLoader] Could not find a Quest-safe fallback shader for generated model materials.");
            return;
        }

        int repaired = 0;
        var renderers = root.GetComponentsInChildren<Renderer>(true);
        foreach (Renderer renderer in renderers)
        {
            Material[] materials = renderer.materials;
            for (int i = 0; i < materials.Length; i++)
            {
                Material converted = CreateQuestSafeMaterial(materials[i], targetShader);
                if (converted == null)
                    continue;

                materials[i] = converted;
                repaired++;
            }

            renderer.materials = materials;
        }

        Debug.Log($"[ModelLoader] Quest material conversion finished. materials={repaired}, shader={targetShader.name}");
    }

    private Shader FindQuestSafeShader()
    {
        string primary = renderGeneratedModelsUnlit
            ? "Universal Render Pipeline/Unlit"
            : "Universal Render Pipeline/Lit";

        string secondary = renderGeneratedModelsUnlit
            ? "Universal Render Pipeline/Lit"
            : "Universal Render Pipeline/Unlit";

        return Shader.Find(primary)
               ?? Shader.Find(secondary)
               ?? Shader.Find("Unlit/Texture")
               ?? Shader.Find("Sprites/Default");
    }

    private static Material CreateQuestSafeMaterial(Material source, Shader targetShader)
    {
        if (targetShader == null)
            return null;

        var material = new Material(targetShader)
        {
            name = source != null ? $"{source.name}_QuestSafe" : "GeneratedModel_QuestSafe"
        };

        Texture baseTexture = FindTexture(source, out Vector2 textureScale, out Vector2 textureOffset);
        Color baseColor = FindColor(source);

        SetTextureIfPresent(material, "_BaseMap", baseTexture, textureScale, textureOffset);
        SetTextureIfPresent(material, "_MainTex", baseTexture, textureScale, textureOffset);
        SetColorIfPresent(material, "_BaseColor", baseColor);
        SetColorIfPresent(material, "_Color", baseColor);
        SetFloatIfPresent(material, "_Metallic", 0f);
        SetFloatIfPresent(material, "_Smoothness", 0.35f);
        SetFloatIfPresent(material, "_Cull", (float)CullMode.Off);

        return material;
    }

    private static Texture FindTexture(Material material, out Vector2 scale, out Vector2 offset)
    {
        scale = Vector2.one;
        offset = Vector2.zero;

        if (material == null)
            return null;

        string[] preferredProperties =
        {
            "baseColorTexture",
            "_BaseMap",
            "_MainTex",
            "diffuseTexture"
        };

        foreach (string property in preferredProperties)
        {
            if (!HasProperty(material, property))
                continue;

            Texture texture = material.GetTexture(property);
            if (texture == null)
                continue;

            scale = material.GetTextureScale(property);
            offset = material.GetTextureOffset(property);
            return texture;
        }

        foreach (string property in material.GetTexturePropertyNames())
        {
            Texture texture = material.GetTexture(property);
            if (texture == null)
                continue;

            scale = material.GetTextureScale(property);
            offset = material.GetTextureOffset(property);
            return texture;
        }

        return material.mainTexture;
    }

    private static Color FindColor(Material material)
    {
        if (material == null)
            return Color.white;

        string[] preferredProperties =
        {
            "baseColorFactor",
            "_BaseColor",
            "_Color",
            "diffuseFactor"
        };

        foreach (string property in preferredProperties)
        {
            if (!HasProperty(material, property))
                continue;

            Color color = material.GetColor(property);
            return LooksLikeUnityErrorPink(color) ? Color.white : color;
        }

        return Color.white;
    }

    private static void SetTextureIfPresent(Material material, string property, Texture texture, Vector2 scale, Vector2 offset)
    {
        if (texture == null || !HasProperty(material, property))
            return;

        material.SetTexture(property, texture);
        material.SetTextureScale(property, scale);
        material.SetTextureOffset(property, offset);
    }

    private static void SetColorIfPresent(Material material, string property, Color color)
    {
        if (HasProperty(material, property))
            material.SetColor(property, color);
    }

    private static void SetFloatIfPresent(Material material, string property, float value)
    {
        if (HasProperty(material, property))
            material.SetFloat(property, value);
    }

    private static bool HasProperty(Material material, string property)
    {
        return material != null && material.HasProperty(property);
    }

    private static bool LooksLikeUnityErrorPink(Color color)
    {
        return color.r > 0.85f && color.g < 0.25f && color.b > 0.75f;
    }

    private static void FitModelToMaxSize(GameObject root, float targetMaxSize)
    {
        if (root == null || targetMaxSize <= 0f)
            return;

        Bounds bounds;
        if (!TryGetRendererBounds(root, out bounds))
            return;

        float currentMaxSize = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
        if (currentMaxSize <= 0.0001f)
            return;

        float scaleFactor = targetMaxSize / currentMaxSize;
        root.transform.localScale *= scaleFactor;
    }

    private Quaternion ApplyYawOffset(Quaternion rotation)
    {
        return rotation * Quaternion.Euler(0f, modelYawOffsetDegrees, 0f);
    }

    private static bool TryGetRendererBounds(GameObject root, out Bounds bounds)
    {
        var renderers = root.GetComponentsInChildren<Renderer>();
        if (renderers == null || renderers.Length == 0)
        {
            bounds = default;
            return false;
        }

        bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);

        return true;
    }
}
