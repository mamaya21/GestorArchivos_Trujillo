using System.Security.Cryptography;
using System.Text.Json;
using GestorArchivos.Application.Common.Storage;
using Microsoft.Extensions.Options;

namespace GestorArchivos.Infrastructure.Storage;

public sealed class ChunkUploadService : IChunkUploadService
{
    private const long MaxFileSize = 4L * 1024 * 1024 * 1024;
    private const int ChunkSize = 64 * 1024 * 1024;
    private const int UploadExpirationHours = 24;

    private readonly string _rootPath;
    private readonly UploadLockManager _lockManager;

    public ChunkUploadService(
        IOptions<StorageOptions> options,
        UploadLockManager lockManager)
    {
        if (string.IsNullOrWhiteSpace(options.Value.RootPath))
            throw new InvalidOperationException(
                "Storage:RootPath no está configurado.");

        _rootPath = Path.GetFullPath(options.Value.RootPath);
        _lockManager = lockManager;
    }

    public async Task<ChunkUploadSession> InitializeAsync(
        Guid ownerId,
        Guid folderId,
        string fileName,
        long fileSize,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        if (fileSize <= 0 || fileSize > MaxFileSize)
            throw new ArgumentOutOfRangeException(
                nameof(fileSize),
                "Tamaño de archivo inválido.");

        var uploadId = Guid.NewGuid();
        var directory = GetUploadDirectory(uploadId);

        Directory.CreateDirectory(directory);

        var expectedChunkCount = checked(
            (int)((fileSize + ChunkSize - 1) / ChunkSize));

        var metadata = new UploadMetadata(
            ownerId,
            folderId,
            fileName,
            fileSize,
            string.IsNullOrWhiteSpace(contentType)
                ? "application/octet-stream"
                : contentType,
            ChunkSize,
            expectedChunkCount,
            DateTime.UtcNow,
            DateTime.UtcNow.AddHours(UploadExpirationHours));

        try
        {
            await File.WriteAllTextAsync(
                GetMetadataPath(directory),
                JsonSerializer.Serialize(metadata),
                cancellationToken);
        }
        catch
        {
            DeleteDirectory(uploadId);
            throw;
        }

        return new ChunkUploadSession(
            uploadId,
            ChunkSize,
            expectedChunkCount,
            metadata.ExpiresAtUtc);
    }

    public async Task UploadChunkAsync(
        Guid uploadId,
        Guid ownerId,
        int chunkIndex,
        Stream content,
        long? contentLength,
        CancellationToken cancellationToken = default)
    {
        var semaphore = _lockManager.Get(uploadId);
        await semaphore.WaitAsync(cancellationToken);

        try
        {
            var metadata = await RequireMetadataAsync(
                uploadId,
                ownerId,
                cancellationToken);

            EnsureNotExpired(uploadId, metadata);

            if (chunkIndex < 0 ||
                chunkIndex >= metadata.ExpectedChunkCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(chunkIndex),
                    "Índice de fragmento inválido.");
            }

            var expectedLength = GetExpectedChunkLength(
                metadata,
                chunkIndex);

            if (contentLength.HasValue &&
                contentLength.Value != expectedLength)
            {
                throw new InvalidDataException(
                    "Tamaño de fragmento inválido.");
            }

            var directory = GetUploadDirectory(uploadId);
            var chunkPath = GetChunkPath(directory, chunkIndex);

            if (File.Exists(chunkPath))
            {
                if (new FileInfo(chunkPath).Length != expectedLength)
                    throw new InvalidDataException(
                        "Existe un fragmento inválido con ese índice.");

                await using var storedStream = File.OpenRead(chunkPath);
                var storedHash = await SHA256.HashDataAsync(storedStream, cancellationToken);
                var incomingHash = await SHA256.HashDataAsync(content, cancellationToken);
                if (!CryptographicOperations.FixedTimeEquals(storedHash, incomingHash))
                    throw new InvalidDataException("El contenido del fragmento no coincide.");
                return;
            }

            var tempPath =
                chunkPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                long bytesWritten = 0;

                await using (var output = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.Asynchronous |
                    FileOptions.SequentialScan))
                {
                    var buffer = new byte[1024 * 1024];

                    while (true)
                    {
                        var read = await content.ReadAsync(
                            buffer.AsMemory(0, buffer.Length),
                            cancellationToken);

                        if (read == 0)
                            break;

                        bytesWritten += read;

                        if (bytesWritten > expectedLength)
                            throw new InvalidDataException(
                                "El fragmento supera el tamaño esperado.");

                        await output.WriteAsync(
                            buffer.AsMemory(0, read),
                            cancellationToken);
                    }
                }

