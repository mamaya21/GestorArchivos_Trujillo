namespace GestorArchivos.Sync.Windows.Monitoring;

public interface ILocalChangeMonitor : IDisposable
{
    event EventHandler<LocalFileSystemChange>? Changed;

    void Start();

    void Stop();

    IDisposable SuppressChanges();
}

public enum LocalFileSystemChangeType
{
    Created,
    Changed,
    Deleted,
    Renamed
}

public sealed record LocalFileSystemChange(
    LocalFileSystemChangeType Type,
    string RelativePath,
    string? PreviousRelativePath,
    bool IsDirectory);