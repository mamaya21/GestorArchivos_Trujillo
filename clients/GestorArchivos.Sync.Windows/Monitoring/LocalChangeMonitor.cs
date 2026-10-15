
using System.Text.Json;
using GestorArchivos.Sync.Windows.Api;
using GestorArchivos.Sync.Windows.Models;
using GestorArchivos.Sync.Windows.Storage;

namespace GestorArchivos.Sync.Windows.Monitoring;

public sealed class LocalChangeMonitor : ILocalChangeMonitor
{
    private readonly ILocalFileStore _fileStore;
    private readonly FileSystemEventBuffer _buffer;
    private readonly object _syncLock = new();
    private readonly object _suppressionLock = new();

    private readonly Dictionary<string, FileSystemWatcher> _watchers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _selectionsPath;

    private int _suppressionCount;
    private bool _started;
    private bool _disposed;

    public event EventHandler<LocalFileSystemChange>? Changed;

    public LocalChangeMonitor(
        ILocalFileStore fileStore,
        FileSystemEventBuffer buffer,
        ISyncApiClient syncApiClient)
    {
        _fileStore = fileStore;
        _buffer = buffer;

        // Se conserva la firma del constructor
        // para mantener la compatibilidad con DI.
        _ = syncApiClient;

        _selectionsPath = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            "GestorArchivos",
            "Sync",
            "selections.json");

