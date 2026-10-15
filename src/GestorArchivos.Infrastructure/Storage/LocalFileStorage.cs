
using GestorArchivos.Application.Common.Storage;
using Microsoft.Extensions.Options;

namespace GestorArchivos.Infrastructure.Storage;

public sealed class LocalFileStorage : IFileStorage
{
    private readonly string _rootPath;

    public LocalFileStorage(IOptions<StorageOptions> options)
    {
        if (string.IsNullOrWhiteSpace(options.Value.RootPath))
            throw new InvalidOperationException(
                "La ruta raíz de almacenamiento no está configurada.");

        _rootPath = Path.GetFullPath(options.Value.RootPath);
        Directory.CreateDirectory(_rootPath);
    }

    public async Task SaveAsync(
        string relativePath,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        var fullPath = GetSafeFullPath(relativePath);
        var directory = Path.GetDirectoryName(fullPath)!;

        Directory.CreateDirectory(directory);

        try
        {
            await using var destination = new FileStream(
                fullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan);

            await content.CopyToAsync(
                destination,
                1024 * 1024,
                cancellationToken);

            await destination.FlushAsync(cancellationToken);
        }
        catch
        {
            // La limpieza del archivo incompleto corresponde
            // al flujo que controla la operación de escritura.
            throw;
        }
    }

    public async Task ReplaceAsync(
        string relativePath,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        var fullPath = GetSafeFullPath(relativePath);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException(
                "El archivo físico no existe.",
                fullPath);

        var directory = Path.GetDirectoryName(fullPath)!;

        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var destination = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan))
            {
                await content.CopyToAsync(
                    destination,
                    1024 * 1024,
                    cancellationToken);

                await destination.FlushAsync(cancellationToken);
            }

            File.Move(
                tempPath,
                fullPath,
                overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    public Task<Stream> OpenReadAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = GetSafeFullPath(relativePath);

        Stream stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            1024 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);

        return Task.FromResult(stream);
    }

    public Task DeleteAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = GetSafeFullPath(relativePath);

        if (File.Exists(fullPath))
            File.Delete(fullPath);

        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = GetSafeFullPath(relativePath);

        return Task.FromResult(File.Exists(fullPath));
    }

    private string GetSafeFullPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException(
                "La ruta relativa es obligatoria.",
                nameof(relativePath));

        if (Path.IsPathRooted(relativePath))
            throw new InvalidOperationException(
                "No se permiten rutas absolutas.");

        var fullPath = Path.GetFullPath(
            Path.Combine(_rootPath, relativePath));

        var rootWithSeparator = _rootPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(
                rootWithSeparator,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "La ruta solicitada está fuera del almacenamiento permitido.");
        }

        return fullPath;
    }
}
