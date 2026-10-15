using GestorArchivos.Sync.Windows.Api;
using GestorArchivos.Sync.Windows.Infrastructure;
using GestorArchivos.Sync.Windows.State;

namespace GestorArchivos.Sync.Windows.Health;

public sealed class SyncHealthService
{
    private readonly ConnectivityService _connectivityService;
    private readonly ISyncApiClient _syncApiClient;
    private readonly SyncRuntimeState _runtimeState;

    public SyncHealthService(
        ConnectivityService connectivityService,
        ISyncApiClient syncApiClient,
        SyncRuntimeState runtimeState)
    {
        _connectivityService = connectivityService;
        _syncApiClient = syncApiClient;
        _runtimeState = runtimeState;
    }

    public async Task<bool> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await _connectivityService.IsAvailableAsync(cancellationToken))
        {
            _runtimeState.IsOnline = false;
            return false;
        }

        try
        {
            var authenticated =
                await _syncApiClient.ValidateTokenAsync(cancellationToken);

            _runtimeState.IsOnline = authenticated;

            return authenticated;
        }
        catch
        {
            _runtimeState.IsOnline = false;
            return false;
        }
    }
}