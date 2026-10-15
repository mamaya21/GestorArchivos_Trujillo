namespace GestorArchivos.Sync.Windows.Logging;

public sealed class SyncLog
{
    private readonly ILogger<SyncLog> _logger;

    public SyncLog(ILogger<SyncLog> logger)
    {
        _logger = logger;
    }

    public void Started()
    {
        _logger.LogInformation("Gestor Archivos Sync iniciado.");
    }

    public void Stopped()
    {
        _logger.LogInformation("Gestor Archivos Sync detenido.");
    }

    public void Online()
    {
        _logger.LogInformation("Conexión con el servidor disponible.");
    }

    public void Offline()
    {
        _logger.LogWarning("Servidor no disponible.");
    }

    public void InitialSyncStarted()
    {
        _logger.LogInformation("Sincronización inicial iniciada.");
    }

    public void InitialSyncCompleted()
    {
        _logger.LogInformation("Sincronización inicial completada.");
    }

    public void ChangeProcessed(long changeId)
    {
        _logger.LogDebug(
            "Cambio remoto {ChangeId} procesado.",
            changeId);
    }

    public void LocalChangeDetected(string path)
    {
        _logger.LogDebug(
            "Cambio local detectado: {Path}.",
            path);
    }

    public void Conflict(string path)
    {
        _logger.LogWarning(
            "Conflicto de sincronización: {Path}.",
            path);
    }

    public void Error(Exception exception, string operation)
    {
        _logger.LogError(
            exception,
            "Error durante {Operation}.",
            operation);
    }
}