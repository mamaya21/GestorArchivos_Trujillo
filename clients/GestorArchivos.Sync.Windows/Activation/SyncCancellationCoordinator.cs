namespace GestorArchivos.Sync.Windows.Activation;

public sealed class SyncCancellationCoordinator : IDisposable
{
    private CancellationTokenSource _source = new();
    private readonly object _gate = new();
    private Task? _engineTask;

    public CancellationToken Token
    {
        get { lock (_gate) return _source.Token; }
    }

    public void Track(Task engineTask)
    {
        lock (_gate) _engineTask = engineTask;
    }

    public async Task StopAndWaitAsync(CancellationToken cancellationToken)
    {
        Task? running;
        lock (_gate)
        {
            _source.Cancel();
            running = _engineTask;
        }

        if (running is not null)
        {
            try { await running.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            if (!_source.IsCancellationRequested) return;
            if (_engineTask is { IsCompleted: false })
                throw new InvalidOperationException("La sincronización anterior aún está terminando.");
            _source.Dispose();
            _source = new CancellationTokenSource();
            _engineTask = null;
        }
    }

    public void Dispose()
    {
        lock (_gate) _source.Dispose();
    }
}
