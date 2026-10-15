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