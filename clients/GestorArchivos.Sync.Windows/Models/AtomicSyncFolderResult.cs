namespace GestorArchivos.Sync.Windows.Models;

public sealed class AtomicSyncFolderResult
{
    public Guid SyncItemId { get; set; }

    public Guid RemoteFolderId { get; set; }

    public string Nombre { get; set; } = string.Empty;

    public Guid? CarpetaPadreId { get; set; }

    public bool AlreadyExisted { get; set; }
}