using UnityEngine;
using UnityEngine.Networking;
using TMPro;
using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using Stopwatch = System.Diagnostics.Stopwatch;

public class APIClient : MonoBehaviour
{
    public event Action<string> StatusChanged;

    [Header("References")]
    [SerializeField] private ServerConfig config;
    [SerializeField] private ModelLoader modelLoader;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private GameObject loadingIndicator;

    [Header("Settings")]
    [SerializeField] private int timeoutSeconds = 120;
    [SerializeField] private bool flipSnapshotVertically = false;
    [SerializeField] private int jpegQuality = 95;
    [SerializeField] private bool useLosslessPngUpload = true;
    [SerializeField] private float foregroundRatio = 0.92f;
    [SerializeField] private int textureSize = 2048;
    [SerializeField] private string remeshOption = "none";
    [SerializeField] private int vertexCount = -1;
    [SerializeField] private bool saveGeneratedModelsToDisk = true;
    [SerializeField] private string generatedModelFolderName = "GeneratedModels";
    [SerializeField] private bool verboseDebugLogging = true;
    [SerializeField][TextArea(5, 12)] private string debugHistory;

    private bool _isSending;
    private byte[] _lastGeneratedGlbBytes;
    public bool IsSending => _isSending;
    public string LastSavedModelPath { get; private set; }

    private void Awake()
    {
        ResolveReferences();
    }

    public void SendImage(Texture2D texture, Action<bool> onCompleted = null)
    {
        if (texture == null)
        {
            Debug.LogError("[APIClient] Cannot send a null texture.");
            onCompleted?.Invoke(false);
            return;
        }

        if (!HasRequiredReferences())
        {
            Destroy(texture);
            onCompleted?.Invoke(false);
            return;
        }

        if (_isSending)
        {
            Debug.LogWarning("[APIClient] Already sending a request. Ignoring.");
            Destroy(texture);
            onCompleted?.Invoke(false);
            return;
        }

        StartCoroutine(SendImageCoroutine(texture, onCompleted));
    }

