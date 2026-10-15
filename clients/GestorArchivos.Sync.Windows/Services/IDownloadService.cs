namespace GestorArchivos.Sync.Windows.Services;

public interface IDownloadService
{
    Task DownloadAsync(
        Guid fileId,
        string relativePath,
        CancellationToken cancellationToken = default);

    Task DownloadIfUnchangedAsync(
        Guid fileId,
        string relativePath,
        string? expectedLocalHash,
        CancellationToken cancellationToken = default);
}