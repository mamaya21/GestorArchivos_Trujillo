using System.Security.Claims;
using System.Security.Cryptography;
using GestorArchivos.Application.Common.Interfaces;
using GestorArchivos.Application.Common.Security;
using GestorArchivos.Application.Common.Storage;
using GestorArchivos.Application.Common.Validation;
using GestorArchivos.Domain.Entities;
using GestorArchivos.Domain.Enums;
using GestorArchivos.Domain.Security;
using GestorArchivos.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GestorArchivos.Api.Controllers.Files;

[ApiController]
[Authorize]
[Route("api/files")]
public class FilesController : ControllerBase
{
    private const long MaxFileSize =
        4L * 1024 * 1024 * 1024;

    private readonly ApplicationDbContext _dbContext;
    private readonly IFileStorage _fileStorage;
    private readonly IFolderPermissionService _permissionService;
    private readonly ISyncChangeService _syncChangeService;

    public FilesController(
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

    [HttpPost("upload")]
    [RequestSizeLimit(MaxFileSize)]
    public async Task<IActionResult> Upload(
        IFormFile file,
        [FromForm] Guid folderId,
        CancellationToken cancellationToken)
    {
        if (file.Length <= 0)
            return BadRequest(new { message = "El archivo est� vac�o." });

        if (file.Length > MaxFileSize)
        {
            return BadRequest(new
            {
                message = "El archivo supera el l�mite de 4 GB."
            });
        }

        var folderExists = await _dbContext.Carpetas
            .AsNoTracking()
            .AnyAsync(
                x => x.Id == folderId &&
                     !x.EnPapelera,
                cancellationToken);

        if (!folderExists)
        {
            return BadRequest(new
            {
                message = "La carpeta no existe."
            });
        }

        if (!await _permissionService.HasPermissionAsync(
                CurrentUserId(),
                folderId,
                PermisoCarpeta.Crear,
                cancellationToken))
        {
            return Forbid();
        }

        if (!FileNameValidator.TryValidate(
                file.FileName,
                out var originalName,
                out var validationError))
        {
            return BadRequest(new
            {
                message = validationError
            });
        }

        var duplicateExists = await _dbContext.Archivos
            .AsNoTracking()
            .AnyAsync(
                x =>
                    x.CarpetaId == folderId &&
                    x.Nombre == originalName &&
                    x.Estado == EstadoArchivo.Activo,
                cancellationToken);

        if (duplicateExists)
        {
            return Conflict(new
            {
                message = "Ya existe un archivo con ese nombre."
            });
        }

        var id = Guid.NewGuid();
        var extension = Path.GetExtension(originalName);
        var physicalName = $"{id:N}{extension}";

        var relativePath = Path.Combine(
            DateTime.UtcNow.ToString("yyyy"),
            DateTime.UtcNow.ToString("MM"),
            physicalName);

        await using var source =
            file.OpenReadStream();

        using var sha256 = SHA256.Create();

        var hash = await sha256.ComputeHashAsync(
            source,
            cancellationToken);

        source.Position = 0;

        await _fileStorage.SaveAsync(
            relativePath,
            source,
            cancellationToken);

        var entity = new Archivo
        {
            Id = id,
            Nombre = originalName,
            NombreFisico = relativePath,
            Extension = extension,
            TipoContenido =
                string.IsNullOrWhiteSpace(file.ContentType)
                    ? "application/octet-stream"
                    : file.ContentType,
            TamanoBytes = file.Length,
            HashSha256 = Convert.ToHexString(hash),
            Version = 1,
            CarpetaId = folderId,
            CreadoPorUsuarioId = CurrentUserId()
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
                cancellationToken: cancellationToken);

            await _dbContext.SaveChangesAsync(
                cancellationToken);
        }
        catch
        {
            await _fileStorage.DeleteAsync(
                relativePath,
                CancellationToken.None);

            throw;
        }

        return CreatedAtAction(
            nameof(GetById),
            new { id = entity.Id },
            new
            {
                entity.Id,
                entity.Nombre,
                entity.TamanoBytes,
                entity.TipoContenido,
                entity.Version,
                entity.FechaCreacion
            });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        var file = await _dbContext.Archivos
            .AsNoTracking()
            .Where(x =>
                x.Id == id &&
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

        if (!await _permissionService.HasPermissionAsync(
                CurrentUserId(),
                file.CarpetaId,
                PermisoCarpeta.Leer,
                cancellationToken))
        {
            return Forbid();
        }

        return Ok(file);
    }

    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(
        Guid id,
        CancellationToken cancellationToken)
    {
        var file = await _dbContext.Archivos
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x =>
                    x.Id == id &&
                    x.Estado == EstadoArchivo.Activo,
                cancellationToken);

        if (file is null)
            return NotFound();

        if (!await _permissionService.HasPermissionAsync(
                CurrentUserId(),
                file.CarpetaId,
                PermisoCarpeta.Leer,
                cancellationToken))
        {
            return Forbid();
        }

        if (!await _fileStorage.ExistsAsync(
                file.NombreFisico,
                cancellationToken))
        {
            return NotFound(new
            {
                message = "El archivo f�sico no existe."
            });
        }

        var stream = await _fileStorage.OpenReadAsync(
            file.NombreFisico,
            cancellationToken);

        return File(
            stream,
            file.TipoContenido,
            file.Nombre,
            enableRangeProcessing: true);
    }

