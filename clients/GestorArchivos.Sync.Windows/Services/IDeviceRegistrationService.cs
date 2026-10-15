using GestorArchivos.Sync.Windows.Models;

namespace GestorArchivos.Sync.Windows.Services;

public interface IDeviceRegistrationService
{
    Task<SyncDevice?> GetRegisteredDeviceAsync(
        CancellationToken cancellationToken = default);

    Task SaveRegistrationAsync(
        SyncDevice device,
        CancellationToken cancellationToken = default);

    Task ClearRegistrationAsync(
        CancellationToken cancellationToken = default);
}