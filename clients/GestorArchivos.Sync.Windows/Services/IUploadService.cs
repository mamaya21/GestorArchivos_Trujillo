namespace GestorArchivos.Sync.Windows.Services;

public interface IUploadService
{
    Task UploadAsync(
        Guid fileId,
        string relativePath,
        long expectedVersion,
        string expectedHash,
        CancellationToken cancellationToken = default);
}