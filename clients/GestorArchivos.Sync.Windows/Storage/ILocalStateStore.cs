using GestorArchivos.Sync.Windows.State;

namespace GestorArchivos.Sync.Windows.Storage;

public interface ILocalStateStore
{
    Task<SyncRuntimeState> LoadAsync(
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        SyncRuntimeState state,
        CancellationToken cancellationToken = default);
}