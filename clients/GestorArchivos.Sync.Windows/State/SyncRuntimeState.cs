namespace GestorArchivos.Sync.Windows.State;

public sealed class SyncRuntimeState
{
    private readonly object _lock = new();

    private Guid? _deviceId;
    private long _lastChangeId;
    private DateTime? _lastSuccessfulSyncUtc;
    private bool _isInitialSyncCompleted;
    private bool _isOnline;

    public Guid? DeviceId
    {
        get
        {
            lock (_lock)
                return _deviceId;
        }
        set
        {
            lock (_lock)
                _deviceId = value;
        }
    }

    public long LastChangeId
    {
        get
        {
            lock (_lock)
                return _lastChangeId;
        }
        set
        {
            lock (_lock)
                _lastChangeId = value;
        }
    }

    public DateTime? LastSuccessfulSyncUtc
    {
        get
        {
            lock (_lock)
                return _lastSuccessfulSyncUtc;
        }
        set
        {
            lock (_lock)
                _lastSuccessfulSyncUtc = value;
        }
    }

    public bool IsInitialSyncCompleted
    {
        get
        {
            lock (_lock)
                return _isInitialSyncCompleted;
        }
        set
        {
            lock (_lock)
                _isInitialSyncCompleted = value;
        }
    }

    public bool IsOnline
    {
        get
        {
            lock (_lock)
                return _isOnline;
        }
        set
        {
            lock (_lock)
                _isOnline = value;
        }
    }
}