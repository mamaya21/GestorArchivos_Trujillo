using GestorArchivos.Sync.Windows.Activation;
using GestorArchivos.Sync.Windows.Logging;
using GestorArchivos.Sync.Windows.Services;

namespace GestorArchivos.Sync.Windows;

public sealed class Worker : BackgroundService
{
    private readonly ISyncEngine _syncEngine;
    private readonly IActivationService _activationService;
    private readonly SyncLog _log;
    private readonly SyncCancellationCoordinator _cancellation;
    private readonly ILogger<Worker> _logger;

    public Worker(
        ISyncEngine syncEngine,
        IActivationService activationService,
        SyncLog log,
        SyncCancellationCoordinator cancellation,
        ILogger<Worker> logger)
    {
        _syncEngine = syncEngine;
        _activationService = activationService;
        _log = log;
        _cancellation = cancellation;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        _log.Started();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!await _activationService.IsActivatedAsync(stoppingToken))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                _cancellation.Reset();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken, _cancellation.Token);
                var running = _syncEngine.RunAsync(linked.Token);
                _cancellation.Track(running);
                try
                {
                    await running;
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested || linked.IsCancellationRequested)
                {
                }

                if (!stoppingToken.IsCancellationRequested)
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.Error(ex, "ejecución del servicio");
            throw;
        }
        finally
        {
            _log.Stopped();
        }
    }
}