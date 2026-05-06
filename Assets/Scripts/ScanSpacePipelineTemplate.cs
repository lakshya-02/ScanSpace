using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace ScanSpace.Template
{
    public class ScanSpacePipelineTemplate : MonoBehaviour
    {
        [SerializeField] private ScanSpaceServerConfig serverConfig;

        public IEnumerator GenerateModelFromCrop(byte[] croppedImageBytes)
        {
            if (serverConfig == null || croppedImageBytes == null || croppedImageBytes.Length == 0)
            {
                Debug.LogWarning("ScanSpace template: missing server config or cropped image bytes.");
                yield break;
            }

            using var form = new WWWForm();
            form.AddBinaryData("image", croppedImageBytes, "scan_crop.png", "image/png");

            using UnityWebRequest request = UnityWebRequest.Post(serverConfig.GenerateUrl, form);
            request.downloadHandler = new DownloadHandlerBuffer();

            if (!string.IsNullOrWhiteSpace(serverConfig.bearerToken))
            {
                request.SetRequestHeader("Authorization", "Bearer " + serverConfig.bearerToken);
            }

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("ScanSpace template: generation request failed - " + request.error);
                yield break;
            }

            byte[] glbBytes = request.downloadHandler.data;
            Debug.Log("ScanSpace template: received GLB bytes = " + glbBytes.Length);

            // Next step in a full project:
            // 1. Pass glbBytes to a runtime GLB loader such as glTFast.
            // 2. Instantiate the loaded model in front of the user.
            // 3. Add Quest controller interactions for move, rotate, scale, save, and remove.
        }
    }
}
