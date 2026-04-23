using UnityEngine;

[CreateAssetMenu(fileName = "ServerConfig", menuName = "Config/ServerConfig")]
public class ServerConfig : ScriptableObject
{
    [Header("SF3D Server Settings")]
    [Tooltip("LAN IP address of the PC running the Stable Fast 3D FastAPI server")]
    public string serverIP = "192.168.1.100";

    [Header("Ports")]
    [Tooltip("Port used by the FastAPI server for generation requests")]
    public int uploadPort = 8000;

    [Tooltip("Legacy secondary port. Keep equal to uploadPort unless you have a custom server.")]
    public int downloadPort = 8000;

    public string UploadURL => $"http://{serverIP}:{uploadPort}";
    public string DownloadURL => $"http://{serverIP}:{downloadPort}";
    public string GenerateURL => $"{UploadURL}/generate";
    public string HealthURL => $"{UploadURL}/health";
}
