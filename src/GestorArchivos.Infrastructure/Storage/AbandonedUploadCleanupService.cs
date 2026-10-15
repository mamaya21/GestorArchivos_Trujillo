using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GestorArchivos.Infrastructure.Storage;

public sealed class AbandonedUploadCleanupService : BackgroundService
{
    private static readonly TimeSpan InitialDelay =
        TimeSpan.FromMinutes(1);

    private static readonly TimeSpan CleanupInterval =
        TimeSpan.FromHours(1);

    private readonly string _uploadsRoot;
    private readonly UploadLockManager _lockManager;
    private readonly ILogger<AbandonedUploadCleanupService> _logger;

    public AbandonedUploadCleanupService(
        IOptions<StorageOptions> storageOptions,
        UploadLockManager lockManager,
        ILogger<AbandonedUploadCleanupService> logger)
    {
        if (string.IsNullOrWhiteSpace(storageOptions.Value.RootPath))
        {
            throw new InvalidOperationException(
                "Storage:RootPath no está configurado.");
        }

        var rootPath =
            Path.GetFullPath(storageOptions.Value.RootPath);

        _uploadsRoot =
            Path.GetFullPath(
                Path.Combine(rootPath, ".uploads"));

        var rootWithSeparator =
            rootPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!_uploadsRoot.StartsWith(
                rootWithSeparator,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Ruta de uploads inválida.");
        }

        _lockManager = lockManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(
                InitialDelay,
                stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await CleanupAsync(stoppingToken);

                await Task.Delay(
                    CleanupInterval,
                    stoppingToken);
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task CleanupAsync(
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_uploadsRoot))
            return;

        string[] directories;

        try
        {
            directories =
                Directory.GetDirectories(_uploadsRoot);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(
                ex,
                "No se pudo enumerar el directorio de uploads.");

            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(
                ex,
                "No existe acceso al directorio de uploads.");

            return;
        }

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directoryName =
                Path.GetFileName(directory);

            if (!Guid.TryParseExact(
                    directoryName,
                    "N",
                    out var uploadId))
            {
                _logger.LogWarning(
                    "Directorio inválido encontrado en .uploads: {UploadDirectory}.",
                    directoryName);

                continue;
            }

            var semaphore =
                _lockManager.Get(uploadId);

            await semaphore.WaitAsync(
                cancellationToken);

            try
            {
                await ProcessDirectoryAsync(
                    directory,
                    cancellationToken);
            }
            catch (Exception ex)
                when (ex is IOException or
                      UnauthorizedAccessException or
                      JsonException)
            {
                _logger.LogWarning(
                    ex,
                    "No se pudo procesar la sesión de upload {UploadDirectory}.",
                    directoryName);
            }
            finally
            {
                semaphore.Release();
            }
        }
    }

    private async Task ProcessDirectoryAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
            return;

        var metadataPath =
            Path.Combine(
                directory,
                "metadata.json");

        if (!File.Exists(metadataPath))
        {
            DeleteOrphanDirectoryIfOld(directory);
            return;
        }

        var json =
            await File.ReadAllTextAsync(
                metadataPath,
                cancellationToken);

        var metadata =
            JsonSerializer.Deserialize<CleanupUploadMetadata>(
                json);

        if (metadata is null)
        {
            _logger.LogWarning(
                "Metadata inválida en upload {UploadDirectory}.",
                Path.GetFileName(directory));

            return;
        }

        if (metadata.ExpiresAtUtc >
            DateTime.UtcNow)
        {
            return;
        }

        Directory.Delete(
            directory,
            recursive: true);

        _logger.LogInformation(
            "Sesión de upload expirada eliminada: {UploadDirectory}.",
            Path.GetFileName(directory));
    }

    private void DeleteOrphanDirectoryIfOld(
        string directory)
    {
        if (!Directory.Exists(directory))
            return;

        var info =
            new DirectoryInfo(directory);

        if (info.LastWriteTimeUtc >
            DateTime.UtcNow.AddHours(-24))
        {
            return;
        }

        Directory.Delete(
            directory,
            recursive: true);

        _logger.LogInformation(
            "Directorio de upload huérfano eliminado: {UploadDirectory}.",
            info.Name);
    }

    private sealed record CleanupUploadMetadata(
        DateTime ExpiresAtUtc);
}