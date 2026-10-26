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
    private readonly HashSet<string> _reportedMissingFolders =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string RemovalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GestorArchivos", "Sync", "removal-requests.json");

    private static readonly string MissingRootsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GestorArchivos", "Sync", "missing-roots.json");

    private static readonly string SelectionGuardPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GestorArchivos", "Sync", "selection-guard.txt");

    private static readonly string RootRenameApprovalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GestorArchivos", "Sync", "root-rename-approval.json");

    private static readonly string RootIdentityPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GestorArchivos", "Sync", "root-identities.json");

    private static async Task<Dictionary<string, string>> LoadRootIdentitiesAsync(CancellationToken ct)
    {
        if (!File.Exists(RootIdentityPath))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var content = await File.ReadAllTextAsync(RootIdentityPath, ct);
        return new Dictionary<string, string>(
            JsonSerializer.Deserialize<Dictionary<string, string>>(content) ??
            new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
    }

    private static async Task SaveRootIdentitiesAsync(
        Dictionary<string, string> identities, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RootIdentityPath)!);
        var temporary = RootIdentityPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(identities), ct);
            File.Move(temporary, RootIdentityPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

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
        await ProcessRemovalRequestsAsync(selections, cancellationToken);
        await _mapping.ReconcileRemovedRootSelectionsAsync(cancellationToken);

        var previousFingerprint = File.Exists(SelectionGuardPath)
            ? await File.ReadAllTextAsync(SelectionGuardPath, cancellationToken)
            : null;
        _suppressDestructiveOperations = !string.Equals(
            previousFingerprint, ComputeSelectionFingerprint(selections), StringComparison.Ordinal);

        // Una selección vacía es válida: el equipo permanece vinculado,
        // pero no se realizan transferencias ni se activa el modo tradicional.
        if (selections.Count == 0)
        {
            await SaveSelectionFingerprintAsync(ComputeSelectionFingerprint(selections), cancellationToken);
            return;
        }

        var snapshot = await _api.GetSnapshotAsync(cancellationToken);

        if (snapshot.RootFolderId == Guid.Empty)
            throw new InvalidOperationException(
                "El servidor no devolvió una raíz Sync válida.");

        await ReconcileSelectedRootRenamesAsync(selections, snapshot, cancellationToken);
        await ReconcileMissingSelectedRootsAsync(selections, cancellationToken);
        await ProcessRemovalRequestsAsync(selections, cancellationToken);
        _suppressDestructiveOperations = !string.Equals(
            previousFingerprint, ComputeSelectionFingerprint(selections), StringComparison.Ordinal);

        // While a root is missing, wait for the quarantine decision without
        // processing other selections. Do not mistake this for a sync failure.
        // In particular, do not advance the baseline or selection guard.
        // Keep missing roots quarantined without blocking healthy selections.
        // Never interpret an absent selected root as a deletion.
        selections = selections.Where(selection => selection.Kind == 1
                ? Directory.Exists(selection.Path)
                : File.Exists(selection.Path)).ToList();
        if (selections.Count == 0)
            return;

        await CaptureFolderIdentitiesAsync(cancellationToken);
        await ReconcileLocalFolderMovesAsync(selections, cancellationToken);

        // Antes de cualquier escritura, detectar movimientos o renombrados locales
        // ambiguos. Sin esta barrera el recorrido por nombre puede crear otro
        // archivo remoto y después enviar el original a la papelera.
        await GuardAgainstUnresolvedLocalMovesAsync(
            selections, cancellationToken);

        await ReconcileRemoteFileRenamesAsync(selections, cancellationToken);

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

        await CaptureFolderIdentitiesAsync(cancellationToken);
        await RefreshBaselineAsync(cancellationToken);
        await SaveSelectionFingerprintAsync(ComputeSelectionFingerprint(selections), cancellationToken);
    }


    // A selected root can only be renamed to a sibling with verified identity.
    // An absent root is never automatically treated as deleted.
    private async Task ReconcileSelectedRootRenamesAsync(
        List<Selection> selections, SyncSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var items = await _api.GetItemsAsync(cancellationToken);
        var baselines = await _baselines.LoadAsync(cancellationToken);
        var changed = false;
        var rootIdentities = await LoadRootIdentitiesAsync(cancellationToken);
        var identitiesChanged = false;
        foreach (var selection in selections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selection.Kind == 1 ? Directory.Exists(selection.Path) : File.Exists(selection.Path))
            {
                var currentIdentity = WindowsFileIdentity.TryGet(selection.Path, selection.Kind == 1);
                if (currentIdentity is not null &&
                    (!rootIdentities.TryGetValue(selection.Path, out var savedIdentity) ||
                     !string.Equals(savedIdentity, currentIdentity, StringComparison.Ordinal)))
                {
                    rootIdentities[selection.Path] = currentIdentity;
                    identitiesChanged = true;
                }
                continue;
            }

            var oldPath = Path.GetFullPath(selection.Path);
            var parent = Path.GetDirectoryName(oldPath);
            if (parent is null || !Directory.Exists(parent))
                continue; // Offline volume or missing parent: do not touch remote data.
            ValidatePathWithoutLinks(parent);

            var item = items.SingleOrDefault(x => PathsEqual(x.RutaLocal, oldPath) &&
                (selection.Kind == 1 ? x.Tipo == SyncItemType.Folder : x.Tipo == SyncItemType.File));
            if (item is null)
            {
                Console.WriteLine($"[SyncRename] Sin asociación para la raíz ausente: {oldPath}");
                continue;
            }
            var descendants = items.Where(x => PathsEqual(x.RutaLocal, oldPath) ||
                (selection.Kind == 1 && IsDescendantPath(x.RutaLocal, oldPath))).ToList();
            var associatedFiles = descendants.Where(x => x.Tipo == SyncItemType.File &&
                x.ArchivoRemotoId.HasValue).ToList();
            if (selection.Kind == 0 && associatedFiles.Count != 1) continue;
            var candidates = selection.Kind == 1
                ? Directory.EnumerateDirectories(parent).ToList()
                : Directory.EnumerateFiles(parent).ToList();
            var verified = new List<string>();
            // The NTFS volume + file ID survives a rename and disambiguates
            // directories containing identical files. It is checked first.
            if (rootIdentities.TryGetValue(oldPath, out var expectedIdentity))
            {
                foreach (var candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsReparsePoint(candidate) ||
                        items.Any(x => !descendants.Contains(x) && PathsEqual(x.RutaLocal, candidate)) ||
                        selections.Any(x => PathsEqual(x.Path, candidate)))
                        continue;
                    var candidateIdentity = WindowsFileIdentity.TryGet(candidate, selection.Kind == 1);
                    if (string.Equals(candidateIdentity, expectedIdentity, StringComparison.Ordinal))
                        verified.Add(candidate);
                }
            }
            // Legacy selections without a stored ID retain conservative content matching.
            // An empty mapping is not proof of identity: never accept every sibling.
            if (verified.Count == 0 && !rootIdentities.ContainsKey(oldPath) && associatedFiles.Count > 0)
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsReparsePoint(candidate) ||
                    items.Any(x => !descendants.Contains(x) && PathsEqual(x.RutaLocal, candidate)) ||
                    selections.Any(x => PathsEqual(x.Path, candidate)))
                    continue;
                var valid = true;
                foreach (var file in associatedFiles)
                {
                    var destinationFile = selection.Kind == 0 ? candidate :
                        Path.Combine(candidate, Path.GetRelativePath(oldPath, file.RutaLocal));
                    var baseline = baselines.SingleOrDefault(x =>
                        x.SyncItemId == file.SyncItemId && x.RemoteFileId == file.ArchivoRemotoId);
                    var remote = snapshot.Files.SingleOrDefault(x => x.Id == file.ArchivoRemotoId);
                    if (baseline is null || remote is null ||
                        !HashesEqual(baseline.LocalHash, baseline.RemoteHash) ||
                        !HashesEqual(remote.HashSha256, baseline.RemoteHash) ||
                        !File.Exists(destinationFile))
                    {
                        valid = false;
                        break;
                    }
                    ValidatePathWithoutLinks(destinationFile);
                    if (!HashesEqual(await HashLocalAsync(destinationFile, cancellationToken),
                            baseline.LocalHash))
                    {
                        valid = false;
                        break;
                    }
                }
                if (valid) verified.Add(candidate);
            }
            // Fallback: some previously uploaded files have no active per-file
            // mapping or baseline. Verify the entire direct-file set against
            // the remote folder by name, size and SHA-256, never by name alone.
            if (verified.Count != 1 && !rootIdentities.ContainsKey(oldPath) && selection.Kind == 1 &&
                item.CarpetaRemotaId is Guid rootFolderId)
            {
                var remoteDirectFiles = snapshot.Files
                    .Where(x => x.CarpetaId == rootFolderId).ToList();
                var remoteDirectFolders = snapshot.Folders
                    .Where(x => x.CarpetaPadreId == rootFolderId).ToList();
                if (remoteDirectFiles.Count > 0 && remoteDirectFolders.Count == 0 &&
                    remoteDirectFiles.All(x => !string.IsNullOrWhiteSpace(x.HashSha256)))
                {
                    // A failed baseline verification must not contaminate the
                    // independent remote-manifest identity check.
                    verified.Clear();
                    foreach (var candidate in candidates)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (IsReparsePoint(candidate) ||
                            items.Any(x => !descendants.Contains(x) &&
                                PathsEqual(x.RutaLocal, candidate)) ||
                            selections.Any(x => PathsEqual(x.Path, candidate)))
                            continue;
                        // Sibling directories may be protected system folders.
                        // They are not eligible rename candidates; do not abort
                        // reconciliation because Windows denies their enumeration.
                        List<string> localFiles;
                        try
                        {
                            ValidatePathWithoutLinks(candidate);
                            localFiles = Directory.EnumerateFiles(candidate).ToList();
                            if (Directory.EnumerateDirectories(candidate).Any() ||
                                localFiles.Count != remoteDirectFiles.Count)
                                continue;
                        }
                        catch (UnauthorizedAccessException) { continue; }
                        catch (IOException) { continue; }
                        var verifiedFiles = true;
                        foreach (var remoteFile in remoteDirectFiles)
                        {
                            var localFile = localFiles.SingleOrDefault(x =>
                                NamesEqual(Path.GetFileName(x), remoteFile.Nombre));
                            if (localFile is null ||
                                new FileInfo(localFile).Length != remoteFile.TamanoBytes)
                            {
                                verifiedFiles = false;
                                break;
                            }
                            ValidatePathWithoutLinks(localFile);
                            if (!HashesEqual(await HashLocalAsync(localFile, cancellationToken),
                                    remoteFile.HashSha256))
                            {
                                verifiedFiles = false;
                                break;
                            }
                        }
                        if (verifiedFiles) verified.Add(candidate);
                    }
                }
            }
            if (verified.Count > 1 && File.Exists(RootRenameApprovalPath))
            {
                // Explicit, one-use operator approval. The destination must still
                // pass the complete identity/content verification above.
                using var approval = JsonDocument.Parse(
                    await File.ReadAllTextAsync(RootRenameApprovalPath, cancellationToken));
                var approvedOld = approval.RootElement.TryGetProperty("OldPath", out var op)
                    ? op.GetString() : null;
                var approvedNew = approval.RootElement.TryGetProperty("NewPath", out var np)
                    ? np.GetString() : null;
                if (approvedOld is not null && approvedNew is not null &&
                    PathsEqual(approvedOld, oldPath))
                {
                    var matching = verified.Where(x => PathsEqual(x, approvedNew)).ToList();
                    if (matching.Count == 1)
                    {
                        verified = matching;
                        Console.WriteLine($"[SyncRename] Aprobación explícita verificada: {approvedNew}");
                    }
                }
            }
            if (verified.Count != 1)
            {
                Console.WriteLine($"[SyncRename] Sin coincidencia única para '{oldPath}': " +
                    $"asociacionesArchivo={associatedFiles.Count}, candidatos={candidates.Count}, " +
                    $"verificados={verified.Count}, carpetaRemota={item.CarpetaRemotaId}, " +
                    $"archivoRemoto={item.ArchivoRemotoId}. Se conserva la selección.");
                // Give the operator exact, already content-verified paths to
                // disambiguate. Never choose one using names or ordering.
                if (verified.Count > 1)
                    foreach (var match in verified.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                        Console.WriteLine($"[SyncRename] Candidato verificado: {match}");
                continue; // Ambiguous or deleted: stop safely, never create a new root.
            }
            var destination = verified[0];
            Console.WriteLine($"[SyncRename] Identidad verificada: '{oldPath}' => '{destination}'.");
            var name = Path.GetFileName(destination);
            ValidateRemoteName(name);
            if (snapshot.Folders.Any(x => x.CarpetaPadreId == snapshot.RootFolderId &&
                    NamesEqual(x.Nombre, name) && x.Id != item.CarpetaRemotaId) ||
                snapshot.Files.Any(x => x.CarpetaId == snapshot.RootFolderId &&
                    NamesEqual(x.Nombre, name) && x.Id != item.ArchivoRemotoId))
                throw new InvalidOperationException(
                    $"Ya existe '{name}' en la raíz web. Resuelve el conflicto antes de sincronizar.");

            if (selection.Kind == 1 && item.CarpetaRemotaId is Guid folderId)
                await _api.RenameRemoteFolderAsync(folderId, name, cancellationToken);
            else if (selection.Kind == 0 && item.ArchivoRemotoId is Guid fileId)
                await _api.RenameRemoteFileAsync(fileId, name, cancellationToken);
            else
                throw new InvalidOperationException($"Asociación incompleta: '{oldPath}'.");

            foreach (var affected in descendants.OrderBy(x => x.RutaLocal.Length))
            {
                var newPath = PathsEqual(affected.RutaLocal, oldPath)
                    ? destination
                    : Path.Combine(destination, Path.GetRelativePath(oldPath, affected.RutaLocal));
                await _api.UpdateItemPathAsync(affected.SyncItemId, newPath, cancellationToken);
                affected.RutaLocal = newPath;
            }
            var updatedBaselines = (await _baselines.LoadAsync(cancellationToken)).ToList();
            foreach (var baseline in updatedBaselines.Where(x =>
                PathsEqual(x.LocalPath, oldPath) ||
                (selection.Kind == 1 && IsDescendantPath(x.LocalPath, oldPath))))
                baseline.LocalPath = PathsEqual(baseline.LocalPath, oldPath)
                    ? destination
                    : Path.Combine(destination, Path.GetRelativePath(oldPath, baseline.LocalPath));
            await _baselines.SaveAsync(updatedBaselines, cancellationToken);
            selection.Path = destination;
            if (File.Exists(RootRenameApprovalPath))
            {
                using var approval = JsonDocument.Parse(
                    await File.ReadAllTextAsync(RootRenameApprovalPath, cancellationToken));
                if (approval.RootElement.TryGetProperty("OldPath", out var approvedOld) &&
                    approvedOld.GetString() is string approvedPath &&
                    PathsEqual(approvedPath, oldPath))
                    File.Delete(RootRenameApprovalPath);
            }
            if (rootIdentities.Remove(oldPath)) identitiesChanged = true;
            var destinationIdentity = WindowsFileIdentity.TryGet(destination, selection.Kind == 1);
            if (destinationIdentity is not null)
            {
                rootIdentities[destination] = destinationIdentity;
                identitiesChanged = true;
            }
            changed = true;
        }
        if (identitiesChanged)
            await SaveRootIdentitiesAsync(rootIdentities, cancellationToken);
        if (changed)
        {
            var directory = Path.GetDirectoryName(SelectionsPath)!;
            Directory.CreateDirectory(directory);
            var temporary = SelectionsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary,
                    JsonSerializer.Serialize(selections), cancellationToken);
                File.Move(temporary, SelectionsPath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }


    // Missing roots are quarantined for repeated sync cycles before trashing.
    // Never infer deletion from a missing parent, unavailable drive, or reparse point.
    private async Task ReconcileMissingSelectedRootsAsync(
        List<Selection> selections, CancellationToken cancellationToken)
    {
        var pending = File.Exists(MissingRootsPath)
            ? JsonSerializer.Deserialize<List<MissingRoot>>(
                await File.ReadAllTextAsync(MissingRootsPath, cancellationToken)) ?? new()
            : new List<MissingRoot>();
        var now = DateTime.UtcNow;
        var items = await _api.GetItemsAsync(cancellationToken);
        var snapshot = await _api.GetSnapshotAsync(cancellationToken);
        var baselines = await _baselines.LoadAsync(cancellationToken);
        var changed = false;
        foreach (var selection in selections.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var present = selection.Kind == 1
                ? Directory.Exists(selection.Path) : File.Exists(selection.Path);
            var record = pending.FirstOrDefault(x =>
                x.Kind == selection.Kind && PathsEqual(x.Path, selection.Path));
            if (present)
            {
                if (record is not null) pending.Remove(record);
                continue;
            }

            var parent = Path.GetDirectoryName(Path.GetFullPath(selection.Path));
            if (parent is null || !Directory.Exists(parent))
            {
                if (record is not null) pending.Remove(record);
                continue;
            }
            ValidatePathWithoutLinks(parent);
            var drive = new DriveInfo(Path.GetPathRoot(selection.Path)!);
            if (!drive.IsReady || drive.DriveType is DriveType.Network or DriveType.Removable)
            {
                if (record is not null) pending.Remove(record);
                continue;
            }

            var matches = items.Where(x => PathsEqual(x.RutaLocal, selection.Path) &&
                (selection.Kind == 1 ? x.Tipo == SyncItemType.Folder : x.Tipo == SyncItemType.File)).ToList();
            if (matches.Count != 1 ||
                (selection.Kind == 1 && !matches[0].CarpetaRemotaId.HasValue) ||
                (selection.Kind == 0 && !matches[0].ArchivoRemotoId.HasValue))
                continue;

            if (record is null)
            {
                pending.Add(new MissingRoot
                {
                    Path = selection.Path,
                    Kind = selection.Kind,
                    FirstSeenUtc = now,
                    Observations = 1
                });
                continue;
            }
            record.Observations++;
            // Folder identity is not persisted independently of its path.
            // A rename of an empty folder or one with changed files is
            // indistinguishable from deletion. Never auto-trash in that case.
            if (selection.Kind == 1)
                continue;
            if (record.Observations < 3 ||
                now - record.FirstSeenUtc < TimeSpan.FromSeconds(15))
                continue;

            // Do not trash remotely changed content using an old local observation.
            var associatedFiles = items.Where(x => x.Tipo == SyncItemType.File &&
                x.ArchivoRemotoId.HasValue &&
                (PathsEqual(x.RutaLocal, selection.Path) ||
                 (selection.Kind == 1 && IsDescendantPath(x.RutaLocal, selection.Path)))).ToList();
            if (associatedFiles.Any(x =>
            {
                var baseline = baselines.SingleOrDefault(y =>
                    y.SyncItemId == x.SyncItemId && y.RemoteFileId == x.ArchivoRemotoId);
                var remote = snapshot.Files.SingleOrDefault(y => y.Id == x.ArchivoRemotoId);
                return baseline is null || remote is null ||
                    !HashesEqual(baseline.RemoteHash, remote.HashSha256);
            }))
                throw new InvalidOperationException(
                    $"La raíz '{selection.Path}' desapareció localmente, pero tiene " +
                    "cambios remotos sin conciliar. Se bloqueó el envío a papelera.");

            // Persist the request before removing the selection. If interrupted,
            // ProcessRemovalRequestsAsync will resume without losing the remote object.
            var requests = File.Exists(RemovalPath)
                ? JsonSerializer.Deserialize<List<RemovalRequest>>(
                    await File.ReadAllTextAsync(RemovalPath, cancellationToken)) ?? new()
                : new List<RemovalRequest>();
            if (!requests.Any(x => PathsEqual(x.Path, selection.Path) &&
                x.Kind == selection.Kind))
                requests.Add(new RemovalRequest
                {
                    Path = selection.Path,
                    Kind = selection.Kind,
                    SendToTrash = true
                });
            await WriteJsonAtomicallyAsync(RemovalPath, requests, cancellationToken);
            selections.Remove(selection);
            await WriteJsonAtomicallyAsync(SelectionsPath, selections, cancellationToken);
            pending.Remove(record);
            changed = true;
        }
        await WriteJsonAtomicallyAsync(MissingRootsPath, pending, cancellationToken);
        if (changed)
            await SaveSelectionFingerprintAsync(
                ComputeSelectionFingerprint(selections), cancellationToken);
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary,
                JsonSerializer.Serialize(value), cancellationToken);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class MissingRoot
    {
        public string Path { get; set; } = string.Empty;
        public int Kind { get; set; }
        public DateTime FirstSeenUtc { get; set; }
        public int Observations { get; set; }
    }

    // Reconciliar renombrados y traslados remotos de archivos mediante su identidad.
    private async Task ReconcileRemoteFileRenamesAsync(
        IReadOnlyList<Selection> selections,
        CancellationToken cancellationToken)
    {
        var items = await _api.GetItemsAsync(cancellationToken);
        var snapshot = await _api.GetSnapshotAsync(cancellationToken);
        var baselines = await _baselines.LoadAsync(cancellationToken);
        var remoteById = snapshot.Files.ToDictionary(x => x.Id);

        foreach (var item in items.Where(x =>
            x.Tipo == SyncItemType.File && x.ArchivoRemotoId.HasValue))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsSelectedItemPath(item.RutaLocal, selections) ||
                !remoteById.TryGetValue(item.ArchivoRemotoId!.Value,
                    out var remote))
                continue;

            var oldPath = Path.GetFullPath(item.RutaLocal);
            var parentPath = Path.GetDirectoryName(oldPath);
            if (parentPath is null || !Directory.Exists(parentPath))
                continue;

            var parentMapping = items.SingleOrDefault(x =>
                x.Tipo == SyncItemType.Folder &&
                x.CarpetaRemotaId.HasValue &&
                PathsEqual(x.RutaLocal, parentPath));

            var destinationParent = parentMapping?.CarpetaRemotaId == remote.CarpetaId
                ? parentPath
                : items.Where(x => x.Tipo == SyncItemType.Folder &&
                    x.CarpetaRemotaId == remote.CarpetaId &&
                    Directory.Exists(x.RutaLocal))
                    .Select(x => x.RutaLocal)
                    .SingleOrDefault();

            // A destination outside the selected local tree is not managed here.
            if (destinationParent is null)
                continue;

            ValidateRemoteName(remote.Nombre);
            var destination = Path.Combine(destinationParent, remote.Nombre);
            if (PathsEqual(oldPath, destination))
                continue;
            if (!IsSelectedItemPath(destination, selections))
                continue;

            ValidatePathWithoutLinks(parentPath);
            ValidatePathWithoutLinks(destinationParent);

            var baseline = baselines.SingleOrDefault(x =>
                x.SyncItemId == item.SyncItemId &&
                x.RemoteFileId == remote.Id);
            if (baseline is null ||
                !HashesEqual(baseline.LocalHash, baseline.RemoteHash) ||
                !HashesEqual(baseline.RemoteHash, remote.HashSha256))
                throw new InvalidOperationException(
                    $"Renombrado remoto sin baseline confiable: '{oldPath}'.");

            if (items.Any(x => x.SyncItemId != item.SyncItemId &&
                PathsEqual(x.RutaLocal, destination)) ||
                Directory.Exists(destination))
                throw new InvalidOperationException(
                    $"Destino del renombrado remoto ocupado: '{destination}'.");

            // Recuperación tras una interrupción entre File.Move y
            // UpdateItemPathAsync: nunca sobrescribir el destino.
            if (File.Exists(oldPath))
            {
                ValidatePathWithoutLinks(oldPath);
                if (File.Exists(destination))
                    throw new InvalidOperationException(
                        $"Ambos nombres existen: '{oldPath}' y '{destination}'.");

                if (!HashesEqual(
                    await HashLocalAsync(oldPath, cancellationToken),
                    baseline.LocalHash))
                    throw new InvalidOperationException(
                        $"El archivo local cambió antes del renombrado: '{oldPath}'.");

                File.Move(oldPath, destination);
            }
            else if (!File.Exists(destination))
            {
                throw new InvalidOperationException(
                    $"No se encuentra el archivo para renombrar: '{oldPath}'.");
            }

            ValidatePathWithoutLinks(destination);
            if (!HashesEqual(
                await HashLocalAsync(destination, cancellationToken),
                baseline.LocalHash))
                throw new InvalidOperationException(
                    $"No coincide el contenido del archivo renombrado: '{destination}'.");

            await _api.UpdateItemPathAsync(
                item.SyncItemId, destination, cancellationToken);

            var updated = (await _baselines.LoadAsync(cancellationToken))
                .ToList();
            var previous = updated.SingleOrDefault(x =>
                x.SyncItemId == item.SyncItemId &&
                x.RemoteFileId == remote.Id);
            if (previous is not null)
            {
                previous.LocalPath = destination;
                previous.RemoteVersion = remote.Version;
                await _baselines.SaveAsync(updated, cancellationToken);
            }
        }
    }

    private async Task CaptureFolderIdentitiesAsync(CancellationToken cancellationToken)
    {
        var identities = await LoadRootIdentitiesAsync(cancellationToken);
        var changed = false;
        var items = await _api.GetItemsAsync(cancellationToken);
        foreach (var item in items.Where(x => x.Tipo == SyncItemType.Folder &&
                                              Directory.Exists(x.RutaLocal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(item.RutaLocal)) continue;
            var identity = WindowsFileIdentity.TryGet(item.RutaLocal, true);
            if (identity is null) continue;
            if (!identities.TryGetValue(item.RutaLocal, out var saved) ||
                !string.Equals(saved, identity, StringComparison.Ordinal))
            {
                identities[item.RutaLocal] = identity;
                changed = true;
            }
        }
        if (changed) await SaveRootIdentitiesAsync(identities, cancellationToken);
    }

    private async Task ReconcileLocalFolderMovesAsync(
        IReadOnlyList<Selection> selections,
        CancellationToken cancellationToken)
    {
        var items = await _api.GetItemsAsync(cancellationToken);
        var snapshot = await _api.GetSnapshotAsync(cancellationToken);
        var folderIdentities = await LoadRootIdentitiesAsync(cancellationToken);
        var selectedRoots = selections.Where(x => x.Kind == 1)
            .Select(x => Path.GetFullPath(x.Path)).ToList();

        // Procesar únicamente carpetas no raíz cuya ubicación anterior
        // ya no existe. La identidad remota debe ser inequívoca.
        var missing = items.Where(x => x.Tipo == SyncItemType.Folder &&
            x.CarpetaRemotaId.HasValue &&
            selectedRoots.Any(root => IsDescendantPath(x.RutaLocal, root)) &&
            !Directory.Exists(x.RutaLocal))
            .OrderBy(x => x.RutaLocal.Length).ToList();

        var missingPaths = missing.Select(x => x.RutaLocal)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _reportedMissingFolders.RemoveWhere(x => !missingPaths.Contains(x));

        foreach (var folder in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A folder removed from the web by another actor no longer has a
            // remote object to protect. Detach only that obsolete mapping.
            // A local disappearance alone never authorizes remote deletion.
            if (!snapshot.Folders.Any(x => x.Id == folder.CarpetaRemotaId))
            {
                await _api.DetachTrashedItemAsync(folder.SyncItemId, cancellationToken);
                folderIdentities.Remove(folder.RutaLocal);
                await SaveRootIdentitiesAsync(folderIdentities, cancellationToken);
                _reportedMissingFolders.Remove(folder.RutaLocal);
                Console.WriteLine($"[SyncFolder] Asociación remota inexistente retirada: {folder.RutaLocal}");
                continue;
            }
            // Si un ancestro también desapareció, será él quien actualice
            // todas las rutas descendientes.
            if (missing.Any(x => x.SyncItemId != folder.SyncItemId &&
                IsDescendantPath(folder.RutaLocal, x.RutaLocal)))
                continue;

            var oldPrefix = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(folder.RutaLocal));
            var descendants = items.Where(x =>
                IsDescendantPath(x.RutaLocal, oldPrefix)).ToList();
            var descendantFiles = descendants.Where(x =>
                x.Tipo == SyncItemType.File &&
                x.ArchivoRemotoId.HasValue).ToList();
            var baselines = await _baselines.LoadAsync(cancellationToken);
            if (folderIdentities.TryGetValue(folder.RutaLocal, out var originalIdentity))
            {
                var matchesByIdentity = new List<string>();
                foreach (var root in selectedRoots.Where(Directory.Exists))
                foreach (var candidate in Directory.EnumerateDirectories(root, "*",
                             SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsReparsePoint(candidate) ||
                        items.Any(x => x.Tipo == SyncItemType.Folder &&
                            PathsEqual(x.RutaLocal, candidate))) continue;
                    if (string.Equals(WindowsFileIdentity.TryGet(candidate, true),
                        originalIdentity, StringComparison.Ordinal))
                        matchesByIdentity.Add(candidate);
                }
                if (matchesByIdentity.Count == 1)
                {
                    var identityDestination = matchesByIdentity[0];
                    var parent = items.SingleOrDefault(x => x.Tipo == SyncItemType.Folder &&
                        x.CarpetaRemotaId.HasValue &&
                        PathsEqual(x.RutaLocal, Path.GetDirectoryName(identityDestination)!));
                    var identityRemote = snapshot.Folders.SingleOrDefault(x => x.Id == folder.CarpetaRemotaId);
                    if (parent is null || identityRemote is null)
                        throw new InvalidOperationException($"Asociación incompleta: {identityDestination}");
                    var name = Path.GetFileName(identityDestination);
                    ValidateRemoteName(name);
                    if (snapshot.Folders.Any(x => x.Id != identityRemote.Id &&
                        x.CarpetaPadreId == parent.CarpetaRemotaId &&
                        NamesEqual(x.Nombre, name)) ||
                        snapshot.Files.Any(x => x.CarpetaId == parent.CarpetaRemotaId &&
                        NamesEqual(x.Nombre, name)))
                        throw new InvalidOperationException($"Destino remoto ocupado: {identityDestination}");
                    if (identityRemote.CarpetaPadreId != parent.CarpetaRemotaId)
                        await _api.MoveRemoteFolderAsync(identityRemote.Id,
                            parent.CarpetaRemotaId!.Value, cancellationToken);
                    if (!NamesEqual(identityRemote.Nombre, name))
                        await _api.RenameRemoteFolderAsync(identityRemote.Id, name, cancellationToken);
                    foreach (var identityAffected in items.Where(x => PathsEqual(x.RutaLocal, folder.RutaLocal) ||
                                 IsDescendantPath(x.RutaLocal, folder.RutaLocal))
                                 .OrderBy(x => x.RutaLocal.Length))
                    {
                        var newPath = PathsEqual(identityAffected.RutaLocal, folder.RutaLocal)
                            ? identityDestination
                            : Path.Combine(identityDestination, Path.GetRelativePath(folder.RutaLocal, identityAffected.RutaLocal));
                        await _api.UpdateItemPathAsync(identityAffected.SyncItemId, newPath, cancellationToken);
                    }
                    var identityUpdated = (await _baselines.LoadAsync(cancellationToken)).ToList();
                    foreach (var baseline in identityUpdated.Where(x =>
                        PathsEqual(x.LocalPath, folder.RutaLocal) ||
                        IsDescendantPath(x.LocalPath, folder.RutaLocal)))
                        baseline.LocalPath = Path.Combine(identityDestination,
                            Path.GetRelativePath(folder.RutaLocal, baseline.LocalPath));
                    await _baselines.SaveAsync(identityUpdated, cancellationToken);
                    folderIdentities.Remove(folder.RutaLocal);
                    folderIdentities[identityDestination] = originalIdentity;
                    await SaveRootIdentitiesAsync(folderIdentities, cancellationToken);
                    Console.WriteLine($"[SyncFolder] Renombrado por identidad: {folder.RutaLocal} => {identityDestination}");
                    continue;
                }
            }
            if (descendantFiles.Count == 0)
            {
                // Empty folders have no content hash to establish a move.
                // A matching Recycle Bin record is positive evidence of deletion;
                // absence alone (including after device re-registration) is not.
                if (!_suppressDestructiveOperations &&
                    IsInWindowsRecycleBin(folder.RutaLocal))
                {
                    await _api.TrashSelectedFolderAsync(folder.CarpetaRemotaId!.Value,
                        cancellationToken);
                    _reportedMissingFolders.Remove(folder.RutaLocal);
                    Console.WriteLine($"[SyncFolder] Carpeta vacía eliminada en Windows enviada a papelera web: {folder.RutaLocal}");
                }
                else if (_reportedMissingFolders.Add(folder.RutaLocal))
                    Console.WriteLine($"[SyncFolder] Carpeta local ausente: " +
                        $"{folder.RutaLocal}. Podría estar eliminada o renombrada; " +
                        "se conserva el remoto hasta tener evidencia.");
                continue;
            }

            // No identificar carpetas con un subconjunto de sus archivos:
            // podría tratarse de una copia parcial o una pérdida de datos.
            if (descendants.Any(x => x.Tipo == SyncItemType.File &&
                !x.ArchivoRemotoId.HasValue))
                throw new InvalidOperationException(
                    $"Asociaciones incompletas en '{folder.RutaLocal}'.");

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
                    // No aceptar como destino una carpeta que todavía
                    // está dentro del árbol anterior ni una que contenga
                    // asociaciones preexistentes de otra carpeta.
                    if (IsDescendantPath(candidate, oldPrefix) ||
                        items.Any(x => x.Tipo == SyncItemType.Folder &&
                            IsDescendantPath(x.RutaLocal, candidate) &&
                            !IsDescendantPath(x.RutaLocal, oldPrefix)))
                        continue;
                    if (IsReparsePoint(candidate))
                        throw new InvalidOperationException(
                            $"Enlace de directorio no permitido: '{candidate}'.");

                    // La carpeta se reconoce por las rutas relativas de
                    // sus archivos asociados y sus hashes de baseline.
                    var verified = true;
                    foreach (var file in descendantFiles)
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
            {
                // A missing path alone cannot authorize remote deletion.
                // Only a matching Windows Recycle Bin entry establishes that
                // this folder was explicitly deleted rather than relocated.
                if (IsInWindowsRecycleBin(folder.RutaLocal))
                {
                    var remoteFolder = snapshot.Folders.SingleOrDefault(x =>
                        x.Id == folder.CarpetaRemotaId);
                    if (remoteFolder is null)
                        continue;

                    // Verify the remote folder and every tracked descendant
                    // are still unchanged before applying a destructive action.
                    var remoteFiles = snapshot.Files.ToDictionary(x => x.Id);
                    var safe = descendantFiles.All(file =>
                    {
                        var baseline = baselines.SingleOrDefault(x =>
                            x.SyncItemId == file.SyncItemId &&
                            x.RemoteFileId == file.ArchivoRemotoId);
                        return baseline is not null &&
                            remoteFiles.TryGetValue(file.ArchivoRemotoId!.Value, out var current) &&
                            baseline.RemoteVersion == current.Version &&
                            HashesEqual(baseline.RemoteHash, current.HashSha256) &&
                            HashesEqual(baseline.LocalHash, baseline.RemoteHash);
                    });
                    if (!safe)
                    {
                        Console.WriteLine($"[SyncFolder] Eliminación pendiente: contenido remoto sin baseline verificable en {folder.RutaLocal}");
                        continue;
                    }

                    await _api.TrashSelectedFolderAsync(remoteFolder.Id, cancellationToken);
                    Console.WriteLine($"[SyncFolder] Carpeta eliminada en Windows enviada a papelera web: {folder.RutaLocal}");
                    continue;
                }

                if (_reportedMissingFolders.Add(folder.RutaLocal))
                    Console.WriteLine($"[SyncFolder] Carpeta local ausente sin evidencia de eliminación: {folder.RutaLocal}. Se conserva el remoto.");
                continue;
            }
            if (candidates.Count != 1)
                throw new InvalidOperationException(
                    $"Movimiento de carpeta ambiguo: '{folder.RutaLocal}'.");

            var destination = candidates[0];
            ValidatePathWithoutLinks(destination);
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

            // Verificar que ningún archivo remoto asociado cambió desde
            // el baseline antes de actualizar las rutas de toda la carpeta.
            foreach (var file in descendantFiles)
            {
                var baseline = baselines.SingleOrDefault(x =>
                    x.SyncItemId == file.SyncItemId &&
                    x.RemoteFileId == file.ArchivoRemotoId);
                var current = snapshot.Files.SingleOrDefault(x =>
                    x.Id == file.ArchivoRemotoId);
                if (baseline is null || current is null ||
                    baseline.RemoteVersion != current.Version ||
                    !HashesEqual(baseline.RemoteHash, current.HashSha256))
                    throw new InvalidOperationException(
                        $"El contenido remoto de '{file.RutaLocal}' cambió. " +
                        "Se bloqueó el movimiento de carpeta.");
            }

            // Revalidar la evidencia local antes de escribir en remoto.
            foreach (var file in descendantFiles)
            {
                var relative = Path.GetRelativePath(oldPrefix, file.RutaLocal);
                var newPath = Path.Combine(destination, relative);
                var baseline = baselines.Single(x =>
                    x.SyncItemId == file.SyncItemId &&
                    x.RemoteFileId == file.ArchivoRemotoId);
                if (!File.Exists(newPath))
                    throw new InvalidOperationException(
                        $"Desapareció un archivo de la carpeta movida: '{newPath}'.");
                ValidatePathWithoutLinks(newPath);
                if (!HashesEqual(await HashLocalAsync(newPath,
                        cancellationToken), baseline.LocalHash))
                    throw new InvalidOperationException(
                        $"Cambió un archivo de la carpeta movida: '{newPath}'.");
            }

            if (remote.CarpetaPadreId != targetParent)
                await _api.MoveRemoteFolderAsync(remote.Id, targetParent,
                    cancellationToken);

            remote = (await _api.GetSnapshotAsync(cancellationToken))
                .Folders.SingleOrDefault(x => x.Id == folder.CarpetaRemotaId);
            if (remote is null || remote.CarpetaPadreId != targetParent)
                throw new InvalidOperationException(
                    $"No se verificó el movimiento de '{destination}'.");

            if (!NamesEqual(remote.Nombre, newName))
                await _api.RenameRemoteFolderAsync(remote.Id, newName,
                    cancellationToken);

            remote = (await _api.GetSnapshotAsync(cancellationToken))
                .Folders.SingleOrDefault(x => x.Id == folder.CarpetaRemotaId);
            if (remote is null || remote.CarpetaPadreId != targetParent ||
                !NamesEqual(remote.Nombre, newName))
                throw new InvalidOperationException(
                    $"No se verificó el renombrado de '{destination}'.");

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
            string NewName, SyncBaselineItem Baseline, bool ContentChanged)>();

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
                if (baseline is null)
                {
                    // A file created and renamed before its first baseline
                    // must not be downloaded again under its previous name.
                    // Accept only a unique same-parent candidate whose bytes
                    // match the active remote file exactly.
                    var sameParent = candidates.Where(x =>
                        PathsEqual(Path.GetDirectoryName(x)!,
                            Path.GetDirectoryName(item.RutaLocal)!)).ToList();
                    var missingInParent = missing.Count(x =>
                        PathsEqual(Path.GetDirectoryName(x.RutaLocal)!,
                            Path.GetDirectoryName(item.RutaLocal)!));
                    var remoteFile = snapshot.Files.SingleOrDefault(x =>
                        x.Id == item.ArchivoRemotoId);
                    if (sameParent.Count == 1 && missingInParent == 1 &&
                        remoteFile is not null &&
                        !snapshot.Files.Any(x => x.Id != remoteFile.Id &&
                            x.CarpetaId == remoteFile.CarpetaId &&
                            NamesEqual(x.Nombre, Path.GetFileName(sameParent[0]))))
                    {
                        var candidate = sameParent[0];
                        ValidatePathWithoutLinks(candidate);
                        if (HashesEqual(await HashLocalAsync(candidate, cancellationToken),
                                remoteFile.HashSha256))
                        {
                            var recoveredName = Path.GetFileName(candidate);
                            ValidateRemoteName(recoveredName);
                            await _api.RenameRemoteFileAsync(remoteFile.Id,
                                recoveredName, cancellationToken);
                            await _api.UpdateItemPathAsync(item.SyncItemId,
                                candidate, cancellationToken);
                            candidates.Remove(candidate);
                            Console.WriteLine($"[SyncRename] Renombrado sin baseline: {item.RutaLocal} => {candidate}");
                        }
                    }
                    continue;
                }
                if (!HashesEqual(baseline.LocalHash, baseline.RemoteHash) ||
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

                // A rename may be accompanied by an edit. If there is
                // exactly one vanished mapped file and one new file in the
                // same directory, treat it as a tentative rename only when
                // no other vanished mapping or new candidate competes.
                // This path is not content identity: keep remote data intact.
                if (matches.Count == 0)
                {
                    var sameParent = candidates.Where(x =>
                        PathsEqual(Path.GetDirectoryName(x)!, Path.GetDirectoryName(item.RutaLocal)!)).ToList();
                    var missingInParent = missing.Count(x =>
                        PathsEqual(Path.GetDirectoryName(x.RutaLocal)!, Path.GetDirectoryName(item.RutaLocal)!));
                    if (sameParent.Count == 1 && missingInParent == 1 &&
                        !snapshot.Files.Any(x => x.CarpetaId ==
                            items.FirstOrDefault(y => y.Tipo == SyncItemType.Folder &&
                                PathsEqual(y.RutaLocal, Path.GetDirectoryName(item.RutaLocal)!))?.CarpetaRemotaId &&
                            NamesEqual(x.Nombre, Path.GetFileName(sameParent[0]))))
                    {
                        matches.Add(sameParent[0]);
                        Console.WriteLine($"[SyncRename] Cambio único de nombre y contenido: " +
                            $"{item.RutaLocal} => {sameParent[0]}");
                    }
                }
                if (matches.Count == 0)
                {
                    var localDirectory = Path.GetDirectoryName(item.RutaLocal);
                    var localSiblings = localDirectory is not null && Directory.Exists(localDirectory)
                        ? string.Join(", ", Directory.EnumerateFiles(localDirectory)
                            .Select(Path.GetFileName).Take(20))
                        : "(carpeta local ausente)";
                    Console.WriteLine($"[SyncGuard] Sin coincidencia para {item.RutaLocal}; " +
                        $"archivos en carpeta: {localSiblings}; hashBase={baseline.LocalHash}; " +
                        $"idRemoto={item.ArchivoRemotoId}");
                    // A missing local file with no matching move candidate
                    // must not stall synchronization of unrelated files.
                    // Remote deletion is never authorized by this branch.
                    Console.WriteLine($"[SyncGuard] Se conserva el archivo remoto " +
                        $"{item.ArchivoRemotoId}; continúa la sincronización.");
                    continue;
                }
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

                var newName = Path.GetFileName(destination);
                ValidateRemoteName(newName);
                var folderId = folderMappings[0].CarpetaRemotaId!.Value;
                var remote = snapshot.Files.SingleOrDefault(x =>
                    x.Id == item.ArchivoRemotoId);
                if (remote is null ||
                    !HashesEqual(remote.HashSha256, baseline.RemoteHash) ||
                    (remote.Version != baseline.RemoteVersion &&
                     !(remote.CarpetaId == folderId &&
                       NamesEqual(remote.Nombre, newName))))
                    throw new InvalidOperationException(
                        $"El archivo remoto de '{item.RutaLocal}' cambió. " +
                        "Se bloqueó el movimiento para proteger el historial.");

                if (snapshot.Files.Any(x => x.Id != remote.Id &&
                    x.CarpetaId == folderId && NamesEqual(x.Nombre, newName)) ||
                    snapshot.Folders.Any(x => x.CarpetaPadreId == folderId &&
                        NamesEqual(x.Nombre, newName)))
                    throw new InvalidOperationException(
                        $"El destino remoto '{destination}' ya está ocupado.");

                plans.Add((item, destination, folderId, newName, baseline,
                    !HashesEqual(await HashLocalAsync(destination, cancellationToken), baseline.LocalHash)));
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
                !HashesEqual(remote.HashSha256, plan.Baseline.RemoteHash) ||
                (remote.Version != plan.Baseline.RemoteVersion &&
                 !(remote.CarpetaId == plan.FolderId &&
                   NamesEqual(remote.Nombre, plan.NewName))))
                throw new InvalidOperationException(
                    $"El archivo remoto cambió antes de mover '{plan.Destination}'.");

            // El contenido local no puede haber cambiado durante el análisis.
            if (!File.Exists(plan.Destination))
                throw new InvalidOperationException(
                    $"El archivo destino desapareció: '{plan.Destination}'.");
            ValidatePathWithoutLinks(plan.Destination);
            if (!plan.ContentChanged && !HashesEqual(await HashLocalAsync(plan.Destination,
                    cancellationToken), plan.Baseline.LocalHash))
                throw new InvalidOperationException(
                    $"El archivo destino cambió: '{plan.Destination}'.");

            // Cada paso es reintentable: si el servidor aceptó el cambio
            // antes de una interrupción, no se vuelve a ejecutar.
            if (remote.CarpetaId != plan.FolderId)
                await _api.MoveRemoteFileAsync(remote.Id, plan.FolderId,
                    cancellationToken);

            // Refrescar después del movimiento: el servidor puede modificar
            // versión y metadatos, y la operación pudo quedar a medio hacer.
            remote = (await _api.GetSnapshotAsync(cancellationToken))
                .Files.SingleOrDefault(x => x.Id == plan.Item.ArchivoRemotoId);
            if (remote is null || remote.CarpetaId != plan.FolderId ||
                !HashesEqual(remote.HashSha256, plan.Baseline.RemoteHash))
                throw new InvalidOperationException(
                    $"No se verificó el destino remoto de '{plan.Destination}'.");

            if (!NamesEqual(remote.Nombre, plan.NewName))
                await _api.RenameRemoteFileAsync(remote.Id, plan.NewName,
                    cancellationToken);

            remote = (await _api.GetSnapshotAsync(cancellationToken))
                .Files.SingleOrDefault(x => x.Id == plan.Item.ArchivoRemotoId);
            if (remote is null || remote.CarpetaId != plan.FolderId ||
                !NamesEqual(remote.Nombre, plan.NewName) ||
                !HashesEqual(remote.HashSha256, plan.Baseline.RemoteHash))
                throw new InvalidOperationException(
                    $"No se verificó el renombrado remoto de '{plan.Destination}'.");

            await _api.UpdateItemPathAsync(plan.Item.SyncItemId,
                plan.Destination, cancellationToken);

            var updated = (await _baselines.LoadAsync(cancellationToken))
                .ToList();
            var existing = updated.SingleOrDefault(x =>
                x.SyncItemId == plan.Item.SyncItemId);
            if (existing is not null)
            {
                // Renaming remotely can increment the version without changing
                // file bytes. Advance only the remote side of the baseline so
                // an edited local file is uploaded, not falsely conflicted.
                existing.LocalPath = plan.Destination;
                existing.RemoteVersion = remote.Version;
                existing.RemoteHash = remote.HashSha256;
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

        // A web trash action must propagate to Windows before the normal
        // local-to-remote scan can interpret the surviving folder as new.
        // Never infer deletion merely because a remote folder is absent
        // from the active snapshot: it may have been moved or renamed.
        foreach (var mapped in items.Where(x =>
            x.Tipo == SyncItemType.Folder &&
            x.CarpetaRemotaId.HasValue &&
            PathsEqual(Path.GetDirectoryName(x.RutaLocal)!, localFolderPath) &&
            !remoteFolders.Any(remote => remote.Id == x.CarpetaRemotaId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await _api.IsItemTrashedAsync(mapped.SyncItemId, cancellationToken))
                continue;

            if (Directory.Exists(mapped.RutaLocal))
            {
                ValidatePathWithoutLinks(mapped.RutaLocal);
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                    mapped.RutaLocal,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }

            await _api.DetachTrashedItemAsync(mapped.SyncItemId, cancellationToken);
            Console.WriteLine($"[SyncFolder] Carpeta enviada a papelera local: {mapped.RutaLocal}");
        }

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
            {
                if (!Directory.Exists(mappedElsewhere.RutaLocal))
                {
                    // An empty folder that disappeared must not be recreated
                    // automatically: it might have been renamed locally.
                    Console.WriteLine($"[SyncFolder] Carpeta remota conservada sin recrear: " +
                        $"{mappedElsewhere.RutaLocal}");
                    continue;
                }
                // The same remote folder was renamed or moved on the web.
                // Preserve its mapping and all descendant mappings; never
                // create a second folder or overwrite an existing directory.
                if (Directory.Exists(localChild) || File.Exists(localChild))
                    throw new InvalidOperationException(
                        $"Destino local ocupado para mover carpeta: '{localChild}'.");

                ValidatePathWithoutLinks(mappedElsewhere.RutaLocal);
                ValidatePathWithoutLinks(localFolderPath);
                var previousPath = mappedElsewhere.RutaLocal;
                var descendantsToMove = items.Where(x =>
                    PathsEqual(x.RutaLocal, previousPath) ||
                    IsDescendantPath(x.RutaLocal, previousPath))
                    .OrderBy(x => x.RutaLocal.Length).ToList();

                try
                {
                    Directory.Move(previousPath, localChild);
                }
                catch (IOException ex) when (
                    (ex.HResult & 0xFFFF) is 5 or 32 or 33)
                {
                    // Windows Explorer, antivirus or an open file can hold a
                    // temporary handle. Retry on the next sync cycle without
                    // changing any mapping or reporting a fatal sync failure.
                    Console.WriteLine(
                        $"[SyncFolder] Movimiento local pendiente (carpeta en uso o acceso denegado): {previousPath} => {localChild}. {ex.Message}");
                    continue;
                }

                foreach (var descendant in descendantsToMove)
                {
                    var newPath = PathsEqual(descendant.RutaLocal, previousPath)
                        ? localChild
                        : Path.Combine(localChild,
                            Path.GetRelativePath(previousPath, descendant.RutaLocal));
                    await _api.UpdateItemPathAsync(
                        descendant.SyncItemId, newPath, cancellationToken);
                }

                var updatedBaselines = (await _baselines.LoadAsync(cancellationToken)).ToList();
                foreach (var baseline in updatedBaselines.Where(x =>
                    PathsEqual(x.LocalPath, previousPath) ||
                    IsDescendantPath(x.LocalPath, previousPath)))
                    baseline.LocalPath = Path.Combine(localChild,
                        Path.GetRelativePath(previousPath, baseline.LocalPath));
                await _baselines.SaveAsync(updatedBaselines, cancellationToken);

                var identities = await LoadRootIdentitiesAsync(cancellationToken);
                foreach (var identity in identities.Where(x =>
                    PathsEqual(x.Key, previousPath) ||
                    IsDescendantPath(x.Key, previousPath)).ToList())
                {
                    identities.Remove(identity.Key);
                    var identityPath = PathsEqual(identity.Key, previousPath)
                        ? localChild
                        : Path.Combine(localChild,
                            Path.GetRelativePath(previousPath, identity.Key));
                    identities[identityPath] = identity.Value;
                }
                await SaveRootIdentitiesAsync(identities, cancellationToken);

                items = await _api.GetItemsAsync(cancellationToken);
                Console.WriteLine($"[SyncFolder] Movimiento remoto aplicado: {previousPath} => {localChild}");
            }

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

        // A remote rename/move changes the physical folder list. Do not
        // process its previous name as a newly created local folder.
        localFolders = Directory.EnumerateDirectories(localFolderPath)
            .ToDictionary(p => Path.GetFileName(p)!, p => p,
                NormalizedNameComparer.Instance);

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
                if (existingMapping.Tipo != SyncItemType.Folder ||
                    !existingMapping.CarpetaRemotaId.HasValue)
                    throw new InvalidOperationException(
                        $"La asociación de '{localFolder.Value}' tiene un tipo incompatible.");

                // El servidor solo desactiva la asociación si comprueba que
                // la carpeta anterior está realmente en la papelera.
                // No eliminar asociaciones de carpetas activas o movidas.
                await _api.DetachTrashedItemAsync(
                    existingMapping.SyncItemId, cancellationToken);
            }

            // A directory may disappear after the initial enumeration.
            // Never create a remote folder from a stale local snapshot.
            if (!Directory.Exists(localFolder.Value))
            {
                Console.WriteLine($"[SyncFolder] Se omitió carpeta local desaparecida: {localFolder.Value}");
                continue;
            }

            if (IsReparsePoint(localFolder.Value))
                throw new InvalidOperationException(
                    $"No se sincronizará un enlace o punto de montaje: '{localFolder.Value}'.");

            await EnsureStillSelectedAsync(localFolder.Value, cancellationToken);
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

            // A concurrent rename/delete after remote creation must not
            // abort the entire sync cycle. Reconciliation handles the mapping
            // on the next pass without treating absence as remote deletion.
            if (Directory.Exists(localFolder.Value))
                await SyncFolderAsync(
                    created.RemoteFolderId,
                    localFolder.Value,
                    cancellationToken);
            else
                Console.WriteLine($"[SyncFolder] Carpeta desapareció durante la creación: {localFolder.Value}");
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
            {
                // A restored remote file can retain its old SyncItem while
                // its local copy was moved to the Windows Recycle Bin.
                // Do not confuse this with a local deletion: a missing local
                // file may only be restored automatically when there is no
                // trusted baseline proving that the local file was removed
                // from an otherwise continuously active remote file.
                var baseline = (await _baselines.LoadAsync(cancellationToken))
                    .FirstOrDefault(x => x.SyncItemId == fileItem.SyncItemId &&
                        x.RemoteFileId == remoteFile.Id);
                if (baseline is not null)
                    continue;

                // An unmatched local sibling may be a newly renamed file.
                // Never resurrect the old name while its identity is unresolved.
                if (localFiles.Values.Any(path =>
                    !remoteFiles.Any(remote => NamesEqual(remote.Nombre,
                        Path.GetFileName(path))) &&
                    !items.Any(mapped => PathsEqual(mapped.RutaLocal, path))))
                {
                    Console.WriteLine($"[SyncFolder] Recuperación aplazada por posible renombrado local: {localPath}");
                    continue;
                }

                await _download.DownloadAsync(remoteFile.Id, localPath, cancellationToken);
                fileItem = await FindItemByLocalPathAsync(localPath, cancellationToken);
                if (fileItem is null)
                    throw new InvalidOperationException(
                        $"No se recuperó la asociación de '{localPath}'.");
                Console.WriteLine($"[SyncFolder] Archivo remoto restaurado: {localPath}");
            }

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
                    // The server moved/renamed this same remote file. Preserve
                    // its SyncItem and version history rather than downloading
                    // it as a new file or failing on the old local mapping.
                    var baseline = (await _baselines.LoadAsync(cancellationToken))
                        .SingleOrDefault(x =>
                            x.SyncItemId == mappedElsewhere.SyncItemId &&
                            x.RemoteFileId == remoteFile.Id);
                    if (baseline is null ||
                        !HashesEqual(baseline.LocalHash, baseline.RemoteHash) ||
                        !HashesEqual(baseline.RemoteHash, remoteFile.HashSha256))
                        throw new InvalidOperationException(
                            $"Movimiento remoto sin baseline confiable: '{mappedElsewhere.RutaLocal}'.");

                    if (File.Exists(localPath) || Directory.Exists(localPath))
                        throw new InvalidOperationException(
                            $"Destino local ocupado: '{localPath}'.");

                    if (File.Exists(mappedElsewhere.RutaLocal))
                    {
                        ValidatePathWithoutLinks(mappedElsewhere.RutaLocal);
                        if (!HashesEqual(
                            await HashLocalAsync(mappedElsewhere.RutaLocal, cancellationToken),
                            baseline.LocalHash))
                            throw new InvalidOperationException(
                                $"El origen local cambió: '{mappedElsewhere.RutaLocal}'.");

                        File.Move(mappedElsewhere.RutaLocal, localPath);
                    }
                    else if (!File.Exists(localPath))
                    {
                        throw new InvalidOperationException(
                            $"No se encuentra el origen del movimiento: '{mappedElsewhere.RutaLocal}'.");
                    }

                    await _api.UpdateItemPathAsync(
                        mappedElsewhere.SyncItemId, localPath, cancellationToken);
                    baseline.LocalPath = localPath;
                    baseline.RemoteVersion = remoteFile.Version;
                    var updatedBaselines = (await _baselines.LoadAsync(cancellationToken)).ToList();
                    var existingBaseline = updatedBaselines.SingleOrDefault(x =>
                        x.SyncItemId == mappedElsewhere.SyncItemId &&
                        x.RemoteFileId == remoteFile.Id);
                    if (existingBaseline is not null)
                    {
                        existingBaseline.LocalPath = localPath;
                        existingBaseline.RemoteVersion = remoteFile.Version;
                        await _baselines.SaveAsync(updatedBaselines, cancellationToken);
                    }
                    fileItem = mappedElsewhere;
                }
                else
                {
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
                if (existingMapping.Tipo != SyncItemType.File ||
                    !existingMapping.ArchivoRemotoId.HasValue)
                    throw new InvalidOperationException(
                        $"La asociación de '{localFile.Value}' tiene un tipo incompatible.");

                // Nunca reemplazar archivos remotos activos o movidos.
                // El servidor solo desasocia si el archivo anterior está
                // confirmado en la papelera.
                await _api.DetachTrashedItemAsync(
                    existingMapping.SyncItemId, cancellationToken);
            }

            ValidatePathWithoutLinks(localFile.Value);
            if (IsReparsePoint(localFile.Value))
                throw new InvalidOperationException(
                    $"No se sincronizará un enlace: '{localFile.Value}'.");

            await using var stream = await _store.OpenReadAsync(
                localFile.Value,
                cancellationToken);

            await EnsureStillSelectedAsync(localFile.Value, cancellationToken);
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
                baseline.RemoteHash!,
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

        // Una selección retirada deja de autorizar acceso a su ruta local.
        // Conservar su historial sin leerla, pero nunca intentar refrescarla.
        // No conservar un baseline si la asociación remota cambió.
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

            // Las selecciones pueden cambiar mientras se ejecuta el ciclo.
            // Comprobar de nuevo la autorización inmediatamente antes de
            // acceder al archivo; una deselección no debe abortar el servicio.
            if (!IsSelectedItemPath(item.RutaLocal,
                    await LoadSelectionsAsync(cancellationToken)))
                continue;

            if (!File.Exists(item.RutaLocal))
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

        // Archivos de bloqueo de Microsoft Office: no son documentos de usuario.
        // Excel/Word pueden mantenerlos abiertos con acceso exclusivo.
        if (name.StartsWith("~$", StringComparison.OrdinalIgnoreCase))
            return true;

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

    private static bool IsInWindowsRecycleBin(string originalPath)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var fullPath = Path.GetFullPath(originalPath);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root)) return false;
            var recycle = Path.Combine(root, "$Recycle.Bin");
            if (!Directory.Exists(recycle)) return false;
            foreach (var userBin in Directory.EnumerateDirectories(recycle))
            {
                IEnumerable<string> entries;
                try { entries = Directory.EnumerateFiles(userBin, "$I*").ToArray(); }
                catch (UnauthorizedAccessException) { continue; }
                catch (IOException) { continue; }
                foreach (var entry in entries)
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(entry);
                        if (bytes.Length < 26) continue;
                        var version = BitConverter.ToInt64(bytes, 0);
                        int offset;
                        if (version == 2)
                        {
                            if (bytes.Length < 28) continue;
                            var characters = BitConverter.ToInt32(bytes, 24);
                            if (characters <= 0 || characters > 32767 ||
                                (long)characters * 2 > bytes.Length - 28) continue;
                            offset = 28;
                        }
                        else if (version == 1) offset = 24;
                        else continue;
                        var recycledPath = Encoding.Unicode.GetString(bytes, offset,
                            bytes.Length - offset).TrimEnd('\0');
                        if (PathsEqual(recycledPath, fullPath))
                        {
                            var payload = Path.Combine(userBin, "$R" + Path.GetFileName(entry)[2..]);
                            if (Directory.Exists(payload)) return true;
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (ArgumentException) { }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
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


    private async Task EnsureStillSelectedAsync(
        string path, CancellationToken cancellationToken)
    {
        var current = await LoadSelectionsAsync(cancellationToken);
        if (!IsSelectedItemPath(path, current))
            throw new OperationCanceledException(
                $"La selección fue retirada durante la sincronización: '{path}'.",
                cancellationToken);
    }

    private async Task ProcessRemovalRequestsAsync(
        IReadOnlyList<Selection> selections, CancellationToken cancellationToken)
    {
        if (!File.Exists(RemovalPath)) return;
        var requests = JsonSerializer.Deserialize<List<RemovalRequest>>(
            await File.ReadAllTextAsync(RemovalPath, cancellationToken)) ?? new();
        if (requests.Count == 0) { File.Delete(RemovalPath); return; }

        foreach (var request in requests.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(request.Path) ||
                !Path.IsPathFullyQualified(request.Path) ||
                (request.Kind != 0 && request.Kind != 1))
                throw new InvalidOperationException("Solicitud de retirada inválida.");

            var path = Path.GetFullPath(request.Path);
            // Si se volvió a seleccionar antes de ejecutar la retirada,
            // la nueva decisión cancela la solicitud antigua. No bloquear
            // la sincronización ni enviar contenido activo a la papelera.
            if (selections.Any(x => PathsEqual(x.Path, path) ||
                (request.Kind == 1 && Path.GetFullPath(x.Path).StartsWith(
                    Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))))
            {
                requests.Remove(request);
                await File.WriteAllTextAsync(RemovalPath,
                    JsonSerializer.Serialize(requests), cancellationToken);
                continue;
            }

            var items = await _api.GetItemsAsync(cancellationToken);
            var matches = items.Where(x => PathsEqual(x.RutaLocal, path) &&
                (request.Kind == 0 ? x.Tipo == SyncItemType.File : x.Tipo == SyncItemType.Folder)).ToList();
            if (matches.Count != 1)
                throw new InvalidOperationException(
                    $"No existe una asociación única para retirar: {path}");

            var item = matches[0];
            try
            {
                if (request.SendToTrash == false)
                    await _api.DeleteItemAsync(item.SyncItemId, cancellationToken);
                else if (request.Kind == 0 && item.ArchivoRemotoId.HasValue)
                    await _api.TrashSelectedFileAsync(item.ArchivoRemotoId.Value, cancellationToken);
                else if (request.Kind == 1 && item.CarpetaRemotaId.HasValue)
                    await _api.TrashSelectedFolderAsync(item.CarpetaRemotaId.Value, cancellationToken);
                else
                    throw new InvalidOperationException($"Asociación remota inválida: {path}");
            }
            catch (SyncApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // El DELETE no es idempotente en la API actual: devuelve 404 si
                // un intento anterior ya movió el objeto a la papelera.
                // No tratar cualquier 404 como éxito: el servidor verifica
                // explícitamente la identidad y el estado en papelera.
                await _api.DetachTrashedItemAsync(item.SyncItemId, cancellationToken);
            }

            // Si el traslado se confirmó ahora, desactivar también la
            // asociación antigua. La operación exige confirmación de papelera
            // en el servidor y evita conflictos al volver a seleccionar.
            var remaining = request.SendToTrash == false
                ? Array.Empty<SyncItem>()
                : (await _api.GetItemsAsync(cancellationToken)).ToArray();
            if (remaining.Any(x => x.SyncItemId == item.SyncItemId))
                await _api.DetachTrashedItemAsync(item.SyncItemId, cancellationToken);

            // Mantener la solicitud hasta que la API haya confirmado el traslado.
            requests.Remove(request);
            await File.WriteAllTextAsync(RemovalPath,
                JsonSerializer.Serialize(requests), cancellationToken);
        }
        if (requests.Count == 0) File.Delete(RemovalPath);
    }

    private sealed class RemovalRequest
    {
        public string Path { get; set; } = string.Empty;
        public int Kind { get; set; }
        public bool? SendToTrash { get; set; }
    }

    private sealed class Selection
    {
        public string Path { get; set; } = string.Empty;
        public int Kind { get; set; }
    }
}
