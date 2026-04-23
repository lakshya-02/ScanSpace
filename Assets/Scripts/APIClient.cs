using UnityEngine;
using UnityEngine.Networking;
using TMPro;
using System.Collections;
using System.Threading.Tasks;

public class APIClient : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private ServerConfig config;
    [SerializeField] private ModelLoader modelLoader;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private GameObject loadingIndicator;

    [Header("Settings")]
    [SerializeField] private int timeoutSeconds = 60;

    private bool _isSending;

    private void Awake()
    {
        ResolveReferences();
    }

    public void SendImage(Texture2D texture)
    {
        if (texture == null)
        {
            Debug.LogError("[APIClient] Cannot send a null texture.");
            return;
        }

        if (!HasRequiredReferences())
        {
            Destroy(texture);
            return;
        }

        if (_isSending)
        {
            Debug.LogWarning("[APIClient] Already sending a request. Ignoring.");
            Destroy(texture);
            return;
        }

        StartCoroutine(SendImageCoroutine(texture));
    }

    private IEnumerator SendImageCoroutine(Texture2D texture)
    {
        _isSending = true;
        SetStatus("Preparing capture...");
        SetLoading(true);

        byte[] jpgBytes = texture.EncodeToJPG(85);
        Destroy(texture);

        SetStatus("Checking backend...");

        using (var healthRequest = UnityWebRequest.Get(config.HealthURL))
        {
            healthRequest.timeout = 10;
            Debug.Log($"[APIClient] Health check -> {config.HealthURL}");
            yield return healthRequest.SendWebRequest();

            if (healthRequest.result != UnityWebRequest.Result.Success)
            {
                string healthError = BuildRequestError("Backend health check failed", healthRequest);
                healthError += " | Verify the PC server is running, the LAN IP is correct, and Android cleartext HTTP is allowed.";

                SetStatus(healthError);
                Debug.LogError($"[APIClient] {healthError}");
                SetLoading(false);
                _isSending = false;
                yield break;
            }
        }

        // Stable Fast 3D's FastAPI server returns the GLB directly from /generate.
        SetStatus("Sending image to SF3D server...");

        var form = new WWWForm();
        form.AddBinaryData("image", jpgBytes, "snapshot.jpg", "image/jpeg");

        using (var generateRequest = UnityWebRequest.Post(config.GenerateURL, form))
        {
            generateRequest.timeout = timeoutSeconds;
            Debug.Log($"[APIClient] Generate request -> {config.GenerateURL}");

            yield return generateRequest.SendWebRequest();

            if (generateRequest.result != UnityWebRequest.Result.Success)
            {
                string errorMsg = BuildRequestError("Generate failed", generateRequest);
                SetStatus(errorMsg);
                Debug.LogError($"[APIClient] Generate failed - {errorMsg}");
                SetLoading(false);
                _isSending = false;
                yield break;
            }

            byte[] glbBytes = generateRequest.downloadHandler.data;

            if (!LooksLikeGlb(glbBytes))
            {
                string responseBody = generateRequest.downloadHandler?.text;
                SetStatus("Server response was not a GLB file.");
                Debug.LogError($"[APIClient] Generate succeeded but response was not GLB. Body: {responseBody}");
                SetLoading(false);
                _isSending = false;
                yield break;
            }

            SetStatus("Model received! Loading...");
            Task<bool> loadTask = modelLoader.LoadModelAsync(glbBytes);
            yield return WaitForTask(loadTask);

            if (loadTask.IsFaulted)
            {
                string exceptionMessage = loadTask.Exception?.GetBaseException().Message ?? "Unknown model load failure.";
                SetStatus($"Model load failed: {exceptionMessage}");
                Debug.LogError($"[APIClient] Model load failed: {exceptionMessage}");
                SetLoading(false);
                _isSending = false;
                yield break;
            }

            if (!loadTask.Result)
            {
                SetStatus("Model load failed.");
                SetLoading(false);
                _isSending = false;
                yield break;
            }

            SetStatus("Model loaded!");
        }

        SetLoading(false);
        _isSending = false;
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

        Debug.Log($"[APIClient] {message}");
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
