using System.Net.Http.Json;
using GestorArchivos.Sync.Windows.Api;
using GestorArchivos.Sync.Windows.Models;
using GestorArchivos.Sync.Windows.Monitoring;
using GestorArchivos.Sync.Windows.Services;
using GestorArchivos.Sync.Windows.Storage;

namespace GestorArchivos.Sync.Windows.Sync;

public sealed class ChangePushService
{
    private readonly HttpClient _httpClient;
    private readonly ISyncApiClient _syncApiClient;
    private readonly ILocalFileStore _localFileStore;
    private readonly IUploadService _uploadService;

    private static readonly string SelectionsPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData),
        "GestorArchivos",
        "Sync",
        "selections.json");

    public ChangePushService(
        HttpClient httpClient,
        ISyncApiClient syncApiClient,
        ILocalFileStore localFileStore,
        IUploadService uploadService)
    {
        _httpClient = httpClient;
        _syncApiClient = syncApiClient;
        _localFileStore = localFileStore;
        _uploadService = uploadService;
    }

    public async Task PushAsync(
        LocalFileSystemChange change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        if (File.Exists(SelectionsPath))
        {
            throw new InvalidOperationException(
                "La sincronización tradicional está suspendida " +
                "mientras exista una configuración selectiva.");
        }

        // No propagar eliminaciones automáticamente.
        if (change.Type == LocalFileSystemChangeType.Deleted)
            return;

        var items = await _syncApiClient.GetItemsAsync(
            cancellationToken);

        switch (change.Type)
        {
            case LocalFileSystemChangeType.Created:
                await CreateAsync(
                    change,
                    items,
                    cancellationToken);
                break;

            case LocalFileSystemChangeType.Changed:
                await UpdateAsync(
                    change,
                    items,
                    cancellationToken);
                break;

            case LocalFileSystemChangeType.Renamed:
                await RenameOrMoveAsync(
                    change,
                    items,
                    cancellationToken);
                break;
        }
    }

    private async Task CreateAsync(
        LocalFileSystemChange change,
        IReadOnlyList<SyncItem> items,
        CancellationToken cancellationToken)
    {
        if (FindByPath(items, change.RelativePath) is not null)
            return;

        if (change.IsDirectory)
        {
            if (!_localFileStore.DirectoryExists(change.RelativePath))
                return;
        }
        else
        {
            if (!_localFileStore.FileExists(change.RelativePath))
                return;
        }

        var parentFolderId = await ResolveParentFolderIdAsync(
            change.RelativePath,
            items,
            cancellationToken);

        var name = Path.GetFileName(change.RelativePath);

        if (string.IsNullOrWhiteSpace(name))
            return;

        if (change.IsDirectory)
        {
            var result = await _syncApiClient.CreateAtomicRemoteFolderAsync(
                parentFolderId,
                name,
                change.RelativePath,
                cancellationToken);

            if (result.SyncItemId == Guid.Empty ||
                result.RemoteFolderId == Guid.Empty ||
                result.CarpetaPadreId != parentFolderId ||
                !string.Equals(
                    result.Nombre,
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Respuesta atómica inválida para '{change.RelativePath}'.");
            }

            return;
        }

        await using var stream = await _localFileStore.OpenReadAsync(
            change.RelativePath,
            cancellationToken);

        var created = await _syncApiClient.CreateAtomicRemoteFileAsync(
            parentFolderId,
            name,
            change.RelativePath,
            stream,
            "application/octet-stream",
            cancellationToken);

        if (created.SyncItemId == Guid.Empty ||
            created.RemoteFileId == Guid.Empty ||
            created.CarpetaId != parentFolderId ||
            created.Version < 1 ||
            string.IsNullOrWhiteSpace(created.HashSha256) ||
            !string.Equals(
                created.Nombre,
                name,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Respuesta atómica inválida para '{change.RelativePath}'.");
        }
    }

    private async Task UpdateAsync(
        LocalFileSystemChange change,
        IReadOnlyList<SyncItem> items,
        CancellationToken cancellationToken)
    {
        if (change.IsDirectory)
            return;

        if (!_localFileStore.FileExists(change.RelativePath))
            return;

        var item = FindByPath(
            items,
            change.RelativePath);

        if (item is null)
        {
            await CreateAsync(
                change,
                items,
                cancellationToken);

            return;
        }

        if (!item.ArchivoRemotoId.HasValue)
            return;

        var remote = await _syncApiClient.GetRemoteFileAsync(
            item.ArchivoRemotoId.Value,
            cancellationToken);

        await _uploadService.UploadAsync(
            remote.Id,
            change.RelativePath,
            remote.Version,
            remote.HashSha256,
            cancellationToken);
    }

    private async Task RenameOrMoveAsync(
        LocalFileSystemChange change,
        IReadOnlyList<SyncItem> items,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(change.PreviousRelativePath))
            return;

        if (change.IsDirectory)
        {
            if (!_localFileStore.DirectoryExists(change.RelativePath))
                return;
        }
        else
        {
            if (!_localFileStore.FileExists(change.RelativePath))
                return;
        }

        var item = FindByPath(
            items,
            change.PreviousRelativePath);

        if (item is null)
        {
            await CreateAsync(
                change,
                items,
                cancellationToken);

            return;
        }

        var oldName = Path.GetFileName(
            change.PreviousRelativePath);

        var newName = Path.GetFileName(
            change.RelativePath);

        if (string.IsNullOrWhiteSpace(newName))
            return;

        var oldParent = NormalizeDirectory(
            Path.GetDirectoryName(change.PreviousRelativePath));

        var newParent = NormalizeDirectory(
            Path.GetDirectoryName(change.RelativePath));

        var nameChanged = !string.Equals(
            oldName,
            newName,
            StringComparison.Ordinal);

        var parentChanged = !string.Equals(
            oldParent,
            newParent,
            StringComparison.OrdinalIgnoreCase);

        if (!nameChanged && !parentChanged)
        {
            await _syncApiClient.UpdateItemPathAsync(
                item.SyncItemId,
                change.RelativePath,
                cancellationToken);

            return;
        }

        if (item.ArchivoRemotoId.HasValue)
        {
            var fileId = item.ArchivoRemotoId.Value;

            if (parentChanged)
            {
                var destinationFolderId = await ResolveFolderIdAsync(
                    newParent,
                    items,
                    cancellationToken);

                using var response = await _httpClient.PutAsJsonAsync(
                    $"api/sync/files/{fileId}/move",
                    new { destinationFolderId },
                    cancellationToken);

                await EnsureSuccessAsync(
                    response,
                    cancellationToken);
            }

            if (nameChanged)
            {
                using var response = await _httpClient.PutAsJsonAsync(
                    $"api/sync/files/{fileId}/rename",
                    new { name = newName },
                    cancellationToken);

                await EnsureSuccessAsync(
                    response,
                    cancellationToken);
            }
        }
        else if (item.CarpetaRemotaId.HasValue)
        {
            var folderId = item.CarpetaRemotaId.Value;

            if (parentChanged)
            {
                var destinationFolderId = await ResolveFolderIdAsync(
                    newParent,
                    items,
                    cancellationToken);

                using var response = await _httpClient.PutAsJsonAsync(
                    $"api/sync/folders/{folderId}/move",
                    new { destinationFolderId },
                    cancellationToken);

                await EnsureSuccessAsync(
                    response,
                    cancellationToken);
            }

            if (nameChanged)
            {
                using var response = await _httpClient.PutAsJsonAsync(
                    $"api/sync/folders/{folderId}/rename",
                    new { name = newName },
                    cancellationToken);

                await EnsureSuccessAsync(
                    response,
                    cancellationToken);
            }
        }
        else
        {
            return;
        }

        await _syncApiClient.UpdateItemPathAsync(
            item.SyncItemId,
            change.RelativePath,
            cancellationToken);

        if (item.CarpetaRemotaId.HasValue)
        {
            await UpdateDescendantPathsAsync(
                change.PreviousRelativePath,
                change.RelativePath,
                item.SyncItemId,
                items,
                cancellationToken);
        }
    }

    private async Task<Guid> ResolveParentFolderIdAsync(
        string relativePath,
        IReadOnlyList<SyncItem> items,
        CancellationToken cancellationToken)
    {
        var parentPath = NormalizeDirectory(
            Path.GetDirectoryName(relativePath));

        return await ResolveFolderIdAsync(
            parentPath,
            items,
            cancellationToken);
    }

    private async Task<Guid> ResolveFolderIdAsync(
        string relativePath,
        IReadOnlyList<SyncItem> items,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath == ".")
        {
            var snapshot = await _syncApiClient.GetSnapshotAsync(
                cancellationToken);

            if (snapshot.RootFolderId == Guid.Empty)
            {
                throw new InvalidOperationException(
                    "El servidor no devolvió una raíz Sync válida.");
            }

            return snapshot.RootFolderId;
        }

        var folder = items.FirstOrDefault(x =>
            x.CarpetaRemotaId.HasValue &&
            string.Equals(
                NormalizeDirectory(x.RutaLocal),
                NormalizeDirectory(relativePath),
                StringComparison.OrdinalIgnoreCase));

        if (folder?.CarpetaRemotaId is Guid folderId)
            return folderId;

        throw new InvalidOperationException(
            $"No existe mapeo Sync para la carpeta local '{relativePath}'.");
    }

    private async Task UpdateDescendantPathsAsync(
        string oldFolderPath,
        string newFolderPath,
        Guid renamedItemId,
        IReadOnlyList<SyncItem> items,
        CancellationToken cancellationToken)
    {
        var oldPrefix =
            NormalizeDirectory(oldFolderPath) +
            Path.DirectorySeparatorChar;

        foreach (var descendant in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (descendant.SyncItemId == renamedItemId)
                continue;

            var currentPath = NormalizeDirectory(
                descendant.RutaLocal);

            if (!currentPath.StartsWith(
                oldPrefix,
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suffix = currentPath[oldPrefix.Length..];

            var newPath = Path.Combine(
                newFolderPath,
                suffix);

            await _syncApiClient.UpdateItemPathAsync(
                descendant.SyncItemId,
                newPath,
                cancellationToken);
        }
    }

    private static SyncItem? FindByPath(
        IEnumerable<SyncItem> items,
        string relativePath)
    {
        var normalized = NormalizeDirectory(relativePath);

        return items.FirstOrDefault(x =>
            string.Equals(
                NormalizeDirectory(x.RutaLocal),
                normalized,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == ".")
            return string.Empty;

        return path
            .Trim()
            .Replace(
                Path.AltDirectorySeparatorChar,
                Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(
            cancellationToken);

        throw new SyncApiException(
            response.StatusCode,
            body);
    }
}