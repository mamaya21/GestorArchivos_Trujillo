using GestorArchivos.Sync.Windows.Models;

namespace GestorArchivos.Sync.Windows.Activation;

public interface IActivationService
{
    Task ActivateAsync(
        SyncDevice device,
        CancellationToken cancellationToken = default);

    Task<bool> IsActivatedAsync(
        CancellationToken cancellationToken = default);
}