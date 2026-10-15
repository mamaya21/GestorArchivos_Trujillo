
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using GestorArchivos.Sync.Windows.Api;
using GestorArchivos.Sync.Windows.Storage;

namespace GestorArchivos.Sync.Windows.Services;

public sealed class DownloadService : IDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly ILocalFileStore _fileStore;
    private readonly ISyncApiClient _apiClient;

    public DownloadService(
        HttpClient httpClient,
        ILocalFileStore fileStore,
        ISyncApiClient apiClient)
    {
        _httpClient = httpClient;
        _fileStore = fileStore;
        _apiClient = apiClient;
    }

    public Task DownloadAsync(
        Guid fileId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        return DownloadIfUnchangedAsync(
            fileId,
            relativePath,
            expectedLocalHash: null,
            cancellationToken);
    }

    public async Task DownloadIfUnchangedAsync(
        Guid fileId,
        string relativePath,
        string? expectedLocalHash,
        CancellationToken cancellationToken = default)
    {
        if (fileId == Guid.Empty)
        {
            throw new ArgumentException(
                "El identificador del archivo es obligatorio.",
                nameof(fileId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var destinationPath = _fileStore.GetFullPath(relativePath);

        var remote = await _apiClient.GetRemoteFileAsync(
            fileId,
            cancellationToken);

        if (remote.Version < 1)
        {
            throw new InvalidDataException(
                $"Versión remota inválida: {fileId}.");
        }

        if (!IsValidSha256(remote.HashSha256))
        {
            throw new InvalidDataException(
                $"SHA-256 remoto inválido: {fileId}.");
        }

        var expectedRemoteHash =
            remote.HashSha256.Trim().ToUpperInvariant();

        var expectedRemoteVersion = remote.Version;

        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"gestor-download-{Guid.NewGuid():N}.tmp");

        try
        {
            var requestUrl =
                $"api/sync/read/files/{fileId}/download" +
                $"?expectedVersion={expectedRemoteVersion.ToString(CultureInfo.InvariantCulture)}" +
                $"&expectedHash={expectedRemoteHash}";

            using var response = await _httpClient.GetAsync(
                requestUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.Conflict ||
                response.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                var body = await response.Content.ReadAsStringAsync(
                    cancellationToken);

                throw new SyncApiException(
                    response.StatusCode,
                    body);
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(
                    cancellationToken);

                throw new SyncApiException(
                    response.StatusCode,
                    body);
            }

            if (!response.Headers.TryGetValues(
                    "X-Sync-File-Version",
                    out var versionHeaders) ||
                !long.TryParse(
                    versionHeaders.SingleOrDefault(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var receivedVersion) ||
                receivedVersion != expectedRemoteVersion)
            {
                throw new InvalidDataException(
                    $"La versión HTTP recibida no coincide con la solicitada: '{relativePath}'.");
            }

            if (!response.Headers.TryGetValues(
                    "X-Sync-File-Sha256",
                    out var hashHeaders))
            {
                throw new InvalidDataException(
                    $"La respuesta no contiene el SHA-256 remoto: '{relativePath}'.");
            }

            var receivedHash = hashHeaders.SingleOrDefault();

            if (!HashesEqual(receivedHash, expectedRemoteHash))
            {
                throw new InvalidDataException(
                    $"El SHA-256 anunciado no coincide con la versión solicitada: '{relativePath}'.");
            }

            await using (var source =
                await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan))
            {
                await source.CopyToAsync(
                    destination,
                    cancellationToken);

                await destination.FlushAsync(cancellationToken);
                destination.Flush(true);
            }

            string downloadedHash;

            await using (var verification = new FileStream(
                temporaryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan))
            {
                downloadedHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(
                        verification,
                        cancellationToken));
            }

            if (!HashesEqual(downloadedHash, expectedRemoteHash))
            {
                throw new InvalidDataException(
                    $"SHA-256 incorrecto en la descarga: '{relativePath}'.");
            }

            // Se comprueba que la versión sigue siendo actual
            // antes de modificar el archivo local.
            var latest = await _apiClient.GetRemoteFileAsync(
                fileId,
                cancellationToken);

            if (latest.Version != expectedRemoteVersion ||
                !HashesEqual(
                    latest.HashSha256,
                    expectedRemoteHash))
            {
                throw new IOException(
                    $"El archivo remoto cambió durante la descarga: '{relativePath}'.");
            }

            await using var verifiedContent = new FileStream(
                temporaryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan);

            var written = await _fileStore.WriteIfUnchangedAsync(
                relativePath,
                verifiedContent,
                expectedLocalHash,
                cancellationToken);

            if (!written)
            {
                throw new IOException(
                    $"El archivo local cambió o existe sin autorización " +
                    $"de reemplazo: '{destinationPath}'. " +
                    "Se canceló la descarga para conservar los cambios locales.");
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool IsValidSha256(string? hash)
    {
        return !string.IsNullOrWhiteSpace(hash) &&
               hash.Length == 64 &&
               hash.All(Uri.IsHexDigit);
    }

    private static bool HashesEqual(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
               !string.IsNullOrWhiteSpace(right) &&
               string.Equals(
                   left.Trim(),
                   right.Trim(),
                   StringComparison.OrdinalIgnoreCase);
    }
}
