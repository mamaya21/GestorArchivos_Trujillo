
using System.Security.Claims;
using GestorArchivos.Api.Security;
using GestorArchivos.Application.Common.Security;
using GestorArchivos.Application.Common.Storage;
using GestorArchivos.Domain.Enums;
using GestorArchivos.Domain.Security;
using GestorArchivos.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GestorArchivos.Api.Controllers.Sync;

[ApiController]
[Authorize(AuthenticationSchemes = SyncDeviceAuthenticationHandler.SchemeName)]
[Route("api/sync/read")]
public class SyncReadController : ControllerBase
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IFileStorage _fileStorage;
    private readonly IFolderPermissionService _permissionService;

    public SyncReadController(
        ApplicationDbContext dbContext,
        IFileStorage fileStorage,
        IFolderPermissionService permissionService)
    {
        _dbContext = dbContext;
        _fileStorage = fileStorage;
        _permissionService = permissionService;
    }

    [HttpGet("files/{fileId:guid}")]
    public async Task<IActionResult> GetFile(
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();

        var file = await _dbContext.Archivos
            .AsNoTracking()
            .Where(x =>
                x.Id == fileId &&
                x.Estado == EstadoArchivo.Activo)
            .Select(x => new
            {
                x.Id,
                x.Nombre,
                x.Extension,
                x.TipoContenido,
                x.TamanoBytes,
                x.HashSha256,
                x.Version,
                x.CarpetaId,
                x.FechaCreacion,
                x.FechaModificacion
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (file is null)
            return NotFound();

        if (!await CanReadAsync(
                file.CarpetaId,
                deviceId,
                userId,
                cancellationToken))
        {
            return Forbid();
        }

        return Ok(file);
    }

    [HttpGet("files/{fileId:guid}/download")]
    public async Task<IActionResult> Download(
        Guid fileId,
        [FromQuery] long? expectedVersion,
        [FromQuery] string? expectedHash,
        CancellationToken cancellationToken)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();

        if (expectedVersion.HasValue && expectedVersion.Value < 1)
        {
            return BadRequest(new
            {
                message = "La versión esperada no es válida."
            });
        }

        if (expectedHash is not null &&
            (expectedHash.Length != 64 ||
             !expectedHash.All(Uri.IsHexDigit)))
        {
            return BadRequest(new
            {
                message = "El SHA-256 esperado no es válido."
            });
        }

        var file = await _dbContext.Archivos
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x =>
                    x.Id == fileId &&
                    x.Estado == EstadoArchivo.Activo,
                cancellationToken);

        if (file is null)
            return NotFound();

        if (!await CanReadAsync(
                file.CarpetaId,
                deviceId,
                userId,
                cancellationToken))
        {
            return Forbid();
        }

        if ((expectedVersion.HasValue &&
             file.Version != expectedVersion.Value) ||
            (expectedHash is not null &&
             !string.Equals(
                 file.HashSha256,
                 expectedHash,
                 StringComparison.OrdinalIgnoreCase)))
        {
            return StatusCode(
                StatusCodes.Status412PreconditionFailed,
                new
                {
                    message = "La versión remota cambió antes de la descarga.",
                    currentVersion = file.Version,
                    currentHash = file.HashSha256
                });
        }

        Stream stream;

        try
        {
            stream = await _fileStorage.OpenReadAsync(
                file.NombreFisico,
                cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return StatusCode(
                StatusCodes.Status409Conflict,
                new
                {
                    message = "El archivo físico ya no está disponible."
                });
        }
        catch (DirectoryNotFoundException)
        {
            return StatusCode(
                StatusCodes.Status409Conflict,
                new
                {
                    message = "El archivo físico ya no está disponible."
                });
        }

        Response.Headers["X-Sync-File-Version"] =
            file.Version.ToString(
                System.Globalization.CultureInfo.InvariantCulture);

        Response.Headers["X-Sync-File-Sha256"] =
            file.HashSha256;

        return File(
            stream,
            file.TipoContenido,
            file.Nombre,
            enableRangeProcessing: true);
    }

    [HttpGet("folders/{folderId:guid}")]
    public async Task<IActionResult> GetFolder(
        Guid folderId,
        CancellationToken cancellationToken)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();

        var folder = await _dbContext.Carpetas
            .AsNoTracking()
            .Where(x =>
                x.Id == folderId &&
                !x.EnPapelera)
            .Select(x => new
            {
                x.Id,
                x.Nombre,
                x.CarpetaPadreId,
                x.FechaCreacion,
                x.FechaModificacion
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (folder is null)
            return NotFound();

        if (!await CanReadAsync(
                folder.Id,
                deviceId,
                userId,
                cancellationToken))
        {
            return Forbid();
        }

        return Ok(folder);
    }

    [HttpGet("folders/{folderId:guid}/contents")]
    public async Task<IActionResult> GetContents(
        Guid folderId,
        CancellationToken cancellationToken)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();

        var folderExists = await _dbContext.Carpetas
            .AsNoTracking()
            .AnyAsync(
                x =>
                    x.Id == folderId &&
                    !x.EnPapelera,
                cancellationToken);

        if (!folderExists)
            return NotFound();

        if (!await CanReadAsync(
                folderId,
                deviceId,
                userId,
                cancellationToken))
        {
            return Forbid();
        }

        var folders = await _dbContext.Carpetas
            .AsNoTracking()
            .Where(x =>
                x.CarpetaPadreId == folderId &&
                !x.EnPapelera)
            .OrderBy(x => x.Nombre)
            .Select(x => new
            {
                x.Id,
                x.Nombre,
                x.CarpetaPadreId,
                x.FechaCreacion,
                x.FechaModificacion
            })
            .ToListAsync(cancellationToken);

        var files = await _dbContext.Archivos
            .AsNoTracking()
            .Where(x =>
                x.CarpetaId == folderId &&
                x.Estado == EstadoArchivo.Activo)
            .OrderBy(x => x.Nombre)
            .Select(x => new
            {
                x.Id,
                x.Nombre,
                x.Extension,
                x.TipoContenido,
                x.TamanoBytes,
                x.HashSha256,
                x.Version,
                x.CarpetaId,
                x.FechaCreacion,
                x.FechaModificacion
            })
            .ToListAsync(cancellationToken);

        var readableFolderIds = new HashSet<Guid>();

        foreach (var child in folders)
        {
            if (await _permissionService.HasPermissionAsync(
                    userId,
                    child.Id,
                    PermisoCarpeta.Leer,
                    cancellationToken))
            {
                readableFolderIds.Add(child.Id);
            }
        }

        return Ok(new
        {
            folderId,
            folders = folders.Where(x => readableFolderIds.Contains(x.Id)),
            files
        });
    }

    private async Task<bool> CanReadAsync(
        Guid folderId,
        Guid deviceId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (!await BelongsToDeviceAsync(
                folderId,
                deviceId,
                userId,
                cancellationToken))
        {
            return false;
        }

        return await _permissionService.HasPermissionAsync(
            userId,
            folderId,
            PermisoCarpeta.Leer,
            cancellationToken);
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
