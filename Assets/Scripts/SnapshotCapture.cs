using UnityEngine;
using Meta.XR;
using System.IO;

public class SnapshotCapture : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private APIClient apiClient;
    [SerializeField] private PassthroughCameraAccess passthroughCamera;

    [Header("Capture Flow")]
    [SerializeField] private bool autoUploadAfterCapture = false;
    [SerializeField] private bool saveCaptureToDisk = true;
    [SerializeField] private string captureFolderName = "QuestCaptures";
    [SerializeField] private KeyCode editorTestKey = KeyCode.C;

    private bool _capturing;
    public string LastCapturePath { get; private set; }

    private void Awake()
    {
        ResolveReferences();
    }

    private void Update()
    {
        if (_capturing) return;

        bool capturePressed =
            OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch) ||
            Input.GetKeyDown(editorTestKey);

        if (capturePressed)
        {
            CaptureSnapshot();
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

            // Get GPU texture from passthrough camera
            Texture srcTexture = passthroughCamera.GetTexture();

            if (srcTexture == null)
            {
                Debug.LogError("[SnapshotCapture] GetTexture() returned null.");
                _capturing = false;
                return;
            }

            // Copy GPU texture into a readable Texture2D via RenderTexture blit
            RenderTexture rt = RenderTexture.GetTemporary(srcTexture.width, srcTexture.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(srcTexture, rt);

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;

            var texture = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            texture.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            texture.Apply();

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            if (saveCaptureToDisk)
                LastCapturePath = SaveTextureToDisk(texture);

            if (autoUploadAfterCapture)
            {
                Debug.Log($"[SnapshotCapture] Sending capture {texture.width}x{texture.height} to APIClient.");
                apiClient.SendImage(texture);
            }
            else
            {
                Debug.Log("[SnapshotCapture] Capture stored locally. Auto upload is disabled.");
                Destroy(texture);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[SnapshotCapture] Capture failed: {e.Message}");
        }
        finally
        {
            _capturing = false;
        }
    }

    private void ResolveReferences()
    {
        if (apiClient == null)
            apiClient = FindFirstObjectByType<APIClient>();

        if (passthroughCamera == null)
            passthroughCamera = FindFirstObjectByType<PassthroughCameraAccess>();
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
