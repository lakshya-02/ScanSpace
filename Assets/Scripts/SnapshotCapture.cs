using UnityEngine;
using Meta.XR;

public class SnapshotCapture : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private APIClient apiClient;
    [SerializeField] private PassthroughCameraAccess passthroughCamera;

    private bool _capturing;

    private void Awake()
    {
        ResolveReferences();
    }

    private void Update()
    {
        if (_capturing) return;

        // A button on right controller
        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch))
        {
            CaptureSnapshot();
        }
    }

    private void CaptureSnapshot()
    {
        ResolveReferences();

        if (apiClient == null)
        {
            Debug.LogError("[SnapshotCapture] APIClient reference is not assigned.");
            return;
        }

        if (passthroughCamera == null)
        {
            Debug.LogError("[SnapshotCapture] PassthroughCameraAccess reference is not assigned.");
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

            Debug.Log($"[SnapshotCapture] Sending capture {texture.width}x{texture.height} to APIClient.");
            apiClient.SendImage(texture);
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
}
