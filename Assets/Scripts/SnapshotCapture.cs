using System.IO;
using Meta.XR;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class SnapshotCapture : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private APIClient apiClient;
    [SerializeField] private PassthroughCameraAccess passthroughCamera;
    [SerializeField] private Transform centerEyeAnchor;
    [SerializeField] private Canvas previewCanvas;
    [SerializeField] private RectTransform previewPanel;
    [SerializeField] private RawImage previewImage;
    [SerializeField] private TMP_Text previewStatusText;

    [Header("Capture Flow")]
    [SerializeField] private bool autoUploadAfterCapture = false;
    [SerializeField] private bool saveCaptureToDisk = true;
    [SerializeField] private string captureFolderName = "QuestCaptures";
    [SerializeField] private KeyCode editorTestKey = KeyCode.C;
    [SerializeField] private KeyCode editorSubmitKey = KeyCode.X;

    [Header("Preview Placement")]
    [SerializeField] private LayerMask placementMask = ~0;
    [SerializeField] private float previewFallbackDistance = 0.9f;
    [SerializeField] private float previewSurfaceOffset = 0.08f;
    [SerializeField] private Vector2 previewPanelSize = new(0.32f, 0.24f);

    private bool _capturing;
    private bool _submitting;
    private Texture2D _pendingCapture;

    public string LastCapturePath { get; private set; }

    private void Awake()
    {
        ResolveReferences();
        EnsurePreviewUI();
        UpdatePreviewState("Press A to capture an image.");
    }

    private void OnEnable()
    {
        ResolveReferences();

        if (apiClient != null)
            apiClient.StatusChanged += HandleApiStatusChanged;
    }

    private void OnDisable()
    {
        if (apiClient != null)
            apiClient.StatusChanged -= HandleApiStatusChanged;
    }

    private void Update()
    {
        if (!_capturing)
        {
            bool capturePressed =
                OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch) ||
                Input.GetKeyDown(editorTestKey);

            if (capturePressed)
            {
                CaptureSnapshot();
            }
        }

        bool submitPressed =
            OVRInput.GetDown(OVRInput.Button.Three, OVRInput.Controller.LTouch) ||
            Input.GetKeyDown(editorSubmitKey);

        if (submitPressed)
        {
            SubmitPendingCapture();
        }
    }

    private void CaptureSnapshot()
    {
        ResolveReferences();

        if (passthroughCamera == null)
        {
            Debug.LogError("[SnapshotCapture] PassthroughCameraAccess reference is not assigned.");
            return;
        }

        if (autoUploadAfterCapture && apiClient == null)
        {
            Debug.LogError("[SnapshotCapture] APIClient reference is not assigned, but auto upload is enabled.");
            return;
        }

        if (!passthroughCamera.IsPlaying)
        {
            Debug.LogWarning("[SnapshotCapture] Passthrough camera is not playing yet.");
            return;
        }

        _capturing = true;

        try
        {
            Debug.Log("[SnapshotCapture] Capture requested.");
            Texture2D texture = CaptureTexture();
            if (texture == null)
                return;

            SetPendingCapture(texture);

            if (saveCaptureToDisk)
                LastCapturePath = SaveTextureToDisk(texture);

            if (autoUploadAfterCapture)
            {
                SubmitPendingCapture();
            }
            else
            {
                UpdatePreviewState("Preview ready. Press X to send to the server.");
                Debug.Log("[SnapshotCapture] Capture stored locally and shown in preview.");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[SnapshotCapture] Capture failed: {e.Message}");
            UpdatePreviewState($"Capture failed: {e.Message}");
        }
        finally
        {
            _capturing = false;
        }
    }

    private void SubmitPendingCapture()
    {
        ResolveReferences();
        EnsurePreviewUI();

        if (_submitting)
        {
            UpdatePreviewState("Send already in progress...");
            return;
        }

        if (apiClient == null)
        {
            Debug.LogError("[SnapshotCapture] APIClient reference is not assigned.");
            UpdatePreviewState("API client missing. Cannot send preview.");
            return;
        }

        if (_pendingCapture == null)
        {
            UpdatePreviewState("No captured image yet. Press A to capture.");
            return;
        }

        if (apiClient.IsSending)
        {
            UpdatePreviewState("Backend request already running...");
            return;
        }

        Texture2D sendTexture = CloneTexture(_pendingCapture);
        if (sendTexture == null)
        {
            UpdatePreviewState("Could not prepare preview for sending.");
            return;
        }

        _submitting = true;
        UpdatePreviewState("Sending preview to the server...");
        apiClient.SendImage(sendTexture, HandleSubmitCompleted);
    }

    private void ResolveReferences()
    {
        if (apiClient == null)
            apiClient = FindFirstObjectByType<APIClient>();

        if (passthroughCamera == null)
            passthroughCamera = FindFirstObjectByType<PassthroughCameraAccess>();

        if (centerEyeAnchor == null)
            centerEyeAnchor = Camera.main != null ? Camera.main.transform : centerEyeAnchor;
    }

    private Texture2D CaptureTexture()
    {
        Texture srcTexture = passthroughCamera.GetTexture();
        if (srcTexture == null)
        {
            Debug.LogError("[SnapshotCapture] GetTexture() returned null.");
            UpdatePreviewState("Camera returned no texture.");
            return null;
        }

        RenderTexture rt = RenderTexture.GetTemporary(srcTexture.width, srcTexture.height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(srcTexture, rt);

        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;

        var texture = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
        texture.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        texture.Apply();

        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        return texture;
    }

    private void SetPendingCapture(Texture2D texture)
    {
        if (_pendingCapture != null)
            Destroy(_pendingCapture);

        _pendingCapture = texture;
        EnsurePreviewUI();
        PositionPreviewPanel();

        if (previewImage != null)
        {
            previewImage.texture = _pendingCapture;
            previewImage.enabled = true;
        }
    }

    private void HandleSubmitCompleted(bool success)
    {
        _submitting = false;
        UpdatePreviewState(
            success
                ? "Model generation started. Press A to capture another image."
                : "Send failed. Press X to retry or A to recapture."
        );
    }

    private void HandleApiStatusChanged(string message)
    {
        if (_submitting || (apiClient != null && apiClient.IsSending))
            UpdatePreviewState(message);
    }

    private void EnsurePreviewUI()
    {
        if (previewCanvas == null)
            previewCanvas = FindFirstObjectByType<Canvas>();

        if (previewCanvas == null)
        {
            GameObject canvasObject = new GameObject("CapturePreviewCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            previewCanvas = canvasObject.GetComponent<Canvas>();
            previewCanvas.renderMode = RenderMode.WorldSpace;

            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 30f;
        }

        if (previewPanel == null)
        {
            GameObject panelObject = new GameObject("CapturePreviewPanel", typeof(RectTransform), typeof(Image));
            panelObject.transform.SetParent(previewCanvas.transform, false);
            previewPanel = panelObject.GetComponent<RectTransform>();

            Image background = panelObject.GetComponent<Image>();
            background.color = new Color(0f, 0f, 0f, 0.72f);

            previewPanel.anchorMin = new Vector2(0.5f, 0.5f);
            previewPanel.anchorMax = new Vector2(0.5f, 0.5f);
            previewPanel.pivot = new Vector2(0.5f, 0.5f);
        }

        previewPanel.sizeDelta = previewPanelSize * 1000f;

        if (previewImage == null)
        {
            GameObject imageObject = new GameObject("CapturePreviewImage", typeof(RectTransform), typeof(RawImage));
            imageObject.transform.SetParent(previewPanel.transform, false);

            previewImage = imageObject.GetComponent<RawImage>();
            previewImage.color = Color.white;
            previewImage.enabled = _pendingCapture != null;

            RectTransform imageRect = imageObject.GetComponent<RectTransform>();
            imageRect.anchorMin = new Vector2(0.5f, 1f);
            imageRect.anchorMax = new Vector2(0.5f, 1f);
            imageRect.pivot = new Vector2(0.5f, 1f);
            imageRect.sizeDelta = new Vector2(380f, 214f);
            imageRect.anchoredPosition = new Vector2(0f, -20f);
        }

        if (previewStatusText == null)
        {
            GameObject textObject = new GameObject("CapturePreviewText", typeof(RectTransform), typeof(TextMeshProUGUI));
            textObject.transform.SetParent(previewPanel.transform, false);

            previewStatusText = textObject.GetComponent<TMP_Text>();
            previewStatusText.fontSize = 22;
            previewStatusText.alignment = TextAlignmentOptions.TopLeft;
            previewStatusText.color = Color.white;

            RectTransform textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0f, 0f);
            textRect.anchorMax = new Vector2(1f, 0f);
            textRect.pivot = new Vector2(0.5f, 0f);
            textRect.offsetMin = new Vector2(20f, 20f);
            textRect.offsetMax = new Vector2(-20f, 0f);
            textRect.sizeDelta = new Vector2(0f, 72f);
        }

        if (_pendingCapture != null && previewImage != null)
            previewImage.texture = _pendingCapture;
    }

    private void PositionPreviewPanel()
    {
        if (previewPanel == null || centerEyeAnchor == null)
            return;

        Vector3 origin = centerEyeAnchor.position;
        Vector3 direction = centerEyeAnchor.forward;

        Vector3 targetPosition;
        if (Physics.Raycast(origin, direction, out RaycastHit hit, 5f, placementMask, QueryTriggerInteraction.Ignore))
            targetPosition = hit.point + hit.normal * previewSurfaceOffset;
        else
            targetPosition = origin + direction * previewFallbackDistance;

        previewCanvas.renderMode = RenderMode.WorldSpace;
        previewCanvas.transform.position = targetPosition;

        Vector3 lookDirection = centerEyeAnchor.position - previewCanvas.transform.position;
        if (lookDirection.sqrMagnitude > 0.0001f)
            previewCanvas.transform.rotation = Quaternion.LookRotation(lookDirection.normalized);
    }

    private void UpdatePreviewState(string message)
    {
        EnsurePreviewUI();

        if (previewStatusText != null)
            previewStatusText.text = $"{message}\nA = Capture preview   X = Send to server";
    }

    private static Texture2D CloneTexture(Texture2D source)
    {
        if (source == null)
            return null;

        var clone = new Texture2D(source.width, source.height, source.format, false);
        clone.SetPixels(source.GetPixels());
        clone.Apply();
        return clone;
    }

    private string SaveTextureToDisk(Texture2D texture)
    {
        string directory = Path.Combine(Application.persistentDataPath, captureFolderName);
        Directory.CreateDirectory(directory);

        string fileName = $"capture_{System.DateTime.Now:yyyyMMdd_HHmmss}.jpg";
        string fullPath = Path.Combine(directory, fileName);
        byte[] jpgBytes = texture.EncodeToJPG(90);
        File.WriteAllBytes(fullPath, jpgBytes);

        Debug.Log($"[SnapshotCapture] Capture saved to: {fullPath}");
        return fullPath;
    }
}
