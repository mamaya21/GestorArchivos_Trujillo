namespace GestorArchivos.Sync.Windows.Models;

public sealed class AtomicSyncFileResult
{
    public Guid SyncItemId { get; set; }
    public Guid RemoteFileId { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public Guid CarpetaId { get; set; }
    public string HashSha256 { get; set; } = string.Empty;
    public int Version { get; set; }
    public bool AlreadyExisted { get; set; }
}