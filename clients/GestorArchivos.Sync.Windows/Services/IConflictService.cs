using GestorArchivos.Sync.Windows.Models;

namespace GestorArchivos.Sync.Windows.Services;

public interface IConflictService
{
    string GetConflictPath(string relativePath);

    Task<string> CreateConflictCopyAsync(
        SyncConflict conflict,
        CancellationToken cancellationToken = default);
}