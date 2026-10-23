using GestorArchivos.Sync.Windows.Models;

namespace GestorArchivos.Sync.Windows.Api;

public interface ISyncApiClient
{
    Task<bool> ValidateTokenAsync(
        CancellationToken cancellationToken = default);

    Task<SyncChangesResponse> GetChangesAsync(
        long afterId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SyncItem>> GetItemsAsync(
        CancellationToken cancellationToken = default);

    Task<SyncSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default);

    Task<SyncItem> RegisterItemAsync(
        string localPath,
        SyncItemType type,
        Guid? remoteFolderId,
        Guid? remoteFileId,
        CancellationToken cancellationToken = default);

    Task UpdateItemPathAsync(
        Guid syncItemId,
        string localPath,
        CancellationToken cancellationToken = default);

    Task DetachTrashedItemAsync(Guid syncItemId, CancellationToken cancellationToken = default);
    Task<bool> IsItemTrashedAsync(Guid syncItemId, CancellationToken cancellationToken = default);

    Task TrashSelectedFileAsync(Guid fileId, CancellationToken cancellationToken = default);
    Task TrashSelectedFolderAsync(Guid folderId, CancellationToken cancellationToken = default);

    Task MoveRemoteFileToTrashAsync(
        Guid fileId,
        long expectedVersion,
        string expectedHash,
        CancellationToken cancellationToken = default);

    Task RenameRemoteFileAsync(Guid fileId, string name, CancellationToken cancellationToken = default);

    Task MoveRemoteFileAsync(Guid fileId, Guid destinationFolderId, CancellationToken cancellationToken = default);

    Task RenameRemoteFolderAsync(Guid folderId, string name, CancellationToken cancellationToken = default);

    Task MoveRemoteFolderAsync(Guid folderId, Guid destinationFolderId, CancellationToken cancellationToken = default);

    Task DeleteItemAsync(
        Guid syncItemId,
        CancellationToken cancellationToken = default);

    Task<RemoteFolder> CreateRemoteFolderAsync(
    Guid parentFolderId,
    string folderName,
    CancellationToken cancellationToken = default);

    Task<RemoteFile> CreateRemoteFileAsync(
        Guid folderId,
        string fileName,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default);

    Task<RemoteFile> GetRemoteFileAsync(
        Guid fileId,
        CancellationToken cancellationToken = default);

    Task<RemoteFolder> GetRemoteFolderAsync(
        Guid folderId,
        CancellationToken cancellationToken = default);

    Task<SyncDevice?> GetCurrentDeviceAsync(
        CancellationToken cancellationToken = default);

    Task<AtomicSyncFolderResult> CreateAtomicRemoteFolderAsync(
        Guid parentFolderId,
        string folderName,
        string localPath,
        CancellationToken cancellationToken = default);

    Task<AtomicSyncFileResult> CreateAtomicRemoteFileAsync(
        Guid folderId,
        string fileName,
        string localPath,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default);
}