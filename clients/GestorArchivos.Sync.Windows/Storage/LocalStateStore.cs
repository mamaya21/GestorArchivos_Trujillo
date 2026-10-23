using System.Text.Json;
using GestorArchivos.Sync.Windows.Constants;
using GestorArchivos.Sync.Windows.State;

namespace GestorArchivos.Sync.Windows.Storage;

public sealed class LocalStateStore : ILocalStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _statePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LocalStateStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        SyncConstants.ApplicationName,
        SyncConstants.SyncDirectoryName,
        SyncConstants.StateFileName))
    {
    }

    // Permite pruebas aisladas sin acceder al estado real de ProgramData.
    public LocalStateStore(string statePath)
    {
        if (string.IsNullOrWhiteSpace(statePath))
            throw new ArgumentException("La ruta de estado es obligatoria.", nameof(statePath));

        _statePath = Path.GetFullPath(statePath);
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
    }

    public async Task<SyncRuntimeState> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (!File.Exists(_statePath))
                return new SyncRuntimeState();

            await using var stream = new FileStream(
                _statePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            return await JsonSerializer.DeserializeAsync<SyncRuntimeState>(
                       stream,
                       JsonOptions,
                       cancellationToken)
                   ?? new SyncRuntimeState();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        SyncRuntimeState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        await _gate.WaitAsync(cancellationToken);

        try
        {
            var temporaryPath = _statePath + ".tmp";

            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    state,
                    JsonOptions,
                    cancellationToken);

                await stream.FlushAsync(cancellationToken);
            }

            File.Move(
                temporaryPath,
                _statePath,
                true);
        }
        finally
        {
            _gate.Release();
        }
    }
}