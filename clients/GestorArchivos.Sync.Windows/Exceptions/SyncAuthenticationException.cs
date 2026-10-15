namespace GestorArchivos.Sync.Windows.Exceptions;

public sealed class SyncAuthenticationException : Exception
{
    public SyncAuthenticationException()
        : base("La autenticación del dispositivo de sincronización no es válida.")
    {
    }

    public SyncAuthenticationException(string message)
        : base(message)
    {
    }

    public SyncAuthenticationException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}