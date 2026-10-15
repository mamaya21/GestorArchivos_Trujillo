namespace GestorArchivos.Sync.Windows.Models;

public sealed class SyncConflict
{
    public Guid FileId { get; set; }
    public string LocalPath { get; set; } = string.Empty;
    public long LocalVersion { get; set; }
    public long RemoteVersion { get; set; }
    public string LocalHash { get; set; } = string.Empty;
    public string RemoteHash { get; set; } = string.Empty;
    public DateTime DetectedUtc { get; set; } = DateTime.UtcNow;
}