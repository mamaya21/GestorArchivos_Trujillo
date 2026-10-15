using System.Net;
using System.Security.Cryptography;
using GestorArchivos.Sync.Windows.Api;
using GestorArchivos.Sync.Windows.Configuration;
using GestorArchivos.Sync.Windows.Logging;
using GestorArchivos.Sync.Windows.Models;
using GestorArchivos.Sync.Windows.Monitoring;
using GestorArchivos.Sync.Windows.Services;
using GestorArchivos.Sync.Windows.State;
using GestorArchivos.Sync.Windows.Storage;
using Microsoft.Extensions.Options;

namespace GestorArchivos.Sync.Windows.Sync;

public sealed class ChangePullService
{
    private readonly ISyncApiClient _syncApiClient;
    private readonly IDownloadService _downloadService;
    private readonly ILocalFileStore _localFileStore;
    private readonly ILocalStateStore _stateStore;
    private readonly ISyncBaselineStore _baselineStore;
    private readonly IConflictService _conflictService;
    private readonly SyncRuntimeState _runtimeState;
    private readonly SyncOptions _options;
    private readonly SyncLog _log;
    private readonly ILocalChangeMonitor _localChangeMonitor;

    private static readonly string SelectionsPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData),
        "GestorArchivos",
        "Sync",
        "selections.json");

    public ChangePullService(
        ISyncApiClient syncApiClient,
        IDownloadService downloadService,
        ILocalFileStore localFileStore,
        ILocalStateStore stateStore,
        ISyncBaselineStore baselineStore,
        IConflictService conflictService,
        SyncRuntimeState runtimeState,
        IOptions<SyncOptions> options,
        SyncLog log,
        ILocalChangeMonitor localChangeMonitor)
    {
        _syncApiClient = syncApiClient;
        _downloadService = downloadService;
        _localFileStore = localFileStore;
        _stateStore = stateStore;
        _baselineStore = baselineStore;
        _conflictService = conflictService;
        _runtimeState = runtimeState;
        _options = options.Value;
        _log = log;
        _localChangeMonitor = localChangeMonitor;
    }

    public async Task PullAsync(
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(SelectionsPath))
        {
            throw new InvalidOperationException(
                "La descarga tradicional está suspendida " +
                "mientras exista una configuración selectiva.");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var response = await _syncApiClient.GetChangesAsync(
                _runtimeState.LastChangeId,
                _options.ChangeBatchSize,
                cancellationToken);

            if (response.Changes.Count == 0)
                break;

            foreach (var change in response.Changes.OrderBy(x => x.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!_runtimeState.DeviceId.HasValue ||
                    change.DispositivoOrigenId !=
                    _runtimeState.DeviceId.Value)
                {
                    using (_localChangeMonitor.SuppressChanges())
                    {
                        await ApplyChangeAsync(
                            change,
                            cancellationToken);
                    }
                }

                // Avanzar el cursor únicamente después
                // de procesar satisfactoriamente el cambio.
                _runtimeState.LastChangeId = change.Id;
                _runtimeState.LastSuccessfulSyncUtc = DateTime.UtcNow;

                await _stateStore.SaveAsync(
                    _runtimeState,
                    cancellationToken);

                _log.ChangeProcessed(change.Id);
            }

            if (!response.HasMore)
                break;
        }
    }

    private async Task ApplyChangeAsync(
        SyncChange change,
        CancellationToken cancellationToken)
    {
        if (change.ArchivoId.HasValue)
        {
            await ApplyFileChangeAsync(
                change.ArchivoId.Value,
                cancellationToken);

            return;
        }

        if (change.CarpetaId.HasValue)
        {
            await ApplyFolderChangeAsync(
                change.CarpetaId.Value,
                cancellationToken);
        }
    }

    private async Task ApplyFileChangeAsync(
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var items = await _syncApiClient.GetItemsAsync(
            cancellationToken);

        var item = items.FirstOrDefault(
            x => x.ArchivoRemotoId == fileId);

        RemoteFile remote;

        try
        {
            remote = await _syncApiClient.GetRemoteFileAsync(
                fileId,
                cancellationToken);
        }
        catch (SyncApiException ex)
            when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // No eliminar archivos locales.
            return;
        }

        var folderPath = await GetFolderPathAsync(
            remote.CarpetaId,
            items,
            cancellationToken);

        var newPath = Path.Combine(
            folderPath,
            remote.Nombre);

        // Validar la ruta antes de cualquier escritura.
        var newFullPath = _localFileStore.GetFullPath(newPath);

        if (item is not null &&
            !PathsEqual(item.RutaLocal, newPath))
        {
            // No mover ni sobrescribir automáticamente
            // archivos cuando cambia su ruta remota.
            return;
        }

        var localExists = File.Exists(newFullPath);

        // El hash debe permanecer disponible durante
        // toda la operación, no solamente dentro del if.
        string? localHash = null;

        if (localExists)
        {
            localHash = await CalculateHashAsync(
                newPath,
                cancellationToken);

            if (HashesEqual(localHash, remote.HashSha256))
            {
                if (item is null)
                {
                    await _syncApiClient.RegisterItemAsync(
                        newPath,
                        SyncItemType.File,
                        null,
                        fileId,
                        cancellationToken);
                }

                await UpdateBaselineAsync(
                    fileId,
                    newPath,
                    cancellationToken);

                return;
            }

            // Nunca reemplazar un archivo local
            // que no esté asociado al archivo remoto.
            if (item is null)
            {
                throw new IOException(
                    $"Existe un archivo local no asociado en '{newPath}'. " +
                    "Se bloqueó la descarga para evitar sobrescribirlo.");
            }

            var baselineItems = await _baselineStore.LoadAsync(
                cancellationToken);

            var previous = baselineItems.FirstOrDefault(
                x => x.SyncItemId == item.SyncItemId);

            // Sin baseline verificable, no se permite
            // reemplazar el contenido local.
            if (previous is null ||
                !HashesEqual(previous.LocalHash, localHash))
            {
                throw new IOException(
                    $"El archivo '{newPath}' contiene cambios locales " +
                    "o carece de un baseline confiable. " +
                    "Se bloqueó la sobrescritura.");
            }

            // Evitar descargar cuando el remoto
            // no cambió respecto del baseline.
            if (previous.RemoteVersion == remote.Version &&
                HashesEqual(
                    previous.RemoteHash,
                    remote.HashSha256))
            {
                return;
            }

            // Reemplazo condicionado al hash local.
            // Si el archivo cambia durante la descarga,
            // el almacenamiento rechazará la escritura.
            await _downloadService.DownloadIfUnchangedAsync(
                fileId,
                newPath,
                localHash,
                cancellationToken);
        }
        else
        {
            // Si existía una asociación anterior,
            // la ausencia del archivo no se interpreta
            // automáticamente como autorización para
            // recrearlo o reemplazarlo.
            if (item is not null)
                return;

            // Descarga inicial: crear únicamente
            // si el archivo continúa sin existir.
            await _downloadService.DownloadAsync(
                fileId,
                newPath,
                cancellationToken);
        }

        if (item is null)
        {
            await _syncApiClient.RegisterItemAsync(
                newPath,
                SyncItemType.File,
                null,
                fileId,
                cancellationToken);
        }

        await UpdateBaselineAsync(
            fileId,
            newPath,
            cancellationToken);
    }

    private async Task UpdateBaselineAsync(
        Guid fileId,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var updatedRemote = await _syncApiClient.GetRemoteFileAsync(
            fileId,
            cancellationToken);

        var downloadedHash = await CalculateHashAsync(
            relativePath,
            cancellationToken);

        if (!HashesEqual(
            downloadedHash,
            updatedRemote.HashSha256))
        {
            throw new IOException(
                $"El archivo remoto cambió durante la sincronización: " +
                $"'{relativePath}'.");
        }

        var currentItems = await _syncApiClient.GetItemsAsync(
            cancellationToken);

        var currentItem = currentItems.FirstOrDefault(
            x => x.ArchivoRemotoId == fileId);

        if (currentItem is null)
        {
            throw new InvalidOperationException(
                $"No se encontró la asociación Sync para '{relativePath}'.");
        }

        var baseline = (await _baselineStore.LoadAsync(
            cancellationToken)).ToDictionary(
                x => x.SyncItemId);

        baseline[currentItem.SyncItemId] = new SyncBaselineItem
        {
            SyncItemId = currentItem.SyncItemId,
            RemoteFileId = fileId,
            Type = SyncItemType.File,
            LocalPath = relativePath,
            LocalHash = downloadedHash,
            RemoteHash = updatedRemote.HashSha256,
            RemoteVersion = updatedRemote.Version,
            LastSyncedUtc = DateTime.UtcNow
        };

        await _baselineStore.SaveAsync(
            baseline.Values.ToList(),
            cancellationToken);
    }

    private async Task ApplyFolderChangeAsync(
        Guid folderId,
        CancellationToken cancellationToken)
    {
        var items = await _syncApiClient.GetItemsAsync(
            cancellationToken);

        var item = items.FirstOrDefault(
            x => x.CarpetaRemotaId == folderId);

        RemoteFolder remote;

        try
        {
            remote = await _syncApiClient.GetRemoteFolderAsync(
                folderId,
                cancellationToken);
        }
        catch (SyncApiException ex)
            when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        var path = await BuildRemoteFolderPathAsync(
            remote,
            items,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(path))
            return;

        if (item is not null &&
            !PathsEqual(item.RutaLocal, path))
        {
            // No mover carpetas que puedan contener
            // modificaciones locales pendientes.
            return;
        }

        if (!_localFileStore.DirectoryExists(path))
        {
            if (item is not null)
            {
                // Podría ser una unidad desconectada.
                return;
            }

            _localFileStore.CreateDirectory(path);
        }

        if (item is null && remote.CarpetaPadreId.HasValue)
        {
            await _syncApiClient.RegisterItemAsync(
                path,
                SyncItemType.Folder,
                folderId,
                null,
                cancellationToken);
        }
    }

    private async Task<string> BuildRemoteFolderPathAsync(
        RemoteFolder folder,
        IReadOnlyList<SyncItem> items,
        CancellationToken cancellationToken)
    {
        if (!folder.CarpetaPadreId.HasValue)
            return string.Empty;

        var parentPath = await GetFolderPathAsync(
            folder.CarpetaPadreId.Value,
            items,
            cancellationToken);

        return Path.Combine(
            parentPath,
            folder.Nombre);
    }

    private async Task<string> GetFolderPathAsync(
        Guid folderId,
        IReadOnlyList<SyncItem> items,
        CancellationToken cancellationToken)
    {
        var mapped = items.FirstOrDefault(
            x => x.CarpetaRemotaId == folderId);

        if (mapped is not null)
            return mapped.RutaLocal;

        var folder = await _syncApiClient.GetRemoteFolderAsync(
            folderId,
            cancellationToken);

        return await BuildRemoteFolderPathAsync(
            folder,
            items,
            cancellationToken);
    }

    private async Task<string> CalculateHashAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        await using var stream = await _localFileStore.OpenReadAsync(
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
        return !string.IsNullOrWhiteSpace(left) &&
               !string.IsNullOrWhiteSpace(right) &&
               string.Equals(
                   left,
                   right,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(
        string? left,
        string? right)
    {
        return string.Equals(
            NormalizePath(left),
            NormalizePath(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string? path)
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
}