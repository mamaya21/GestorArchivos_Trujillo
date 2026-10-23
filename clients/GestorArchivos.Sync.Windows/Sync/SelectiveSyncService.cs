using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GestorArchivos.Sync.Windows.Api;
using GestorArchivos.Sync.Windows.Models;
using GestorArchivos.Sync.Windows.Services;
using GestorArchivos.Sync.Windows.State;
using GestorArchivos.Sync.Windows.Storage;

namespace GestorArchivos.Sync.Windows.Sync;

public sealed class SelectiveSyncService
{
    private readonly ISyncApiClient _api;
    private readonly ILocalFileStore _store;
    private readonly IDownloadService _download;
    private readonly IUploadService _upload;
    private readonly IConflictService _conflicts;
    private readonly ISyncBaselineStore _baselines;
    private readonly SelectiveSyncMappingService _mapping;
    private bool _suppressDestructiveOperations;

    private static readonly string SelectionGuardPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GestorArchivos", "Sync", "selection-guard.txt");

    private static readonly string SelectionsPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData),
        "GestorArchivos",
        "Sync",
        "selections.json");

    public SelectiveSyncService(
        ISyncApiClient api,
        ILocalFileStore store,
        IDownloadService download,
        IUploadService upload,
        IConflictService conflicts,
        ISyncBaselineStore baselines,
        SelectiveSyncMappingService mapping)
    {
        _api = api;
        _store = store;
        _download = download;
        _upload = upload;
        _conflicts = conflicts;
        _baselines = baselines;
        _mapping = mapping;
    }

    public async Task ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        var selections = await LoadSelectionsAsync(cancellationToken);

        var fingerprint = ComputeSelectionFingerprint(selections);
        var previousFingerprint = File.Exists(SelectionGuardPath)
            ? await File.ReadAllTextAsync(SelectionGuardPath, cancellationToken)
            : null;
        _suppressDestructiveOperations = !string.Equals(
            previousFingerprint, fingerprint, StringComparison.Ordinal);

        // Una selección vacía es válida: el equipo permanece vinculado,
        // pero no se realizan transferencias ni se activa el modo tradicional.
        if (selections.Count == 0)
        {
            await SaveSelectionFingerprintAsync(fingerprint, cancellationToken);
            return;
        }

        var snapshot = await _api.GetSnapshotAsync(cancellationToken);

        if (snapshot.RootFolderId == Guid.Empty)
            throw new InvalidOperationException(
                "El servidor no devolvió una raíz Sync válida.");

        foreach (var selection in selections)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (selection.Kind == 1 &&
                !Directory.Exists(selection.Path))
            {
                throw new DirectoryNotFoundException(
                    $"Carpeta no disponible: {selection.Path}");
            }

            if (selection.Kind == 0 &&
                !File.Exists(selection.Path))
            {
                throw new FileNotFoundException(
                    $"Archivo no disponible: {selection.Path}",
                    selection.Path);
            }
        }

        await ReconcileLocalFolderMovesAsync(selections, cancellationToken);

        // Antes de cualquier escritura, detectar movimientos o renombrados locales
        // ambiguos. Sin esta barrera el recorrido por nombre puede crear otro
        // archivo remoto y después enviar el original a la papelera.
        await GuardAgainstUnresolvedLocalMovesAsync(
            selections, cancellationToken);

        await _mapping.PrepareMappingsAsync(cancellationToken);
        await _mapping.CreateMissingFoldersAsync(cancellationToken);

        foreach (var selection in selections)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (selection.Kind == 0)
            {
                await SyncSelectedFileAsync(
                    selection.Path,
                    snapshot.RootFolderId,
                    cancellationToken);
            }
            else
            {
                var rootItem = await FindItemByLocalPathAsync(
                    selection.Path,
                    cancellationToken);

                if (rootItem?.Tipo != SyncItemType.Folder ||
                    rootItem.CarpetaRemotaId is not Guid remoteFolderId)
                {
                    throw new InvalidOperationException(
                        $"Asociación remota inválida: '{selection.Path}'.");
                }

                await SyncFolderAsync(
                    remoteFolderId,
                    selection.Path,
                    cancellationToken);
            }
        }

        await RefreshBaselineAsync(cancellationToken);
        await SaveSelectionFingerprintAsync(fingerprint, cancellationToken);
    }

    private async Task ReconcileLocalFolderMovesAsync(
        IReadOnlyList<Selection> selections,
        CancellationToken cancellationToken)
    {
        var items = await _api.GetItemsAsync(cancellationToken);
        var snapshot = await _api.GetSnapshotAsync(cancellationToken);
        var selectedRoots = selections.Where(x => x.Kind == 1)
            .Select(x => Path.GetFullPath(x.Path)).ToList();

        // Procesar únicamente carpetas no raíz cuya ubicación anterior
        // ya no existe. La identidad remota debe ser inequívoca.
        var missing = items.Where(x => x.Tipo == SyncItemType.Folder &&
            x.CarpetaRemotaId.HasValue &&
            selectedRoots.Any(root => IsDescendantPath(x.RutaLocal, root)) &&
            !Directory.Exists(x.RutaLocal))
            .OrderBy(x => x.RutaLocal.Length).ToList();

        foreach (var folder in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Si un ancestro también desapareció, será él quien actualice
            // todas las rutas descendientes.
            if (missing.Any(x => x.SyncItemId != folder.SyncItemId &&
                IsDescendantPath(folder.RutaLocal, x.RutaLocal)))
                continue;

            var oldPrefix = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(folder.RutaLocal));
            var descendants = items.Where(x =>
                IsDescendantPath(x.RutaLocal, oldPrefix)).ToList();

            var candidates = new List<string>();
            foreach (var root in selectedRoots)
            {
                foreach (var candidate in Directory.EnumerateDirectories(
                    root, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (items.Any(x => x.Tipo == SyncItemType.Folder &&
                        PathsEqual(x.RutaLocal, candidate)))
                        continue;
                    if (IsReparsePoint(candidate))
                        throw new InvalidOperationException(
                            $"Enlace de directorio no permitido: '{candidate}'.");

                    // La carpeta se reconoce por las rutas relativas de
                    // sus archivos asociados y sus hashes de baseline.
                    var files = descendants.Where(x =>
                        x.Tipo == SyncItemType.File &&
                        x.ArchivoRemotoId.HasValue).ToList();
                    if (files.Count == 0)
                        continue;
                    var baselines = await _baselines.LoadAsync(cancellationToken);
                    var verified = true;
                    foreach (var file in files)
                    {
                        var relative = Path.GetRelativePath(oldPrefix,
                            file.RutaLocal);
                        var newPath = Path.Combine(candidate, relative);
                        var baseline = baselines.SingleOrDefault(x =>
                            x.SyncItemId == file.SyncItemId &&
                            x.RemoteFileId == file.ArchivoRemotoId);
                        if (baseline is null ||
                            !HashesEqual(baseline.LocalHash, baseline.RemoteHash) ||
                            !File.Exists(newPath))
                        {
                            verified = false;
                            break;
                        }
                        ValidatePathWithoutLinks(newPath);
                        if (!HashesEqual(await HashLocalAsync(newPath,
                                cancellationToken), baseline.LocalHash))
                        {
                            verified = false;
                            break;
                        }
                    }
                    if (verified)
                        candidates.Add(candidate);
                }
            }

            if (candidates.Count == 0)
                continue;
            if (candidates.Count != 1)
                throw new InvalidOperationException(
                    $"Movimiento de carpeta ambiguo: '{folder.RutaLocal}'.");

            var destination = candidates[0];
            var destinationParent = Path.GetDirectoryName(destination)!;
            var parentMapping = items.SingleOrDefault(x =>
                x.Tipo == SyncItemType.Folder &&
                x.CarpetaRemotaId.HasValue &&
                PathsEqual(x.RutaLocal, destinationParent));
            if (parentMapping is null)
                throw new InvalidOperationException(
                    $"Destino de carpeta sin asociación: '{destinationParent}'.");

            var remote = snapshot.Folders.SingleOrDefault(x =>
                x.Id == folder.CarpetaRemotaId);
            if (remote is null)
                throw new InvalidOperationException(
                    $"Carpeta remota no disponible: '{folder.RutaLocal}'.");

            var newName = Path.GetFileName(destination);
            ValidateRemoteName(newName);
            var targetParent = parentMapping.CarpetaRemotaId!.Value;
            if (snapshot.Folders.Any(x => x.Id != remote.Id &&
                x.CarpetaPadreId == targetParent &&
                NamesEqual(x.Nombre, newName)) ||
                snapshot.Files.Any(x => x.CarpetaId == targetParent &&
                NamesEqual(x.Nombre, newName)))
                throw new InvalidOperationException(
                    $"Destino remoto ocupado: '{destination}'.");

            // Evitar mover una carpeta dentro de sí misma.
            var currentParent = targetParent;
            var seen = new HashSet<Guid>();
            while (seen.Add(currentParent))
            {
                if (currentParent == remote.Id)
                    throw new InvalidOperationException(
                        "Movimiento de carpeta cíclico bloqueado.");
                var ancestor = snapshot.Folders.FirstOrDefault(x =>
                    x.Id == currentParent);
                if (ancestor?.CarpetaPadreId is not Guid next)
                    break;
                currentParent = next;
            }

            if (remote.CarpetaPadreId != targetParent)
                await _api.MoveRemoteFolderAsync(remote.Id, targetParent,
                    cancellationToken);
            if (!NamesEqual(remote.Nombre, newName))
                await _api.RenameRemoteFolderAsync(remote.Id, newName,
                    cancellationToken);

            var affected = items.Where(x =>
                PathsEqual(x.RutaLocal, oldPrefix) ||
                IsDescendantPath(x.RutaLocal, oldPrefix)).ToList();
            foreach (var item in affected.OrderBy(x => x.RutaLocal.Length))
            {
                var newPath = PathsEqual(item.RutaLocal, oldPrefix)
                    ? destination
                    : Path.Combine(destination, Path.GetRelativePath(
                        oldPrefix, item.RutaLocal));
                await _api.UpdateItemPathAsync(item.SyncItemId, newPath,
                    cancellationToken);
                item.RutaLocal = newPath;
            }

            var updated = (await _baselines.LoadAsync(cancellationToken))
                .ToList();
            foreach (var baseline in updated.Where(x =>
                PathsEqual(x.LocalPath, oldPrefix) ||
                IsDescendantPath(x.LocalPath, oldPrefix)))
            {
                baseline.LocalPath = PathsEqual(baseline.LocalPath, oldPrefix)
                    ? destination
                    : Path.Combine(destination, Path.GetRelativePath(
                        oldPrefix, baseline.LocalPath));
            }
            await _baselines.SaveAsync(updated, cancellationToken);
        }
    }

    private static bool IsDescendantPath(string child, string parent)
    {
        var prefix = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(child).StartsWith(
            prefix, StringComparison.OrdinalIgnoreCase);
    }

    private async Task GuardAgainstUnresolvedLocalMovesAsync(
        IReadOnlyList<Selection> selections,
        CancellationToken cancellationToken)
    {
        var items = await _api.GetItemsAsync(cancellationToken);
        var baselines = await _baselines.LoadAsync(cancellationToken);
        var snapshot = await _api.GetSnapshotAsync(cancellationToken);
        var plans = new List<(SyncItem Item, string Destination, Guid FolderId,
            string NewName, SyncBaselineItem Baseline)>();

        foreach (var selection in selections.Where(x => x.Kind == 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = Path.GetFullPath(selection.Path);
            var prefix = Path.TrimEndingDirectorySeparator(root) +
                Path.DirectorySeparatorChar;
            var mapped = items.Where(x =>
                x.RutaLocal.StartsWith(prefix,
                    StringComparison.OrdinalIgnoreCase)).ToList();
            var missing = mapped.Where(x =>
                x.Tipo == SyncItemType.File &&
                x.ArchivoRemotoId.HasValue &&
                !File.Exists(x.RutaLocal)).ToList();
            if (missing.Count == 0)
                continue;

            var candidates = Directory.EnumerateFiles(
                    root, "*", SearchOption.AllDirectories)
                .Where(x => !IsInternalSyncFileName(Path.GetFileName(x)))
                .Where(x => !mapped.Any(y => PathsEqual(y.RutaLocal, x)))
                .ToList();

            foreach (var item in missing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var baseline = baselines.SingleOrDefault(x =>
                    x.SyncItemId == item.SyncItemId &&
                    x.RemoteFileId == item.ArchivoRemotoId);
                if (baseline is null ||
                    !HashesEqual(baseline.LocalHash, baseline.RemoteHash) ||
                    !baseline.RemoteVersion.HasValue)
                    continue;

                var matches = new List<string>();
                foreach (var candidate in candidates)
                {
                    ValidatePathWithoutLinks(candidate);
                    if (HashesEqual(await HashLocalAsync(candidate,
                            cancellationToken), baseline.LocalHash))
                        matches.Add(candidate);
                }

                if (matches.Count == 0)
                    continue;
                if (matches.Count != 1)
                    throw new InvalidOperationException(
                        $"Movimiento ambiguo de '{item.RutaLocal}': " +
                        "varios archivos tienen el mismo contenido.");

                var destination = matches[0];
                var folderPath = Path.GetDirectoryName(destination)!;
                var folderMappings = items.Where(x =>
                    x.Tipo == SyncItemType.Folder &&
                    x.CarpetaRemotaId.HasValue &&
                    PathsEqual(x.RutaLocal, folderPath)).ToList();
                if (folderMappings.Count != 1)
                    throw new InvalidOperationException(
                        $"La carpeta destino '{folderPath}' no tiene " +
                        "una asociación remota única y verificable.");

                var remote = snapshot.Files.SingleOrDefault(x =>
                    x.Id == item.ArchivoRemotoId);
                if (remote is null ||
                    remote.Version != baseline.RemoteVersion ||
                    !HashesEqual(remote.HashSha256, baseline.RemoteHash))
                    throw new InvalidOperationException(
                        $"El archivo remoto de '{item.RutaLocal}' cambió. " +
                        "Se bloqueó el movimiento para proteger el historial.");

                var newName = Path.GetFileName(destination);
                ValidateRemoteName(newName);
                var folderId = folderMappings[0].CarpetaRemotaId!.Value;
                if (snapshot.Files.Any(x => x.Id != remote.Id &&
                    x.CarpetaId == folderId && NamesEqual(x.Nombre, newName)) ||
                    snapshot.Folders.Any(x => x.CarpetaPadreId == folderId &&
                        NamesEqual(x.Nombre, newName)))
                    throw new InvalidOperationException(
                        $"El destino remoto '{destination}' ya está ocupado.");

                plans.Add((item, destination, folderId, newName, baseline));
            }
        }

        if (plans.GroupBy(x => x.Destination,
                StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            throw new InvalidOperationException(
                "Varios archivos remotos apuntan al mismo destino local.");

        // La prevalidación completa termina antes de la primera escritura remota.
        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remote = (await _api.GetSnapshotAsync(cancellationToken))
                .Files.SingleOrDefault(x => x.Id == plan.Item.ArchivoRemotoId);
            if (remote is null ||
                remote.Version != plan.Baseline.RemoteVersion ||
                !HashesEqual(remote.HashSha256, plan.Baseline.RemoteHash))
                throw new InvalidOperationException(
                    $"El archivo remoto cambió antes de mover '{plan.Destination}'.");

            // Cada paso es reintentable: si el servidor aceptó el cambio
            // antes de una interrupción, no se vuelve a ejecutar.
            if (remote.CarpetaId != plan.FolderId)
                await _api.MoveRemoteFileAsync(remote.Id, plan.FolderId,
                    cancellationToken);
            if (!NamesEqual(remote.Nombre, plan.NewName))
                await _api.RenameRemoteFileAsync(remote.Id, plan.NewName,
                    cancellationToken);

            await _api.UpdateItemPathAsync(plan.Item.SyncItemId,
                plan.Destination, cancellationToken);

            var updated = (await _baselines.LoadAsync(cancellationToken))
                .ToList();
            var existing = updated.SingleOrDefault(x =>
                x.SyncItemId == plan.Item.SyncItemId);
            if (existing is not null)
            {
                existing.LocalPath = plan.Destination;
                await _baselines.SaveAsync(updated, cancellationToken);
            }
        }
    }

    private async Task SyncSelectedFileAsync(
        string localPath,
        Guid remoteRootId,
        CancellationToken cancellationToken)
    {
        if (IsInternalSyncFileName(Path.GetFileName(localPath)))
            throw new InvalidOperationException(
                "No se permite sincronizar archivos internos del sistema.");

        if (!File.Exists(localPath))
            throw new FileNotFoundException(
                $"Archivo no disponible: '{localPath}'.",
                localPath);

        ValidatePathWithoutLinks(localPath);
        var item = await FindItemByLocalPathAsync(
            localPath,
            cancellationToken);

        if (item is not null)
        {
            if (item.Tipo != SyncItemType.File ||
                !item.ArchivoRemotoId.HasValue)
            {
                throw new InvalidOperationException(
                    $"Asociación de archivo inválida: '{localPath}'.");
            }

            await ReconcileFileAsync(item, cancellationToken);
            return;
        }

        var snapshot = await _api.GetSnapshotAsync(cancellationToken);
        var name = Path.GetFileName(localPath);

        if (snapshot.Files.Any(f =>
            f.CarpetaId == remoteRootId &&
            NamesEqual(f.Nombre, name)))
        {
            throw new InvalidOperationException(
                $"El archivo remoto '{name}' ya existe sin asociación verificable.");
        }

        await using var stream = await _store.OpenReadAsync(
            localPath,
            cancellationToken);

        var created = await _api.CreateAtomicRemoteFileAsync(
            remoteRootId,
            name,
            localPath,
            stream,
            "application/octet-stream",
            cancellationToken);

        ValidateAtomicFileResult(created, remoteRootId, name, localPath);
    }

    private async Task SyncFolderAsync(
        Guid remoteFolderId,
        string localFolderPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(localFolderPath))
            throw new DirectoryNotFoundException(
                $"Carpeta no disponible: '{localFolderPath}'.");

        ValidatePathWithoutLinks(localFolderPath);
        var snapshot = await _api.GetSnapshotAsync(cancellationToken);
        var items = await _api.GetItemsAsync(cancellationToken);

        var remoteFolders = snapshot.Folders
            .Where(f => f.CarpetaPadreId == remoteFolderId)
            .ToList();

        var remoteFiles = snapshot.Files
            .Where(f => f.CarpetaId == remoteFolderId)
            .ToList();

        foreach (var folder in remoteFolders)
            ValidateRemoteName(folder.Nombre);

        foreach (var file in remoteFiles)
            ValidateRemoteName(file.Nombre);

        var localFolders = Directory
            .EnumerateDirectories(localFolderPath)
            .ToDictionary(
                p => Path.GetFileName(p)!,
                p => p,
                NormalizedNameComparer.Instance);

        var localFiles = Directory
            .EnumerateFiles(localFolderPath)
            .Where(p => !IsInternalSyncFileName(Path.GetFileName(p)))
            .ToDictionary(
                p => Path.GetFileName(p)!,
                p => p,
                NormalizedNameComparer.Instance);

        // PRIMERO: resolver eliminaciones remotas y asociaciones antiguas.
        // Esto debe ejecutarse ANTES de reconciliar archivos o crear otros.
        await HandleRemoteFileDeletionsAsync(
            snapshot.Files.Select(f => f.Id).ToHashSet(),
            remoteFiles.Select(f => f.Id).ToHashSet(),
            localFolderPath,
            localFiles,
            items,
            cancellationToken);

        // Refrescar las asociaciones después de limpiar las obsoletas.
        items = await _api.GetItemsAsync(cancellationToken);

        // Refrescar el listado físico después de mover archivos recuperables.
        localFiles = Directory
            .EnumerateFiles(localFolderPath)
            .Where(p => !IsInternalSyncFileName(Path.GetFileName(p)))
            .ToDictionary(
                p => Path.GetFileName(p)!,
                p => p,
                NormalizedNameComparer.Instance);

        // REMOTO -> LOCAL: CARPETAS
        foreach (var remoteFolder in remoteFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (localFiles.ContainsKey(remoteFolder.Nombre))
                throw new InvalidOperationException(
                    $"Conflicto de tipo: '{remoteFolder.Nombre}'.");

            var localChild = localFolders.TryGetValue(
                remoteFolder.Nombre,
                out var matchingLocalFolderPath)
                    ? matchingLocalFolderPath
                    : Path.Combine(localFolderPath, remoteFolder.Nombre);

            var existingMapping = items.FirstOrDefault(x =>
                PathsEqual(x.RutaLocal, localChild));

            if (existingMapping is not null &&
                (existingMapping.Tipo != SyncItemType.Folder ||
                 existingMapping.CarpetaRemotaId != remoteFolder.Id))
            {
                throw new InvalidOperationException(
                    $"La carpeta '{localChild}' tiene otra asociación remota.");
            }

            var mappedElsewhere = items.FirstOrDefault(x =>
                x.CarpetaRemotaId == remoteFolder.Id &&
                !PathsEqual(x.RutaLocal, localChild));

            if (mappedElsewhere is not null)
                throw new InvalidOperationException(
                    $"La carpeta remota ya está asociada a '{mappedElsewhere.RutaLocal}'.");

            if (matchingLocalFolderPath is null)
            {
                if (existingMapping is not null)
                    continue;

                Directory.CreateDirectory(localChild);
            }

            ValidatePathWithoutLinks(localChild);

            await EnsureMappingAsync(
                localChild,
                SyncItemType.Folder,
                remoteFolder.Id,
                null,
                items,
                cancellationToken);

            await SyncFolderAsync(
                remoteFolder.Id,
                localChild,
                cancellationToken);
        }

        // LOCAL -> REMOTO: CARPETAS
        foreach (var localFolder in localFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (remoteFolders.Any(f =>
                NamesEqual(f.Nombre, localFolder.Key)))
            {
                continue;
            }

            if (remoteFiles.Any(f =>
                NamesEqual(f.Nombre, localFolder.Key)))
            {
                throw new InvalidOperationException(
                    $"Conflicto de tipo remoto: '{localFolder.Value}'.");
            }

            var existingMapping = items.FirstOrDefault(x =>
                PathsEqual(x.RutaLocal, localFolder.Value));

            if (existingMapping is not null)
            {
                throw new InvalidOperationException(
                    $"Se bloqueó la recreación de '{localFolder.Value}'. " +
                    "La carpeta ya tiene una asociación remota registrada.");
            }

            if (IsReparsePoint(localFolder.Value))
                throw new InvalidOperationException(
                    $"No se sincronizará un enlace o punto de montaje: '{localFolder.Value}'.");

            var created = await _api.CreateAtomicRemoteFolderAsync(
                remoteFolderId,
                localFolder.Key,
                localFolder.Value,
                cancellationToken);

            if (created.SyncItemId == Guid.Empty ||
                created.RemoteFolderId == Guid.Empty ||
                created.CarpetaPadreId != remoteFolderId ||
                !NamesEqual(created.Nombre, localFolder.Key))
            {
                throw new InvalidOperationException(
                    $"Respuesta atómica inválida para '{localFolder.Value}'.");
            }

            var confirmed = await FindItemByLocalPathAsync(
                localFolder.Value,
                cancellationToken);

            if (confirmed is null ||
                confirmed.SyncItemId != created.SyncItemId ||
                confirmed.Tipo != SyncItemType.Folder ||
                confirmed.CarpetaRemotaId != created.RemoteFolderId)
            {
                throw new InvalidOperationException(
                    $"No se pudo verificar la asociación atómica de '{localFolder.Value}'.");
            }

            await SyncFolderAsync(
                created.RemoteFolderId,
                localFolder.Value,
                cancellationToken);
        }

        // REMOTO -> LOCAL: ARCHIVOS
        foreach (var remoteFile in remoteFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (localFolders.ContainsKey(remoteFile.Nombre))
                throw new InvalidOperationException(
                    $"Conflicto de tipo: '{remoteFile.Nombre}'.");

            var localPath = localFiles.TryGetValue(
                remoteFile.Nombre,
                out var matchingLocalFilePath)
                    ? matchingLocalFilePath
                    : Path.Combine(localFolderPath, remoteFile.Nombre);

            if (File.Exists(localPath))
                ValidatePathWithoutLinks(localPath);

            var fileItem = await FindItemByLocalPathAsync(
                localPath,
                cancellationToken);

            var existsLocally = File.Exists(localPath);

            if (!existsLocally && fileItem is not null)
                continue;

            if (existsLocally && fileItem is null)
            {
                throw new InvalidOperationException(
                    $"Colisión local/remota sin asociación: '{localPath}'.");
            }

            if (fileItem is null)
            {
                var mappedElsewhere = items.FirstOrDefault(x =>
                    x.ArchivoRemotoId == remoteFile.Id);

                if (mappedElsewhere is not null &&
                    !PathsEqual(mappedElsewhere.RutaLocal, localPath))
                {
                    throw new InvalidOperationException(
                        $"El archivo remoto ya está asociado a '{mappedElsewhere.RutaLocal}'.");
                }

                await _download.DownloadAsync(
                    remoteFile.Id,
                    localPath,
                    cancellationToken);

                fileItem = await _api.RegisterItemAsync(
                    localPath,
                    SyncItemType.File,
                    null,
                    remoteFile.Id,
                    cancellationToken);
            }
            else if (fileItem.Tipo != SyncItemType.File ||
                     fileItem.ArchivoRemotoId != remoteFile.Id)
            {
                throw new InvalidOperationException(
                    $"Asociación remota diferente: '{localPath}'.");
            }

            await ReconcileFileAsync(
                fileItem,
                cancellationToken);
        }

        // Obtener el estado remoto actualizado antes de procesar
        // eliminaciones locales. El snapshot inicial puede haber cambiado.
        snapshot = await _api.GetSnapshotAsync(cancellationToken);

        remoteFiles = snapshot.Files
            .Where(f => f.CarpetaId == remoteFolderId)
            .ToList();

        items = await _api.GetItemsAsync(cancellationToken);

        // Resolver eliminaciones remotas ocurridas durante este ciclo.
        await HandleRemoteFileDeletionsAsync(
            snapshot.Files.Select(f => f.Id).ToHashSet(),
            remoteFiles.Select(f => f.Id).ToHashSet(),
            localFolderPath,
            localFiles,
            items,
            cancellationToken);

        items = await _api.GetItemsAsync(cancellationToken);

        // Procesar eliminaciones iniciadas desde Windows.
        await HandleLocalFileDeletionsAsync(
            remoteFiles,
            localFolderPath,
            items,
            cancellationToken);

        // Evitar subir archivos que ya fueron trasladados a
        // ArchivosRecuperados durante este mismo ciclo.
        localFiles = Directory
            .EnumerateFiles(localFolderPath)
            .Where(p => !IsInternalSyncFileName(Path.GetFileName(p)))
            .ToDictionary(
                p => Path.GetFileName(p)!,
                p => p,
                NormalizedNameComparer.Instance);

        // Refrescar el estado remoto para no recrear archivos
        // a partir de información desactualizada.
        snapshot = await _api.GetSnapshotAsync(cancellationToken);

        remoteFiles = snapshot.Files
            .Where(f => f.CarpetaId == remoteFolderId)
            .ToList();

        remoteFolders = snapshot.Folders
            .Where(f => f.CarpetaPadreId == remoteFolderId)
            .ToList();

        items = await _api.GetItemsAsync(cancellationToken);

        // LOCAL -> REMOTO: ARCHIVOS
        foreach (var localFile in localFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var matchingRemoteFiles = remoteFiles.Where(f =>
                NamesEqual(f.Nombre, localFile.Key)).ToList();
            if (matchingRemoteFiles.Count > 0)
            {
                if (matchingRemoteFiles.Count != 1)
                    throw new InvalidOperationException($"Nombre remoto duplicado: '{localFile.Key}'.");
                var associated = await FindItemByLocalPathAsync(localFile.Value, cancellationToken);
                if (associated?.Tipo != SyncItemType.File ||
                    associated.ArchivoRemotoId != matchingRemoteFiles[0].Id)
                    throw new InvalidOperationException($"Colisión sin asociación verificable: '{localFile.Value}'.");
                continue;
            }

            if (remoteFolders.Any(f =>
                NamesEqual(f.Nombre, localFile.Key)))
            {
                throw new InvalidOperationException(
                    $"Conflicto de tipo remoto: '{localFile.Value}'.");
            }

            var existingMapping = items.FirstOrDefault(x =>
                PathsEqual(x.RutaLocal, localFile.Value));

            if (existingMapping is not null)
            {
                throw new InvalidOperationException(
                    $"Se bloqueó la recreación de '{localFile.Value}'. " +
                    "El archivo ya tiene una asociación remota registrada.");
            }

            ValidatePathWithoutLinks(localFile.Value);
            if (IsReparsePoint(localFile.Value))
                throw new InvalidOperationException(
                    $"No se sincronizará un enlace: '{localFile.Value}'.");

            await using var stream = await _store.OpenReadAsync(
                localFile.Value,
                cancellationToken);

            var created = await _api.CreateAtomicRemoteFileAsync(
                remoteFolderId,
                localFile.Key,
                localFile.Value,
                stream,
                "application/octet-stream",
                cancellationToken);

            ValidateAtomicFileResult(
                created,
                remoteFolderId,
                localFile.Key,
                localFile.Value);
        }
    }

    private async Task HandleLocalFileDeletionsAsync(
        IReadOnlyList<RemoteFile> remoteFiles,
        string localFolderPath,
        IReadOnlyList<SyncItem> items,
        CancellationToken cancellationToken)
    {
        if (_suppressDestructiveOperations)
            return;

        var baselines = await _baselines.LoadAsync(cancellationToken);
        foreach (var remote in remoteFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mappings = items.Where(x =>
                x.Tipo == SyncItemType.File &&
                x.ArchivoRemotoId == remote.Id).ToList();

            // No se debe eliminar un archivo remoto si no existe una
            // asociación única y comprobable con este directorio.
            if (mappings.Count != 1)
                continue;

            var item = mappings[0];
            var parent = Path.GetDirectoryName(Path.GetFullPath(item.RutaLocal));
            if (parent is null || !PathsEqual(parent, localFolderPath) ||
                !NamesEqual(Path.GetFileName(item.RutaLocal), remote.Nombre) ||
                File.Exists(item.RutaLocal))
                continue;

            // Si la ruta cambió de tipo, o el archivo está inaccesible,
            // no convertirlo en una eliminación.
            if (Directory.Exists(item.RutaLocal))
                throw new InvalidOperationException(
                    $"Conflicto de tipo en '{item.RutaLocal}'.");

            var baseline = baselines.FirstOrDefault(x =>
                x.SyncItemId == item.SyncItemId &&
                x.RemoteFileId == remote.Id);
            if (baseline is null ||
                !baseline.RemoteVersion.HasValue ||
                baseline.RemoteVersion.Value < 1 ||
                !HashesEqual(baseline.LocalHash, baseline.RemoteHash))
            {
                continue;
            }

            // Si hubo cambios en la web desde la última sincronización,
            // no eliminar: debe resolverse el conflicto por separado.
            if (remote.Version != baseline.RemoteVersion ||
                !HashesEqual(remote.HashSha256, baseline.RemoteHash))
                continue;

            await _api.MoveRemoteFileToTrashAsync(
                remote.Id,
                baseline.RemoteVersion.Value,
                baseline.RemoteHash,
                cancellationToken);
        }
    }

    private async Task HandleRemoteFileDeletionsAsync(
    HashSet<Guid> allRemoteFileIds,
    HashSet<Guid> currentFolderFileIds,
    string localFolderPath,
    Dictionary<string, string> localFiles,
    IReadOnlyList<SyncItem> items,
    CancellationToken cancellationToken)
    {
        if (_suppressDestructiveOperations)
            return;

        var baselines = await _baselines.LoadAsync(cancellationToken);

        var folderPath = Path.GetFullPath(localFolderPath);

        var folderItems = items
            .Where(x =>
                x.Tipo == SyncItemType.File &&
                x.ArchivoRemotoId.HasValue &&
                PathsEqual(
                    Path.GetDirectoryName(
                        Path.GetFullPath(x.RutaLocal)),
                    folderPath))
            .ToList();

        foreach (var item in folderItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remoteId = item.ArchivoRemotoId!.Value;

            // El archivo sigue existiendo en esta carpeta remota.
            if (currentFolderFileIds.Contains(remoteId))
                continue;

            // El archivo existe en otra carpeta remota.
            // No interpretar un traslado como eliminación.
            if (allRemoteFileIds.Contains(remoteId))
                continue;

            var localPath = Path.GetFullPath(item.RutaLocal);

            var baseline = baselines.FirstOrDefault(x =>
                x.SyncItemId == item.SyncItemId &&
                x.RemoteFileId == remoteId);

            // Una ruta ocupada por una carpeta no debe tratarse
            // como un archivo eliminado.
            if (Directory.Exists(localPath))
            {
                throw new InvalidOperationException(
                    $"Conflicto de tipo en '{localPath}'.");
            }

            if (File.Exists(localPath))
            {
                // Para retirar o desvincular un archivo existente
                // se exige un baseline confiable.
                if (baseline is null ||
                    string.IsNullOrWhiteSpace(baseline.LocalHash) ||
                    string.IsNullOrWhiteSpace(baseline.RemoteHash) ||
                    !HashesEqual(
                        baseline.LocalHash,
                        baseline.RemoteHash))
                {
                    throw new InvalidOperationException(
                        $"No se puede resolver la eliminación remota " +
                        $"de '{localPath}': falta un baseline confiable.");
                }

                ValidatePathWithoutLinks(localPath);

                var currentHash = await HashLocalAsync(
                    localPath,
                    cancellationToken);

                if (HashesEqual(currentHash, baseline.LocalHash))
                {
                    // El contenido coincide con la última sincronización.
                    // Conservar una copia recuperable antes de retirarlo.
                    var recoveryDirectory = Path.Combine(
                        Environment.GetFolderPath(
                            Environment.SpecialFolder.LocalApplicationData),
                        "GestorArchivos",
                        "Sync",
                        "ArchivosRecuperados");

                    Directory.CreateDirectory(recoveryDirectory);

                    var recoveryPath = Path.Combine(
                        recoveryDirectory,
                        $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_" +
                        $"{Guid.NewGuid():N}_" +
                        Path.GetFileName(localPath));

                    File.Move(localPath, recoveryPath);

                    localFiles.Remove(
                        Path.GetFileName(localPath));
                }

                // Si el hash cambió, conservar el archivo local.
                // Después de desvincularlo podrá tratarse como
                // archivo nuevo durante LOCAL -> REMOTO.
            }

            // También se ejecuta si el archivo ya no existe
            // físicamente. Así se limpian asociaciones antiguas
            // de eliminaciones procesadas en ciclos anteriores.
            await _api.DeleteItemAsync(
                item.SyncItemId,
                cancellationToken);
        }
    }

    private static void ValidateAtomicFileResult(
        AtomicSyncFileResult result,
        Guid folderId,
        string fileName,
        string localPath)
    {
        if (result.SyncItemId == Guid.Empty ||
            result.RemoteFileId == Guid.Empty ||
            result.CarpetaId != folderId ||
            !NamesEqual(result.Nombre, fileName) ||
            result.Version < 1 ||
            string.IsNullOrWhiteSpace(result.HashSha256))
        {
            throw new InvalidOperationException(
                $"El servidor devolvió una asociación de archivo inválida para '{localPath}'.");
        }
    }

    private async Task EnsureMappingAsync(
        string path,
        SyncItemType type,
        Guid remoteFolderId,
        Guid? remoteFileId,
        IReadOnlyList<SyncItem> items,
        CancellationToken cancellationToken)
    {
        var current = await FindItemByLocalPathAsync(
            path,
            cancellationToken);

        if (current is not null)
        {
            var matches = type == SyncItemType.Folder
                ? current.Tipo == type &&
                  current.CarpetaRemotaId == remoteFolderId
                : current.Tipo == type &&
                  current.ArchivoRemotoId == remoteFileId;

            if (!matches)
                throw new InvalidOperationException(
                    $"Asociación remota diferente: '{path}'.");

            return;
        }

        var mappedElsewhere = items.FirstOrDefault(x =>
            type == SyncItemType.Folder
                ? x.CarpetaRemotaId == remoteFolderId
                : x.ArchivoRemotoId == remoteFileId);

        if (mappedElsewhere is not null &&
            !PathsEqual(mappedElsewhere.RutaLocal, path))
        {
            throw new InvalidOperationException(
                $"El elemento remoto ya está asociado a '{mappedElsewhere.RutaLocal}'.");
        }

        await _api.RegisterItemAsync(
            path,
            type,
            remoteFolderId,
            remoteFileId,
            cancellationToken);
    }

    private async Task ReconcileFileAsync(
    SyncItem item,
    CancellationToken cancellationToken)
    {
        if (!item.ArchivoRemotoId.HasValue)
            throw new InvalidOperationException(
                $"Archivo sin asociación remota: '{item.RutaLocal}'.");

        RemoteFile remote;

        try
        {
            remote = await _api.GetRemoteFileAsync(
                item.ArchivoRemotoId.Value,
                cancellationToken);
        }
        catch (SyncApiException ex) when (
            ex.Message.Contains(
                "404 (NotFound)",
                StringComparison.OrdinalIgnoreCase))
        {
            // Un 404 no basta para asumir que el archivo fue eliminado.
            // Verificar el estado actual del servidor.
            var snapshot = await _api.GetSnapshotAsync(cancellationToken);

            if (snapshot.Files.Any(x =>
                x.Id == item.ArchivoRemotoId.Value))
            {
                throw new InvalidOperationException(
                    $"El archivo '{item.RutaLocal}' continúa en el snapshot " +
                    "pero su consulta individual devuelve 404.",
                    ex);
            }

            var parent = Path.GetDirectoryName(
                Path.GetFullPath(item.RutaLocal));

            if (string.IsNullOrWhiteSpace(parent) ||
                !Directory.Exists(parent))
            {
                throw new InvalidOperationException(
                    $"La carpeta local de '{item.RutaLocal}' no está disponible.",
                    ex);
            }

            ValidatePathWithoutLinks(parent);

            var items = await _api.GetItemsAsync(cancellationToken);

            var localFiles = Directory
                .EnumerateFiles(parent)
                .Where(p => !IsInternalSyncFileName(Path.GetFileName(p)))
                .ToDictionary(
                    p => Path.GetFileName(p)!,
                    p => p,
                    NormalizedNameComparer.Instance);

            // Procesar la eliminación mediante el mecanismo existente,
            // que verifica el baseline y conserva una copia recuperable.
            await HandleRemoteFileDeletionsAsync(
                snapshot.Files.Select(x => x.Id).ToHashSet(),
                snapshot.Files.Select(x => x.Id).ToHashSet(),
                parent,
                localFiles,
                items,
                cancellationToken);

            return;
        }

        if (!_store.FileExists(item.RutaLocal))
            return;

        var localHash = await HashLocalAsync(
            item.RutaLocal,
            cancellationToken);

        var baselineItems = await _baselines.LoadAsync(
            cancellationToken);

        var previous = baselineItems.FirstOrDefault(x =>
            x.SyncItemId == item.SyncItemId);

        if (HashesEqual(localHash, remote.HashSha256))
            return;

        if (previous is null)
            throw new InvalidOperationException(
                $"Sin baseline verificable: '{item.RutaLocal}'. " +
                "Se bloqueó la sobrescritura.");

        var localChanged = !HashesEqual(
            localHash,
            previous.LocalHash);

        var remoteChanged =
            remote.Version != previous.RemoteVersion ||
            !HashesEqual(
                remote.HashSha256,
                previous.RemoteHash);

        if (localChanged && !remoteChanged)
        {
            await _upload.UploadAsync(
                remote.Id,
                item.RutaLocal,
                remote.Version,
                remote.HashSha256,
                cancellationToken);

            return;
        }

        if (!localChanged && remoteChanged)
        {
            await _download.DownloadIfUnchangedAsync(
                remote.Id,
                item.RutaLocal,
                localHash,
                cancellationToken);

            return;
        }

        if (localChanged && remoteChanged)
        {
            await _conflicts.CreateConflictCopyAsync(
                new SyncConflict
                {
                    FileId = remote.Id,
                    LocalPath = item.RutaLocal,
                    LocalVersion = 0,
                    RemoteVersion = remote.Version,
                    LocalHash = localHash,
                    RemoteHash = remote.HashSha256,
                    DetectedUtc = DateTime.UtcNow
                },
                cancellationToken);

            await _download.DownloadIfUnchangedAsync(
                remote.Id,
                item.RutaLocal,
                localHash,
                cancellationToken);

            return;
        }

        throw new InvalidOperationException(
            $"Diferencia de hashes sin cambios registrados: '{item.RutaLocal}'.");
    }

    private async Task RefreshBaselineAsync(
        CancellationToken cancellationToken)
    {
        var selections = await LoadSelectionsAsync(cancellationToken);
        var items = await _api.GetItemsAsync(cancellationToken);
        var snapshot = await _api.GetSnapshotAsync(cancellationToken);
        var old = await _baselines.LoadAsync(cancellationToken);

        var remoteFiles = snapshot.Files.ToDictionary(x => x.Id);

        var associatedItems = items
            .Where(x =>
                x.Tipo == SyncItemType.File &&
                x.ArchivoRemotoId.HasValue)
            .GroupBy(x => x.SyncItemId)
            .ToDictionary(x => x.Key, x => x.First());

        // La deselección no debe borrar el historial de una asociación vigente.
        // No conservar un baseline si la ruta o el archivo asociado cambiaron.
        var baseline = old
            .Where(x =>
                x.RemoteFileId.HasValue &&
                associatedItems.TryGetValue(x.SyncItemId, out var item) &&
                item.ArchivoRemotoId == x.RemoteFileId &&
                PathsEqual(item.RutaLocal, x.LocalPath) &&
                remoteFiles.ContainsKey(x.RemoteFileId.Value))
            .GroupBy(x => x.SyncItemId)
            .ToDictionary(x => x.Key, x => x.First());

        // Solo actualizar baselines de elementos actualmente seleccionados.
        foreach (var item in associatedItems.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsSelectedItemPath(item.RutaLocal, selections))
                continue;

            var remoteId = item.ArchivoRemotoId!.Value;

            if (!remoteFiles.TryGetValue(remoteId, out var remote))
                continue;

            if (!_store.FileExists(item.RutaLocal))
                continue;

            ValidatePathWithoutLinks(item.RutaLocal);

            var localHash = await HashLocalAsync(
                item.RutaLocal,
                cancellationToken);

            if (!HashesEqual(localHash, remote.HashSha256))
                continue;

            baseline[item.SyncItemId] = new SyncBaselineItem
            {
                SyncItemId = item.SyncItemId,
                RemoteFileId = remoteId,
                Type = SyncItemType.File,
                LocalPath = item.RutaLocal,
                LocalHash = localHash,
                RemoteHash = remote.HashSha256,
                RemoteVersion = remote.Version,
                LastSyncedUtc = DateTime.UtcNow
            };
        }

        await _baselines.SaveAsync(
            baseline.Values.ToList(),
            cancellationToken);
    }

    private async Task<SyncItem?> FindItemByLocalPathAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        var items = await _api.GetItemsAsync(cancellationToken);

        return items.FirstOrDefault(x =>
            PathsEqual(x.RutaLocal, fullPath));
    }

    private static async Task<string> HashLocalAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            1024 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);

        return Convert.ToHexString(
            await SHA256.HashDataAsync(
                stream,
                cancellationToken));
    }

    private static string ComputeSelectionFingerprint(IReadOnlyList<Selection> selections)
    {
        var canonical = string.Join("\n", selections
            .Select(x => $"{x.Kind}:{Path.TrimEndingDirectorySeparator(Path.GetFullPath(x.Path)).Normalize(NormalizationForm.FormC).ToUpperInvariant()}")
            .OrderBy(x => x, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static async Task SaveSelectionFingerprintAsync(
        string fingerprint, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SelectionGuardPath)!);
        var temporary = SelectionGuardPath + ".tmp";
        await File.WriteAllTextAsync(temporary, fingerprint, cancellationToken);
        File.Move(temporary, SelectionGuardPath, true);
    }

    private static async Task<List<Selection>> LoadSelectionsAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(SelectionsPath))
            return [];

        var json = await File.ReadAllTextAsync(
            SelectionsPath,
            cancellationToken);

        var selections = JsonSerializer.Deserialize<List<Selection>>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidOperationException(
                "Configuración de selección inválida.");

        var unique = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var selection in selections)
        {
            if (selection is null ||
                string.IsNullOrWhiteSpace(selection.Path) ||
                !Path.IsPathFullyQualified(selection.Path) ||
                (selection.Kind != 0 && selection.Kind != 1))
            {
                throw new InvalidOperationException(
                    "La configuración contiene una selección inválida.");
            }

            selection.Path = Path.GetFullPath(selection.Path);

            if (!unique.Add(selection.Path))
                throw new InvalidOperationException(
                    $"Ruta duplicada: '{selection.Path}'.");
        }

        return selections;
    }

    private bool IsSelectedItemPath(
        string itemPath,
        IReadOnlyList<Selection> selections)
    {
        string fullPath;

        try
        {
            fullPath = Path.GetFullPath(
                Path.IsPathRooted(itemPath)
                    ? itemPath
                    : _store.GetFullPath(itemPath));
        }
        catch (Exception ex) when (
            ex is ArgumentException ||
            ex is InvalidOperationException ||
            ex is NotSupportedException ||
            ex is PathTooLongException)
        {
            return false;
        }

        foreach (var selection in selections)
        {
            if (selection.Kind == 0)
            {
                if (PathsEqual(fullPath, selection.Path))
                    return true;

                continue;
            }

            var root = Path.TrimEndingDirectorySeparator(
                selection.Path);

            var normalizedFullPath = fullPath.Normalize(NormalizationForm.FormC);
            var normalizedRoot = root.Normalize(NormalizationForm.FormC);

            if (PathsEqual(fullPath, root) ||
                normalizedFullPath.StartsWith(
                    normalizedRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidatePathWithoutLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && IsReparsePoint(current))
                throw new InvalidOperationException($"Ruta con enlace o punto de montaje: '{current}'.");
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        return (File.GetAttributes(path) &
                FileAttributes.ReparsePoint) != 0;
    }

    // Solo excluye los nombres técnicos exactos generados por el cliente Sync.
    // No excluye otros archivos del usuario con extensión .tmp o .backup.
    private static bool IsInternalSyncFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        const string prefix = ".gestor-sync-";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var suffix = name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            ? ".tmp"
            : name.EndsWith(".backup", StringComparison.OrdinalIgnoreCase)
                ? ".backup"
                : null;

        if (suffix is null)
            return false;

        var identifier = name.AsSpan(prefix.Length,
            name.Length - prefix.Length - suffix.Length);

        return identifier.Length == 32 &&
               identifier.ToString().All(Uri.IsHexDigit);
    }

    private static void ValidateRemoteName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name is "." or ".." ||
            !string.Equals(
                Path.GetFileName(name),
                name,
                StringComparison.Ordinal) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains('/') ||
            name.Contains('\\') ||
            name.EndsWith(' ') ||
            name.EndsWith('.'))
        {
            throw new InvalidOperationException(
                $"Nombre remoto inseguro: '{name}'.");
        }
    }

    private static bool PathsEqual(
        string? left,
        string? right)
    {
        return string.Equals(
            NormalizePath(left).Normalize(NormalizationForm.FormC),
            NormalizePath(right).Normalize(NormalizationForm.FormC),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(path));
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

    private static bool NamesEqual(string? left, string? right)
    {
        return string.Equals(
            left?.Normalize(NormalizationForm.FormC),
            right?.Normalize(NormalizationForm.FormC),
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NormalizedNameComparer : IEqualityComparer<string>
    {
        public static NormalizedNameComparer Instance { get; } = new();

        public bool Equals(string? x, string? y) => NamesEqual(x, y);

        public int GetHashCode(string value)
        {
            return StringComparer.OrdinalIgnoreCase.GetHashCode(
                value.Normalize(NormalizationForm.FormC));
        }
    }

    private sealed class Selection
    {
        public string Path { get; set; } = string.Empty;
        public int Kind { get; set; }
    }
}
