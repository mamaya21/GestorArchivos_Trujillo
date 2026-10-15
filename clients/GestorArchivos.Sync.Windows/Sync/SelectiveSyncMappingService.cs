using System.Text.Json;
using GestorArchivos.Sync.Windows.Api;
using GestorArchivos.Sync.Windows.Models;

namespace GestorArchivos.Sync.Windows.Sync;

public sealed class SelectiveSyncMappingService
{
    private readonly ISyncApiClient _syncApiClient;

    private static readonly string SelectionsPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData),
        "GestorArchivos",
        "Sync",
        "selections.json");

    public SelectiveSyncMappingService(
        ISyncApiClient syncApiClient)
    {
        _syncApiClient = syncApiClient;
    }

    public async Task<IReadOnlyList<SelectiveSyncMapping>> GetMappingsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SelectionsPath))
            return [];

        var json = await File.ReadAllTextAsync(
            SelectionsPath,
            cancellationToken);

        var selections =
            JsonSerializer.Deserialize<List<SelectiveSyncSelection>>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                })
            ?? throw new InvalidOperationException(
                "La configuración de selección no es válida.");

        var snapshot = await _syncApiClient.GetSnapshotAsync(
            cancellationToken);

        var items = await _syncApiClient.GetItemsAsync(
            cancellationToken);

        return BuildMappings(selections, snapshot, items);
    }

    public async Task<IReadOnlyList<SelectiveSyncMapping>> PrepareMappingsAsync(
        CancellationToken cancellationToken = default)
    {
        var mappings = await GetMappingsAsync(cancellationToken);

        if (mappings.Count == 0)
            return mappings;

        var snapshot = await _syncApiClient.GetSnapshotAsync(
            cancellationToken);

        if (snapshot.RootFolderId == Guid.Empty)
            throw new InvalidOperationException(
                "El dispositivo no tiene una carpeta raíz válida.");

        var items = await _syncApiClient.GetItemsAsync(
            cancellationToken);

        ValidateRemoteCollisions(mappings);

        foreach (var mapping in mappings)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (mapping.DeviceRootFolderId != snapshot.RootFolderId)
                throw new InvalidOperationException(
                    "La carpeta raíz cambió durante la preparación.");

            ValidateLocalSelection(mapping);

            var name = Path.GetFileName(mapping.LocalPath);

            var associatedItems = items
                .Where(x => PathsEqual(x.RutaLocal, mapping.LocalPath))
                .ToList();

            if (associatedItems.Count > 1)
                throw new InvalidOperationException(
                    $"Existen asociaciones duplicadas para '{mapping.LocalPath}'.");

            var associated = associatedItems.SingleOrDefault();

            if (mapping.SyncItemId.HasValue)
            {
                if (associated is null ||
                    associated.SyncItemId != mapping.SyncItemId.Value)
                {
                    throw new InvalidOperationException(
                        $"La asociación de '{mapping.LocalPath}' cambió.");
                }
            }
            else if (associated is not null)
            {
                throw new InvalidOperationException(
                    $"Apareció una asociación inesperada para '{mapping.LocalPath}'.");
            }

            if (mapping.Kind == 1)
            {
                var remoteFolders = snapshot.Folders
                    .Where(x =>
                        x.CarpetaPadreId == snapshot.RootFolderId &&
                        string.Equals(
                            x.Nombre,
                            name,
                            StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var fileCollision = snapshot.Files.Any(x =>
                    x.CarpetaId == snapshot.RootFolderId &&
                    string.Equals(
                        x.Nombre,
                        name,
                        StringComparison.OrdinalIgnoreCase));

                if (fileCollision)
                    throw new InvalidOperationException(
                        $"Ya existe un archivo remoto llamado '{name}'.");

                if (remoteFolders.Count > 1)
                    throw new InvalidOperationException(
                        $"Existen varias carpetas remotas llamadas '{name}'.");

                if (mapping.SyncItemId.HasValue)
                {
                    if (!mapping.RemoteFolderId.HasValue ||
                        !remoteFolders.Any(x =>
                            x.Id == mapping.RemoteFolderId.Value))
                    {
                        throw new InvalidOperationException(
                            $"La carpeta remota asociada a '{name}' no coincide.");
                    }
                }
                else if (remoteFolders.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"La carpeta remota '{name}' ya existe sin asociación verificable.");
                }

                if (mapping.RemoteFolderId.HasValue)
                {
                    var otherOwners = items.Where(x =>
                        x.CarpetaRemotaId == mapping.RemoteFolderId &&
                        !PathsEqual(x.RutaLocal, mapping.LocalPath));

                    if (otherOwners.Any())
                        throw new InvalidOperationException(
                            $"La carpeta remota '{name}' ya pertenece a otra ruta.");
                }
            }
            else
            {
                var remoteFiles = snapshot.Files
                    .Where(x =>
                        x.CarpetaId == snapshot.RootFolderId &&
                        string.Equals(
                            x.Nombre,
                            name,
                            StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var folderCollision = snapshot.Folders.Any(x =>
                    x.CarpetaPadreId == snapshot.RootFolderId &&
                    string.Equals(
                        x.Nombre,
                        name,
                        StringComparison.OrdinalIgnoreCase));

                if (folderCollision)
                    throw new InvalidOperationException(
                        $"Ya existe una carpeta remota llamada '{name}'.");

                if (remoteFiles.Count > 1)
                    throw new InvalidOperationException(
                        $"Existen varios archivos remotos llamados '{name}'.");

                if (mapping.SyncItemId.HasValue)
                {
                    if (!mapping.RemoteFileId.HasValue ||
                        !remoteFiles.Any(x =>
                            x.Id == mapping.RemoteFileId.Value))
                    {
                        throw new InvalidOperationException(
                            $"El archivo remoto asociado a '{name}' no coincide.");
                    }
                }
                else if (remoteFiles.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"El archivo remoto '{name}' ya existe sin asociación verificable.");
                }

                if (mapping.RemoteFileId.HasValue)
                {
                    var otherOwners = items.Where(x =>
                        x.ArchivoRemotoId == mapping.RemoteFileId &&
                        !PathsEqual(x.RutaLocal, mapping.LocalPath));

                    if (otherOwners.Any())
                        throw new InvalidOperationException(
                            $"El archivo remoto '{name}' ya pertenece a otra ruta.");
                }
            }
        }

        return mappings;
    }

    public async Task<IReadOnlyList<SelectiveSyncMapping>> CreateMissingFoldersAsync(
        CancellationToken cancellationToken = default)
    {
        var mappings = await PrepareMappingsAsync(cancellationToken);

        foreach (var mapping in mappings.Where(x =>
            x.Kind == 1 &&
            !x.SyncItemId.HasValue &&
            !x.RemoteFolderId.HasValue))
        {
            cancellationToken.ThrowIfCancellationRequested();

            ValidateLocalSelection(mapping);

            var name = Path.GetFileName(mapping.LocalPath);

            // Revalidar el estado remoto y las asociaciones
            // inmediatamente antes de crear.
            var snapshot = await _syncApiClient.GetSnapshotAsync(
                cancellationToken);

            var items = await _syncApiClient.GetItemsAsync(
                cancellationToken);

            if (snapshot.RootFolderId != mapping.DeviceRootFolderId)
                throw new InvalidOperationException(
                    "La carpeta raíz del dispositivo cambió.");

            var currentMatches = items.Where(x =>
                PathsEqual(x.RutaLocal, mapping.LocalPath)).ToList();

            if (currentMatches.Count > 1)
                throw new InvalidOperationException($"Asociaciones duplicadas: '{mapping.LocalPath}'.");

            if (currentMatches.Count == 1)
            {
                var current = currentMatches[0];
                var remoteMatch = snapshot.Folders.Any(x =>
                    x.Id == current.CarpetaRemotaId &&
                    x.CarpetaPadreId == snapshot.RootFolderId &&
                    string.Equals(x.Nombre, name, StringComparison.OrdinalIgnoreCase));

                if (current.Tipo != SyncItemType.Folder ||
                    !current.CarpetaRemotaId.HasValue || !remoteMatch)
                    throw new InvalidOperationException($"Asociación inválida: '{mapping.LocalPath}'.");

                mapping.SyncItemId = current.SyncItemId;
                mapping.RemoteFolderId = current.CarpetaRemotaId;
                continue;
            }

            var occupiedByFolder = snapshot.Folders.Any(x =>
                x.CarpetaPadreId == snapshot.RootFolderId &&
                string.Equals(
                    x.Nombre,
                    name,
                    StringComparison.OrdinalIgnoreCase));

            var occupiedByFile = snapshot.Files.Any(x =>
                x.CarpetaId == snapshot.RootFolderId &&
                string.Equals(
                    x.Nombre,
                    name,
                    StringComparison.OrdinalIgnoreCase));

            if (occupiedByFolder || occupiedByFile)
                throw new InvalidOperationException(
                    $"El nombre remoto '{name}' ya está ocupado.");

            AtomicSyncFolderResult created;
            try
            {
                created = await _syncApiClient.CreateAtomicRemoteFolderAsync(
                    snapshot.RootFolderId, name, mapping.LocalPath, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Si el servidor confirmó la transacción pero se perdió la respuesta,
                // recuperar únicamente la asociación exacta del dispositivo.
                var recovered = await _syncApiClient.GetItemsAsync(cancellationToken);
                var recoveredMatches = recovered.Where(x =>
                    PathsEqual(x.RutaLocal, mapping.LocalPath)).ToList();
                if (recoveredMatches.Count != 1 || recoveredMatches[0].Tipo != SyncItemType.Folder ||
                    !recoveredMatches[0].CarpetaRemotaId.HasValue)
                    throw;

                var latest = await _syncApiClient.GetSnapshotAsync(cancellationToken);
                if (latest.RootFolderId != snapshot.RootFolderId ||
                    !latest.Folders.Any(x => x.Id == recoveredMatches[0].CarpetaRemotaId &&
                        x.CarpetaPadreId == snapshot.RootFolderId &&
                        string.Equals(x.Nombre, name, StringComparison.OrdinalIgnoreCase)))
                    throw;

                mapping.SyncItemId = recoveredMatches[0].SyncItemId;
                mapping.RemoteFolderId = recoveredMatches[0].CarpetaRemotaId;
                continue;
            }

            if (created.SyncItemId == Guid.Empty ||
                created.RemoteFolderId == Guid.Empty ||
                created.CarpetaPadreId != snapshot.RootFolderId ||
                !string.Equals(created.Nombre, name,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"El servidor devolvió una asociación atómica inválida para '{name}'.");
            }

            // Verificar contra el estado persistido, también en reintentos.
            var confirmedItems = await _syncApiClient.GetItemsAsync(cancellationToken);
            var matches = confirmedItems.Where(x =>
                PathsEqual(x.RutaLocal, mapping.LocalPath)).ToList();
            if (matches.Count != 1 ||
                matches[0].SyncItemId != created.SyncItemId ||
                matches[0].Tipo != SyncItemType.Folder ||
                matches[0].CarpetaRemotaId != created.RemoteFolderId)
            {
                throw new InvalidOperationException(
                    $"No se pudo verificar la asociación atómica de '{name}'.");
            }

            mapping.RemoteFolderId = created.RemoteFolderId;
            mapping.SyncItemId = created.SyncItemId;
        }

        return mappings;
    }

    private static IReadOnlyList<SelectiveSyncMapping> BuildMappings(
        IReadOnlyList<SelectiveSyncSelection> selections,
        SyncSnapshot snapshot,
        IReadOnlyList<SyncItem> items)
    {
        if (snapshot.RootFolderId == Guid.Empty)
            throw new InvalidOperationException(
                "El dispositivo no tiene una carpeta raíz válida.");

        var result = new List<SelectiveSyncMapping>();

        var visited = new HashSet<string>(
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

            var localPath = NormalizePath(selection.Path);

            if (!visited.Add(localPath))
                throw new InvalidOperationException(
                    $"La ruta '{localPath}' está seleccionada más de una vez.");

            var matchingItems = items
                .Where(x => PathsEqual(x.RutaLocal, localPath))
                .ToList();

            if (matchingItems.Count > 1)
                throw new InvalidOperationException(
                    $"Existen asociaciones duplicadas para '{localPath}'.");

            var existing = matchingItems.SingleOrDefault();

            if (existing is not null)
            {
                var expectedType = selection.Kind == 1
                    ? SyncItemType.Folder
                    : SyncItemType.File;

                if (existing.Tipo != expectedType)
                    throw new InvalidOperationException(
                        $"El tipo de asociación de '{localPath}' no coincide.");

                if (selection.Kind == 1 &&
                    !existing.CarpetaRemotaId.HasValue)
                {
                    throw new InvalidOperationException(
                        $"La carpeta '{localPath}' no tiene identificador remoto.");
                }

                if (selection.Kind == 0 &&
                    !existing.ArchivoRemotoId.HasValue)
                {
                    throw new InvalidOperationException(
                        $"El archivo '{localPath}' no tiene identificador remoto.");
                }
            }

            result.Add(new SelectiveSyncMapping
            {
                LocalPath = localPath,
                Kind = selection.Kind,
                SyncItemId = existing?.SyncItemId,
                RemoteFolderId = existing?.CarpetaRemotaId,
                RemoteFileId = existing?.ArchivoRemotoId,
                DeviceRootFolderId = snapshot.RootFolderId
            });
        }

        ValidateSelectionOverlap(result);
        ValidateRemoteCollisions(result);

        return result;
    }

    private static void ValidateSelectionOverlap(
        IReadOnlyList<SelectiveSyncMapping> mappings)
    {
        foreach (var folder in mappings.Where(x => x.Kind == 1))
        {
            var folderPrefix =
                Path.TrimEndingDirectorySeparator(folder.LocalPath) +
                Path.DirectorySeparatorChar;

            foreach (var other in mappings)
            {
                if (ReferenceEquals(folder, other))
                    continue;

                if (other.LocalPath.StartsWith(
                    folderPrefix,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"La selección '{other.LocalPath}' está dentro de " +
                        $"la carpeta ya seleccionada '{folder.LocalPath}'.");
                }
            }
        }
    }

    private static void ValidateRemoteCollisions(
        IReadOnlyList<SelectiveSyncMapping> mappings)
    {
        var folderIds = new HashSet<Guid>();
        var fileIds = new HashSet<Guid>();
        var names = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in mappings)
        {
            if (mapping.RemoteFolderId.HasValue &&
                !folderIds.Add(mapping.RemoteFolderId.Value))
            {
                throw new InvalidOperationException(
                    "Dos selecciones apuntan a la misma carpeta remota.");
            }

            if (mapping.RemoteFileId.HasValue &&
                !fileIds.Add(mapping.RemoteFileId.Value))
            {
                throw new InvalidOperationException(
                    "Dos selecciones apuntan al mismo archivo remoto.");
            }

            var name = Path.GetFileName(mapping.LocalPath);

            if (!names.Add(name))
                throw new InvalidOperationException(
                    $"Dos selecciones utilizan el mismo nombre remoto: '{name}'.");
        }
    }

    private static void ValidateLocalSelection(
        SelectiveSyncMapping mapping)
    {
        if (mapping.Kind == 1)
        {
            if (!Directory.Exists(mapping.LocalPath))
                throw new DirectoryNotFoundException(
                    $"Carpeta seleccionada no disponible: '{mapping.LocalPath}'.");
        }
        else
        {
            if (!File.Exists(mapping.LocalPath))
                throw new FileNotFoundException(
                    $"Archivo seleccionado no disponible: '{mapping.LocalPath}'.",
                    mapping.LocalPath);
        }

        // No atravesar enlaces simbólicos, junctions
        // ni puntos de montaje.
        var current = mapping.LocalPath;

        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException(
                        $"La selección utiliza un enlace o punto de montaje: '{current}'.");
            }

            var parent = Path.GetDirectoryName(current);

            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(
                    parent,
                    current,
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) ||
            string.IsNullOrWhiteSpace(right))
            return false;

        return string.Equals(
            NormalizePath(left),
            NormalizePath(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        return Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path));
    }

    private sealed class SelectiveSyncSelection
    {
        public string Path { get; set; } = string.Empty;
        public int Kind { get; set; }
    }
}

public sealed class SelectiveSyncMapping
{
    public string LocalPath { get; set; } = string.Empty;
    public int Kind { get; set; }
    public Guid? SyncItemId { get; set; }
    public Guid? RemoteFolderId { get; set; }
    public Guid? RemoteFileId { get; set; }
    public Guid DeviceRootFolderId { get; set; }
}
