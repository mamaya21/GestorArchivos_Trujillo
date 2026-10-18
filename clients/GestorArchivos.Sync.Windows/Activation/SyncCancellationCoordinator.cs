namespace GestorArchivos.Sync.Windows.Activation;

// Una cancelación es definitiva para esta ejecución del servicio.
// Tras desvincular, el servicio Windows debe reiniciarse para una nueva activación.
public sealed class SyncCancellationCoordinator : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    public CancellationToken Token => _source.Token;
    public void Cancel() => _source.Cancel();
    public void Dispose() => _source.Dispose();
}
