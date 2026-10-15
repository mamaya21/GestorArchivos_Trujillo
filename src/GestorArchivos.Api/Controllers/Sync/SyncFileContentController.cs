

using System.Security.Claims;
using System.Security.Cryptography;
using GestorArchivos.Api.Security;
using GestorArchivos.Application.Common.Interfaces;
using GestorArchivos.Application.Common.Security;
using GestorArchivos.Domain.Security;
using GestorArchivos.Application.Common.Storage;
using GestorArchivos.Domain.Entities;
using GestorArchivos.Domain.Enums;
using GestorArchivos.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GestorArchivos.Api.Controllers.Sync;

[ApiController]
[Authorize(AuthenticationSchemes = SyncDeviceAuthenticationHandler.SchemeName)]
[Route("api/sync/files")]
public class SyncFileContentController : ControllerBase
{
    private const long MaxFileSize = 4L * 1024 * 1024 * 1024;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private readonly ApplicationDbContext _dbContext;
    private readonly IFileStorage _fileStorage;
    private readonly IFolderPermissionService _permissionService;
    private readonly ISyncChangeService _syncChangeService;

    public SyncFileContentController(
        ApplicationDbContext dbContext,
        IFileStorage fileStorage,
        IFolderPermissionService permissionService,
        ISyncChangeService syncChangeService)
    {
        _dbContext = dbContext;
        _fileStorage = fileStorage;
        _permissionService = permissionService;
        _syncChangeService = syncChangeService;
    }

    [HttpPost("{fileId:guid}/trash")]
    public async Task<IActionResult> MoveToTrash(
        Guid fileId,
        [FromBody] SyncTrashRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ExpectedVersion < 1 ||
            string.IsNullOrWhiteSpace(request.ExpectedHash) ||
            request.ExpectedHash.Length != 64 ||
            !request.ExpectedHash.All(Uri.IsHexDigit))
            return BadRequest(new { message = "Versión o hash inválidos." });

        var file = await _dbContext.Archivos.FirstOrDefaultAsync(
            x => x.Id == fileId && x.Estado == EstadoArchivo.Activo,
            cancellationToken);
        if (file is null)
            return NotFound();

        if (!await BelongsToDeviceAsync(
                file.CarpetaId, CurrentDeviceId(), CurrentUserId(),
                cancellationToken))
            return Forbid();

        if (!await _permissionService.HasPermissionAsync(
                CurrentUserId(), file.CarpetaId,
                PermisoCarpeta.Eliminar, cancellationToken))
            return Forbid();

        if (file.Version != request.ExpectedVersion ||
            !string.Equals(file.HashSha256, request.ExpectedHash,
                StringComparison.OrdinalIgnoreCase))
            return Conflict(new { message = "El archivo remoto cambió. No se eliminó." });

        var locked = await _dbContext.BloqueosArchivos.AnyAsync(
            x => x.ArchivoId == fileId && x.Activo &&
                 x.ExpiraUtc > DateTime.UtcNow,
            cancellationToken);
        if (locked)
            return Conflict(new { message = "El archivo tiene un bloqueo activo." });

        var now = DateTime.UtcNow;
        file.Estado = EstadoArchivo.EnPapelera;
        file.FechaEliminacion = now;
        file.FechaModificacion = now;
        file.EliminacionLoteId = Guid.NewGuid();
        file.Version++;

