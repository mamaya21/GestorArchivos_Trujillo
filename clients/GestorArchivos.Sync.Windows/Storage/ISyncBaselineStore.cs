using GestorArchivos.Sync.Windows.Models;

namespace GestorArchivos.Sync.Windows.Storage;

public interface ISyncBaselineStore
{
    Task<IReadOnlyList<SyncBaselineItem>> LoadAsync(
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        IReadOnlyCollection<SyncBaselineItem> items,
        CancellationToken cancellationToken = default);
}