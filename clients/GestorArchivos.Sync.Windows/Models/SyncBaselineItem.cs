namespace GestorArchivos.Sync.Windows.Models;

public sealed class SyncBaselineItem
{
    public Guid SyncItemId { get; set; }

    public Guid? RemoteFileId { get; set; }

    public Guid? RemoteFolderId { get; set; }

    public SyncItemType Type { get; set; }

    public string LocalPath { get; set; } = string.Empty;

    public string? LocalHash { get; set; }

    public string? RemoteHash { get; set; }

    public long? RemoteVersion { get; set; }

    public DateTime LastSyncedUtc { get; set; }
}