        _buffer.ChangeReady += OnChangeReady;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_syncLock)
        {
            _started = true;
            RefreshWatchers();
        }
    }

    public void Stop()
    {
        if (_disposed)
            return;

        lock (_syncLock)
        {
            _started = false;

            foreach (var watcher in _watchers.Values)
                watcher.EnableRaisingEvents = false;
        }
    }

    public IDisposable SuppressChanges()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_suppressionLock)
        {
            _suppressionCount++;
        }

        return new SuppressionScope(this);
    }

    private void RefreshWatchers()
    {
        var roots = GetWatchRoots();

        foreach (var existingPath in _watchers.Keys.ToList())
        {
            if (roots.Contains(existingPath))
                continue;

            var watcher = _watchers[existingPath];

            watcher.EnableRaisingEvents = false;
            watcher.Dispose();

            _watchers.Remove(existingPath);
        }

        foreach (var root in roots)
        {
            if (_watchers.TryGetValue(root, out var existing))
            {
                existing.EnableRaisingEvents = _started;
                continue;
            }

            if (!Directory.Exists(root))
                continue;

            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter =
                    NotifyFilters.FileName |
                    NotifyFilters.DirectoryName |
                    NotifyFilters.LastWrite |
                    NotifyFilters.Size |
                    NotifyFilters.CreationTime,
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = false
            };

            watcher.Created += OnCreated;
            watcher.Changed += OnChanged;
            watcher.Deleted += OnDeleted;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnWatcherError;

            _watchers.Add(root, watcher);

            watcher.EnableRaisingEvents = _started;
        }
    }

    private HashSet<string> GetWatchRoots()
    {
        var roots = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        var selections = LoadSelections();

        // Comportamiento heredado cuando todavía
        // no existe una configuración de selección.
        if (selections.Count == 0)
        {
            roots.Add(_fileStore.RootPath);
            return roots;
        }

        foreach (var selection in selections)
        {
            if (string.IsNullOrWhiteSpace(selection.Path))
                continue;

            string fullPath;

            try
            {
                fullPath = Path.GetFullPath(selection.Path);
            }
            catch (Exception ex) when (
                ex is ArgumentException ||
                ex is NotSupportedException ||
                ex is PathTooLongException)
            {
                continue;
            }

            if (selection.Kind == 1)
            {
                if (Directory.Exists(fullPath))
                    roots.Add(fullPath);
            }
            else if (selection.Kind == 0)
            {
                var parent = Path.GetDirectoryName(fullPath);

                if (!string.IsNullOrWhiteSpace(parent) &&
                    Directory.Exists(parent))
                {
                    roots.Add(parent);
                }
            }
        }

        // Evitar watchers superpuestos.
        var ordered = roots
            .OrderBy(path => path.Length)
            .ToList();

        var result = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var root in ordered)
        {
            if (result.Any(parent =>
                    IsSameOrDescendant(root, parent)))
            {
                continue;
            }

            result.Add(root);
        }

        return result;
    }

    private List<LocalSelection> LoadSelections()
    {
        if (!File.Exists(_selectionsPath))
            return new List<LocalSelection>();

        try
        {
            var json = File.ReadAllText(_selectionsPath);

            return JsonSerializer.Deserialize<List<LocalSelection>>(
                       json,
                       new JsonSerializerOptions
                       {
                           PropertyNameCaseInsensitive = true
                       })
                   ?? new List<LocalSelection>();
        }
        catch (IOException)
        {
            return new List<LocalSelection>();
        }
        catch (UnauthorizedAccessException)
        {
            return new List<LocalSelection>();
        }
        catch (JsonException)
        {
            return new List<LocalSelection>();
        }
    }

    private bool IsSelected(string fullPath)
    {
        var selections = LoadSelections();

        if (selections.Count == 0)
        {
            return IsSameOrDescendant(
                fullPath,
                _fileStore.RootPath);
        }

        foreach (var selection in selections)
        {
            if (string.IsNullOrWhiteSpace(selection.Path))
                continue;

            string selectedPath;

            try
            {
                selectedPath = Path.GetFullPath(selection.Path);
            }
            catch (Exception ex) when (
                ex is ArgumentException ||
                ex is NotSupportedException ||
                ex is PathTooLongException)
            {
                continue;
            }

            if (selection.Kind == 0)
            {
                if (string.Equals(
                        fullPath,
                        selectedPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (selection.Kind == 1)
            {
                if (IsSameOrDescendant(
                        fullPath,
                        selectedPath))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsSameOrDescendant(
        string path,
        string root)
    {
        var normalizedPath = Path.GetFullPath(path);
        var normalizedRoot = Path.GetFullPath(root);

        if (string.Equals(
                normalizedPath,
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = Path.TrimEndingDirectorySeparator(
                         normalizedRoot)
                     + Path.DirectorySeparatorChar;

        return normalizedPath.StartsWith(
            prefix,
            StringComparison.OrdinalIgnoreCase);
    }

    private void OnCreated(
        object sender,
        FileSystemEventArgs e)
    {
        Add(
            LocalFileSystemChangeType.Created,
            e.FullPath,
            null,
            Directory.Exists(e.FullPath));
    }

    private void OnChanged(
        object sender,
        FileSystemEventArgs e)
    {
        Add(
            LocalFileSystemChangeType.Changed,
            e.FullPath,
            null,
            Directory.Exists(e.FullPath));
    }

    private void OnDeleted(
        object sender,
        FileSystemEventArgs e)
    {
        // No propagar eliminaciones automáticamente.
        // Una carpeta o unidad desconectada no debe
        // provocar borrados en el servidor.
    }

    private void OnRenamed(
        object sender,
        RenamedEventArgs e)
    {
        if (IsSuppressed())
            return;

        if (!IsSelected(e.FullPath) ||
            !IsSelected(e.OldFullPath))
        {
            return;
        }

        if (ShouldIgnore(e.FullPath) ||
            ShouldIgnore(e.OldFullPath))
        {
            return;
        }

        _buffer.Add(
            new LocalFileSystemChange(
                LocalFileSystemChangeType.Renamed,
                ToSyncPath(e.FullPath),
                ToSyncPath(e.OldFullPath),
                Directory.Exists(e.FullPath)));
    }

    private void Add(
        LocalFileSystemChangeType type,
        string fullPath,
        string? previousPath,
        bool isDirectory)
    {
        if (IsSuppressed())
            return;

        if (!IsSelected(fullPath))
            return;

        if (ShouldIgnore(fullPath))
            return;

        _buffer.Add(
            new LocalFileSystemChange(
                type,
                ToSyncPath(fullPath),
                previousPath,
                isDirectory));
    }

    private string ToSyncPath(string fullPath)
    {
        if (IsSameOrDescendant(
                fullPath,
                _fileStore.RootPath))
        {
            return Path.GetRelativePath(
                _fileStore.RootPath,
                fullPath);
        }

        // Ruta externa seleccionada.
        // El mapeo remoto se implementará
        // en el motor de sincronización.
        return Path.GetFullPath(fullPath);
    }

    private void OnWatcherError(
        object sender,
        ErrorEventArgs e)
    {
        // No emitir eliminaciones ante fallos
        // del watcher o pérdida de acceso.
    }

    private bool IsSuppressed()
    {
        lock (_suppressionLock)
        {
            return _suppressionCount > 0;
        }
    }

    private void ReleaseSuppression()
    {
        lock (_suppressionLock)
        {
            if (_suppressionCount > 0)
                _suppressionCount--;
        }
    }

    private static bool ShouldIgnore(string path)
    {
        return path.EndsWith(
                   ".sync.tmp",
                   StringComparison.OrdinalIgnoreCase)
               ||
               path.EndsWith(
                   ".tmp",
                   StringComparison.OrdinalIgnoreCase);
    }

    private void OnChangeReady(
        object? sender,
        LocalFileSystemChange change)
    {
        if (IsSuppressed())
            return;

        Changed?.Invoke(this, change);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        lock (_syncLock)
        {
            _started = false;

            foreach (var watcher in _watchers.Values)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }

            _watchers.Clear();
        }

        _buffer.ChangeReady -= OnChangeReady;
    }

    private sealed class LocalSelection
    {
        public string Path { get; set; } = string.Empty;
        public int Kind { get; set; }
    }

    private sealed class SuppressionScope : IDisposable
    {
        private LocalChangeMonitor? _owner;

        public SuppressionScope(LocalChangeMonitor owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(
                ref _owner,
                null);

            owner?.ReleaseSuppression();
        }
    }
}
