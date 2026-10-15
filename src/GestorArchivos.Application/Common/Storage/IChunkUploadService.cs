namespace GestorArchivos.Application.Common.Storage;

public interface IChunkUploadService
{
    Task<ChunkUploadSession> InitializeAsync(
        Guid ownerId,
        Guid folderId,
        string fileName,
        long fileSize,
        string contentType,
        CancellationToken cancellationToken = default);

    Task UploadChunkAsync(
        Guid uploadId,
        Guid ownerId,
        int chunkIndex,
        Stream content,
        long? contentLength,
        CancellationToken cancellationToken = default);

    Task<ChunkUploadStatus?> GetStatusAsync(
        Guid uploadId,
        Guid ownerId,
        CancellationToken cancellationToken = default);

    Task<CompletedChunkUpload> CompleteAsync(
        Guid uploadId,
        Guid ownerId,
        CancellationToken cancellationToken = default);

    Task CancelAsync(
        Guid uploadId,
        Guid ownerId,
        CancellationToken cancellationToken = default);
}

public sealed record ChunkUploadSession(
    Guid UploadId,
    int ChunkSize,
    int ExpectedChunkCount,
    DateTime ExpiresAtUtc);

public sealed record ChunkUploadStatus(
    Guid UploadId,
    Guid FolderId,
    string FileName,
    long FileSize,
    string ContentType,
    int ChunkSize,
    int ExpectedChunkCount,
    DateTime ExpiresAtUtc,
    IReadOnlyCollection<int> ReceivedChunks);

public sealed record CompletedChunkUpload(
    Guid UploadId,
    Guid FolderId,
    string FileName,
    string ContentType,
    long FileSize,
    string HashSha256,
    string RelativePath);