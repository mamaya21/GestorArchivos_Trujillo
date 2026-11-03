using GestorArchivos.Sync.Windows.Models;
using GestorArchivos.Sync.Windows.Services;

namespace GestorArchivos.Sync.Windows.Activation;

public sealed class ActivationService : IActivationService
{
    private readonly IDeviceRegistrationService _deviceRegistrationService;

    public ActivationService(
        IDeviceRegistrationService deviceRegistrationService)
    {
        _deviceRegistrationService = deviceRegistrationService;
    }

    public async Task ActivateAsync(
        SyncDevice device,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.DeviceId == Guid.Empty)
            throw new ArgumentException("DeviceId inválido.", nameof(device));

        if (device.RootFolderId == Guid.Empty)
            throw new ArgumentException("RootFolderId inválido.", nameof(device));

        if (string.IsNullOrWhiteSpace(device.Token))
            throw new ArgumentException("Token inválido.", nameof(device));

        var current = await _deviceRegistrationService.GetRegisteredDeviceAsync(cancellationToken);
        if (current is not null && current.DeviceId != Guid.Empty &&
            current.DeviceId != device.DeviceId && !string.IsNullOrWhiteSpace(current.Token))
            throw new InvalidOperationException("Este equipo ya está vinculado. Desvincúlelo antes de activar otra cuenta.");

        await _deviceRegistrationService.SaveRegistrationAsync(
            device,
            cancellationToken);
    }

    public async Task<bool> IsActivatedAsync(
        CancellationToken cancellationToken = default)
    {
        var device =
            await _deviceRegistrationService.GetRegisteredDeviceAsync(
                cancellationToken);

        return device is not null &&
               device.DeviceId != Guid.Empty &&
               device.RootFolderId != Guid.Empty &&
               !string.IsNullOrWhiteSpace(device.Token);
    }
}