        await _syncChangeService.RegisterAsync(
            file.CarpetaId, file.Id, null, "FILE_DELETED",
            file.Version, cancellationToken: cancellationToken);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "El archivo cambió durante la operación." });
        }

        return NoContent();
    }

    [HttpPut("{fileId:guid}/content")]
    [RequestSizeLimit(MaxFileSize)]
    public async Task<IActionResult> UpdateContent(
        Guid fileId,
        [FromQuery] long expectedVersion,
        [FromQuery] string expectedHash,
        CancellationToken cancellationToken)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();
        var now = DateTime.UtcNow;

        if (expectedVersion < 1)
            return BadRequest(new { message = "La versión esperada no es válida." });

        if (string.IsNullOrWhiteSpace(expectedHash) ||
            expectedHash.Length != 64 ||
            !expectedHash.All(Uri.IsHexDigit))
        {
            return BadRequest(new { message = "El SHA-256 esperado no es válido." });
        }

        if (!Request.ContentLength.HasValue ||
            Request.ContentLength.Value <= 0)
        {
            return BadRequest(new { message = "El contenido del archivo está vacío." });
        }

        if (Request.ContentLength.Value > MaxFileSize)
            return BadRequest(new { message = "El archivo supera el límite de 4 GB." });

        var file = await _dbContext.Archivos
            .FirstOrDefaultAsync(
                x => x.Id == fileId &&
                     x.Estado == EstadoArchivo.Activo,
                cancellationToken);

        if (file is null)
            return NotFound();

        if (!await BelongsToDeviceAsync(
                file.CarpetaId,
                deviceId,
                userId,
                cancellationToken))
        {
            return Forbid();
        }

        if (!await _permissionService.HasPermissionAsync(
                userId,
                file.CarpetaId,
                PermisoCarpeta.Modificar,
                cancellationToken))
        {
            return Forbid();
        }

        var fileLock = await _dbContext.BloqueosArchivos
            .FirstOrDefaultAsync(
                x => x.ArchivoId == fileId && x.Activo,
                cancellationToken);

        if (fileLock is null ||
            fileLock.UsuarioId != userId ||
            fileLock.DispositivoId != deviceId ||
            fileLock.ExpiraUtc <= now)
        {
            return Conflict(new
            {
                message = "El dispositivo no posee un bloqueo activo válido sobre el archivo."
            });
        }

        var tempFile = Path.GetTempFileName();

        try
        {
            string newHash;
            long bytesWritten = 0;

            using (var sha256 = SHA256.Create())
            await using (var tempStream = new FileStream(
                tempFile,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[1024 * 1024];

                while (true)
                {
                    var read = await Request.Body.ReadAsync(
                        buffer.AsMemory(0, buffer.Length),
                        cancellationToken);

                    if (read == 0)
                        break;

                    bytesWritten += read;

                    if (bytesWritten > MaxFileSize)
                    {
                        return BadRequest(new
                        {
                            message = "El archivo supera el límite de 4 GB."
                        });
                    }

                    sha256.TransformBlock(buffer, 0, read, null, 0);

                    await tempStream.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken);
                }

                if (bytesWritten == 0)
                {
                    return BadRequest(new
                    {
                        message = "El contenido del archivo está vacío."
                    });
                }

                sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                newHash = Convert.ToHexString(sha256.Hash!);
            }

            var normalizedExpectedHash =
                expectedHash.Trim().ToUpperInvariant();

            if (file.Version != expectedVersion ||
                !string.Equals(
                    file.HashSha256,
                    normalizedExpectedHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                return await CreateConflictCopyAsync(
                    file,
                    tempFile,
                    newHash,
                    bytesWritten,
                    deviceId,
                    userId,
                    cancellationToken);
            }

            var oldPhysicalPath = file.NombreFisico;
            var newPhysicalPath = CreatePhysicalPath(file.Extension);

            await using (var source = new FileStream(
                tempFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await _fileStorage.SaveAsync(
                    newPhysicalPath,
                    source,
                    cancellationToken);
            }

            file.NombreFisico = newPhysicalPath;
            file.TamanoBytes = bytesWritten;
            file.HashSha256 = newHash;
            file.Version++;
            file.FechaModificacion = DateTime.UtcNow;
            fileLock.Activo = false;

            // La cola se registra en el mismo SaveChanges que
            // publica la nueva ruta física.
            _dbContext.Set<ArchivoFisicoPendienteEliminacion>().Add(
                new ArchivoFisicoPendienteEliminacion
                {
                    Id = Guid.NewGuid(),
                    RutaFisica = oldPhysicalPath,
                    FechaRegistroUtc = DateTime.UtcNow,
                    EliminarDespuesUtc = DateTime.UtcNow + Retention
                });

            await _syncChangeService.RegisterAsync(
                file.CarpetaId,
                file.Id,
                null,
                "FILE_UPDATED",
                file.Version,
                deviceId,
                cancellationToken);

            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                _dbContext.ChangeTracker.Clear();

                var currentFile = await _dbContext.Archivos
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        x => x.Id == fileId &&
                             x.Estado == EstadoArchivo.Activo,
                        CancellationToken.None);

                if (currentFile is null)
                {
                    await _fileStorage.DeleteAsync(
                        newPhysicalPath,
                        CancellationToken.None);

                    return Conflict(new
                    {
                        message = "El archivo fue eliminado mientras se sincronizaba."
                    });
                }

                try
                {
                    return await CreateConflictCopyAsync(
                        currentFile,
                        tempFile,
                        newHash,
                        bytesWritten,
                        deviceId,
                        userId,
                        CancellationToken.None);
                }
                finally
                {
                    await _fileStorage.DeleteAsync(
                        newPhysicalPath,
                        CancellationToken.None);
                }
            }
            catch
            {
                await _fileStorage.DeleteAsync(
                    newPhysicalPath,
                    CancellationToken.None);

                throw;
            }

            // No eliminar oldPhysicalPath aquí.
            // La cola persistente lo eliminará después de 7 días.

            return Ok(new
            {
                file.Id,
                file.Version,
                file.HashSha256,
                file.TamanoBytes,
                file.FechaModificacion
            });
        }
        finally
        {
            if (System.IO.File.Exists(tempFile))
                System.IO.File.Delete(tempFile);
        }
    }

    private async Task<IActionResult> CreateConflictCopyAsync(
        Archivo originalFile,
        string tempFile,
        string hash,
        long size,
        Guid deviceId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        // Una copia por conflicto crea un archivo nuevo.
        if (!await _permissionService.HasPermissionAsync(
                userId,
                originalFile.CarpetaId,
                PermisoCarpeta.Crear,
                cancellationToken))
        {
            return Forbid();
        }

        var deviceName = await _dbContext.DispositivosSync
            .AsNoTracking()
            .Where(x => x.Id == deviceId)
            .Select(x => x.Nombre)
            .FirstOrDefaultAsync(cancellationToken);

        deviceName = SanitizeConflictPart(
            string.IsNullOrWhiteSpace(deviceName)
                ? "Dispositivo"
                : deviceName);

        var conflictName = await CreateUniqueConflictNameAsync(
            originalFile,
            deviceName,
            cancellationToken);

        var conflictId = Guid.NewGuid();
        var extension = Path.GetExtension(conflictName);
        var physicalPath = CreatePhysicalPath(extension);

        await using (var source = new FileStream(
            tempFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await _fileStorage.SaveAsync(
                physicalPath,
                source,
                cancellationToken);
        }

        var conflictFile = new Archivo
        {
            Id = conflictId,
            Nombre = conflictName,
            NombreFisico = physicalPath,
            Extension = extension,
            TipoContenido = originalFile.TipoContenido,
            TamanoBytes = size,
            HashSha256 = hash,
            Version = 1,
            CarpetaId = originalFile.CarpetaId,
            CreadoPorUsuarioId = userId,
            FechaCreacion = DateTime.UtcNow
        };

        _dbContext.Archivos.Add(conflictFile);

        await _syncChangeService.RegisterAsync(
            conflictFile.CarpetaId,
            conflictFile.Id,
            null,
            "FILE_CONFLICT_CREATED",
            conflictFile.Version,
            deviceId,
            cancellationToken);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await _fileStorage.DeleteAsync(
                physicalPath,
                CancellationToken.None);

            throw;
        }

        return Conflict(new
        {
            message = "Se detectó un conflicto. Ambas versiones fueron conservadas.",
            original = new
            {
                originalFile.Id,
                originalFile.Version,
                originalFile.HashSha256
            },
            conflict = new
            {
                conflictFile.Id,
                conflictFile.Nombre,
                conflictFile.Version,
                conflictFile.HashSha256
            }
        });
    }

    private async Task<string> CreateUniqueConflictNameAsync(
        Archivo originalFile,
        string deviceName,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(originalFile.Nombre);
        var baseName = Path.GetFileNameWithoutExtension(originalFile.Nombre);
        var timestamp = DateTime.UtcNow;

        var suffix =
            $" (conflicto {deviceName} {timestamp:yyyy-MM-dd HH-mm-ss})";

        var maxBaseLength =
            Math.Max(1, 255 - extension.Length - suffix.Length);

        if (baseName.Length > maxBaseLength)
            baseName = baseName[..maxBaseLength];

        var candidate = $"{baseName}{suffix}{extension}";
        var counter = 2;

        while (await _dbContext.Archivos
            .AsNoTracking()
            .AnyAsync(
                x => x.CarpetaId == originalFile.CarpetaId &&
                     x.Nombre == candidate &&
                     x.Estado == EstadoArchivo.Activo,
                cancellationToken))
        {
            var numberedSuffix =
                $" (conflicto {deviceName} {timestamp:yyyy-MM-dd HH-mm-ss}) ({counter})";

            maxBaseLength = Math.Max(
                1,
                255 - extension.Length - numberedSuffix.Length);

            var adjustedBaseName =
                baseName.Length > maxBaseLength
                    ? baseName[..maxBaseLength]
                    : baseName;

            candidate =
                $"{adjustedBaseName}{numberedSuffix}{extension}";

            counter++;
        }

        return candidate;
    }

    private static string SanitizeConflictPart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();

        var sanitized = new string(
            value
                .Where(x =>
                    !invalid.Contains(x) &&
                    !char.IsControl(x))
                .ToArray())
            .Trim()
            .TrimEnd('.');

        return string.IsNullOrWhiteSpace(sanitized)
            ? "Dispositivo"
            : sanitized;
    }

    private static string CreatePhysicalPath(string extension)
    {
        var now = DateTime.UtcNow;

        return Path.Combine(
            now.ToString("yyyy"),
            now.ToString("MM"),
            $"{Guid.NewGuid():N}{extension}");
    }

    private async Task<bool> BelongsToDeviceAsync(
        Guid folderId,
        Guid deviceId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var rootFolderId = await _dbContext.DispositivosSync
            .AsNoTracking()
            .Where(x =>
                x.Id == deviceId &&
                x.UsuarioId == userId &&
                x.Activo)
            .Select(x => (Guid?)x.CarpetaRaizId)
            .FirstOrDefaultAsync(cancellationToken);

        if (!rootFolderId.HasValue)
            return false;

        Guid? currentId = folderId;
        var visited = new HashSet<Guid>();

        while (currentId.HasValue)
        {
            if (!visited.Add(currentId.Value))
                return false;

            if (currentId.Value == rootFolderId.Value)
                return true;

            currentId = await _dbContext.Carpetas
                .AsNoTracking()
                .Where(x => x.Id == currentId.Value)
                .Select(x => x.CarpetaPadreId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return false;
    }

    private Guid CurrentDeviceId()
    {
        var value = User.FindFirstValue("device_id");

        if (!Guid.TryParse(value, out var deviceId))
            throw new UnauthorizedAccessException();

        return deviceId;
    }

    private Guid CurrentUserId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!Guid.TryParse(value, out var userId))
            throw new UnauthorizedAccessException();

        return userId;
    }
}

public sealed record SyncTrashRequest(long ExpectedVersion, string ExpectedHash);

