using System.Collections.Concurrent;

namespace GestorArchivos.Infrastructure.Storage;

public sealed class UploadLockManager
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public SemaphoreSlim Get(Guid uploadId)
    {
        return _locks.GetOrAdd(
            uploadId,
            _ => new SemaphoreSlim(1, 1));
    }
}