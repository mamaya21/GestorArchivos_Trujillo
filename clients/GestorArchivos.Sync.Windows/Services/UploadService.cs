using System.Net;
using System.Net.Http.Headers;
using GestorArchivos.Sync.Windows.Api;
using GestorArchivos.Sync.Windows.Exceptions;
using GestorArchivos.Sync.Windows.Storage;
using GestorArchivos.Sync.Windows.Sync;

namespace GestorArchivos.Sync.Windows.Services;

public sealed class UploadService : IUploadService
{
    private readonly HttpClient _httpClient;
    private readonly ILocalFileStore _fileStore;
    private readonly FileLockService _fileLockService;

    public UploadService(
        HttpClient httpClient,
        ILocalFileStore fileStore,
        FileLockService fileLockService)
    {
        _httpClient = httpClient;
        _fileStore = fileStore;
        _fileLockService = fileLockService;
    }

    public async Task UploadAsync(
        Guid fileId,
        string relativePath,
        long expectedVersion,
        string expectedHash,
        CancellationToken cancellationToken = default)
    {
        if (!_fileStore.FileExists(relativePath))
        {
            throw new FileNotFoundException(
                $"No existe el archivo local '{relativePath}'.");
        }

        var lockAcquired = false;

        try
        {
            await _fileLockService.AcquireAsync(
                fileId,
                cancellationToken);

            lockAcquired = true;

            await using var stream =
                await _fileStore.OpenReadAsync(
                    relativePath,
                    cancellationToken);

            using var content =
                new StreamContent(stream);

            content.Headers.ContentType =
                new MediaTypeHeaderValue(
                    "application/octet-stream");

            var url =
                $"api/sync/files/{fileId}/content?expectedVersion={expectedVersion}&expectedHash={Uri.EscapeDataString(expectedHash)}";

            using var response =
                await _httpClient.PutAsync(
                    url,
                    content,
                    cancellationToken);

            if (response.StatusCode ==
                HttpStatusCode.Conflict)
            {
                throw new SyncConflictException(
                    $"Conflicto sincronizando {relativePath}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var body =
                    await response.Content.ReadAsStringAsync(
                        cancellationToken);

                throw new SyncApiException(
                    response.StatusCode,
                    body);
            }
        }
        finally
        {
            if (lockAcquired)
            {
                try
                {
                    await _fileLockService.ReleaseAsync(
                        fileId,
                        CancellationToken.None);
                }
                catch
                {
                    // El lock expira automáticamente en el servidor.
                }
            }
        }
    }
}