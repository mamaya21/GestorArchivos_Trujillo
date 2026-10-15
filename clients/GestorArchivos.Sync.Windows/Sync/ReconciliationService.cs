using System.Net;
using System.Security.Cryptography;
using GestorArchivos.Sync.Windows.Api;
using GestorArchivos.Sync.Windows.Models;
using GestorArchivos.Sync.Windows.Monitoring;
using GestorArchivos.Sync.Windows.Services;
using GestorArchivos.Sync.Windows.Storage;

namespace GestorArchivos.Sync.Windows.Sync;

public sealed class ReconciliationService
{
    private readonly ISyncApiClient _syncApiClient;
    private readonly ILocalFileStore _fileStore;
    private readonly IDownloadService _downloadService;
    private readonly IUploadService _uploadService;
    private readonly IConflictService _conflictService;
    private readonly ISyncBaselineStore _baselineStore;
    private readonly ILocalChangeMonitor _localChangeMonitor;

    private static readonly string SelectionsPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData),
        "GestorArchivos",
        "Sync",
        "selections.json");

    public ReconciliationService(
        ISyncApiClient syncApiClient,
        ILocalFileStore fileStore,
        IDownloadService downloadService,
        IUploadService uploadService,
        IConflictService conflictService,
        ISyncBaselineStore baselineStore,
        ILocalChangeMonitor localChangeMonitor,
        ChangePushService changePushService)
    {
        _syncApiClient = syncApiClient;
        _fileStore = fileStore;
        _downloadService = downloadService;
        _uploadService = uploadService;
        _conflictService = conflictService;
        _baselineStore = baselineStore;
        _localChangeMonitor = localChangeMonitor;

        // Compatibilidad con la inyección de dependencias.
        _ = changePushService;
    }

    public async Task ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        // La reconciliación tradicional no debe ejecutarse
        // mientras exista una configuración selectiva.
        if (File.Exists(SelectionsPath))
        {
            throw new InvalidOperationException(
                "La reconciliación tradicional está suspendida " +
                "mientras exista una configuración selectiva.");
        }

        using var suppression =
            _localChangeMonitor.SuppressChanges();

        var items = await _syncApiClient.GetItemsAsync(
            cancellationToken);

        var baselineItems = await _baselineStore.LoadAsync(
            cancellationToken);

        var baseline = baselineItems.ToDictionary(
            x => x.SyncItemId);

        if (baseline.Count == 0)
        {
            await CreateBaselineAsync(
                items,
                baselineItems,
                cancellationToken);

            return;
        }

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            baseline.TryGetValue(
                item.SyncItemId,
                out var previous);

            if (item.Tipo == SyncItemType.Folder)
            {
                await ReconcileFolderAsync(
                    item,
                    previous,
                    cancellationToken);

                continue;
            }

            if (item.Tipo == SyncItemType.File)
            {
                await ReconcileFileAsync(
                    item,
                    previous,
                    cancellationToken);
            }
        }

        var currentItems = await _syncApiClient.GetItemsAsync(
            cancellationToken);

        await CreateBaselineAsync(
            currentItems,
            baselineItems,
            cancellationToken);
    }

    private async Task ReconcileFolderAsync(
        SyncItem item,
        SyncBaselineItem? previous,
        CancellationToken cancellationToken)
    {
        if (!item.CarpetaRemotaId.HasValue)
            return;

        try
        {
            await _syncApiClient.GetRemoteFolderAsync(
                item.CarpetaRemotaId.Value,
                cancellationToken);
        }
        catch (SyncApiException ex)
            when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // No eliminar carpetas locales.
            return;
        }

        if (_fileStore.DirectoryExists(item.RutaLocal))
            return;

        // No recrear carpetas previamente sincronizadas
        // que podrían estar en unidades desconectadas.
        if (previous is null)
        {
            _fileStore.CreateDirectory(
                item.RutaLocal);
        }
    }

    private async Task ReconcileFileAsync(
        SyncItem item,
        SyncBaselineItem? previous,
        CancellationToken cancellationToken)
    {
        if (!item.ArchivoRemotoId.HasValue)
            return;

        RemoteFile remote;

        try
        {
            remote = await _syncApiClient.GetRemoteFileAsync(
                item.ArchivoRemotoId.Value,
                cancellationToken);
        }
        catch (SyncApiException ex)
            when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // No eliminar archivos locales.
            return;
        }

        var localExists =
            _fileStore.FileExists(item.RutaLocal);

        if (previous is null)
        {
            if (!localExists)
            {
                await _downloadService.DownloadAsync(
                    remote.Id,
                    item.RutaLocal,
                    cancellationToken);
            }

            return;
        }

        var remoteChanged =
            previous.RemoteVersion != remote.Version ||
            !HashesEqual(
                previous.RemoteHash,
                remote.HashSha256);

        if (!localExists)
        {
            // No interpretar como eliminación.
            return;
        }

        var localHash = await CalculateHashAsync(
            item.RutaLocal,
            cancellationToken);

        // Si ambos archivos ya son idénticos,
        // no se necesita transferir información.
        if (HashesEqual(localHash, remote.HashSha256))
            return;

        var localChanged = !HashesEqual(
            previous.LocalHash,
            localHash);

        if (!localChanged && !remoteChanged)
            return;

        if (localChanged && !remoteChanged)
        {
            await _uploadService.UploadAsync(
                remote.Id,
                item.RutaLocal,
                remote.Version,
                remote.HashSha256,
                cancellationToken);

            return;
        }

        if (!localChanged && remoteChanged)
        {
            // Reemplazar únicamente si el contenido local
            // sigue teniendo el hash que acabamos de verificar.
            await _downloadService.DownloadIfUnchangedAsync(
                remote.Id,
                item.RutaLocal,
                localHash,
                cancellationToken);

            return;
        }

        if (localChanged && remoteChanged)
        {
            // Conservar una copia del archivo local
            // antes de descargar la versión remota.
            await _conflictService.CreateConflictCopyAsync(
                new SyncConflict
                {
                    FileId = remote.Id,
                    LocalPath = item.RutaLocal,
                    LocalVersion =
                        previous.RemoteVersion ?? 0,
                    RemoteVersion = remote.Version,
                    LocalHash = localHash,
                    RemoteHash = remote.HashSha256,
                    DetectedUtc = DateTime.UtcNow
                },
                cancellationToken);

            // Si el archivo local cambió después de calcular
            // su hash, se cancela el reemplazo.
            await _downloadService.DownloadIfUnchangedAsync(
                remote.Id,
                item.RutaLocal,
                localHash,
                cancellationToken);

            return;
        }

        throw new InvalidOperationException(
            $"No se pudo reconciliar el archivo '{item.RutaLocal}'.");
    }

    private async Task CreateBaselineAsync(
        IReadOnlyList<SyncItem> items,
        IReadOnlyList<SyncBaselineItem> previousItems,
        CancellationToken cancellationToken)
    {
        var baseline = previousItems.ToDictionary(
            x => x.SyncItemId);

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(item.RutaLocal))
                continue;

            baseline.TryGetValue(
                item.SyncItemId,
                out var previous);

            if (item.Tipo == SyncItemType.Folder)
            {
                if (_fileStore.DirectoryExists(item.RutaLocal))
                {
                    baseline[item.SyncItemId] =
                        new SyncBaselineItem
                        {
                            SyncItemId = item.SyncItemId,
                            RemoteFolderId = item.CarpetaRemotaId,
                            Type = SyncItemType.Folder,
                            LocalPath = item.RutaLocal,
                            LastSyncedUtc = DateTime.UtcNow
                        };
                }

                continue;
            }

            if (!item.ArchivoRemotoId.HasValue)
                continue;

            RemoteFile remote;

            try
            {
                remote = await _syncApiClient.GetRemoteFileAsync(
                    item.ArchivoRemotoId.Value,
                    cancellationToken);
            }
            catch (SyncApiException ex)
                when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Conservar el baseline anterior.
                continue;
            }

            if (!_fileStore.FileExists(item.RutaLocal))
            {
                // Conservar el baseline anterior.
                continue;
            }

            var localHash = await CalculateHashAsync(
                item.RutaLocal,
                cancellationToken);

            if (!HashesEqual(localHash, remote.HashSha256))
            {
                // Existe una diferencia pendiente.
                // No registrar un falso estado sincronizado.
                continue;
            }

            baseline[item.SyncItemId] =
                new SyncBaselineItem
                {
                    SyncItemId = item.SyncItemId,
                    RemoteFileId = item.ArchivoRemotoId,
                    Type = SyncItemType.File,
                    LocalPath = item.RutaLocal,
                    LocalHash = localHash,
                    RemoteHash = remote.HashSha256,
                    RemoteVersion = remote.Version,
                    LastSyncedUtc = DateTime.UtcNow
                };
        }

        await _baselineStore.SaveAsync(
            baseline.Values.ToList(),
            cancellationToken);
    }

    private async Task<string> CalculateHashAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        await using var stream =
            await _fileStore.OpenReadAsync(
                relativePath,
                cancellationToken);

        var hash = await SHA256.HashDataAsync(
            stream,
            cancellationToken);

        return Convert.ToHexString(hash);
    }

    private static bool HashesEqual(
        string? left,
        string? right)
    {
        return string.Equals(
            left,
            right,
            StringComparison.OrdinalIgnoreCase);
    }
}