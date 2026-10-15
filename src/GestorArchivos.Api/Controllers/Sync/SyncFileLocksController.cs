
using System.Security.Claims;
using GestorArchivos.Api.Security;
using GestorArchivos.Application.Common.Security;
using GestorArchivos.Domain.Security;
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
public class SyncFileLocksController : ControllerBase
{
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(10);

    private readonly ApplicationDbContext _dbContext;
    private readonly IFolderPermissionService _permissionService;

    public SyncFileLocksController(
        ApplicationDbContext dbContext,
        IFolderPermissionService permissionService)
    {
        _dbContext = dbContext;
        _permissionService = permissionService;
    }

    [HttpPost("{fileId:guid}/lock")]
    public async Task<IActionResult> Acquire(
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();
        var now = DateTime.UtcNow;

        var file = await _dbContext.Archivos
            .AsNoTracking()
            .Where(x =>
                x.Id == fileId &&
                x.Estado == EstadoArchivo.Activo)
            .Select(x => new
            {
                x.Id,
                x.CarpetaId
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (file is null)
            return NotFound();

        if (!await BelongsToDeviceAsync(
                file.CarpetaId,
                deviceId,
                userId,
                cancellationToken))
            return Forbid();

        if (!await _permissionService.HasPermissionAsync(
                userId,
                file.CarpetaId,
                PermisoCarpeta.Modificar,
                cancellationToken))
            return Forbid();

        var activeLock = await _dbContext.BloqueosArchivos
            .FirstOrDefaultAsync(
                x => x.ArchivoId == fileId &&
                     x.Activo,
                cancellationToken);

        if (activeLock is not null)
        {
            if (activeLock.ExpiraUtc > now)
            {
                if (activeLock.DispositivoId == deviceId &&
                    activeLock.UsuarioId == userId)
                {
                    activeLock.ExpiraUtc = now.Add(LockDuration);

                    await _dbContext.SaveChangesAsync(cancellationToken);

                    return Ok(CreateResponse(activeLock));
                }

                return Conflict(new
                {
                    message = "El archivo está siendo utilizado por otro usuario o dispositivo.",
                    activeLock.UsuarioId,
                    activeLock.DispositivoId,
                    activeLock.FechaBloqueoUtc,
                    activeLock.ExpiraUtc
                });
            }

            activeLock.Activo = false;

            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                _dbContext.ChangeTracker.Clear();

                return Conflict(new
                {
                    message = "El estado del bloqueo cambió simultáneamente. Intente nuevamente."
                });
            }

            _dbContext.ChangeTracker.Clear();

            var currentActiveLock = await _dbContext.BloqueosArchivos
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.ArchivoId == fileId &&
                         x.Activo,
                    cancellationToken);

            if (currentActiveLock is not null)
            {
                return Conflict(new
                {
                    message = "El archivo acaba de ser bloqueado por otro usuario o dispositivo.",
                    currentActiveLock.UsuarioId,
                    currentActiveLock.DispositivoId,
                    currentActiveLock.FechaBloqueoUtc,
                    currentActiveLock.ExpiraUtc
                });
            }
        }

        var fileLock = new BloqueoArchivo
        {
            ArchivoId = fileId,
            UsuarioId = userId,
            DispositivoId = deviceId,
            FechaBloqueoUtc = now,
            ExpiraUtc = now.Add(LockDuration),
            Activo = true
        };

        _dbContext.BloqueosArchivos.Add(fileLock);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            _dbContext.ChangeTracker.Clear();

            var winner = await _dbContext.BloqueosArchivos
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.ArchivoId == fileId &&
                         x.Activo,
                    cancellationToken);

            if (winner is not null)
            {
                return Conflict(new
                {
                    message = "El archivo acaba de ser bloqueado por otro usuario o dispositivo.",
                    winner.UsuarioId,
                    winner.DispositivoId,
                    winner.FechaBloqueoUtc,
                    winner.ExpiraUtc
                });
            }

            return Conflict(new
            {
                message = "No se pudo adquirir el bloqueo. Intente nuevamente."
            });
        }

        return Ok(CreateResponse(fileLock));
    }

    [HttpPost("{fileId:guid}/lock/renew")]
    public async Task<IActionResult> Renew(
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();
        var now = DateTime.UtcNow;

        var fileLock = await _dbContext.BloqueosArchivos
            .FirstOrDefaultAsync(
                x =>
                    x.ArchivoId == fileId &&
                    x.Activo &&
                    x.DispositivoId == deviceId &&
                    x.UsuarioId == userId,
                cancellationToken);

        if (fileLock is null)
            return NotFound();

        var folderId = await _dbContext.Archivos
            .AsNoTracking()
            .Where(x => x.Id == fileId && x.Estado == EstadoArchivo.Activo)
            .Select(x => (Guid?)x.CarpetaId)
            .FirstOrDefaultAsync(cancellationToken);

        if (!folderId.HasValue)
            return NotFound();

        if (!await BelongsToDeviceAsync(
                folderId.Value, deviceId, userId, cancellationToken) ||
            !await _permissionService.HasPermissionAsync(
                userId, folderId.Value,
                PermisoCarpeta.Modificar, cancellationToken))
            return Forbid();

        if (fileLock.ExpiraUtc <= now)
        {
            fileLock.Activo = false;

            await _dbContext.SaveChangesAsync(cancellationToken);

            return Conflict(new
            {
                message = "El bloqueo ha expirado."
            });
        }

        fileLock.ExpiraUtc = now.Add(LockDuration);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(CreateResponse(fileLock));
    }

    [HttpDelete("{fileId:guid}/lock")]
    public async Task<IActionResult> Release(
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();

        var fileLock = await _dbContext.BloqueosArchivos
            .FirstOrDefaultAsync(
                x =>
                    x.ArchivoId == fileId &&
                    x.Activo &&
                    x.DispositivoId == deviceId &&
                    x.UsuarioId == userId,
                cancellationToken);

        if (fileLock is null)
            return NoContent();

        fileLock.Activo = false;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return NoContent();
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

    private static object CreateResponse(BloqueoArchivo fileLock)
    {
        return new
        {
            lockId = fileLock.Id,
            fileId = fileLock.ArchivoId,
            fileLock.UsuarioId,
            fileLock.DispositivoId,
            fileLock.FechaBloqueoUtc,
            fileLock.ExpiraUtc
        };
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
        var value = User.FindFirstValue(
            ClaimTypes.NameIdentifier);

        if (!Guid.TryParse(value, out var userId))
            throw new UnauthorizedAccessException();

        return userId;
    }
}