    private IEnumerator SendImageCoroutine(Texture2D texture, Action<bool> onCompleted)
    {
        _isSending = true;
        SetStatus("Preparing capture...");
        SetLoading(true);
        LogDebug($"SendImage start | source tex: {texture.width}x{texture.height} fmt={texture.format}");

        byte[] imageBytes;
        string uploadFileName;
        string uploadMimeType;
        try
        {
            if (flipSnapshotVertically)
            {
                FlipTextureVerticalInPlace(texture);
                LogDebug("Applied vertical flip to snapshot before upload.");
            }

            if (useLosslessPngUpload)
            {
                imageBytes = texture.EncodeToPNG();
                uploadFileName = "snapshot.png";
                uploadMimeType = "image/png";
            }
            else
            {
                int quality = Mathf.Clamp(jpegQuality, 50, 100);
                imageBytes = texture.EncodeToJPG(quality);
                uploadFileName = "snapshot.jpg";
                uploadMimeType = "image/jpeg";
            }
        }
        catch (Exception ex)
        {
            Destroy(texture);
            SetStatus($"Image encode failed: {ex.Message}");
            Debug.LogError($"[APIClient] Image encode failed: {ex}");
            CompleteRequest(false, onCompleted);
            yield break;
        }

        Destroy(texture);

        if (imageBytes == null || imageBytes.Length == 0)
        {
            SetStatus("Image encode failed: empty image data.");
            Debug.LogError("[APIClient] Image encode returned empty data.");
            CompleteRequest(false, onCompleted);
            yield break;
        }

        LogDebug($"Upload image ready | file={uploadFileName} | mime={uploadMimeType} | bytes={imageBytes.Length}");

        SetStatus("Checking backend...");

        bool healthOk = false;
        var healthTimer = Stopwatch.StartNew();
        using (var healthRequest = UnityWebRequest.Get(config.HealthURL))
        {
            healthRequest.timeout = 10;
            ApplyAuthHeader(healthRequest);
            Debug.Log($"[APIClient] Health check -> {config.HealthURL}");
            UnityWebRequestAsyncOperation healthOp;
            try
            {
                healthOp = healthRequest.SendWebRequest();
            }
            catch (Exception ex)
            {
                string startError = $"Health request start failed: {ex.Message}";
                SetStatus(startError);
                LogDebug(startError);
                CompleteRequest(false, onCompleted);
                yield break;
            }

            yield return healthOp;
            healthTimer.Stop();

            if (healthRequest.result == UnityWebRequest.Result.Success)
            {
                healthOk = true;
                LogDebug($"Health OK | code={healthRequest.responseCode} | {healthTimer.ElapsedMilliseconds} ms");
            }
            else
            {
                string healthError = BuildRequestError("Backend health check failed", healthRequest);
                Debug.LogWarning($"[APIClient] {healthError}");
                LogDebug($"Health failed | {healthError} | {healthTimer.ElapsedMilliseconds} ms");
                SetStatus("Health check failed. Trying generation anyway...");
            }
        }

        SetStatus(healthOk ? "Sending image to SF3D server..." : "Sending image to SF3D server without preflight...");

        var form = new WWWForm();
        form.AddBinaryData("image", imageBytes, uploadFileName, uploadMimeType);

        string generateUrl = BuildGenerateUrl();
        using (var generateRequest = UnityWebRequest.Post(generateUrl, form))
        {
            generateRequest.timeout = timeoutSeconds;
            generateRequest.useHttpContinue = false;
            generateRequest.downloadHandler = new DownloadHandlerBuffer();
            generateRequest.SetRequestHeader("Accept", "model/gltf-binary, application/octet-stream, */*");
            ApplyAuthHeader(generateRequest);
            Debug.Log($"[APIClient] Generate request -> {generateUrl} | {uploadMimeType} bytes: {imageBytes.Length}");
            LogDebug($"Generate start | url={generateUrl} | timeout={timeoutSeconds}s | mime={uploadMimeType} | bytes={imageBytes.Length}");

            var generateTimer = Stopwatch.StartNew();
            UnityWebRequestAsyncOperation generateOp;
            try
            {
                generateOp = generateRequest.SendWebRequest();
            }
            catch (Exception ex)
            {
                string startError = $"Generate request start failed: {ex.Message}";
                SetStatus(startError);
                LogDebug(startError);
                CompleteRequest(false, onCompleted);
                yield break;
            }

            yield return generateOp;
            generateTimer.Stop();

            if (generateRequest.result != UnityWebRequest.Result.Success)
            {
                string errorMsg = BuildRequestError("Generate failed", generateRequest);
                errorMsg += $" | Base URL: {config.baseUrl}";
                errorMsg += $" | {generateTimer.ElapsedMilliseconds} ms";
                SetStatus(errorMsg);
                Debug.LogError($"[APIClient] Generate failed - {errorMsg}");
                LogDebug($"Generate failed | {errorMsg}");
                CompleteRequest(false, onCompleted);
                yield break;
            }

            byte[] glbBytes = generateRequest.downloadHandler.data;
            _lastGeneratedGlbBytes = glbBytes;
            string responseHeaders = generateRequest.GetResponseHeaders() == null
                ? "none"
                : string.Join(", ", generateRequest.GetResponseHeaders());
            LogDebug(
                $"Generate OK | code={generateRequest.responseCode} | ms={generateTimer.ElapsedMilliseconds} | glbBytes={(glbBytes == null ? 0 : glbBytes.Length)} | headers={responseHeaders}"
            );

            if (!LooksLikeGlb(glbBytes))
            {
                string responseBody = generateRequest.downloadHandler?.text;
                SetStatus("Server response was not a GLB file.");
                Debug.LogError($"[APIClient] Generate succeeded but response was not GLB. Body: {responseBody}");
                LogDebug($"Generate non-GLB response | body={TrimForLog(responseBody, 300)}");
                CompleteRequest(false, onCompleted);
                yield break;
            }

            if (saveGeneratedModelsToDisk)
            {
                string savedPath = SaveGeneratedModel(glbBytes, "scan_model");
                if (!string.IsNullOrEmpty(savedPath))
                    LogDebug($"Model saved | {savedPath}");
            }

            SetStatus("Model received! Loading...");
            LogDebug($"Model load start | glbBytes={glbBytes.Length}");
            Task<bool> loadTask = modelLoader.LoadModelAsync(glbBytes);
            yield return WaitForTask(loadTask);

            if (loadTask.IsFaulted)
            {
                string exceptionMessage = loadTask.Exception?.GetBaseException().Message ?? "Unknown model load failure.";
                SetStatus($"Model load failed: {exceptionMessage}");
                Debug.LogError($"[APIClient] Model load failed: {exceptionMessage}");
                LogDebug($"Model load faulted | {exceptionMessage}");
                CompleteRequest(false, onCompleted);
                yield break;
            }

            if (!loadTask.Result)
            {
                SetStatus("Model load failed.");
                LogDebug("Model load returned false.");
                CompleteRequest(false, onCompleted);
                yield break;
            }

            SetStatus("Model loaded!");
            LogDebug("Model load complete.");
        }

        CompleteRequest(true, onCompleted);
    }

