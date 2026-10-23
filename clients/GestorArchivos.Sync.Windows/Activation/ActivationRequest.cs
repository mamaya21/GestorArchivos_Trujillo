namespace GestorArchivos.Sync.Windows.Activation;

public sealed class ActivationRequest
{
    public Guid DeviceId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public Guid RootFolderId { get; set; }
    public string RootFolderName { get; set; } = string.Empty;
    public bool StartFresh { get; set; }
    public string Token { get; set; } = string.Empty;
}