using System.Text.Json;
using GestorArchivos.Sync.Windows.Constants;
using GestorArchivos.Sync.Windows.Models;

namespace GestorArchivos.Sync.Windows.Storage;

public sealed class SyncBaselineStore : ISyncBaselineStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SyncBaselineStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            SyncConstants.ApplicationName,
            SyncConstants.SyncDirectoryName);

        Directory.CreateDirectory(directory);

        _path = Path.Combine(
            directory,
            "baseline.json");
    }

    public async Task<IReadOnlyList<SyncBaselineItem>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (!File.Exists(_path))
                return [];

            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            return await JsonSerializer.DeserializeAsync<
                       List<SyncBaselineItem>>(
                       stream,
                       JsonOptions,
                       cancellationToken)
                   ?? [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        IReadOnlyCollection<SyncBaselineItem> items,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var temporaryPath = _path + ".tmp";

            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    items,
                    JsonOptions,
                    cancellationToken);

                await stream.FlushAsync(cancellationToken);
            }

            File.Move(
                temporaryPath,
                _path,
                true);
        }
        finally
        {
            _gate.Release();
        }
    }
}