using System.IO;
using System.Collections;
using Meta.XR;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class SnapshotCapture : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private APIClient apiClient;
    [SerializeField] private ModelLoader modelLoader;
    [SerializeField] private PassthroughCameraAccess passthroughCamera;
    [SerializeField] private Transform centerEyeAnchor;
    [SerializeField] private Canvas previewCanvas;
    [SerializeField] private RectTransform previewPanel;
    [SerializeField] private RawImage previewImage;
    [SerializeField] private TMP_Text previewStatusText;
    [SerializeField] private TMP_Text appTitleText;
    [SerializeField] private Transform rightControllerAnchor;
    [SerializeField] private RectTransform cropFrame;
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip captureSound;
    [SerializeField] private AudioClip successSound;
    [SerializeField] private TMP_Text transientMessageText;

    [Header("Capture Flow")]
    [SerializeField] private bool autoUploadAfterCapture = false;
    [SerializeField] private bool saveCaptureToDisk = true;
    [SerializeField] private string captureFolderName = "QuestCaptures";
    [SerializeField] private float captureDelaySeconds = 2f;
    [SerializeField] private KeyCode editorTestKey = KeyCode.C;
    [SerializeField] private KeyCode editorSaveModelKey = KeyCode.X;
    [SerializeField] private KeyCode editorPlaceModelKey = KeyCode.B;
    [SerializeField] private KeyCode editorRemoveModelKey = KeyCode.Y;

    [Header("Upload Settings")]
    [SerializeField] private int uploadMaxDimension = 1280;
    [SerializeField] private bool flipCapturedImageVertically = false;
    [SerializeField] private bool useCropBoundary = true;
    [SerializeField] private Vector2 defaultCropCenter = new(0.5f, 0.5f);
    [SerializeField] private Vector2 defaultCropSize = new(0.72f, 0.72f);
    [SerializeField] private float minCropSize = 0.18f;
    [SerializeField] private float cropMoveSpeed = 0.55f;
    [SerializeField] private float cropResizeSpeed = 0.65f;
    [SerializeField] private bool verboseDebugLogging = true;

    [Header("Preview Placement")]
    [SerializeField] private LayerMask placementMask = ~0;
    [SerializeField] private float previewFallbackDistance = 0.9f;
    [SerializeField] private float previewSurfaceOffset = 0.08f;
    [SerializeField] private Vector2 previewPanelSize = new(0.58f, 0.4f);
    [SerializeField] private bool flipPreviewImageVertically = false;

    [Header("UI Layout")]
    [SerializeField] private string appTitle = "ScanSpace";
    [SerializeField] private float titleFontSize = 34f;
    [SerializeField] private float statusFontSize = 20f;
    [SerializeField] private Vector2 titleOffsetMin = new(18f, -56f);
    [SerializeField] private Vector2 titleOffsetMax = new(-18f, -8f);
    [SerializeField] private Vector2 previewImageSize = new(520f, 292f);
    [SerializeField] private Vector2 previewImageAnchoredPosition = new(0f, -62f);
    [SerializeField] private Vector2 statusOffsetMin = new(18f, 14f);
    [SerializeField] private Vector2 statusOffsetMax = new(-18f, 0f);
    [SerializeField] private float statusHeight = 72f;
    [SerializeField] private Color cropBorderColor = new(0.05f, 0.9f, 1f, 0.95f);

    [Header("Feedback")]
    [SerializeField] private bool useGeneratedFallbackSounds = true;
    [SerializeField] private float transientMessageSeconds = 1.4f;
    [SerializeField] private float transientFadeSeconds = 0.35f;

    [Header("Model Controls")]
    [SerializeField] private float stickDeadzone = 0.18f;
    [SerializeField] private float modelMoveSpeed = 0.65f;
    [SerializeField] private float modelRotateSpeed = 90f;
    [SerializeField] private float modelPitchRollSpeed = 70f;
    [SerializeField] private float modelScaleSpeed = 0.9f;
    [SerializeField] private float modelDragTriggerThreshold = 0.1f;
    [SerializeField] private float modelDragFallbackDistance = 1.1f;
    [SerializeField] private float modelDragDistanceSpeed = 1.25f;

    private bool _capturing;
    private bool _submitting;
    private Texture2D _pendingCapture;
    private Coroutine _captureDelayCoroutine;
    private readonly Image[] _cropBorders = new Image[4];
    private Vector2 _cropCenter;
    private Vector2 _cropSize;
    private bool _hasGeneratedModelForPendingCapture;
    private bool _isDraggingModel;
    private float _modelDragDistance;
    private Coroutine _transientMessageCoroutine;

    public string LastCapturePath { get; private set; }

    private void Awake()
    {
        ResolveReferences();
        ResetCropBoundary();
        EnsurePreviewUI();
        UpdatePreviewState("Press A to capture an image.");
    }

    private void OnEnable()
    {
        ResolveReferences();

        if (apiClient != null)
        {
            apiClient.StatusChanged += HandleApiStatusChanged;
            apiClient.ModelSavedLocally += HandleModelSavedLocally;
        }
    }

    private void OnDisable()
    {
        if (apiClient != null)
        {
            apiClient.StatusChanged -= HandleApiStatusChanged;
            apiClient.ModelSavedLocally -= HandleModelSavedLocally;
        }
    }

    private void Update()
    {
        bool editorCapturePressed = false;
        bool editorSavePressed = false;
        bool editorPlacePressed = false;
        bool editorRemovePressed = false;

#if UNITY_EDITOR || ENABLE_LEGACY_INPUT_MANAGER
        editorCapturePressed = Input.GetKeyDown(editorTestKey);
        editorSavePressed = Input.GetKeyDown(editorSaveModelKey);
        editorPlacePressed = Input.GetKeyDown(editorPlaceModelKey);
        editorRemovePressed = Input.GetKeyDown(editorRemoveModelKey);
#endif

        bool requestRunning = _submitting || (apiClient != null && apiClient.IsSending);
        bool cropSelectionActive = IsCropSelectionActive(requestRunning);

        if (!_capturing && !requestRunning)
        {
            bool capturePressed =
                OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch) ||
                editorCapturePressed;

            if (capturePressed)
            {
                RequestCapture();
            }
        }

        bool savePressed =
            GetButtonDown(OVRInput.Button.Three, OVRInput.RawButton.X, OVRInput.Controller.LTouch) ||
            editorSavePressed;

        if (savePressed)
        {
            SaveCurrentModel();
        }

        bool generateOrPlacePressed =
            GetButtonDown(OVRInput.Button.Two, OVRInput.RawButton.B, OVRInput.Controller.RTouch) ||
            editorPlacePressed;

        if (generateOrPlacePressed)
        {
            if (cropSelectionActive)
                SubmitPendingCapture();
            else
                PlaceCurrentModelAtPreview();
        }

        bool removePressed =
            GetButtonDown(OVRInput.Button.Four, OVRInput.RawButton.Y, OVRInput.Controller.LTouch) ||
            editorRemovePressed;

        if (removePressed)
        {
            RemoveOrCancel();
        }

        UpdateCropBoxControls(requestRunning);
        UpdateModelDragControls();
        UpdateModelStickControls();
    }

    private void RequestCapture()
    {
        if (captureDelaySeconds <= 0f)
        {
            CaptureSnapshot();
            return;
        }

        if (_captureDelayCoroutine != null)
        {
            UpdatePreviewState("Capture countdown already running...");
            return;
        }

        _captureDelayCoroutine = StartCoroutine(CaptureAfterDelay());
    }

    private IEnumerator CaptureAfterDelay()
    {
        float remaining = captureDelaySeconds;
        while (remaining > 0f)
        {
            UpdatePreviewState($"Move hands away. Capturing in {Mathf.CeilToInt(remaining)}...");
            yield return new WaitForSeconds(0.25f);
            remaining -= 0.25f;
        }

        _captureDelayCoroutine = null;
        CaptureSnapshot();
    }

    private void CaptureSnapshot()
    {
        ResolveReferences();

        if (_submitting || (apiClient != null && apiClient.IsSending))
        {
            LogDebug("Capture ignored because a send is already running.");
            UpdatePreviewState("Generating model. Please wait...");
            return;
        }

        if (passthroughCamera == null)
        {
            Debug.LogError("[SnapshotCapture] PassthroughCameraAccess reference is not assigned.");
            UpdatePreviewState("Passthrough camera missing.");
            return;
        }

        if (autoUploadAfterCapture && apiClient == null)
        {
            Debug.LogError("[SnapshotCapture] APIClient reference is not assigned, but auto upload is enabled.");
            UpdatePreviewState("API client missing. Cannot upload.");
            return;
        }

        if (!passthroughCamera.IsPlaying)
        {
            Debug.LogWarning("[SnapshotCapture] Passthrough camera is not playing yet.");
            UpdatePreviewState("Camera not ready yet. Try again.");
            return;
        }

        _capturing = true;

        try
        {
            Debug.Log("[SnapshotCapture] Capture requested.");
            Texture2D texture = CaptureTexture();
            if (texture == null)
                return;

            PlayFeedbackSound(captureSound);
            LogDebug($"Capture ready | {texture.width}x{texture.height} fmt={texture.format}");

            SetPendingCapture(texture);

            if (saveCaptureToDisk)
            {
                try
                {
                    LastCapturePath = SaveTextureToDisk(texture);
                }
                catch (System.Exception saveException)
                {
                    Debug.LogWarning($"[SnapshotCapture] Capture preview could not be saved to disk: {saveException.Message}");
                    LogDebug($"Capture disk save skipped | {saveException.Message}");
                }
            }

            bool shouldSubmitImmediately = apiClient != null && autoUploadAfterCapture;
            LogDebug(
                $"Capture flow | autoUpload={autoUploadAfterCapture} playing={Application.isPlaying} apiClient={(apiClient != null)} submit={shouldSubmitImmediately}"
            );

            if (shouldSubmitImmediately)
            {
                UpdatePreviewState("Preview ready. Sending to the server...");
                SubmitPendingCapture();
            }
            else
            {
                UpdatePreviewState("Preview ready. Adjust boundary, then press B.");
                Debug.Log("[SnapshotCapture] Capture stored locally and shown in preview.");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[SnapshotCapture] Capture failed: {e.Message}");
            LogDebug($"Capture failed | {e.Message}");
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
        LogDebug($"Submit requested | pending={_pendingCapture != null} submitting={_submitting} apiClient={(apiClient != null)} apiSending={(apiClient != null && apiClient.IsSending)}");

        if (_submitting)
        {
            UpdatePreviewState("Generating model. Please wait...");
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
            UpdatePreviewState("Generating model. Please wait...");
            return;
        }

        Debug.Log("[SnapshotCapture] Sending pending capture to APIClient.");
        Texture2D sendTexture = CreateUploadTexture(_pendingCapture);
        if (sendTexture == null)
        {
            UpdatePreviewState("Could not prepare preview for sending.");
            return;
        }

        LogDebug($"Prepared upload texture | {sendTexture.width}x{sendTexture.height} fmt={sendTexture.format}");

        _submitting = true;
        UpdatePreviewState("Generating 3D...");
        apiClient.SendImage(sendTexture, HandleSubmitCompleted);
    }

    private void ResolveReferences()
    {
        if (apiClient == null)
            apiClient = FindFirstObjectByType<APIClient>();

        if (modelLoader == null)
            modelLoader = FindFirstObjectByType<ModelLoader>();

        if (passthroughCamera == null)
            passthroughCamera = FindFirstObjectByType<PassthroughCameraAccess>();

        if (centerEyeAnchor == null)
            centerEyeAnchor = Camera.main != null ? Camera.main.transform : centerEyeAnchor;

        if (rightControllerAnchor == null)
            rightControllerAnchor = FindNamedTransform("RightControllerAnchor") ?? FindNamedTransform("RightHandAnchor");

        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();

        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.spatialBlend = 0f;
        }

        if (useGeneratedFallbackSounds)
        {
            if (captureSound == null)
                captureSound = CreateToneClip("ScanSpaceCaptureTone", 740f, 0.08f, 0.16f);

            if (successSound == null)
                successSound = CreateToneClip("ScanSpaceSuccessTone", 980f, 0.12f, 0.18f);
        }
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

        LogDebug($"Source texture | {srcTexture.width}x{srcTexture.height} type={srcTexture.GetType().Name}");

        RenderTexture rt = RenderTexture.GetTemporary(srcTexture.width, srcTexture.height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(srcTexture, rt);

        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;

        var texture = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
        texture.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);

        if (flipCapturedImageVertically)
        {
            FlipTextureVerticalInPlace(texture);
            LogDebug("Applied vertical flip to captured camera image.");
        }

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
        _hasGeneratedModelForPendingCapture = false;
        ResetCropBoundary();
        EnsurePreviewUI();
        SetPreviewVisible(true);
        PositionPreviewPanel();

        if (previewImage != null)
        {
            previewImage.texture = _pendingCapture;
            previewImage.enabled = true;
            ApplyPreviewImageOrientation();
            UpdateCropFrame();
        }
    }

    private void HandleSubmitCompleted(bool success)
    {
        _submitting = false;
        _hasGeneratedModelForPendingCapture = success;
        LogDebug($"Submit completed | success={success}");
        UpdateCropFrame();

        if (success)
            PlayFeedbackSound(successSound);

        UpdatePreviewState(
            success
                ? "Model ready. Hold trigger to drag."
                : "Send failed. Press A to recapture."
        );
    }

    private void HandleApiStatusChanged(string message)
    {
        LogDebug($"API status | {message}");
        if (_submitting || (apiClient != null && apiClient.IsSending))
            UpdatePreviewState(message);
    }

    private void HandleModelSavedLocally(string modelPath)
    {
        LogDebug($"Model saved locally | {modelPath}");
        ShowTransientMessage("Saved locally");
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
            ApplyPreviewImageOrientation();

            RectTransform imageRect = imageObject.GetComponent<RectTransform>();
        }

        ApplyPreviewImageLayout();
        EnsureCropFrame();
        EnsureInstructionTitle();

        if (previewStatusText == null)
        {
            GameObject textObject = new GameObject("CapturePreviewText", typeof(RectTransform), typeof(TextMeshProUGUI));
            textObject.transform.SetParent(previewPanel.transform, false);

            previewStatusText = textObject.GetComponent<TMP_Text>();
            previewStatusText.color = Color.white;
            previewStatusText.textWrappingMode = TextWrappingModes.Normal;
            previewStatusText.overflowMode = TextOverflowModes.Ellipsis;
        }

        ApplyStatusTextLayout();
        EnsureTransientMessageText();

        if (appTitleText != null)
        {
            appTitleText.text = appTitle;
            appTitleText.fontSize = titleFontSize;
        }

        if (_pendingCapture != null && previewImage != null)
        {
            previewImage.texture = _pendingCapture;
            ApplyPreviewImageOrientation();
            UpdateCropFrame();
        }
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

        Vector3 fromHeadToPreview = previewCanvas.transform.position - centerEyeAnchor.position;
        if (fromHeadToPreview.sqrMagnitude > 0.0001f)
            previewCanvas.transform.rotation = Quaternion.LookRotation(fromHeadToPreview.normalized, Vector3.up);
    }

    private void UpdatePreviewState(string message)
    {
        EnsurePreviewUI();

        if (previewStatusText != null)
            previewStatusText.text = $"{message}\n{BuildControlHint()}";
    }

    private void PlaceCurrentModelAtPreview()
    {
        ResolveReferences();
        EnsurePreviewUI();

        if (modelLoader == null)
        {
            UpdatePreviewState("Model loader missing. Cannot place model.");
            Debug.LogError("[SnapshotCapture] ModelLoader reference is not assigned.");
            return;
        }

        if (!modelLoader.HasCurrentModel)
        {
            UpdatePreviewState("No generated model yet. Capture first.");
            return;
        }

        PositionPreviewPanel();

        if (previewCanvas == null)
        {
            UpdatePreviewState("Preview position missing. Cannot place model.");
            return;
        }

        if (modelLoader.MoveCurrentModelToPreview(previewCanvas.transform))
        {
            SetPreviewVisible(false);
            Debug.Log("[SnapshotCapture] Model placed at preview image pose.");
        }
        else
        {
            UpdatePreviewState("Could not place generated model.");
        }
    }

    private void RemoveCurrentModel()
    {
        ResolveReferences();

        if (modelLoader == null)
        {
            UpdatePreviewState("Model loader missing. Cannot remove model.");
            Debug.LogError("[SnapshotCapture] ModelLoader reference is not assigned.");
            return;
        }

        SetPreviewVisible(true);
        UpdatePreviewState(
            modelLoader.RemoveCurrentModel()
                ? "Model removed. Press A to capture again."
                : "No generated model to remove."
        );
    }

    private void RemoveOrCancel()
    {
        if (modelLoader != null && modelLoader.HasCurrentModel)
        {
            RemoveCurrentModel();
            return;
        }

        if (_pendingCapture != null)
        {
            Destroy(_pendingCapture);
            _pendingCapture = null;
            _hasGeneratedModelForPendingCapture = false;
            SetCropFrameVisible(false);
            UpdatePreviewState("Preview cleared. Press A to capture.");
            return;
        }

        RemoveCurrentModel();
    }

    private void SaveCurrentModel()
    {
        ResolveReferences();

        if (apiClient == null)
        {
            UpdatePreviewState("API client missing. Cannot save model.");
            Debug.LogError("[SnapshotCapture] APIClient reference is not assigned.");
            return;
        }

        bool saved = apiClient.SaveLatestGeneratedModelToDisk();
        if (saved)
            ShowTransientMessage("Saved locally");

        UpdatePreviewState(
            saved
                ? "Model saved on device."
                : "No generated model to save yet."
        );
    }

    private void UpdateModelStickControls()
    {
        if (IsCropSelectionActive(false))
            return;

        if (modelLoader == null || !modelLoader.HasCurrentModel || (apiClient != null && apiClient.IsSending))
            return;

        Vector2 moveAxis = OVRInput.Get(OVRInput.RawAxis2D.RThumbstick);
        Vector2 adjustAxis = OVRInput.Get(OVRInput.RawAxis2D.LThumbstick);

        if (!_isDraggingModel && moveAxis.sqrMagnitude > stickDeadzone * stickDeadzone)
        {
            Transform basis = centerEyeAnchor != null ? centerEyeAnchor : transform;
            Vector3 forward = Vector3.ProjectOnPlane(basis.forward, Vector3.up).normalized;
            Vector3 right = Vector3.ProjectOnPlane(basis.right, Vector3.up).normalized;
            Vector3 delta = (right * moveAxis.x + forward * moveAxis.y) * (modelMoveSpeed * Time.deltaTime);
            modelLoader.NudgeCurrentModel(delta);
        }

        bool pitchRollMode =
            OVRInput.Get(OVRInput.RawAxis1D.LHandTrigger) > 0.35f ||
            OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch);

        if (pitchRollMode)
        {
            Transform modelTransform = modelLoader.CurrentModelTransform;
            if (modelTransform == null)
                return;

            if (Mathf.Abs(adjustAxis.y) > stickDeadzone)
                modelLoader.RotateCurrentModel(modelTransform.right, -adjustAxis.y * modelPitchRollSpeed * Time.deltaTime, Space.World);

            if (Mathf.Abs(adjustAxis.x) > stickDeadzone)
                modelLoader.RotateCurrentModel(modelTransform.forward, -adjustAxis.x * modelPitchRollSpeed * Time.deltaTime, Space.World);
        }
        else
        {
            if (Mathf.Abs(adjustAxis.x) > stickDeadzone)
                modelLoader.RotateCurrentModel(adjustAxis.x * modelRotateSpeed * Time.deltaTime);

            if (Mathf.Abs(adjustAxis.y) > stickDeadzone)
            {
                float multiplier = 1f + adjustAxis.y * modelScaleSpeed * Time.deltaTime;
                modelLoader.ScaleCurrentModel(Mathf.Max(0.01f, multiplier));
            }
        }
    }

    private void UpdateCropBoxControls(bool requestRunning)
    {
        if (!IsCropSelectionActive(requestRunning))
        {
            UpdateCropFrame();
            return;
        }

        bool changed = false;
        Vector2 moveAxis = OVRInput.Get(OVRInput.RawAxis2D.RThumbstick);
        Vector2 resizeAxis = OVRInput.Get(OVRInput.RawAxis2D.LThumbstick);

        if (moveAxis.sqrMagnitude > stickDeadzone * stickDeadzone)
        {
            _cropCenter += moveAxis * (cropMoveSpeed * Time.deltaTime);
            changed = true;
        }

        if (Mathf.Abs(resizeAxis.x) > stickDeadzone || Mathf.Abs(resizeAxis.y) > stickDeadzone)
        {
            _cropSize += resizeAxis * (cropResizeSpeed * Time.deltaTime);
            changed = true;
        }

        if (changed)
        {
            ClampCropBoundary();
            UpdateCropFrame();
        }
    }

    private void UpdateModelDragControls()
    {
        if (IsCropSelectionActive(false) || modelLoader == null || !modelLoader.HasCurrentModel)
        {
            _isDraggingModel = false;
            return;
        }

        bool triggerHeld = OVRInput.Get(OVRInput.RawAxis1D.RIndexTrigger) >= modelDragTriggerThreshold
                           || OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch)
                           || OVRInput.Get(OVRInput.RawButton.RIndexTrigger);

        if (!triggerHeld)
        {
            if (_isDraggingModel)
                modelLoader.FlushCurrentModelState();

            _isDraggingModel = false;
            return;
        }

        Transform pointer = rightControllerAnchor != null ? rightControllerAnchor : centerEyeAnchor;
        Transform currentModel = modelLoader.CurrentModelTransform;
        if (pointer == null || currentModel == null)
            return;

        if (!_isDraggingModel)
        {
            _isDraggingModel = true;
            float startDistance = Vector3.Distance(pointer.position, currentModel.position);
            if (startDistance < 0.05f)
                startDistance = modelDragFallbackDistance;

            _modelDragDistance = Mathf.Clamp(
                startDistance,
                0.35f,
                3f
            );
        }

        Vector2 distanceAxis = OVRInput.Get(OVRInput.RawAxis2D.RThumbstick);
        if (Mathf.Abs(distanceAxis.y) > stickDeadzone)
            _modelDragDistance = Mathf.Clamp(
                _modelDragDistance + distanceAxis.y * modelDragDistanceSpeed * Time.deltaTime,
                0.25f,
                4f
            );

        Vector3 targetPosition = pointer.position + pointer.forward * Mathf.Max(0.1f, _modelDragDistance);
        if (Physics.Raycast(pointer.position, pointer.forward, out RaycastHit hit, 5f, placementMask, QueryTriggerInteraction.Ignore)
            && !modelLoader.IsPartOfCurrentModel(hit.transform))
        {
            targetPosition = hit.point + hit.normal * previewSurfaceOffset;
        }

        modelLoader.MoveCurrentModelTo(targetPosition, currentModel.rotation);
    }

    private static bool GetButtonDown(OVRInput.Button button, OVRInput.RawButton rawButton, OVRInput.Controller controller)
    {
        return OVRInput.GetDown(button)
               || OVRInput.GetDown(button, controller)
               || OVRInput.GetDown(rawButton);
    }

    private void SetPreviewVisible(bool visible)
    {
        if (previewCanvas != null)
            previewCanvas.gameObject.SetActive(visible);
    }

    private bool IsCropSelectionActive(bool requestRunning)
    {
        return useCropBoundary
               && _pendingCapture != null
               && !_hasGeneratedModelForPendingCapture
               && !requestRunning;
    }

    private string BuildControlHint()
    {
        if (_submitting || (apiClient != null && apiClient.IsSending))
            return "Generating model...\nPlease wait";

        if (IsCropSelectionActive(false))
            return "A Retake | B Generate | Y Clear\nRight stick move crop | Left stick resize";

        if (modelLoader != null && modelLoader.HasCurrentModel)
            return "Trigger drag | R stick move | L stick yaw/scale\nHold L grip: pitch/roll | X Save | Y Remove";

        return "A Capture\nMove hands away before the countdown ends";
    }

    private void LogDebug(string message)
    {
        if (verboseDebugLogging)
            Debug.Log($"[SnapshotCapture][Trace] {message}");
    }

    private void ApplyPreviewImageOrientation()
    {
        if (previewImage == null)
            return;

        previewImage.uvRect = flipPreviewImageVertically
            ? new Rect(0f, 1f, 1f, -1f)
            : new Rect(0f, 0f, 1f, 1f);
    }

    private void EnsureCropFrame()
    {
        if (previewImage == null)
            return;

        if (cropFrame == null)
        {
            GameObject frameObject = new GameObject("CropBoundary", typeof(RectTransform));
            frameObject.transform.SetParent(previewImage.transform, false);
            cropFrame = frameObject.GetComponent<RectTransform>();
            cropFrame.anchorMin = new Vector2(0.5f, 0.5f);
            cropFrame.anchorMax = new Vector2(0.5f, 0.5f);
            cropFrame.pivot = new Vector2(0.5f, 0.5f);
        }

        EnsureCropBorder(0, "Top");
        EnsureCropBorder(1, "Bottom");
        EnsureCropBorder(2, "Left");
        EnsureCropBorder(3, "Right");
        UpdateCropFrame();
    }

    private void EnsureCropBorder(int index, string name)
    {
        if (_cropBorders[index] != null)
            return;

        Transform existing = cropFrame.Find(name);
        if (existing != null && existing.TryGetComponent(out Image image))
        {
            _cropBorders[index] = image;
            image.color = cropBorderColor;
            return;
        }

        _cropBorders[index] = CreateCropBorder(cropFrame, name, cropBorderColor);
    }

    private static Image CreateCropBorder(RectTransform parent, string name, Color color)
    {
        GameObject borderObject = new GameObject(name, typeof(RectTransform), typeof(Image));
        borderObject.transform.SetParent(parent, false);

        Image image = borderObject.GetComponent<Image>();
        image.color = color;
        return image;
    }

    private void EnsureInstructionTitle()
    {
        if (previewPanel == null)
            return;

        if (appTitleText == null)
        {
            GameObject titleObject = new GameObject("ScanSpaceTitle", typeof(RectTransform), typeof(TextMeshProUGUI));
            titleObject.transform.SetParent(previewPanel.transform, false);
            appTitleText = titleObject.GetComponent<TMP_Text>();
            appTitleText.alignment = TextAlignmentOptions.Center;
            appTitleText.color = Color.white;
            appTitleText.fontStyle = FontStyles.Bold;
        }

        RectTransform titleRect = appTitleText.rectTransform;
        titleRect.anchorMin = new Vector2(0f, 1f);
        titleRect.anchorMax = new Vector2(1f, 1f);
        titleRect.pivot = new Vector2(0.5f, 1f);
        titleRect.offsetMin = titleOffsetMin;
        titleRect.offsetMax = titleOffsetMax;
    }

    private void ApplyPreviewImageLayout()
    {
        if (previewImage == null)
            return;

        RectTransform imageRect = previewImage.rectTransform;
        imageRect.anchorMin = new Vector2(0.5f, 1f);
        imageRect.anchorMax = new Vector2(0.5f, 1f);
        imageRect.pivot = new Vector2(0.5f, 1f);
        imageRect.sizeDelta = previewImageSize;
        imageRect.anchoredPosition = previewImageAnchoredPosition;
    }

    private void ApplyStatusTextLayout()
    {
        if (previewStatusText == null)
            return;

        previewStatusText.fontSize = statusFontSize;
        previewStatusText.alignment = TextAlignmentOptions.Center;

        RectTransform textRect = previewStatusText.rectTransform;
        textRect.anchorMin = new Vector2(0f, 0f);
        textRect.anchorMax = new Vector2(1f, 0f);
        textRect.pivot = new Vector2(0.5f, 0f);
        textRect.offsetMin = statusOffsetMin;
        textRect.offsetMax = statusOffsetMax;
        textRect.sizeDelta = new Vector2(0f, statusHeight);
    }

    private void EnsureTransientMessageText()
    {
        if (previewPanel == null)
            return;

        if (transientMessageText == null)
        {
            GameObject textObject = new GameObject("ScanSpaceTransientMessage", typeof(RectTransform), typeof(TextMeshProUGUI));
            textObject.transform.SetParent(previewPanel.transform, false);

            transientMessageText = textObject.GetComponent<TMP_Text>();
            transientMessageText.alignment = TextAlignmentOptions.Center;
            transientMessageText.fontStyle = FontStyles.Bold;
            transientMessageText.fontSize = statusFontSize;
            transientMessageText.text = string.Empty;
            transientMessageText.color = new Color(0.72f, 1f, 0.78f, 0f);
            transientMessageText.raycastTarget = false;
        }

        RectTransform textRect = transientMessageText.rectTransform;
        textRect.anchorMin = new Vector2(0.5f, 0f);
        textRect.anchorMax = new Vector2(0.5f, 0f);
        textRect.pivot = new Vector2(0.5f, 0f);
        textRect.anchoredPosition = new Vector2(0f, statusHeight + 18f);
        textRect.sizeDelta = new Vector2(360f, 44f);
    }

    private void PlayFeedbackSound(AudioClip clip)
    {
        if (clip == null)
            return;

        ResolveReferences();
        if (audioSource != null)
            audioSource.PlayOneShot(clip);
    }

    private static AudioClip CreateToneClip(string clipName, float frequency, float durationSeconds, float volume)
    {
        const int sampleRate = 24000;
        int sampleCount = Mathf.Max(1, Mathf.RoundToInt(sampleRate * durationSeconds));
        float[] samples = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            float t = i / (float)sampleRate;
            float envelope = 1f - i / (float)sampleCount;
            samples[i] = Mathf.Sin(2f * Mathf.PI * frequency * t) * volume * envelope;
        }

        AudioClip clip = AudioClip.Create(clipName, sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    private void ShowTransientMessage(string message)
    {
        EnsurePreviewUI();

        if (transientMessageText == null)
            return;

        if (_transientMessageCoroutine != null)
            StopCoroutine(_transientMessageCoroutine);

        _transientMessageCoroutine = StartCoroutine(TransientMessageRoutine(message));
    }

    private IEnumerator TransientMessageRoutine(string message)
    {
        transientMessageText.text = message;
        transientMessageText.gameObject.SetActive(true);

        Color visible = transientMessageText.color;
        visible.a = 1f;
        transientMessageText.color = visible;

        yield return new WaitForSeconds(transientMessageSeconds);

        float fadeDuration = Mathf.Max(0.01f, transientFadeSeconds);
        float elapsed = 0f;
        while (elapsed < fadeDuration)
        {
            elapsed += Time.deltaTime;
            Color faded = visible;
            faded.a = Mathf.Lerp(1f, 0f, Mathf.Clamp01(elapsed / fadeDuration));
            transientMessageText.color = faded;
            yield return null;
        }

        Color hidden = visible;
        hidden.a = 0f;
        transientMessageText.color = hidden;
        transientMessageText.text = string.Empty;
        _transientMessageCoroutine = null;
    }

    private void UpdateCropFrame()
    {
        if (cropFrame == null || previewImage == null)
            return;

        RectTransform imageRect = previewImage.rectTransform;
        Vector2 imageSize = imageRect.rect.size;
        if (imageSize.x <= 0f || imageSize.y <= 0f)
            imageSize = imageRect.sizeDelta;

        cropFrame.sizeDelta = new Vector2(imageSize.x * _cropSize.x, imageSize.y * _cropSize.y);
        cropFrame.anchoredPosition = new Vector2(
            (_cropCenter.x - 0.5f) * imageSize.x,
            (_cropCenter.y - 0.5f) * imageSize.y
        );

        const float thickness = 4f;
        SetCropBorderRect(_cropBorders[0], new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, thickness), Vector2.zero);
        SetCropBorderRect(_cropBorders[1], new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, thickness), Vector2.zero);
        SetCropBorderRect(_cropBorders[2], new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(thickness, 0f), Vector2.zero);
        SetCropBorderRect(_cropBorders[3], new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(thickness, 0f), Vector2.zero);

        SetCropFrameVisible(IsCropSelectionActive(_submitting || (apiClient != null && apiClient.IsSending)));
    }

    private static void SetCropBorderRect(Image border, Vector2 anchorMin, Vector2 anchorMax, Vector2 sizeDelta, Vector2 anchoredPosition)
    {
        if (border == null)
            return;

        RectTransform rect = border.rectTransform;
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = sizeDelta;
        rect.anchoredPosition = anchoredPosition;
    }

    private void SetCropFrameVisible(bool visible)
    {
        if (cropFrame != null)
            cropFrame.gameObject.SetActive(visible);
    }

    private void ResetCropBoundary()
    {
        _cropCenter = defaultCropCenter;
        _cropSize = defaultCropSize;
        ClampCropBoundary();
    }

    private void ClampCropBoundary()
    {
        float safeMin = Mathf.Clamp(minCropSize, 0.05f, 1f);
        _cropSize.x = Mathf.Clamp(_cropSize.x, safeMin, 1f);
        _cropSize.y = Mathf.Clamp(_cropSize.y, safeMin, 1f);
        _cropCenter.x = Mathf.Clamp(_cropCenter.x, _cropSize.x * 0.5f, 1f - _cropSize.x * 0.5f);
        _cropCenter.y = Mathf.Clamp(_cropCenter.y, _cropSize.y * 0.5f, 1f - _cropSize.y * 0.5f);
    }

    private static Texture2D CloneTexture(Texture2D source, int maxDimension)
    {
        if (source == null)
            return null;

        int targetWidth = source.width;
        int targetHeight = source.height;

        if (maxDimension > 0 && (source.width > maxDimension || source.height > maxDimension))
        {
            float scale = Mathf.Min((float)maxDimension / source.width, (float)maxDimension / source.height);
            targetWidth = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
            targetHeight = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));
        }

        RenderTexture rt = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(source, rt);

        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;

        var clone = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, false);
        clone.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
        clone.Apply();

        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        return clone;
    }

    private Texture2D CreateUploadTexture(Texture2D source)
    {
        if (source == null)
            return null;

        if (!useCropBoundary)
            return CloneTexture(source, uploadMaxDimension);

        RectInt cropRect = GetCropPixelRect(source);
        Texture2D cropped = CropTexture(source, cropRect);
        if (cropped == null)
            return CloneTexture(source, uploadMaxDimension);

        Texture2D upload = CloneTexture(cropped, uploadMaxDimension);
        Destroy(cropped);
        LogDebug($"Crop upload | rect={cropRect.x},{cropRect.y},{cropRect.width}x{cropRect.height}");
        return upload;
    }

    private RectInt GetCropPixelRect(Texture2D source)
    {
        ClampCropBoundary();

        int x = Mathf.RoundToInt((_cropCenter.x - _cropSize.x * 0.5f) * source.width);
        int y = Mathf.RoundToInt((_cropCenter.y - _cropSize.y * 0.5f) * source.height);
        int width = Mathf.RoundToInt(_cropSize.x * source.width);
        int height = Mathf.RoundToInt(_cropSize.y * source.height);

        x = Mathf.Clamp(x, 0, source.width - 1);
        y = Mathf.Clamp(y, 0, source.height - 1);
        width = Mathf.Clamp(width, 1, source.width - x);
        height = Mathf.Clamp(height, 1, source.height - y);
        return new RectInt(x, y, width, height);
    }

    private static Texture2D CropTexture(Texture2D source, RectInt cropRect)
    {
        if (source == null || cropRect.width <= 0 || cropRect.height <= 0)
            return null;

        var cropped = new Texture2D(cropRect.width, cropRect.height, TextureFormat.RGBA32, false);
        Color32[] sourcePixels = source.GetPixels32();
        Color32[] croppedPixels = new Color32[cropRect.width * cropRect.height];

        for (int row = 0; row < cropRect.height; row++)
        {
            int sourceIndex = (cropRect.y + row) * source.width + cropRect.x;
            int targetIndex = row * cropRect.width;
            System.Array.Copy(sourcePixels, sourceIndex, croppedPixels, targetIndex, cropRect.width);
        }

        cropped.SetPixels32(croppedPixels);
        cropped.Apply();
        return cropped;
    }

    private static void FlipTextureVerticalInPlace(Texture2D texture)
    {
        if (texture == null || texture.width <= 1 || texture.height <= 1)
            return;

        int width = texture.width;
        int height = texture.height;
        Color32[] pixels = texture.GetPixels32();

        for (int y = 0; y < height / 2; y++)
        {
            int topRowStart = y * width;
            int bottomRowStart = (height - 1 - y) * width;
            for (int x = 0; x < width; x++)
            {
                int topIndex = topRowStart + x;
                int bottomIndex = bottomRowStart + x;
                Color32 temp = pixels[topIndex];
                pixels[topIndex] = pixels[bottomIndex];
                pixels[bottomIndex] = temp;
            }
        }

        texture.SetPixels32(pixels);
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

    private static Transform FindNamedTransform(string objectName)
    {
        GameObject found = GameObject.Find(objectName);
        return found != null ? found.transform : null;
    }
}
