using System.Security.Cryptography;
using GestorArchivos.Sync.Windows.Api;
using GestorArchivos.Sync.Windows.Models;
using GestorArchivos.Sync.Windows.Monitoring;
using GestorArchivos.Sync.Windows.Services;
using GestorArchivos.Sync.Windows.State;
using GestorArchivos.Sync.Windows.Storage;

namespace GestorArchivos.Sync.Windows.Sync;

public sealed class InitialSyncService
{
    private readonly ISyncApiClient _apiClient;
    private readonly ILocalFileStore _localFileStore;
    private readonly IDownloadService _downloadService;
    private readonly SyncRuntimeState _runtimeState;
    private readonly ILocalStateStore _localStateStore;
    private readonly ILocalChangeMonitor _localChangeMonitor;

    private static readonly string SelectionsPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData),
        "GestorArchivos",
        "Sync",
        "selections.json");

    public InitialSyncService(
        ISyncApiClient apiClient,
        ILocalFileStore localFileStore,
        IDownloadService downloadService,
        SyncRuntimeState runtimeState,
        ILocalStateStore localStateStore,
        ILocalChangeMonitor localChangeMonitor)
    {
        _apiClient = apiClient;
        _localFileStore = localFileStore;
        _downloadService = downloadService;
        _runtimeState = runtimeState;
        _localStateStore = localStateStore;
        _localChangeMonitor = localChangeMonitor;
    }

    public async Task ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(SelectionsPath))
        {
            throw new InvalidOperationException(
                "La sincronización inicial tradicional está bloqueada " +
                "porque existe una configuración selectiva.");
        }

        if (_runtimeState.IsInitialSyncCompleted)
            return;

        using var suppression =
            _localChangeMonitor.SuppressChanges();

        var snapshot = await _apiClient.GetSnapshotAsync(
            cancellationToken);

        var existingItems = await _apiClient.GetItemsAsync(
            cancellationToken);

        var registeredFolders = existingItems
            .Where(x => x.CarpetaRemotaId.HasValue)
            .Select(x => x.CarpetaRemotaId!.Value)
            .ToHashSet();

        var registeredFiles = existingItems
            .Where(x => x.ArchivoRemotoId.HasValue)
            .Select(x => x.ArchivoRemotoId!.Value)
            .ToHashSet();

        var folderPaths = new Dictionary<Guid, string>();
        var foldersById = snapshot.Folders.ToDictionary(x => x.Id);
        var visiting = new HashSet<Guid>();

        string BuildFolderPath(Guid folderId)
        {
            if (folderPaths.TryGetValue(folderId, out var cached))
                return cached;

            if (!foldersById.TryGetValue(folderId, out var folder))
            {
                throw new InvalidOperationException(
                    $"No se encontró la carpeta remota {folderId}.");
            }

            if (!visiting.Add(folderId))
            {
                throw new InvalidOperationException(
                    $"Se detectó un ciclo en la jerarquía remota: {folderId}.");
            }

            try
            {
                string path;

                if (folderId == snapshot.RootFolderId)
                {
                    path = string.Empty;
                }
                else
                {
                    if (!folder.CarpetaPadreId.HasValue)
                    {
                        throw new InvalidOperationException(
                            $"La carpeta {folderId} no tiene carpeta padre.");
                    }

                    var parentPath = BuildFolderPath(
                        folder.CarpetaPadreId.Value);

                    ValidateName(folder.Nombre);

                    path = Path.Combine(
                        parentPath,
                        folder.Nombre);
                }

                folderPaths[folderId] = path;
                return path;
            }
            finally
            {
                visiting.Remove(folderId);
            }
        }

        // Validar previamente las rutas y colisiones
        // antes de crear archivos o carpetas.
        var usedPaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var folder in snapshot.Folders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = BuildFolderPath(folder.Id);

            if (folder.Id == snapshot.RootFolderId)
                continue;

            var fullPath = _localFileStore.GetFullPath(
                relativePath);

            if (!usedPaths.Add(fullPath))
            {
                throw new InvalidOperationException(
                    $"Ruta remota duplicada: '{relativePath}'.");
            }

            if (_localFileStore.FileExists(relativePath))
            {
                throw new IOException(
                    $"Existe un archivo donde debería haber " +
                    $"una carpeta: '{relativePath}'.");
            }
        }

        foreach (var file in snapshot.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ValidateName(file.Nombre);

            var folderPath = BuildFolderPath(
                file.CarpetaId);

            var relativePath = Path.Combine(
                folderPath,
                file.Nombre);

            var fullPath = _localFileStore.GetFullPath(
                relativePath);

            if (!usedPaths.Add(fullPath))
            {
                throw new InvalidOperationException(
                    $"Ruta remota duplicada: '{relativePath}'.");
            }

            if (_localFileStore.DirectoryExists(relativePath))
            {
                throw new IOException(
                    $"Existe una carpeta donde debería haber " +
                    $"un archivo: '{relativePath}'.");
            }
        }

        // Crear las carpetas mediante ILocalFileStore,
        // conservando sus validaciones de seguridad.
        foreach (var folder in snapshot.Folders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = BuildFolderPath(
                folder.Id);

            if (folder.Id == snapshot.RootFolderId)
                continue;

            if (!_localFileStore.DirectoryExists(relativePath))
            {
                _localFileStore.CreateDirectory(
                    relativePath);
            }

            if (registeredFolders.Add(folder.Id))
            {
                await _apiClient.RegisterItemAsync(
                    relativePath,
                    SyncItemType.Folder,
                    folder.Id,
                    null,
                    cancellationToken);
            }
        }

        // Descargar únicamente archivos ausentes.
        // Si existen, verificar que coincidan con el remoto.
        foreach (var file in snapshot.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var folderPath = BuildFolderPath(
                file.CarpetaId);

            var relativePath = Path.Combine(
                folderPath,
                file.Nombre);

            var remote = await _apiClient.GetRemoteFileAsync(
                file.Id,
                cancellationToken);

            if (_localFileStore.FileExists(relativePath))
            {
                var localHash = await CalculateHashAsync(
                    relativePath,
                    cancellationToken);

                if (!HashesEqual(
                    localHash,
                    remote.HashSha256))
                {
                    throw new IOException(
                        $"El archivo local '{relativePath}' ya existe " +
                        "y su contenido difiere del remoto. " +
                        "Se bloqueó la sobrescritura.");
                }
            }
            else
            {
                await _downloadService.DownloadAsync(
                    file.Id,
                    relativePath,
                    cancellationToken);
            }

            if (registeredFiles.Add(file.Id))
            {
                await _apiClient.RegisterItemAsync(
                    relativePath,
                    SyncItemType.File,
                    null,
                    file.Id,
                    cancellationToken);
            }
        }

        // No marcar como completada una sincronización
        // si el estado remoto cambió durante el proceso.
        var finalSnapshot = await _apiClient.GetSnapshotAsync(
            cancellationToken);

        if (finalSnapshot.LastChangeId != snapshot.LastChangeId)
        {
            throw new IOException(
                "El estado remoto cambió durante la sincronización inicial. " +
                "Se requiere repetir el proceso antes de confirmar el cursor.");
        }

        _runtimeState.LastChangeId = snapshot.LastChangeId;
        _runtimeState.IsInitialSyncCompleted = true;
        _runtimeState.LastSuccessfulSyncUtc = DateTime.UtcNow;

        await _localStateStore.SaveAsync(
            _runtimeState,
            cancellationToken);
    }

    private async Task<string> CalculateHashAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        await using var stream =
            await _localFileStore.OpenReadAsync(
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

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name == "." ||
            name == ".." ||
            name.Contains('/') ||
            name.Contains('\\') ||
            name.Contains(':') ||
            Path.IsPathRooted(name) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException(
                $"Nombre remoto inválido: '{name}'.");
        }
    }
}