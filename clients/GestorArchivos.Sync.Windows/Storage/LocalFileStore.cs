using System.Text.Json;
using GestorArchivos.Sync.Windows.Configuration;
using Microsoft.Extensions.Options;

namespace GestorArchivos.Sync.Windows.Storage;

public sealed class LocalFileStore : ILocalFileStore
{
    private readonly string _rootPath;
    private readonly string _rootPathWithSeparator;
    private readonly string _selectionsPath;

    public LocalFileStore(IOptions<SyncOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _rootPath = Path.GetFullPath(options.Value.LocalRootPath);
        _rootPathWithSeparator = AddSeparator(_rootPath);

        _selectionsPath = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            "GestorArchivos",
            "Sync",
            "selections.json");

        Directory.CreateDirectory(_rootPath);
    }

    public string RootPath => _rootPath;

    public string GetFullPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var fullPath = Path.GetFullPath(
            Path.IsPathRooted(relativePath)
                ? relativePath
                : Path.Combine(_rootPath, relativePath));

        if (IsInsideRoot(fullPath) || IsAuthorizedSelection(fullPath))
            return fullPath;

        throw new InvalidOperationException(
            $"Ruta de sincronización no autorizada: '{fullPath}'.");
    }

    public bool FileExists(string relativePath)
    {
        return File.Exists(GetFullPath(relativePath));
    }

    public bool DirectoryExists(string relativePath)
    {
        return Directory.Exists(GetFullPath(relativePath));
    }

    public Task<Stream> OpenReadAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = GetFullPath(relativePath);
        ValidateNoReparsePoints(path);

        Stream stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return Task.FromResult(stream);
    }

    public async Task WriteAsync(
        string relativePath,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();

        var destinationPath = GetFullPath(relativePath);
        ValidateNoReparsePoints(destinationPath);

        var directory = Path.GetDirectoryName(destinationPath);

        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException(
                $"Destino inválido: '{destinationPath}'.");

        Directory.CreateDirectory(directory);
        ValidateNoReparsePoints(destinationPath);

        var operationId = Guid.NewGuid().ToString("N");

        var temporaryPath = Path.Combine(
            directory,
            $".gestor-sync-{operationId}.tmp");

        var backupPath = Path.Combine(
            directory,
            $".gestor-sync-{operationId}.backup");

        try
        {
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await content.CopyToAsync(
                    destination,
                    1024 * 1024,
                    cancellationToken);

                await destination.FlushAsync(cancellationToken);
                destination.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ValidateNoReparsePoints(destinationPath);

            if (File.Exists(destinationPath))
            {
                File.Replace(
                    temporaryPath,
                    destinationPath,
                    backupPath,
                    ignoreMetadataErrors: false);
            }
            else
            {
                File.Move(
                    temporaryPath,
                    destinationPath,
                    overwrite: false);
            }
        }
        finally
        {
            TryDeleteTemporary(temporaryPath);
            TryDeleteTemporary(backupPath);
        }
    }

    public async Task<bool> WriteIfUnchangedAsync(
        string relativePath,
        Stream content,
        string? expectedLocalHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();

        var destinationPath = GetFullPath(relativePath);

        ValidateNoReparsePoints(destinationPath);

        var directory = Path.GetDirectoryName(destinationPath);

        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException(
                $"Destino inválido: '{destinationPath}'.");

        Directory.CreateDirectory(directory);
        ValidateNoReparsePoints(destinationPath);

        return await ConditionalLocalFileWriter.WriteAsync(
            destinationPath,
            content,
            expectedLocalHash,
            cancellationToken);
    }

    public void CreateDirectory(string relativePath)
    {
        var path = GetFullPath(relativePath);
        ValidateNoReparsePoints(path);
        Directory.CreateDirectory(path);
    }

    public void DeleteFile(string relativePath)
    {
        var path = GetFullPath(relativePath);
        ValidateNoReparsePoints(path);

        if (File.Exists(path))
            File.Delete(path);
    }

    public void DeleteDirectory(
        string relativePath,
        bool recursive = true)
    {
        var path = GetFullPath(relativePath);
        ValidateNoReparsePoints(path);

        if (Directory.Exists(path))
            Directory.Delete(path, recursive);
    }

    public void MoveFile(
        string sourceRelativePath,
        string destinationRelativePath)
    {
        var source = GetFullPath(sourceRelativePath);
        var destination = GetFullPath(destinationRelativePath);

        ValidateNoReparsePoints(source);
        ValidateNoReparsePoints(destination);

        var directory = Path.GetDirectoryName(destination);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            ValidateNoReparsePoints(destination);
        }

        File.Move(source, destination, overwrite: false);
    }

    public void MoveDirectory(
        string sourceRelativePath,
        string destinationRelativePath)
    {
        var source = GetFullPath(sourceRelativePath);
        var destination = GetFullPath(destinationRelativePath);

        ValidateNoReparsePoints(source);
        ValidateNoReparsePoints(destination);

        var parent = Path.GetDirectoryName(destination);

        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
            ValidateNoReparsePoints(destination);
        }

        Directory.Move(source, destination);
    }

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void ValidateNoReparsePoints(string fullPath)
    {
        var current = Path.GetFullPath(fullPath);

        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        $"No se permite operar sobre enlaces o puntos de montaje: '{current}'.");
                }
            }

            var parent = Path.GetDirectoryName(current);

            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(
                    parent,
                    current,
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }
    }

    private bool IsInsideRoot(string fullPath)
    {
        return string.Equals(
                   fullPath,
                   _rootPath,
                   StringComparison.OrdinalIgnoreCase)
               ||
               fullPath.StartsWith(
                   _rootPathWithSeparator,
                   StringComparison.OrdinalIgnoreCase);
    }

    private bool IsAuthorizedSelection(string fullPath)
    {
        foreach (var selection in LoadSelections())
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

                continue;
            }

            if (selection.Kind == 1)
            {
                if (string.Equals(
                    fullPath,
                    selectedPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (fullPath.StartsWith(
                    AddSeparator(selectedPath),
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private IEnumerable<LocalSelection> LoadSelections()
    {
        if (!File.Exists(_selectionsPath))
            return Array.Empty<LocalSelection>();

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
        catch (JsonException)
        {
            return Array.Empty<LocalSelection>();
        }
        catch (IOException)
        {
            return Array.Empty<LocalSelection>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<LocalSelection>();
        }
    }

    private static string AddSeparator(string path)
    {
        return Path.TrimEndingDirectorySeparator(path)
               + Path.DirectorySeparatorChar;
    }

    private sealed class LocalSelection
    {
        public string Path { get; set; } = string.Empty;
        public int Kind { get; set; }
    }
}