using System.Collections.Concurrent;
using GestorArchivos.Sync.Windows.Constants;

namespace GestorArchivos.Sync.Windows.Monitoring;

public sealed class FileSystemEventBuffer : IDisposable
{
    private readonly ConcurrentDictionary<string, BufferedChange> _changes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Timer _timer;
    private readonly object _sync = new();

    private bool _disposed;
    private int _generation;

    public event EventHandler<LocalFileSystemChange>? ChangeReady;

    public FileSystemEventBuffer()
    {
        _timer = new Timer(
            Flush,
            null,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    public void Add(LocalFileSystemChange change)
    {
        int generation;

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            generation = _generation;
        }

        var key = NormalizeKey(change.RelativePath);

        _changes.AddOrUpdate(
            key,
            _ => new BufferedChange(
                change,
                generation),
            (_, existing) =>
                existing.Merge(
                    change,
                    generation));

        lock (_sync)
        {
            if (_disposed)
                return;

            _timer.Change(
                SyncConstants.FileSystemEventBufferMilliseconds,
                Timeout.Infinite);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _generation++;

            _timer.Change(
                Timeout.Infinite,
                Timeout.Infinite);

            _changes.Clear();
        }
    }

    private void Flush(object? state)
    {
        int generation;

        lock (_sync)
        {
            if (_disposed)
                return;

            generation = _generation;
        }

        foreach (var entry in _changes.ToArray())
        {
            if (!_changes.TryRemove(
                    entry.Key,
                    out var buffered))
            {
                continue;
            }

            bool shouldRaise;

            lock (_sync)
            {
                shouldRaise =
                    !_disposed &&
                    buffered.Generation == _generation &&
                    buffered.Generation == generation;
            }

            if (!shouldRaise)
                continue;

            ChangeReady?.Invoke(
                this,
                buffered.Change);
        }
    }

    private static string NormalizeKey(
        string relativePath)
    {
        return relativePath
            .Trim()
            .Replace(
                Path.AltDirectorySeparatorChar,
                Path.DirectorySeparatorChar)
            .TrimEnd(
                Path.DirectorySeparatorChar);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _generation++;

            _timer.Change(
                Timeout.Infinite,
                Timeout.Infinite);

            _changes.Clear();
        }

        _timer.Dispose();
    }

    private sealed class BufferedChange
    {
        public LocalFileSystemChange Change { get; private set; }
        public int Generation { get; private set; }

        public BufferedChange(
            LocalFileSystemChange change,
            int generation)
        {
            Change = change;
            Generation = generation;
        }

        public BufferedChange Merge(
            LocalFileSystemChange incoming,
            int generation)
        {
            if (Generation != generation)
            {
                Change = incoming;
                Generation = generation;
                return this;
            }

            if (Change.Type ==
                    LocalFileSystemChangeType.Created &&
                incoming.Type ==
                    LocalFileSystemChangeType.Changed)
            {
                return this;
            }

            Change = incoming;
            return this;
        }
    }
}