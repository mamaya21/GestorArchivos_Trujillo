namespace GestorArchivos.Sync.Windows.Services;

public interface ISyncEngine
{
    Task RunAsync(CancellationToken cancellationToken = default);
}