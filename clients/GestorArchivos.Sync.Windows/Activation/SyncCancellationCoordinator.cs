namespace GestorArchivos.Sync.Windows.Activation;

public sealed class SyncCancellationCoordinator : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    private readonly object _gate = new();
    private Task? _engineTask;
    public CancellationToken Token => _source.Token;

    public void Track(Task engineTask)
    {
        lock (_gate) _engineTask = engineTask;
    }

    public async Task StopAndWaitAsync(CancellationToken cancellationToken)
    {
        _source.Cancel();
        Task? running;
        lock (_gate) running = _engineTask;
        if (running is not null)
            await running.WaitAsync(cancellationToken);
    }

    public void Dispose() => _source.Dispose();
}