                if (bytesWritten != expectedLength)
                    throw new InvalidDataException(
                        "El fragmento está incompleto.");

                File.Move(tempPath, chunkPath);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<ChunkUploadStatus?> GetStatusAsync(
        Guid uploadId,
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        var semaphore = _lockManager.Get(uploadId);
        await semaphore.WaitAsync(cancellationToken);

        try
        {
            var metadata = await ReadMetadataAsync(
                uploadId,
                cancellationToken);

            if (metadata is null || metadata.OwnerId != ownerId)
                return null;

            EnsureNotExpired(uploadId, metadata);

            var directory = GetUploadDirectory(uploadId);

            var chunks = Directory
                .EnumerateFiles(directory, "*.chunk")
                .Select(Path.GetFileNameWithoutExtension)
                .Select(x =>
                    int.TryParse(x, out var index)
                        ? index
                        : -1)
                .Where(x =>
                    x >= 0 &&
                    x < metadata.ExpectedChunkCount)
                .OrderBy(x => x)
                .ToArray();

            return new ChunkUploadStatus(
                uploadId,
                metadata.FolderId,
                metadata.FileName,
                metadata.FileSize,
                metadata.ContentType,
                metadata.ChunkSize,
                metadata.ExpectedChunkCount,
                metadata.ExpiresAtUtc,
                chunks);
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<CompletedChunkUpload> CompleteAsync(
        Guid uploadId,
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        var semaphore = _lockManager.Get(uploadId);
        await semaphore.WaitAsync(cancellationToken);

        string? finalPath = null;

        try
        {
            var metadata = await RequireMetadataAsync(
                uploadId,
                ownerId,
                cancellationToken);

            EnsureNotExpired(uploadId, metadata);

            var directory = GetUploadDirectory(uploadId);

            for (var index = 0;
                 index < metadata.ExpectedChunkCount;
                 index++)
            {
                var chunkPath = GetChunkPath(directory, index);

                if (!File.Exists(chunkPath))
                    throw new InvalidDataException(
                        $"Falta el fragmento {index}.");

                var expectedLength = GetExpectedChunkLength(
                    metadata,
                    index);

                if (new FileInfo(chunkPath).Length != expectedLength)
                    throw new InvalidDataException(
                        $"El fragmento {index} tiene un tamaño inválido.");
            }

            var fileId = Guid.NewGuid();
            var extension = Path.GetExtension(metadata.FileName);

            var relativePath = Path.Combine(
                DateTime.UtcNow.ToString("yyyy"),
                DateTime.UtcNow.ToString("MM"),
                $"{fileId:N}{extension}");

            finalPath = GetSafeStoragePath(relativePath);

            Directory.CreateDirectory(
                Path.GetDirectoryName(finalPath)!);

            using var sha256 = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);

            long assembledBytes = 0;

            await using (var output = new FileStream(
                finalPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan))
            {
                var buffer = new byte[1024 * 1024];

                for (var index = 0;
                     index < metadata.ExpectedChunkCount;
                     index++)
                {
                    var chunkPath = GetChunkPath(
                        directory,
                        index);

                    await using var input = new FileStream(
                        chunkPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        1024 * 1024,
                        FileOptions.Asynchronous |
                        FileOptions.SequentialScan);

                    while (true)
                    {
                        var read = await input.ReadAsync(
                            buffer.AsMemory(0, buffer.Length),
                            cancellationToken);

                        if (read == 0)
                            break;

                        assembledBytes += read;

                        sha256.AppendData(
                            buffer,
                            0,
                            read);

                        await output.WriteAsync(
                            buffer.AsMemory(0, read),
                            cancellationToken);
                    }
                }
            }

            if (assembledBytes != metadata.FileSize)
                throw new InvalidDataException(
                    "El tamaño ensamblado no coincide con el esperado.");

            var hash = Convert.ToHexString(
                sha256.GetHashAndReset());

            var result = new CompletedChunkUpload(
                uploadId,
                metadata.FolderId,
                metadata.FileName,
                metadata.ContentType,
                metadata.FileSize,
                hash,
                relativePath);

            finalPath = null;

            return result;
        }
        catch
        {
            if (finalPath is not null &&
                File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }

            throw;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task CancelAsync(
        Guid uploadId,
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        var semaphore = _lockManager.Get(uploadId);
        await semaphore.WaitAsync(cancellationToken);

        try
        {
            var metadata = await ReadMetadataAsync(
                uploadId,
                cancellationToken);

            if (metadata is null)
                return;

            if (metadata.OwnerId != ownerId)
                throw new UnauthorizedAccessException();

            DeleteDirectory(uploadId);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<UploadMetadata> RequireMetadataAsync(
        Guid uploadId,
        Guid ownerId,
        CancellationToken cancellationToken)
    {
        var metadata = await ReadMetadataAsync(
            uploadId,
            cancellationToken);

        if (metadata is null)
            throw new FileNotFoundException(
                "La sesión de carga no existe.");

        if (metadata.OwnerId != ownerId)
            throw new UnauthorizedAccessException();

        return metadata;
    }

    private async Task<UploadMetadata?> ReadMetadataAsync(
        Guid uploadId,
        CancellationToken cancellationToken)
    {
        var path = GetMetadataPath(
            GetUploadDirectory(uploadId));

        if (!File.Exists(path))
            return null;

        var json = await File.ReadAllTextAsync(
            path,
            cancellationToken);

        return JsonSerializer.Deserialize<UploadMetadata>(json);
    }

    private static long GetExpectedChunkLength(
        UploadMetadata metadata,
        int chunkIndex)
    {
        if (chunkIndex < metadata.ExpectedChunkCount - 1)
            return metadata.ChunkSize;

        return metadata.FileSize -
               ((long)chunkIndex * metadata.ChunkSize);
    }

    private void EnsureNotExpired(
        Guid uploadId,
        UploadMetadata metadata)
    {
        if (metadata.ExpiresAtUtc > DateTime.UtcNow)
            return;

        DeleteDirectory(uploadId);

        throw new TimeoutException(
            "La sesión de carga ha expirado.");
    }

    private string GetUploadDirectory(Guid uploadId)
    {
        return GetSafeStoragePath(
            Path.Combine(
                ".uploads",
                uploadId.ToString("N")));
    }

    private static string GetMetadataPath(string directory)
    {
        return Path.Combine(directory, "metadata.json");
    }

    private static string GetChunkPath(
        string directory,
        int chunkIndex)
    {
        return Path.Combine(
            directory,
            $"{chunkIndex:D8}.chunk");
    }

    private void DeleteDirectory(Guid uploadId)
    {
        var directory = GetUploadDirectory(uploadId);

        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private string GetSafeStoragePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidOperationException(
                "No se permiten rutas absolutas.");

        var fullPath = Path.GetFullPath(
            Path.Combine(_rootPath, relativePath));

        var rootWithSeparator =
            _rootPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(
                rootWithSeparator,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Ruta de almacenamiento inválida.");
        }

        return fullPath;
    }

    private sealed record UploadMetadata(
        Guid OwnerId,
        Guid FolderId,
        string FileName,
        long FileSize,
        string ContentType,
        int ChunkSize,
        int ExpectedChunkCount,
        DateTime CreatedAtUtc,
        DateTime ExpiresAtUtc);
}