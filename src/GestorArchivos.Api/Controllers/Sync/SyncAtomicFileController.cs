using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using GestorArchivos.Api.Security;
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

namespace GestorArchivos.Api.Controllers.Sync;

[ApiController]
[Authorize(AuthenticationSchemes = SyncDeviceAuthenticationHandler.SchemeName)]
[Route("api/sync/files/atomic")]
public sealed class SyncAtomicFileController : ControllerBase
{
    private const long MaxFileSize = 4L * 1024 * 1024 * 1024;

    private readonly ApplicationDbContext _db;
    private readonly ISyncChangeService _changes;
    private readonly IFolderPermissionService _permissions;
    private readonly IFileStorage _storage;

    public SyncAtomicFileController(
        ApplicationDbContext db,
        ISyncChangeService changes,
        IFolderPermissionService permissions,
        IFileStorage storage)
    {
        _db = db;
        _changes = changes;
        _permissions = permissions;
        _storage = storage;
    }

    [HttpPost]
    [RequestSizeLimit(MaxFileSize + 1024 * 1024)]
    public async Task<IActionResult> Create(
        [FromForm] Guid folderId,
        [FromForm] string localPath,
        [FromForm] IFormFile file,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(
                User.FindFirstValue("device_id"),
                out var deviceId) ||
            !Guid.TryParse(
                User.FindFirstValue(ClaimTypes.NameIdentifier),
                out var userId))
        {
            return Unauthorized();
        }

        if (folderId == Guid.Empty ||
            string.IsNullOrWhiteSpace(localPath) ||
            localPath.Length > 2048 ||
            file is null ||
            file.Length < 0 ||
            file.Length > MaxFileSize)
        {
            return BadRequest(new
            {
                message = "Datos de archivo inválidos."
            });
        }

        if (!FileNameValidator.TryValidate(
                file.FileName,
                out var fileName,
                out var validationError))
        {
            return BadRequest(new { message = validationError });
        }

        var device = await _db.DispositivosSync
            .AsNoTracking()
            .Where(x =>
                x.Id == deviceId &&
                x.UsuarioId == userId &&
                x.Activo)
            .Select(x => new { x.CarpetaRaizId })
            .FirstOrDefaultAsync(cancellationToken);

        if (device is null)
            return Unauthorized();

        if (!await BelongsToRootAsync(
                folderId,
                device.CarpetaRaizId,
                cancellationToken))
        {
            return Forbid();
        }

        if (!await _permissions.HasPermissionAsync(
                userId,
                folderId,
                PermisoCarpeta.Crear,
                cancellationToken))
        {
            return Forbid();
        }

        var folderExists = await _db.Carpetas
            .AsNoTracking()
            .AnyAsync(
                x => x.Id == folderId && !x.EnPapelera,
                cancellationToken);

        if (!folderExists)
            return NotFound();

        await using var transaction =
            await _db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

        var existingItem = await _db.ElementosSync
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.DispositivoId == deviceId &&
                     x.RutaLocal == localPath &&
                     x.Activo,
                cancellationToken);

        if (existingItem is not null)
        {
            if (!existingItem.Activo ||
                existingItem.Tipo != TipoElementoSync.Archivo ||
                !existingItem.ArchivoRemotoId.HasValue)
            {
                return Conflict(new
                {
                    message = "La ruta local ya tiene otra asociación."
                });
            }

            var existingFile = await _db.Archivos
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.Id == existingItem.ArchivoRemotoId.Value &&
                         x.Estado == EstadoArchivo.Activo,
                    cancellationToken);

            if (existingFile is null ||
                existingFile.CarpetaId != folderId ||
                existingFile.Nombre != fileName)
            {
                return Conflict(new
                {
                    message = "La asociación existente no coincide."
                });
            }

            return Ok(new
            {
                syncItemId = existingItem.Id,
                remoteFileId = existingFile.Id,
                existingFile.Nombre,
                existingFile.CarpetaId,
                existingFile.HashSha256,
                existingFile.Version,
                alreadyExisted = true
            });
        }

        var duplicate = await _db.Archivos
            .AsNoTracking()
            .AnyAsync(
                x => x.CarpetaId == folderId &&
                     x.Nombre == fileName &&
                     x.Estado == EstadoArchivo.Activo,
                cancellationToken);

        if (duplicate)
        {
            return Conflict(new
            {
                message = "Ya existe un archivo remoto con ese nombre."
            });
        }

        var fileId = Guid.NewGuid();
        var extension = Path.GetExtension(fileName);

        var now = DateTime.UtcNow;

        var relativePath = Path.Combine(
            now.ToString("yyyy"),
            now.ToString("MM"),
            $"{fileId:N}{extension}");

        var physicalSaveAttempted = false;
        var committed = false;

        try
        {
            string hash;

            await using (var hashStream = file.OpenReadStream())
            {
                hash = Convert.ToHexString(
                    await SHA256.HashDataAsync(
                        hashStream,
                        cancellationToken));
            }

            await using (var contentStream = file.OpenReadStream())
            {
                physicalSaveAttempted = true;

                await _storage.SaveAsync(
                    relativePath,
                    contentStream,
                    cancellationToken);
            }

            var remoteFile = new Archivo
            {
                Id = fileId,
                Nombre = fileName,
                NombreFisico = relativePath,
                Extension = extension,
                TipoContenido =
                    string.IsNullOrWhiteSpace(file.ContentType)
                        ? "application/octet-stream"
                        : file.ContentType,
                TamanoBytes = file.Length,
                HashSha256 = hash,
                Version = 1,
                CarpetaId = folderId,
                CreadoPorUsuarioId = userId
            };

            var syncItem = new ElementoSync
            {
                DispositivoId = deviceId,
                RutaLocal = localPath,
                Tipo = TipoElementoSync.Archivo,
                ArchivoRemotoId = fileId
            };

            _db.Archivos.Add(remoteFile);
            _db.ElementosSync.Add(syncItem);

            await _changes.RegisterAsync(
                folderId,
                fileId,
                null,
                "FILE_CREATED",
                1,
                deviceId,
                cancellationToken);

            await _db.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            committed = true;

            return Ok(new
            {
                syncItemId = syncItem.Id,
                remoteFileId = remoteFile.Id,
                remoteFile.Nombre,
                remoteFile.CarpetaId,
                remoteFile.HashSha256,
                remoteFile.Version,
                alreadyExisted = false
            });
        }
        catch
        {
            if (!committed)
            {
                try
                {
                    await transaction.RollbackAsync(
                        CancellationToken.None);
                }
                catch
                {
                    // Conservar la excepción original.
                }

                if (physicalSaveAttempted)
                {
                    try
                    {
                        await _storage.DeleteAsync(
                            relativePath,
                            CancellationToken.None);
                    }
                    catch
                    {
                        // Requiere limpieza posterior del archivo huérfano.
                    }
                }
            }

            throw;
        }
    }

    private async Task<bool> BelongsToRootAsync(
        Guid folderId,
        Guid rootId,
        CancellationToken cancellationToken)
    {
        Guid? current = folderId;
        var visited = new HashSet<Guid>();

        while (current.HasValue)
        {
            if (!visited.Add(current.Value))
                return false;

            var folder = await _db.Carpetas
                .AsNoTracking()
                .Where(x => x.Id == current.Value && !x.EnPapelera)
                .Select(x => new
                {
                    x.Id,
                    x.CarpetaPadreId
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (folder is null)
                return false;

            if (folder.Id == rootId)
                return true;

            current = folder.CarpetaPadreId;
        }

        return false;
    }
}