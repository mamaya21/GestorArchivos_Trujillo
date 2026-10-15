
using System.Text.Json;
using GestorArchivos.Sync.Windows.Api;
using GestorArchivos.Sync.Windows.Configuration;
using GestorArchivos.Sync.Windows.Health;
using GestorArchivos.Sync.Windows.Logging;
using GestorArchivos.Sync.Windows.Monitoring;
using GestorArchivos.Sync.Windows.State;
using GestorArchivos.Sync.Windows.Storage;
using GestorArchivos.Sync.Windows.Sync;
using Microsoft.Extensions.Options;

namespace GestorArchivos.Sync.Windows.Services;

public sealed class SyncEngine : ISyncEngine, IDisposable
{
    private readonly SyncHealthService _healthService;
    private readonly InitialSyncService _initialSyncService;
    private readonly ReconciliationService _reconciliationService;
    private readonly ChangePullService _changePullService;
    private readonly ChangePushService _changePushService;
    private readonly SyncBaselineService _baselineService;
    private readonly SelectiveSyncService _selectiveSyncService;
    private readonly ISyncApiClient _syncApiClient;
    private readonly ILocalChangeMonitor _localChangeMonitor;
    private readonly ILocalStateStore _stateStore;
    private readonly SyncRuntimeState _runtimeState;
    private readonly SyncLog _log;
    private readonly SyncOptions _options;

    private readonly SemaphoreSlim _syncGate = new(1, 1);

    private CancellationToken _stoppingToken;
    private bool _monitorStarted;
    private bool _disposed;

    private static readonly string SelectionsPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData),
        "GestorArchivos",
        "Sync",
        "selections.json");

    public SyncEngine(
        SyncHealthService healthService,
        InitialSyncService initialSyncService,
        ReconciliationService reconciliationService,
        ChangePullService changePullService,
        ChangePushService changePushService,
        SyncBaselineService baselineService,
        SelectiveSyncService selectiveSyncService,
        ISyncApiClient syncApiClient,
        ILocalChangeMonitor localChangeMonitor,
        ILocalStateStore stateStore,
        SyncRuntimeState runtimeState,
        SyncLog log,
        IOptions<SyncOptions> options)
    {
        _healthService = healthService;
        _initialSyncService = initialSyncService;
        _reconciliationService = reconciliationService;
        _changePullService = changePullService;
        _changePushService = changePushService;
        _baselineService = baselineService;
        _selectiveSyncService = selectiveSyncService;
        _syncApiClient = syncApiClient;
        _localChangeMonitor = localChangeMonitor;
        _stateStore = stateStore;
        _runtimeState = runtimeState;
        _log = log;
        _options = options.Value;
    }

    public async Task RunAsync(
        CancellationToken cancellationToken = default)
    {
        _stoppingToken = cancellationToken;

        var savedState = await _stateStore.LoadAsync(
            cancellationToken);

        _runtimeState.LastChangeId = savedState.LastChangeId;
        _runtimeState.LastSuccessfulSyncUtc = savedState.LastSuccessfulSyncUtc;
        _runtimeState.IsInitialSyncCompleted = savedState.IsInitialSyncCompleted;

        _localChangeMonitor.Changed += OnLocalChanged;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (HasConfiguredSelections() && _monitorStarted)
                    {
                        _localChangeMonitor.Stop();
                        _monitorStarted = false;
                    }

                    if (!await _healthService.CheckAsync(cancellationToken))
                    {
                        _runtimeState.IsOnline = false;
                        _log.Offline();

                        await DelayAsync(cancellationToken);
                        continue;
                    }

                    await _syncGate.WaitAsync(cancellationToken);

                    try
                    {
                        if (HasConfiguredSelections() && _monitorStarted)
                        {
                            _localChangeMonitor.Stop();
                            _monitorStarted = false;
                        }

                        _runtimeState.IsOnline = true;
                        _log.Online();

                        if (!_runtimeState.DeviceId.HasValue)
                        {
                            var device = await _syncApiClient
                                .GetCurrentDeviceAsync(cancellationToken);

                            if (device is null)
                            {
                                throw new InvalidOperationException(
                                    "No se pudo identificar el dispositivo Sync.");
                            }

                            _runtimeState.DeviceId = device.DeviceId;
                        }

                        if (HasConfiguredSelections())
                        {
                            await _selectiveSyncService.ExecuteAsync(
                                cancellationToken);
                        }
                        else
                        {
                            var initialSyncWasPending =
                                !_runtimeState.IsInitialSyncCompleted;

                            await _initialSyncService.ExecuteAsync(
                                cancellationToken);

                            if (initialSyncWasPending)
                            {
                                await _baselineService.RefreshAsync(
                                    cancellationToken);
                            }
                            else
                            {
                                await _reconciliationService.ReconcileAsync(
                                    cancellationToken);
                            }

                            await _changePullService.PullAsync(
                                cancellationToken);

                            await _baselineService.RefreshAsync(
                                cancellationToken);
                        }

                        _runtimeState.LastSuccessfulSyncUtc = DateTime.UtcNow;

                        await _stateStore.SaveAsync(
                            _runtimeState,
                            cancellationToken);

                        if (!_monitorStarted && !HasConfiguredSelections())
                        {
                            _localChangeMonitor.Start();
                            _monitorStarted = true;
                        }
                    }
                    finally
                    {
                        _syncGate.Release();
                    }
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _runtimeState.IsOnline = false;
                    _log.Error(ex, "sincronización");
                }

                await DelayAsync(cancellationToken);
            }
        }
        finally
        {
            _runtimeState.IsOnline = false;

            if (_monitorStarted)
            {
                _localChangeMonitor.Stop();
                _monitorStarted = false;
            }

            _localChangeMonitor.Changed -= OnLocalChanged;
        }
    }

    private static bool HasConfiguredSelections()
    {
        if (!File.Exists(SelectionsPath))
            return false;

        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(SelectionsPath));

            // La existencia del archivo mantiene activo el modo selectivo,
            // incluso con un arreglo vacío. Nunca se reactiva automáticamente
            // el modo tradicional.
            return true;
        }
        catch (JsonException)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private void OnLocalChanged(
        object? sender,
        LocalFileSystemChange change)
    {
        _ = ProcessLocalChangeAsync(change);
    }

    private async Task ProcessLocalChangeAsync(
        LocalFileSystemChange change)
    {
        try
        {
            _log.LocalChangeDetected(change.RelativePath);

            await _syncGate.WaitAsync(_stoppingToken);

            try
            {
                if (!_runtimeState.IsOnline || HasConfiguredSelections())
                    return;

                await _changePushService.PushAsync(
                    change,
                    _stoppingToken);

                await _baselineService.RefreshAsync(_stoppingToken);

                _runtimeState.LastSuccessfulSyncUtc = DateTime.UtcNow;

                await _stateStore.SaveAsync(
                    _runtimeState,
                    _stoppingToken);
            }
            finally
            {
                _syncGate.Release();
            }
        }
        catch (OperationCanceledException)
            when (_stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.Error(
                ex,
                $"procesamiento del cambio local {change.RelativePath}");
        }
    }

    private async Task DelayAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(
                TimeSpan.FromSeconds(_options.PollIntervalSeconds),
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _runtimeState.IsOnline = false;

        if (_monitorStarted)
        {
            _localChangeMonitor.Stop();
            _monitorStarted = false;
        }

        _localChangeMonitor.Changed -= OnLocalChanged;
        _syncGate.Dispose();
    }
}
