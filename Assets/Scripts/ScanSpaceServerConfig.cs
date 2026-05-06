using UnityEngine;

namespace ScanSpace.Template
{
    [CreateAssetMenu(fileName = "ScanSpaceServerConfig", menuName = "ScanSpace/Server Config")]
    public class ScanSpaceServerConfig : ScriptableObject
    {
        [Header("Generation Server")]
        [Tooltip("Example: https://your-hugging-face-space.hf.space")]
        public string baseUrl = "https://your-hugging-face-space.hf.space";

        [Tooltip("Endpoint expected to accept an image and return a binary GLB.")]
        public string generatePath = "/generate";

        [Tooltip("Optional bearer token for a private Hugging Face Space or endpoint.")]
        public string bearerToken = "";

        public string GenerateUrl => baseUrl.TrimEnd('/') + "/" + generatePath.TrimStart('/');
    }
}
