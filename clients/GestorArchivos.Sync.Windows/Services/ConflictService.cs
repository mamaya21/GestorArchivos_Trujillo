
using GestorArchivos.Sync.Windows.Models;
using GestorArchivos.Sync.Windows.Storage;

namespace GestorArchivos.Sync.Windows.Services;

public sealed class ConflictService : IConflictService
{
    private readonly ILocalFileStore _fileStore;

    public ConflictService(ILocalFileStore fileStore)
    {
        _fileStore = fileStore;
    }

    public string GetConflictPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var directory = Path.GetDirectoryName(relativePath);
        var fileName = Path.GetFileNameWithoutExtension(relativePath);
        var extension = Path.GetExtension(relativePath);

        var conflictName =
            $"{fileName} (conflicto {Environment.MachineName} {DateTime.UtcNow:yyyyMMdd-HHmmss}){extension}";

        return string.IsNullOrWhiteSpace(directory)
            ? conflictName
            : Path.Combine(directory, conflictName);
    }

    public async Task<string> CreateConflictCopyAsync(
        SyncConflict conflict,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conflict);

        var conflictPath = GetConflictPath(conflict.LocalPath);

        // Evitar sobrescribir una copia de conflicto anterior.
        while (_fileStore.FileExists(conflictPath))
        {
            var directory = Path.GetDirectoryName(conflictPath);
            var fileName = Path.GetFileNameWithoutExtension(conflictPath);
            var extension = Path.GetExtension(conflictPath);

            var candidate =
                $"{fileName} {Guid.NewGuid():N}{extension}";

            conflictPath = string.IsNullOrEmpty(directory)
                ? candidate
                : Path.Combine(directory, candidate);
        }

        await using var source = await _fileStore.OpenReadAsync(
            conflict.LocalPath,
            cancellationToken);

        await _fileStore.WriteAsync(
            conflictPath,
            source,
            cancellationToken);

        return conflictPath;
    }
}