    public bool SaveLatestGeneratedModelToDisk()
    {
        if (!LooksLikeGlb(_lastGeneratedGlbBytes))
        {
            SetStatus("No generated model to save yet.");
            Debug.LogWarning("[APIClient] No generated GLB bytes are available to save.");
            return false;
        }

        string savedPath = SaveGeneratedModel(_lastGeneratedGlbBytes, "scan_model_saved");
        if (string.IsNullOrEmpty(savedPath))
        {
            SetStatus("Could not save generated model.");
            return false;
        }

        SetStatus("Model saved on device.");
        return true;
    }

    private void CompleteRequest(bool success, Action<bool> onCompleted)
    {
        SetLoading(false);
        _isSending = false;
        LogDebug($"Request complete | success={success}");
        onCompleted?.Invoke(success);
    }

    private static IEnumerator WaitForTask(Task task)
    {
        while (!task.IsCompleted)
            yield return null;
    }

    private static string BuildRequestError(string prefix, UnityWebRequest request)
    {
        string errorMsg = request.result == UnityWebRequest.Result.ConnectionError
            ? $"{prefix}: Network error: {request.error}"
            : $"{prefix}: Server error ({request.responseCode}): {request.error}";

        string responseBody = request.downloadHandler?.text;
        if (!string.IsNullOrWhiteSpace(responseBody))
            errorMsg += $" | Response: {responseBody}";

        return errorMsg;
    }

    private static bool LooksLikeGlb(byte[] bytes)
    {
        return bytes != null
               && bytes.Length >= 4
               && bytes[0] == (byte)'g'
               && bytes[1] == (byte)'l'
               && bytes[2] == (byte)'T'
               && bytes[3] == (byte)'F';
    }

    private void SetStatus(string message)
    {
        if (statusText != null)
            statusText.text = message;

        StatusChanged?.Invoke(message);
        Debug.Log($"[APIClient] {message}");
    }

    private void LogDebug(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        debugHistory = string.IsNullOrWhiteSpace(debugHistory)
            ? line
            : $"{line}\n{debugHistory}";

        const int maxChars = 3000;
        if (debugHistory.Length > maxChars)
            debugHistory = debugHistory.Substring(0, maxChars);

        if (verboseDebugLogging)
            Debug.Log($"[APIClient][Trace] {line}");
    }

    private static string TrimForLog(string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            return value;

        return value.Substring(0, maxChars) + "...";
    }

    private string SaveGeneratedModel(byte[] glbBytes, string filePrefix)
    {
        if (!LooksLikeGlb(glbBytes))
            return null;

        try
        {
            string directory = Path.Combine(Application.persistentDataPath, generatedModelFolderName);
            Directory.CreateDirectory(directory);

            string fileName = $"{filePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.glb";
            string fullPath = Path.Combine(directory, fileName);
            File.WriteAllBytes(fullPath, glbBytes);
            LastSavedModelPath = fullPath;
            Debug.Log($"[APIClient] Generated model saved to: {fullPath}");
            return fullPath;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[APIClient] Failed to save generated model: {ex}");
            LogDebug($"Save model failed | {ex.Message}");
            return null;
        }
    }

    private string BuildGenerateUrl()
    {
        string ratio = Mathf.Clamp(foregroundRatio, 0.5f, 1f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        int safeTextureSize = Mathf.Clamp(textureSize, 256, 2048);
        int safeVertexCount = Mathf.Clamp(vertexCount, -1, 50000);
        string safeRemesh = remeshOption == "triangle" || remeshOption == "quad" ? remeshOption : "none";

        return $"{config.GenerateURL}?foreground_ratio={ratio}&texture_size={safeTextureSize}&remesh_option={safeRemesh}&vertex_count={safeVertexCount}";
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
        texture.Apply(false, false);
    }

    private void SetLoading(bool active)
    {
        if (loadingIndicator != null)
            loadingIndicator.SetActive(active);
    }

    private void ResolveReferences()
    {
        if (modelLoader == null)
            modelLoader = FindFirstObjectByType<ModelLoader>();

        if (config == null)
            config = Resources.Load<ServerConfig>("ServerConfig");

        if (statusText == null)
            statusText = FindFirstObjectByType<TMP_Text>();
    }

    private void ApplyAuthHeader(UnityWebRequest request)
    {
        if (request == null || config == null || !config.HasBearerToken)
            return;

        request.SetRequestHeader("Authorization", $"Bearer {config.bearerToken.Trim()}");
    }

    private bool HasRequiredReferences()
    {
        ResolveReferences();

        if (config == null)
        {
            SetStatus("ServerConfig is missing.");
            Debug.LogError("[APIClient] ServerConfig is not assigned. Assign Assets/ServerConfig.asset to the APIClient component.");
            return false;
        }

        if (modelLoader == null)
        {
            SetStatus("ModelLoader is missing.");
            Debug.LogError("[APIClient] ModelLoader is not assigned and could not be found in the scene.");
            return false;
        }

        return true;
    }
}
