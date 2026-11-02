namespace GestorArchivos.Sync.Windows.Storage;

public sealed class LocalFileBusyException : IOException
{
    public LocalFileBusyException(string path, Exception innerException)
        : base($"Archivo ocupado temporalmente: '{path}'.", innerException)
    {
    }
}
