using UnityEngine;

[CreateAssetMenu(fileName = "ServerConfig", menuName = "Config/ServerConfig")]
public class ServerConfig : ScriptableObject
{
    [Header("API Gateway")]
    [Tooltip("Public API base URL. Can be a Cloudflare Tunnel HTTPS URL or local LAN URL.")]
    public string baseUrl = "http://192.168.1.100:8000";

    [Tooltip("Health endpoint path exposed by the API gateway.")]
    public string healthPath = "/health";

    [Tooltip("Generation endpoint path exposed by the API gateway.")]
    public string generatePath = "/generate";

    [Header("Auth")]
    [Tooltip("Optional bearer token for the gateway. Leave blank for local development.")]
    public string bearerToken = "";

    public string HealthURL => CombineUrl(baseUrl, healthPath);
    public string GenerateURL => CombineUrl(baseUrl, generatePath);

    public bool HasBearerToken => !string.IsNullOrWhiteSpace(bearerToken);

    private static string CombineUrl(string root, string path)
    {
        string normalizedRoot = (root ?? string.Empty).Trim().TrimEnd('/');
        string normalizedPath = string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim();

        if (!normalizedPath.StartsWith("/"))
            normalizedPath = "/" + normalizedPath;

        return normalizedRoot + normalizedPath;
    }
}
