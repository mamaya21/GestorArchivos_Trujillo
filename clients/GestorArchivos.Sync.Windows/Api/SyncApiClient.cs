using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GestorArchivos.Sync.Windows.Models;
using GestorArchivos.Sync.Windows.Security;

namespace GestorArchivos.Sync.Windows.Api;

public sealed class SyncApiClient : ISyncApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ICredentialStore _credentialStore;

    public SyncApiClient(
        HttpClient httpClient,
        ICredentialStore credentialStore)
    {
        _httpClient = httpClient;
        _credentialStore = credentialStore;
    }

    public async Task<bool> ValidateTokenAsync(
        CancellationToken cancellationToken = default)
    {
        var token = await _credentialStore.GetTokenAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(token))
            return false;

        using var response = await _httpClient.PostAsJsonAsync(
            "api/sync/auth/validate",
            new { token },
            cancellationToken);

        if (response.StatusCode is
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return false;

        await EnsureSuccessAsync(response, cancellationToken);
        return true;
    }

    public async Task<SyncChangesResponse> GetChangesAsync(
        long afterId,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        if (afterId < 0)
            throw new ArgumentOutOfRangeException(nameof(afterId));

        limit = Math.Clamp(limit, 1, 500);

        using var response = await _httpClient.GetAsync(
            $"api/sync/changes?afterId={afterId}&limit={limit}",
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<SyncChangesResponse>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "La API devolvió una respuesta Sync vacía.");
    }

    public async Task<IReadOnlyList<SyncItem>> GetItemsAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            "api/sync/items", cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<List<SyncItem>>(
            cancellationToken: cancellationToken) ?? [];
    }

    public async Task<SyncSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            "api/sync/snapshot", cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<SyncSnapshot>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "El servidor devolvió un snapshot vacío.");
    }

    public async Task<SyncItem> RegisterItemAsync(
        string localPath,
        SyncItemType type,
        Guid? remoteFolderId,
        Guid? remoteFileId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "api/sync/items",
            new { localPath, type, remoteFolderId, remoteFileId },
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<SyncItem>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "El servidor devolvió un elemento de sincronización vacío.");
    }

    public async Task UpdateItemPathAsync(
        Guid syncItemId,
        string localPath,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutAsJsonAsync(
            $"api/sync/items/{syncItemId}/path",
            new { localPath },
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<bool> IsItemTrashedAsync(Guid syncItemId, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            $"api/sync/items/{syncItemId}/trash-status", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("trashed").GetBoolean();
    }

    public async Task DetachTrashedItemAsync(Guid syncItemId, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync(
            $"api/sync/items/{syncItemId}/detach-if-trashed", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task TrashSelectedFileAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"api/sync/files/{fileId}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task TrashSelectedFolderAsync(Guid folderId, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"api/sync/folders/{folderId}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task MoveRemoteFileToTrashAsync(
        Guid fileId,
        long expectedVersion,
        string expectedHash,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            $"api/sync/files/{fileId}/trash",
            new { expectedVersion, expectedHash },
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task RenameRemoteFileAsync(
        Guid fileId, string name,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutAsJsonAsync(
            $"api/sync/files/{fileId}/rename",
            new { name }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task MoveRemoteFileAsync(
        Guid fileId, Guid destinationFolderId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutAsJsonAsync(
            $"api/sync/files/{fileId}/move",
            new { destinationFolderId }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task RenameRemoteFolderAsync(Guid folderId, string name, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutAsJsonAsync($"api/sync/folders/{folderId}/rename", new { name }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task MoveRemoteFolderAsync(Guid folderId, Guid destinationFolderId, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutAsJsonAsync($"api/sync/folders/{folderId}/move", new { destinationFolderId }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task DeleteItemAsync(
        Guid syncItemId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync(
            $"api/sync/items/{syncItemId}", cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<RemoteFolder> CreateRemoteFolderAsync(
        Guid parentFolderId,
        string folderName,
        CancellationToken cancellationToken = default)
    {
        if (parentFolderId == Guid.Empty)
            throw new ArgumentException(
                "El identificador de la carpeta padre no es válido.",
                nameof(parentFolderId));

        ArgumentException.ThrowIfNullOrWhiteSpace(folderName);

        using var response = await _httpClient.PostAsJsonAsync(
            "api/sync/folders",
            new { parentFolderId, name = folderName },
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<RemoteFolder>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "El servidor devolvió una carpeta remota vacía.");
    }

    public async Task<RemoteFile> CreateRemoteFileAsync(
        Guid folderId,
        string fileName,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);

        using var form = new MultipartFormDataContent();
        using var fileContent = new StreamContent(content);

        fileContent.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(contentType)
                ? "application/octet-stream"
                : contentType);

        form.Add(fileContent, "file", fileName);
        form.Add(new StringContent(folderId.ToString()), "folderId");

        using var response = await _httpClient.PostAsync(
            "api/sync/files", form, cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<RemoteFile>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "El servidor devolvió un archivo remoto vacío.");
    }

    public async Task<RemoteFile> GetRemoteFileAsync(
        Guid fileId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            $"api/sync/read/files/{fileId}", cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<RemoteFile>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "El servidor devolvió un archivo remoto vacío.");
    }

    public async Task<RemoteFolder> GetRemoteFolderAsync(
        Guid folderId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            $"api/sync/read/folders/{folderId}", cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<RemoteFolder>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "El servidor devolvió una carpeta remota vacía.");
    }

    public async Task<SyncDevice?> GetCurrentDeviceAsync(
        CancellationToken cancellationToken = default)
    {
        var token = await _credentialStore.GetTokenAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(token))
            return null;

        using var response = await _httpClient.PostAsJsonAsync(
            "api/sync/auth/validate",
            new { token },
            cancellationToken);

        if (response.StatusCode is
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return null;

        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<SyncDevice>(
            cancellationToken: cancellationToken);
    }

    public async Task<AtomicSyncFolderResult> CreateAtomicRemoteFolderAsync(
        Guid parentFolderId,
        string folderName,
        string localPath,
        CancellationToken cancellationToken = default)
    {
        if (parentFolderId == Guid.Empty)
            throw new ArgumentException(
                "El identificador de la carpeta padre no es válido.",
                nameof(parentFolderId));

        ArgumentException.ThrowIfNullOrWhiteSpace(folderName);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        using var response = await _httpClient.PostAsJsonAsync(
            "api/sync/folders/atomic",
            new { parentFolderId, name = folderName, localPath },
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<AtomicSyncFolderResult>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "El servidor devolvió una respuesta de creación atómica vacía.");
    }

    public async Task<AtomicSyncFileResult> CreateAtomicRemoteFileAsync(
        Guid folderId,
        string fileName,
        string localPath,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        if (folderId == Guid.Empty)
            throw new ArgumentException(
                "Carpeta remota inválida.", nameof(folderId));

        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        ArgumentNullException.ThrowIfNull(content);

        if (content.CanSeek && content.Length - content.Position > 64L * 1024 * 1024)
            return await CreateResumableRemoteFileAsync(
                folderId, fileName, localPath, content, cancellationToken);

        using var form = new MultipartFormDataContent();
        using var fileContent = new StreamContent(content);

        fileContent.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(contentType)
                ? "application/octet-stream"
                : contentType);

        form.Add(fileContent, "file", fileName);
        form.Add(new StringContent(folderId.ToString()), "folderId");
        form.Add(new StringContent(localPath), "localPath");

        using var response = await _httpClient.PostAsync(
            "api/sync/files/atomic", form, cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<AtomicSyncFileResult>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "El servidor devolvió una respuesta de archivo atómico vacía.");
    }

    private async Task<AtomicSyncFileResult> CreateResumableRemoteFileAsync(
        Guid folderId, string fileName, string localPath, Stream content,
        CancellationToken cancellationToken)
    {
        var start = content.Position;
        var size = content.Length - start;
        var hash = Convert.ToHexString(
            await SHA256.HashDataAsync(content, cancellationToken));
        content.Position = start;

        var checkpointRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GestorArchivos", "Sync", "uploads");
        Directory.CreateDirectory(checkpointRoot);
        var key = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(folderId + "|" + localPath)));
        var checkpointPath = Path.Combine(checkpointRoot, key + ".json");

        ResumableCheckpoint? checkpoint = null;
        if (File.Exists(checkpointPath))
        {
            try
            {
                checkpoint = JsonSerializer.Deserialize<ResumableCheckpoint>(
                    await File.ReadAllTextAsync(checkpointPath, cancellationToken));
            }
            catch (JsonException)
            {
                checkpoint = null;
            }
        }

        if (checkpoint is not null &&
            (checkpoint.FileSize != size || checkpoint.Hash != hash ||
             checkpoint.FileName != fileName))
            checkpoint = null;

        ResumableStatus? status = null;
        if (checkpoint is not null)
        {
            using var lookup = await _httpClient.GetAsync(
                $"api/sync/uploads/{checkpoint.UploadId}/status", cancellationToken);
            if (lookup.StatusCode == HttpStatusCode.NotFound)
            {
                // The server removes the session after committing. If the final
                // HTTP response was lost, replay through the atomic endpoint:
                // it verifies the exact bytes before acknowledging the mapping.
                // A changed remote file produces a conflict, never false success.
                content.Position = start;
                using var form = new MultipartFormDataContent();
                using var fileContent = new StreamContent(content);
                fileContent.Headers.ContentType =
                    new MediaTypeHeaderValue("application/octet-stream");
                form.Add(fileContent, "file", fileName);
                form.Add(new StringContent(folderId.ToString()), "folderId");
                form.Add(new StringContent(localPath), "localPath");
                using var replay = await _httpClient.PostAsync(
                    "api/sync/files/atomic", form, cancellationToken);
                await EnsureSuccessAsync(replay, cancellationToken);
                var confirmed = await replay.Content.ReadFromJsonAsync<AtomicSyncFileResult>(
                    cancellationToken: cancellationToken)
                    ?? throw new InvalidOperationException("Confirmación de recuperación vacía.");
                File.Delete(checkpointPath);
                return confirmed;
            }
            else
            {
                await EnsureSuccessAsync(lookup, cancellationToken);
                status = await lookup.Content.ReadFromJsonAsync<ResumableStatus>(
                    cancellationToken: cancellationToken);
                if (status is null || status.FolderId != folderId ||
                    status.FileSize != size || status.FileName != fileName)
                {
                    checkpoint = null;
                    status = null;
                }
            }
        }

        if (checkpoint is null)
        {
            using var initialized = await _httpClient.PostAsJsonAsync(
                "api/sync/uploads/initialize",
                new { folderId, fileName, fileSize = size }, cancellationToken);
            await EnsureSuccessAsync(initialized, cancellationToken);
            var session = await initialized.Content.ReadFromJsonAsync<ResumableSession>(
                cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Sesión de carga vacía.");
            checkpoint = new ResumableCheckpoint(session.UploadId, size, hash, fileName);
            await File.WriteAllTextAsync(checkpointPath,
                JsonSerializer.Serialize(checkpoint), cancellationToken);
            status = new ResumableStatus(folderId, fileName, size,
                session.ChunkSize, session.ExpectedChunkCount, Array.Empty<int>());
        }

        if (status is null || status.ChunkSize <= 0 ||
            status.ChunkSize > 64 * 1024 * 1024)
            throw new InvalidOperationException("Estado de carga inválido.");

        var received = status.ReceivedChunks.ToHashSet();
        var buffer = new byte[status.ChunkSize];
        for (var index = 0; index < status.ExpectedChunkCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (received.Contains(index))
                continue;

            var offset = (long)index * status.ChunkSize;
            var length = (int)Math.Min(status.ChunkSize, size - offset);
            content.Position = start + offset;
            await content.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken);
            using var payload = new ByteArrayContent(buffer, 0, length);
            using var uploaded = await _httpClient.PutAsync(
                $"api/sync/uploads/{checkpoint.UploadId}/chunks/{index}",
                payload, cancellationToken);
            await EnsureSuccessAsync(uploaded, cancellationToken);
        }

        using var completed = await _httpClient.PostAsJsonAsync(
            $"api/sync/files/atomic/uploads/{checkpoint.UploadId}/complete",
            new { localPath }, cancellationToken);
        await EnsureSuccessAsync(completed, cancellationToken);
        var result = await completed.Content.ReadFromJsonAsync<AtomicSyncFileResult>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Confirmación de carga vacía.");
        File.Delete(checkpointPath);
        return result;
    }

    private sealed record ResumableCheckpoint(
        Guid UploadId, long FileSize, string Hash, string FileName);

    private sealed record ResumableSession(
        Guid UploadId, int ChunkSize, int ExpectedChunkCount);

    private sealed record ResumableStatus(
        Guid FolderId, string FileName, long FileSize,
        int ChunkSize, int ExpectedChunkCount, int[] ReceivedChunks);

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        throw new SyncApiException(response.StatusCode, body);
    }
}