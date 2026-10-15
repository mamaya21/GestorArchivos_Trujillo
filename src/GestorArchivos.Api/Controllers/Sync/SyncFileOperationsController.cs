using System.Security.Claims;
using System.Security.Cryptography;
using GestorArchivos.Application.Common.Storage;
using GestorArchivos.Domain.Entities;
using GestorArchivos.Api.Security;
using GestorArchivos.Application.Common.Interfaces;
using GestorArchivos.Application.Common.Security;
using GestorArchivos.Application.Common.Validation;
using GestorArchivos.Domain.Enums;
using GestorArchivos.Domain.Security;
using GestorArchivos.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GestorArchivos.Api.Controllers.Sync;

[ApiController]
[Authorize(AuthenticationSchemes = SyncDeviceAuthenticationHandler.SchemeName)]
[Route("api/sync/files")]
public class SyncFileOperationsController : ControllerBase
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ISyncChangeService _syncChangeService;
    private readonly IFolderPermissionService _permissionService;
    private readonly IFileStorage _fileStorage;

    public SyncFileOperationsController(
        ApplicationDbContext dbContext,
        ISyncChangeService syncChangeService,
        IFolderPermissionService permissionService,
        IFileStorage fileStorage)
    {
        _dbContext = dbContext;
        _syncChangeService = syncChangeService;
        _permissionService = permissionService;
        _fileStorage = fileStorage;
    }

    [HttpPost]
    [RequestSizeLimit(4L * 1024 * 1024 * 1024)]
    public async Task<IActionResult> Create(
        [FromForm] Guid folderId,
        [FromForm] IFormFile file,
        CancellationToken cancellationToken)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();

        if (file is null || file.Length <= 0)
            return BadRequest(new { message = "El archivo es obligatorio." });

        if (file.Length > 4L * 1024 * 1024 * 1024)
            return BadRequest(new { message = "El archivo supera el límite de 4 GB." });

        if (!await BelongsToDeviceAsync(folderId, deviceId, userId, cancellationToken))
            return Forbid();

        if (!await _permissionService.HasPermissionAsync(
                userId, folderId, PermisoCarpeta.Crear, cancellationToken))
            return Forbid();

        var folderExists = await _dbContext.Carpetas
            .AsNoTracking()
            .AnyAsync(x => x.Id == folderId && !x.EnPapelera, cancellationToken);

        if (!folderExists)
            return NotFound(new { message = "La carpeta no existe." });

        if (!FileNameValidator.TryValidate(
                file.FileName, out var fileName, out var validationError))
            return BadRequest(new { message = validationError });

        var duplicate = await _dbContext.Archivos
            .AsNoTracking()
            .AnyAsync(
                x => x.CarpetaId == folderId &&
                     x.Nombre == fileName &&
                     x.Estado == EstadoArchivo.Activo,
                cancellationToken);

        if (duplicate)
            return Conflict(new { message = "Ya existe un archivo con ese nombre." });

        var id = Guid.NewGuid();
        var extension = Path.GetExtension(fileName);
        var physicalName = $"{id:N}{extension}";
        var relativePath = Path.Combine(
            DateTime.UtcNow.ToString("yyyy"),
            DateTime.UtcNow.ToString("MM"),
            physicalName);

        string hash;

        await using (var source = file.OpenReadStream())
        {
            using var sha256 = SHA256.Create();
            hash = Convert.ToHexString(
                await sha256.ComputeHashAsync(source, cancellationToken));

            source.Position = 0;
            await _fileStorage.SaveAsync(relativePath, source, cancellationToken);
        }

        var entity = new Archivo
        {
            Id = id,
            Nombre = fileName,
            NombreFisico = relativePath,
            Extension = extension,
            TipoContenido = string.IsNullOrWhiteSpace(file.ContentType)
                ? "application/octet-stream"
                : file.ContentType,
            TamanoBytes = file.Length,
            HashSha256 = hash,
            Version = 1,
            CarpetaId = folderId,
            CreadoPorUsuarioId = userId
        };

        try
        {
            _dbContext.Archivos.Add(entity);

            await _syncChangeService.RegisterAsync(
                folderId,
                entity.Id,
                null,
                "FILE_CREATED",
                entity.Version,
                deviceId,
                cancellationToken);

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await _fileStorage.DeleteAsync(relativePath, CancellationToken.None);
            throw;
        }

        return Created(
            $"/api/sync/read/files/{entity.Id}",
            new
            {
                entity.Id,
                entity.Nombre,
                entity.CarpetaId,
                entity.Extension,
                entity.TipoContenido,
                entity.TamanoBytes,
                entity.HashSha256,
                entity.Version,
                entity.FechaCreacion
            });
    }

    [HttpPut("{fileId:guid}/rename")]
    public async Task<IActionResult> Rename(
        Guid fileId,
        RenameSyncFileRequest request,
        CancellationToken cancellationToken)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();

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
            return Forbid();

        if (!await _permissionService.HasPermissionAsync(
                userId,
                file.CarpetaId,
                PermisoCarpeta.Modificar,
                cancellationToken))
            return Forbid();

        if (!FileNameValidator.TryValidate(
                request.Name,
                out var newName,
                out var validationError))
        {
            return BadRequest(new { message = validationError });
        }

        if (file.Nombre == newName)
            return NoContent();

        var duplicate = await _dbContext.Archivos
            .AsNoTracking()
            .AnyAsync(
                x => x.Id != fileId &&
                     x.CarpetaId == file.CarpetaId &&
                     x.Nombre == newName &&
                     x.Estado == EstadoArchivo.Activo,
                cancellationToken);

        if (duplicate)
            return Conflict(new
            {
                message = "Ya existe un archivo con ese nombre."
            });

        file.Nombre = newName;
        file.Extension = Path.GetExtension(newName);
        file.Version++;
        file.FechaModificacion = DateTime.UtcNow;

        await _syncChangeService.RegisterAsync(
            file.CarpetaId,
            file.Id,
            null,
            "FILE_RENAMED",
            file.Version,
            deviceId,
            cancellationToken);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new
            {
                message = "El archivo fue modificado simultáneamente."
            });
        }

        return Ok(new
        {
            file.Id,
            file.Nombre,
            file.Extension,
            file.Version,
            file.FechaModificacion
        });
    }

    [HttpPut("{fileId:guid}/move")]
    public async Task<IActionResult> Move(
        Guid fileId,
        MoveSyncFileRequest request,
        CancellationToken cancellationToken)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();

        var file = await _dbContext.Archivos
            .FirstOrDefaultAsync(
                x => x.Id == fileId &&
                     x.Estado == EstadoArchivo.Activo,
                cancellationToken);

        if (file is null)
            return NotFound();

        var sourceFolderId = file.CarpetaId;

        if (!await BelongsToDeviceAsync(
                sourceFolderId,
                deviceId,
                userId,
                cancellationToken))
            return Forbid();

        if (!await BelongsToDeviceAsync(
                request.DestinationFolderId,
                deviceId,
                userId,
                cancellationToken))
            return Forbid();

        if (!await _permissionService.HasPermissionAsync(
                userId,
                sourceFolderId,
                PermisoCarpeta.Modificar,
                cancellationToken))
            return Forbid();

        if (!await _permissionService.HasPermissionAsync(
                userId,
                request.DestinationFolderId,
                PermisoCarpeta.Crear,
                cancellationToken))
            return Forbid();

        var destinationExists = await _dbContext.Carpetas
            .AsNoTracking()
            .AnyAsync(
                x => x.Id == request.DestinationFolderId &&
                     !x.EnPapelera,
                cancellationToken);

        if (!destinationExists)
            return NotFound(new
            {
                message = "La carpeta destino no existe."
            });

        if (sourceFolderId == request.DestinationFolderId)
            return NoContent();

        var duplicate = await _dbContext.Archivos
            .AsNoTracking()
            .AnyAsync(
                x => x.Id != fileId &&
                     x.CarpetaId == request.DestinationFolderId &&
                     x.Nombre == file.Nombre &&
                     x.Estado == EstadoArchivo.Activo,
                cancellationToken);

        if (duplicate)
            return Conflict(new
            {
                message = "Ya existe un archivo con ese nombre en la carpeta destino."
            });

        file.CarpetaId = request.DestinationFolderId;
        file.Version++;
        file.FechaModificacion = DateTime.UtcNow;

        await _syncChangeService.RegisterAsync(
            sourceFolderId,
            file.Id,
            null,
            "FILE_MOVED",
            file.Version,
            deviceId,
            cancellationToken);

        await _syncChangeService.RegisterAsync(
            file.CarpetaId,
            file.Id,
            null,
            "FILE_MOVED",
            file.Version,
            deviceId,
            cancellationToken);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new
            {
                message = "El archivo fue modificado simultáneamente."
            });
        }

        return Ok(new
        {
            file.Id,
            file.CarpetaId,
            file.Version,
            file.FechaModificacion
        });
    }

    [HttpDelete("{fileId:guid}")]
    public async Task<IActionResult> Delete(
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();

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
            return Forbid();

        if (!await _permissionService.HasPermissionAsync(
                userId,
                file.CarpetaId,
                PermisoCarpeta.Eliminar,
                cancellationToken))
            return Forbid();

        var now = DateTime.UtcNow;

        file.Estado = EstadoArchivo.EnPapelera;
        file.FechaEliminacion = now;
        file.FechaModificacion = now;
        file.EliminacionLoteId = Guid.NewGuid();
        file.Version++;

        var activeLocks = await _dbContext.BloqueosArchivos
            .Where(x => x.ArchivoId == fileId && x.Activo)
            .ToListAsync(cancellationToken);

        foreach (var fileLock in activeLocks)
            fileLock.Activo = false;

        await _syncChangeService.RegisterAsync(
            file.CarpetaId,
            file.Id,
            null,
            "FILE_DELETED",
            file.Version,
            deviceId,
            cancellationToken);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new
            {
                message = "El archivo fue modificado simultáneamente."
            });
        }

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

public sealed record RenameSyncFileRequest(string Name);

public sealed record MoveSyncFileRequest(Guid DestinationFolderId);