namespace GestorArchivos.Sync.Windows.Configuration;

public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    public string ApiBaseUrl { get; set; } = string.Empty;
    public string LocalRootPath { get; set; } = string.Empty;
    public int PollIntervalSeconds { get; set; } = 5;
    public int ChangeBatchSize { get; set; } = 200;
}