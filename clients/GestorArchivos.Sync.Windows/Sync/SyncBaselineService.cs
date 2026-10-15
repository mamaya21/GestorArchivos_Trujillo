
using System.Net;
using System.Security.Cryptography;
using GestorArchivos.Sync.Windows.Api;
using GestorArchivos.Sync.Windows.Models;
using GestorArchivos.Sync.Windows.Storage;

namespace GestorArchivos.Sync.Windows.Sync;

public sealed class SyncBaselineService
{
    private readonly ISyncApiClient _syncApiClient;
    private readonly ILocalFileStore _fileStore;
    private readonly ISyncBaselineStore _baselineStore;

    private static readonly string SelectionsPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData),
        "GestorArchivos",
        "Sync",
        "selections.json");

    public SyncBaselineService(
        ISyncApiClient syncApiClient,
        ILocalFileStore fileStore,
        ISyncBaselineStore baselineStore)
    {
        _syncApiClient = syncApiClient;
        _fileStore = fileStore;
        _baselineStore = baselineStore;
    }

    public async Task RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        // No modificar el baseline tradicional mientras
        // exista una configuración de sincronización selectiva.
        if (File.Exists(SelectionsPath))
        {
            throw new InvalidOperationException(
                "La actualización del baseline está suspendida " +
                "mientras exista una configuración de " +
                "sincronización selectiva.");
        }

        var items = await _syncApiClient.GetItemsAsync(
            cancellationToken);

        var previousItems = await _baselineStore.LoadAsync(
            cancellationToken);

        var previousById = previousItems.ToDictionary(
            x => x.SyncItemId);

        var baseline = new List<SyncBaselineItem>();

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(item.RutaLocal))
                continue;

            previousById.TryGetValue(
                item.SyncItemId,
                out var previous);

            if (item.Tipo == SyncItemType.Folder)
            {
                if (!_fileStore.DirectoryExists(item.RutaLocal))
                {
                    // No actualizar el baseline de una carpeta
                    // ausente o situada en una unidad desconectada.
                    if (previous is not null)
                        baseline.Add(previous);

                    continue;
                }

                baseline.Add(
                    new SyncBaselineItem
                    {
                        SyncItemId = item.SyncItemId,
                        RemoteFolderId = item.CarpetaRemotaId,
                        Type = SyncItemType.Folder,
                        LocalPath = item.RutaLocal,
                        LastSyncedUtc = DateTime.UtcNow
                    });

                continue;
            }

            if (item.Tipo != SyncItemType.File ||
                !item.ArchivoRemotoId.HasValue)
            {
                if (previous is not null)
                    baseline.Add(previous);

                continue;
            }

            // Preservar el estado anterior si el archivo
            // local no está disponible.
            if (!_fileStore.FileExists(item.RutaLocal))
            {
                if (previous is not null)
                    baseline.Add(previous);

                continue;
            }

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
                // No eliminar el baseline anterior cuando
                // desaparezca el archivo remoto.
                if (previous is not null)
                    baseline.Add(previous);

                continue;
            }

            var localHash = await CalculateHashAsync(
                item.RutaLocal,
                cancellationToken);

            // El baseline debe representar el último estado
            // efectivamente sincronizado, no simplemente
            // los dos estados observados en este momento.
            //
            // Si ambos hashes coinciden, es seguro registrar
            // la versión remota como sincronizada.
            if (HashesEqual(localHash, remote.HashSha256))
            {
                baseline.Add(
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
                    });

                continue;
            }

            // Los contenidos difieren: conservar el baseline
            // anterior para que la reconciliación pueda
            // detectar cambios y conflictos pendientes.
            if (previous is not null)
            {
                baseline.Add(previous);
            }

            // Si no hay baseline anterior, no inventar
            // un estado sincronizado.
        }

        // Conservar registros previos que ya no aparecen
        // en el listado actual, sin interpretarlos como
        // eliminaciones confirmadas.
        var includedIds = baseline
            .Select(x => x.SyncItemId)
            .ToHashSet();

        foreach (var previous in previousItems)
        {
            if (includedIds.Add(previous.SyncItemId))
                baseline.Add(previous);
        }

        await _baselineStore.SaveAsync(
            baseline,
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
        return !string.IsNullOrWhiteSpace(left) &&
               !string.IsNullOrWhiteSpace(right) &&
               string.Equals(
                   left,
                   right,
                   StringComparison.OrdinalIgnoreCase);
    }
}
