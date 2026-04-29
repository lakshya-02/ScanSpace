using UnityEngine;
using GLTFast;
using System;
using System.Collections;
using System.IO;
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

    [Header("Appearance")]
    [SerializeField] private bool animateModelAppear = true;
    [SerializeField] private float appearAnimationSeconds = 0.28f;
    [SerializeField] private float appearStartScale = 0.05f;

    [Header("Local Persistence")]
    [SerializeField] private bool restoreSavedModelOnStart = true;
    [SerializeField] private string persistenceFileName = "scan_space_saved_model.json";
    [SerializeField] private float persistenceWriteCooldownSeconds = 0.75f;

    private GameObject _currentModel;
    private string _currentModelPath;
    private bool _isRestoringSavedModel;
    private bool _hasPendingTransformPersistence;
    private float _lastMetadataWriteTime = -999f;
    private Coroutine _scaleInCoroutine;

    public bool HasCurrentModel => _currentModel != null;
    public Transform CurrentModelTransform => _currentModel != null ? _currentModel.transform : null;

    [Serializable]
    private class SavedModelDatabase
    {
        public SavedModelRecord latest;
    }

    [Serializable]
    private class SavedModelRecord
    {
        public string modelPath;
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
        public string savedAtUtc;
    }

    private async void Start()
    {
        if (!restoreSavedModelOnStart)
            return;

        await RestoreSavedModelIfAnyAsync();
    }

    private void Update()
    {
        FlushPendingTransformPersistenceIfReady();
    }

    private void OnValidate()
    {
        if (spawnDistance < 0.1f)
            spawnDistance = 0.1f;

        if (placedModelMaxSize < 0.05f)
            placedModelMaxSize = 0.05f;

        if (appearAnimationSeconds < 0.01f)
            appearAnimationSeconds = 0.01f;

        appearStartScale = Mathf.Clamp(appearStartScale, 0.01f, 1f);
        persistenceWriteCooldownSeconds = Mathf.Max(0.1f, persistenceWriteCooldownSeconds);

        if (centerEyeAnchor == null)
            centerEyeAnchor = Camera.main != null ? Camera.main.transform : centerEyeAnchor;
    }

    public async Task<bool> LoadModelAsync(byte[] glbBytes)
    {
        return await LoadModelInternalAsync(glbBytes, null, null);
    }

    public bool SaveCurrentModelState(string modelPath)
    {
        return SaveCurrentModelState(modelPath, false);
    }

    public bool SaveCurrentModelState(string modelPath, bool quiet)
    {
        if (_currentModel == null)
        {
            if (!quiet)
                Debug.LogWarning("[ModelLoader] No loaded model transform to save.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(modelPath))
        {
            if (!quiet)
                Debug.LogWarning("[ModelLoader] Cannot save model metadata without a model file path.");
            return false;
        }

        try
        {
            if (!File.Exists(modelPath))
            {
                if (!quiet)
                    Debug.LogWarning($"[ModelLoader] Model file does not exist yet: {modelPath}");
                return false;
            }

            SavedModelDatabase database = new SavedModelDatabase
            {
                latest = new SavedModelRecord
                {
                    modelPath = modelPath,
                    position = _currentModel.transform.position,
                    rotation = _currentModel.transform.rotation,
                    scale = _currentModel.transform.localScale,
                    savedAtUtc = DateTime.UtcNow.ToString("O")
                }
            };

            string metadataPath = GetPersistenceFilePath();
            string metadataDirectory = Path.GetDirectoryName(metadataPath);
            if (!string.IsNullOrEmpty(metadataDirectory))
                Directory.CreateDirectory(metadataDirectory);

            File.WriteAllText(metadataPath, JsonUtility.ToJson(database, true));
            _currentModelPath = modelPath;
            _lastMetadataWriteTime = Time.unscaledTime;
            _hasPendingTransformPersistence = false;

            if (!quiet)
                Debug.Log($"[ModelLoader] Saved local model metadata: {metadataPath}");

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ModelLoader] Failed to save local model metadata: {ex}");
            return false;
        }
    }

    private async Task<bool> LoadModelInternalAsync(byte[] glbBytes, SavedModelRecord restoredState, string sourcePath)
    {
        if (glbBytes == null || glbBytes.Length == 0)
        {
            Debug.LogError("[ModelLoader] Invalid .glb bytes.");
            return false;
        }

        if (!ResolveCenterEyeAnchor())
            return false;

        StopScaleInAnimation();
        _hasPendingTransformPersistence = false;

        if (_currentModel != null)
            Destroy(_currentModel);

        GetInitialTransform(restoredState, out Vector3 spawnPos, out Quaternion spawnRot, out Vector3 targetScale);
        Debug.Log($"[ModelLoader][Trace] Load start | glbBytes={glbBytes.Length} | spawnPos={spawnPos} | spawnDistance={spawnDistance} | restored={restoredState != null}");

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
        parent.transform.localScale = targetScale;

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
        _currentModelPath = sourcePath;

        if (animateModelAppear && Application.isPlaying)
            PlayScaleIn(parent, targetScale);

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

        StopScaleInAnimation();

        if (targetMaxSize > 0f)
            FitModelToMaxSize(_currentModel, targetMaxSize);

        _currentModel.transform.SetPositionAndRotation(position, rotation);

        if (_currentModel.TryGetComponent(out BoxCollider box))
            FitColliderToRenderers(_currentModel, box);

        if (targetMaxSize > 0f)
            Debug.Log($"[ModelLoader] Model moved to preview pose. position={position}, targetMaxSize={targetMaxSize:0.00}");

        PersistCurrentTransformThrottled();
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

        StopScaleInAnimation();

        Destroy(_currentModel);
        _currentModel = null;
        _currentModelPath = null;
        _hasPendingTransformPersistence = false;
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

        StopScaleInAnimation();
        _currentModel.transform.position += worldDelta;
        PersistCurrentTransformThrottled();
        return true;
    }

    public bool RotateCurrentModel(float yawDegrees)
    {
        return RotateCurrentModel(Vector3.up, yawDegrees, Space.World);
    }

    public bool RotateCurrentModel(Vector3 axis, float degrees, Space space = Space.World)
    {
        if (_currentModel == null || axis.sqrMagnitude < 0.0001f || Mathf.Abs(degrees) < 0.001f)
            return false;

        StopScaleInAnimation();
        _currentModel.transform.Rotate(axis.normalized, degrees, space);
        PersistCurrentTransformThrottled();
        return true;
    }

    public bool RotateCurrentModel(Vector3 eulerDegrees, Space space = Space.Self)
    {
        if (_currentModel == null || eulerDegrees.sqrMagnitude < 0.0001f)
            return false;

        StopScaleInAnimation();
        _currentModel.transform.Rotate(eulerDegrees, space);
        PersistCurrentTransformThrottled();
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
        return ScaleCurrentModel(new Vector3(scaleMultiplier, scaleMultiplier, scaleMultiplier));
    }

    public bool ScaleCurrentModel(Vector3 scaleMultiplier)
    {
        if (_currentModel == null)
            return false;

        if (Mathf.Abs(scaleMultiplier.x - 1f) < 0.001f
            && Mathf.Abs(scaleMultiplier.y - 1f) < 0.001f
            && Mathf.Abs(scaleMultiplier.z - 1f) < 0.001f)
            return false;

        if (scaleMultiplier.x <= 0f || scaleMultiplier.y <= 0f || scaleMultiplier.z <= 0f)
            return false;

        StopScaleInAnimation();
        Vector3 currentScale = _currentModel.transform.localScale;
        Vector3 nextScale = new Vector3(
            currentScale.x * scaleMultiplier.x,
            currentScale.y * scaleMultiplier.y,
            currentScale.z * scaleMultiplier.z
        );

        float maxAxis = Mathf.Max(nextScale.x, nextScale.y, nextScale.z);
        float minAxis = Mathf.Min(nextScale.x, nextScale.y, nextScale.z);
        if (minAxis < 0.02f || maxAxis > 4f)
            return false;

        _currentModel.transform.localScale = nextScale;

        if (_currentModel.TryGetComponent(out BoxCollider box))
            FitColliderToRenderers(_currentModel, box);

        PersistCurrentTransformThrottled();
        return true;
    }

    public bool FlushCurrentModelState()
    {
        if (string.IsNullOrWhiteSpace(_currentModelPath))
            return false;

        return SaveCurrentModelState(_currentModelPath, true);
    }

    private async Task RestoreSavedModelIfAnyAsync()
    {
        if (_currentModel != null)
            return;

        string metadataPath = GetPersistenceFilePath();
        if (!File.Exists(metadataPath))
            return;

        try
        {
            SavedModelDatabase database = JsonUtility.FromJson<SavedModelDatabase>(File.ReadAllText(metadataPath));
            SavedModelRecord record = database?.latest;
            if (record == null || string.IsNullOrWhiteSpace(record.modelPath))
                return;

            if (!File.Exists(record.modelPath))
            {
                Debug.LogWarning($"[ModelLoader] Saved model file is missing, skipping restore: {record.modelPath}");
                return;
            }

            _isRestoringSavedModel = true;
            bool loaded = await LoadModelInternalAsync(File.ReadAllBytes(record.modelPath), record, record.modelPath);
            Debug.Log(loaded
                ? $"[ModelLoader] Restored saved model from: {record.modelPath}"
                : "[ModelLoader] Saved model restore failed.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ModelLoader] Failed to restore saved model metadata: {ex}");
        }
        finally
        {
            _isRestoringSavedModel = false;
        }
    }

    private bool ResolveCenterEyeAnchor()
    {
        if (centerEyeAnchor != null)
            return true;

        if (Camera.main != null)
        {
            centerEyeAnchor = Camera.main.transform;
            return true;
        }

        Debug.LogError("[ModelLoader] centerEyeAnchor is not assigned.");
        return false;
    }

    private void GetInitialTransform(SavedModelRecord restoredState, out Vector3 position, out Quaternion rotation, out Vector3 scale)
    {
        if (restoredState != null)
        {
            position = restoredState.position;
            rotation = IsValidQuaternion(restoredState.rotation) ? restoredState.rotation : Quaternion.identity;
            scale = SanitizeScale(restoredState.scale);
            return;
        }

        Vector3 forward = Vector3.ProjectOnPlane(centerEyeAnchor.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.0001f)
            forward = centerEyeAnchor.forward.sqrMagnitude > 0.0001f ? centerEyeAnchor.forward : Vector3.forward;

        forward.Normalize();
        position = centerEyeAnchor.position + forward * Mathf.Max(0.1f, spawnDistance);
        position.y = centerEyeAnchor.position.y - 0.15f;

        Vector3 lookDirection = Vector3.ProjectOnPlane(centerEyeAnchor.position - position, Vector3.up);
        if (lookDirection.sqrMagnitude < 0.0001f)
            lookDirection = centerEyeAnchor.position - position;

        rotation = ApplyYawOffset(Quaternion.LookRotation(lookDirection.normalized, Vector3.up));
        scale = Vector3.one;
    }

    private void PlayScaleIn(GameObject modelRoot, Vector3 targetScale)
    {
        if (modelRoot == null)
            return;

        StopScaleInAnimation();

        _scaleInCoroutine = StartCoroutine(ScaleInCoroutine(modelRoot.transform, targetScale));
    }

    private void StopScaleInAnimation()
    {
        if (_scaleInCoroutine == null)
            return;

        StopCoroutine(_scaleInCoroutine);
        _scaleInCoroutine = null;
    }

    private IEnumerator ScaleInCoroutine(Transform target, Vector3 targetScale)
    {
        Vector3 startScale = targetScale * appearStartScale;
        float duration = Mathf.Max(0.01f, appearAnimationSeconds);
        float elapsed = 0f;
        target.localScale = startScale;

        while (elapsed < duration && target != null)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            t = t * t * (3f - 2f * t);
            target.localScale = Vector3.Lerp(startScale, targetScale, t);
            yield return null;
        }

        if (target != null)
            target.localScale = targetScale;

        _scaleInCoroutine = null;
    }

    private void PersistCurrentTransformThrottled()
    {
        if (_isRestoringSavedModel || string.IsNullOrWhiteSpace(_currentModelPath) || !Application.isPlaying)
            return;

        if (Time.unscaledTime - _lastMetadataWriteTime < persistenceWriteCooldownSeconds)
        {
            _hasPendingTransformPersistence = true;
            return;
        }

        if (SaveCurrentModelState(_currentModelPath, true))
            _hasPendingTransformPersistence = false;
    }

    private void FlushPendingTransformPersistenceIfReady()
    {
        if (!_hasPendingTransformPersistence || _isRestoringSavedModel || string.IsNullOrWhiteSpace(_currentModelPath))
            return;

        if (Time.unscaledTime - _lastMetadataWriteTime < persistenceWriteCooldownSeconds)
            return;

        SaveCurrentModelState(_currentModelPath, true);
    }

    private string GetPersistenceFilePath()
    {
        string safeFileName = string.IsNullOrWhiteSpace(persistenceFileName)
            ? "scan_space_saved_model.json"
            : persistenceFileName;

        return Path.Combine(Application.persistentDataPath, safeFileName);
    }

    private static Vector3 SanitizeScale(Vector3 scale)
    {
        if (scale.sqrMagnitude < 0.0001f)
            return Vector3.one;

        return new Vector3(
            Mathf.Clamp(Mathf.Abs(scale.x), 0.02f, 4f),
            Mathf.Clamp(Mathf.Abs(scale.y), 0.02f, 4f),
            Mathf.Clamp(Mathf.Abs(scale.z), 0.02f, 4f)
        );
    }

    private static bool IsValidQuaternion(Quaternion rotation)
    {
        float lengthSquared = rotation.x * rotation.x
                              + rotation.y * rotation.y
                              + rotation.z * rotation.z
                              + rotation.w * rotation.w;

        return lengthSquared > 0.0001f;
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
