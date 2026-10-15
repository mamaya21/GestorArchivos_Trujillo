namespace GestorArchivos.Sync.Windows.Constants;

public static class SyncConstants
{
    public const string ApplicationName = "GestorArchivos";
    public const string SyncDirectoryName = "Sync";

    public const string StateFileName = "sync-state.json";
    public const string CredentialFileName = "credentials.dat";

    public const int DefaultPollIntervalSeconds = 5;
    public const int DefaultChangeBatchSize = 200;
    public const int MaximumChangeBatchSize = 500;

    public const int FileSystemEventBufferMilliseconds = 750;
}