namespace GestorArchivos.Sync.Windows.Models;

public sealed class SyncDevice
{
    public Guid DeviceId { get; set; }
    public Guid UserId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public Guid RootFolderId { get; set; }
    public string RootFolderName { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
}