using System.Text.Json;
using GestorArchivos.Sync.Windows.Constants;
using GestorArchivos.Sync.Windows.Models;
using GestorArchivos.Sync.Windows.Security;

namespace GestorArchivos.Sync.Windows.Services;

public sealed class DeviceRegistrationService : IDeviceRegistrationService
{
    private readonly ICredentialStore _credentialStore;
    private readonly string _devicePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DeviceRegistrationService(ICredentialStore credentialStore)
    {
        _credentialStore = credentialStore;

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            SyncConstants.ApplicationName,
            SyncConstants.SyncDirectoryName);

        Directory.CreateDirectory(directory);

        _devicePath = Path.Combine(directory, "device.json");
    }

    public async Task<SyncDevice?> GetRegisteredDeviceAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (!File.Exists(_devicePath))
                return null;

            await using var stream = new FileStream(
                _devicePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            var device = await JsonSerializer.DeserializeAsync<SyncDevice>(
                stream,
                cancellationToken: cancellationToken);

            if (device is null)
                return null;

            device.Token = await _credentialStore.GetTokenAsync(cancellationToken)
                           ?? string.Empty;

            return device;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveRegistrationAsync(
        SyncDevice device,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Token);

        await _gate.WaitAsync(cancellationToken);

        try
        {
            await _credentialStore.SaveTokenAsync(
                device.Token,
                cancellationToken);

            var storedDevice = new SyncDevice
            {
                DeviceId = device.DeviceId,
                DeviceName = device.DeviceName,
                RootFolderId = device.RootFolderId,
                RootFolderName = device.RootFolderName,
                Token = string.Empty
            };

            var temporaryPath = _devicePath + ".tmp";

            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    storedDevice,
                    cancellationToken: cancellationToken);

                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _devicePath, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearRegistrationAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            await _credentialStore.DeleteTokenAsync(cancellationToken);

            if (File.Exists(_devicePath))
                File.Delete(_devicePath);
        }
        finally
        {
            _gate.Release();
        }
    }
}