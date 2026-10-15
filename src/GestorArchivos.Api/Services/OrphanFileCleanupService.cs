
using System.Data;
using GestorArchivos.Domain.Entities;
using GestorArchivos.Infrastructure.Identity;
using GestorArchivos.Infrastructure.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GestorArchivos.Api.Services;

public sealed class OrphanFileCleanupService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);

    private const string LockName =
        "GestorArchivos:PhysicalFileCleanup";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OrphanFileCleanupService> _logger;
    private readonly string _rootPath;
    private readonly SemaphoreSlim _executionLock = new(1, 1);

    public OrphanFileCleanupService(
        IServiceScopeFactory scopeFactory,
        IOptions<StorageOptions> options,
        ILogger<OrphanFileCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(options.Value.RootPath))
        {
            throw new InvalidOperationException(
                "Storage:RootPath no está configurado.");
        }

        _rootPath = Path.GetFullPath(options.Value.RootPath);
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await CleanupAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Falló la limpieza automática de archivos.");
                }
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public async Task CleanupAsync(CancellationToken cancellationToken)
    {
        await _executionLock.WaitAsync(cancellationToken);

        try
        {
            await ProcessCleanupAsync(cancellationToken);
        }
        finally
        {
            _executionLock.Release();
        }
    }

    private async Task ProcessCleanupAsync(
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();

        var db = scope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>();

        var connectionString = db.Database.GetConnectionString();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "No se encontró la conexión SQL Server.");
        }

        // Conexión independiente para mantener el bloqueo distribuido
        // durante todo el procesamiento.
        await using var lockConnection =
            new SqlConnection(connectionString);

        await lockConnection.OpenAsync(cancellationToken);

        var lockAcquired = false;

        try
        {
            await using (var command = lockConnection.CreateCommand())
            {
                command.CommandText = """
                    DECLARE @result int;

                    EXEC @result = sys.sp_getapplock
                        @Resource = @resource,
                        @LockMode = 'Exclusive',
                        @LockOwner = 'Session',
                        @LockTimeout = 0;

                    SELECT @result;
                    """;

                command.Parameters.Add(
                    new SqlParameter(
                        "@resource",
                        SqlDbType.NVarChar,
                        255)
                    {
                        Value = LockName
                    });

                var result = Convert.ToInt32(
                    await command.ExecuteScalarAsync(cancellationToken));

                if (result < 0)
                {
                    _logger.LogInformation(
                        "Otra instancia está procesando la limpieza.");

                    return;
                }

                lockAcquired = true;
            }

            await ProcessPendingFilesAsync(db, cancellationToken);
        }
        finally
        {
            if (lockAcquired &&
                lockConnection.State == ConnectionState.Open)
            {
                try
                {
                    await using var command =
                        lockConnection.CreateCommand();

                    command.CommandText = """
                        EXEC sys.sp_releaseapplock
                            @Resource = @resource,
                            @LockOwner = 'Session';
                        """;

                    command.Parameters.Add(
                        new SqlParameter(
                            "@resource",
                            SqlDbType.NVarChar,
                            255)
                        {
                            Value = LockName
                        });

                    await command.ExecuteNonQueryAsync(
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "No se pudo liberar explícitamente el bloqueo SQL.");
                }
            }
        }
    }

    private async Task ProcessPendingFilesAsync(
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        var pending = await db
            .Set<ArchivoFisicoPendienteEliminacion>()
            .AsNoTracking()
            .Where(x => x.EliminarDespuesUtc <= now)
            .OrderBy(x => x.EliminarDespuesUtc)
            .Take(100)
            .ToListAsync(cancellationToken);

        foreach (var item in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var path = GetSafeFullPath(item.RutaFisica);

                var referenced = await db.Archivos
                    .AsNoTracking()
                    .AnyAsync(
                        x => x.NombreFisico == item.RutaFisica,
                        cancellationToken);

                if (referenced)
                {
                    await RecordErrorAsync(
                        db,
                        item.Id,
                        "El archivo todavía está referenciado.",
                        cancellationToken);

                    continue;
                }

                if (File.Exists(path))
                {
                    var attributes = File.GetAttributes(path);

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        await RecordErrorAsync(
                            db,
                            item.Id,
                            "La ruta es un enlace simbólico.",
                            cancellationToken);

                        continue;
                    }

                    // La ruta debe corresponder a una versión
                    // física inmutable que nunca se reutiliza.
                    await using (var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.None,
                        4096,
                        FileOptions.DeleteOnClose))
                    {
                    }
                }

                if (File.Exists(path))
                {
                    await RecordErrorAsync(
                        db,
                        item.Id,
                        "El archivo continúa existiendo.",
                        cancellationToken);

                    continue;
                }

                db.ChangeTracker.Clear();

                var queueItem = await db
                    .Set<ArchivoFisicoPendienteEliminacion>()
                    .FindAsync(
                        new object[] { item.Id },
                        cancellationToken);

                if (queueItem is not null)
                {
                    db.Remove(queueItem);

                    try
                    {
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        db.ChangeTracker.Clear();
                    }
                }

                _logger.LogInformation(
                    "Versión física obsoleta eliminada: {Path}",
                    item.RutaFisica);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Error procesando {Path}. Se reintentará.",
                    item.RutaFisica);

                try
                {
                    await RecordErrorAsync(
                        db,
                        item.Id,
                        ex.Message,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception retryException)
                {
                    _logger.LogError(
                        retryException,
                        "No se pudo registrar el reintento de {Path}.",
                        item.RutaFisica);

                    db.ChangeTracker.Clear();
                }
            }
        }
    }

    private static async Task RecordErrorAsync(
        ApplicationDbContext db,
        Guid id,
        string message,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();

        var item = await db
            .Set<ArchivoFisicoPendienteEliminacion>()
            .FindAsync(
                new object[] { id },
                cancellationToken);

        if (item is null)
            return;

        item.Intentos++;

        item.UltimoError = message.Length > 2000
            ? message[..2000]
            : message;

        item.EliminarDespuesUtc =
            DateTime.UtcNow.Add(RetryDelay);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
        }
    }

    private string GetSafeFullPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException(
                "Ruta física no válida.");
        }

        var fullPath = Path.GetFullPath(
            Path.Combine(_rootPath, relativePath));

        var rootWithSeparator =
            _rootPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(
            rootWithSeparator,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "La ruta está fuera del almacenamiento.");
        }

        return fullPath;
    }
}
