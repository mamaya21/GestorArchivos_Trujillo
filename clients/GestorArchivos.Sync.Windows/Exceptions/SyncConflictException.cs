namespace GestorArchivos.Sync.Windows.Exceptions;

public sealed class SyncConflictException : Exception
{
    public SyncConflictException(string message)
        : base(message)
    {
    }

    public SyncConflictException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}