    [HttpGet("{id:guid}/open")]
    public async Task<IActionResult> Open(
        Guid id,
        CancellationToken cancellationToken)
    {
        var file = await _dbContext.Archivos
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x =>
                    x.Id == id &&
                    x.Estado == EstadoArchivo.Activo,
                cancellationToken);

        if (file is null)
            return NotFound();

        if (!await _permissionService.HasPermissionAsync(
                CurrentUserId(),
                file.CarpetaId,
                PermisoCarpeta.Leer,
                cancellationToken))
        {
            return Forbid();
        }

        if (!await _fileStorage.ExistsAsync(
                file.NombreFisico,
                cancellationToken))
        {
            return NotFound(new
            {
                message = "El archivo f�sico no existe."
            });
        }

        var stream = await _fileStorage.OpenReadAsync(
            file.NombreFisico,
            cancellationToken);

        Response.Headers.ContentDisposition =
            $"inline; filename*=UTF-8''{Uri.EscapeDataString(file.Nombre)}";
        Response.Headers.XContentTypeOptions = "nosniff";

        var contentType = string.Equals(
            Path.GetExtension(file.Nombre),
            ".txt",
            StringComparison.OrdinalIgnoreCase)
            ? "text/plain; charset=utf-8"
            : file.TipoContenido;

        return File(
            stream,
            contentType,
            enableRangeProcessing: true);
    }

    [HttpPut("{id:guid}/rename")]
    public async Task<IActionResult> Rename(
        Guid id,
        RenameFileRequest request,
        CancellationToken cancellationToken)
    {
        var file = await _dbContext.Archivos
            .FirstOrDefaultAsync(
                x =>
                    x.Id == id &&
                    x.Estado == EstadoArchivo.Activo,
                cancellationToken);

        if (file is null)
            return NotFound();

        if (!await _permissionService.HasPermissionAsync(
                CurrentUserId(),
                file.CarpetaId,
                PermisoCarpeta.Modificar,
                cancellationToken))
        {
            return Forbid();
        }

        if (!FileNameValidator.TryValidate(
                request.Name,
                out var newName,
                out var validationError))
        {
            return BadRequest(new
            {
                message = validationError
            });
        }

        var duplicateExists = await _dbContext.Archivos
            .AnyAsync(
                x =>
                    x.Id != id &&
                    x.CarpetaId == file.CarpetaId &&
                    x.Nombre == newName &&
                    x.Estado == EstadoArchivo.Activo,
                cancellationToken);

        if (duplicateExists)
        {
            return Conflict(new
            {
                message = "Ya existe un archivo con ese nombre."
            });
        }

        if (file.Nombre == newName)
            return NoContent();

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
            cancellationToken: cancellationToken);

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> MoveToTrash(
        Guid id,
        CancellationToken cancellationToken)
    {
        var file = await _dbContext.Archivos
            .FirstOrDefaultAsync(
                x =>
                    x.Id == id &&
                    x.Estado == EstadoArchivo.Activo,
                cancellationToken);

        if (file is null)
            return NotFound();

        if (!await _permissionService.HasPermissionAsync(
                CurrentUserId(),
                file.CarpetaId,
                PermisoCarpeta.Eliminar,
                cancellationToken))
        {
            return Forbid();
        }

        var now = DateTime.UtcNow;

        file.Estado = EstadoArchivo.EnPapelera;
        file.FechaEliminacion = now;
        file.FechaModificacion = now;
        file.EliminacionLoteId = Guid.NewGuid();
        file.Version++;

        await _syncChangeService.RegisterAsync(
            file.CarpetaId,
            file.Id,
            null,
            "FILE_DELETED",
            file.Version,
            cancellationToken: cancellationToken);

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        return NoContent();
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

public sealed record RenameFileRequest(string Name);