using System.Text.Json;

namespace GestorArchivos.Sync.Windows.Services;

internal sealed class SelectiveWakeSignal : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly List<FileSystemWatcher> _watchers = [];
    private string _configuration = "";

    public void Configure(string selectionsPath)
    {
        string json;
        try { json = File.ReadAllText(selectionsPath); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        if (json == _configuration) return;

        List<(string Path, int Kind)> selections = [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("path", out var path) &&
                    !entry.TryGetProperty("Path", out path)) continue;
                if (!entry.TryGetProperty("kind", out var kind) &&
                    !entry.TryGetProperty("Kind", out kind)) continue;
                var value = path.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    selections.Add((value, kind.GetInt32()));
            }
        }
        catch (JsonException) { return; }

        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        _configuration = json;

        foreach (var selection in selections)
        {
            try
            {
                var directory = selection.Kind == 1
                    ? selection.Path : Path.GetDirectoryName(selection.Path);
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                    continue;
                var watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = selection.Kind == 1,
                    Filter = selection.Kind == 1 ? "*" : Path.GetFileName(selection.Path),
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite |
                        NotifyFilters.Size | NotifyFilters.CreationTime
                };
                watcher.Changed += OnChange;
                watcher.Created += OnChange;
                watcher.Deleted += OnChange;
                watcher.Renamed += OnRename;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
        }
    }

    private void OnChange(object sender, FileSystemEventArgs args)
    {
        var name = Path.GetFileName(args.FullPath);
        if (name.StartsWith(".gestor-", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("~$", StringComparison.OrdinalIgnoreCase)) return;
        if (_signal.CurrentCount == 0)
        {
            try { _signal.Release(); }
            catch (SemaphoreFullException) { }
        }
    }

    private void OnRename(object sender, RenamedEventArgs args) => OnChange(sender, args);

    public async Task WaitAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        if (!await _signal.WaitAsync(interval, cancellationToken)) return;
        await Task.Delay(700, cancellationToken);
        while (_signal.Wait(0)) { }
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        _signal.Dispose();
    }
}
