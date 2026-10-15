using System.Security.Cryptography;

namespace GestorArchivos.Sync.Windows.Storage;

public static class ConditionalLocalFileWriter
{
    public static async Task<bool> WriteAsync(
        string destinationPath,
        Stream content,
        string? expectedLocalHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(content);

        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("Directorio inválido.");

        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".gestor-sync-{Guid.NewGuid():N}.tmp");

        var backupPath = Path.Combine(
            directory,
            $".gestor-sync-{Guid.NewGuid():N}.backup");

        try
        {
            await using (var temporary = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await content.CopyToAsync(
                    temporary,
                    cancellationToken);

                await temporary.FlushAsync(cancellationToken);
                temporary.Flush(true);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (expectedLocalHash is null)
            {
                if (File.Exists(destinationPath))
                    return false;

                try
                {
                    File.Move(
                        temporaryPath,
                        destinationPath,
                        overwrite: false);

                    return true;
                }
                catch (IOException) when (File.Exists(destinationPath))
                {
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(expectedLocalHash))
                return false;

            FileStream original;

            try
            {
                original = new FileStream(
                    destinationPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Delete,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }

            await using (original)
            {
                var actualHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(
                        original,
                        cancellationToken));

                if (!string.Equals(
                    actualHash,
                    expectedLocalHash,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();

                File.Replace(
                    temporaryPath,
                    destinationPath,
                    backupPath,
                    ignoreMetadataErrors: false);

                return true;
            }
        }
        finally
        {
            TryDeleteInternalFile(temporaryPath);
            TryDeleteInternalFile(backupPath);
        }
    }

    private static void TryDeleteInternalFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // La limpieza pendiente no debe ocultar
            // el resultado de la operación principal.
        }
        catch (UnauthorizedAccessException)
        {
            // No forzar la eliminación si Windows
            // mantiene bloqueado el archivo.
        }
    }
}