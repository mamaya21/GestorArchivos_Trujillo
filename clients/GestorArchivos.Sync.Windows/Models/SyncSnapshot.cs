namespace GestorArchivos.Sync.Windows.Models;

public sealed class SyncSnapshot
{
    public Guid RootFolderId { get; set; }
    public long LastChangeId { get; set; }
    public List<RemoteFolder> Folders { get; set; } = [];
    public List<RemoteFile> Files { get; set; } = [];
}