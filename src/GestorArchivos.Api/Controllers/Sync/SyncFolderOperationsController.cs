using System.Security.Claims;
using System.Text;
using GestorArchivos.Api.Security;
using GestorArchivos.Application.Common.Interfaces;
using GestorArchivos.Application.Common.Security;
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
[Route("api/sync/folders")]
public class SyncFolderOperationsController : ControllerBase
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ISyncChangeService _syncChangeService;
    private readonly IFolderPermissionService _permissionService;

    public SyncFolderOperationsController(
        ApplicationDbContext dbContext,
        ISyncChangeService syncChangeService,
        IFolderPermissionService permissionService)
    {
        _dbContext = dbContext;
        _syncChangeService = syncChangeService;
        _permissionService = permissionService;
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        CreateSyncFolderRequest request,
        CancellationToken cancellationToken)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();

        if (!TryValidateFolderName(
                request.Name,
                out var name,
                out var error))
        {
            return BadRequest(new { message = error });
        }

        if (!await BelongsToDeviceAsync(
                request.ParentFolderId,
                deviceId,
                userId,
                cancellationToken))
            return Forbid();

        if (!await _permissionService.HasPermissionAsync(
                userId,
                request.ParentFolderId,
                PermisoCarpeta.Crear,
                cancellationToken))
            return Forbid();

        var parentExists = await _dbContext.Carpetas
            .AsNoTracking()
            .AnyAsync(
                x => x.Id == request.ParentFolderId &&
                     !x.EnPapelera,
                cancellationToken);

        if (!parentExists)
            return NotFound(new
            {
                message = "La carpeta padre no existe."
            });

        var duplicate = await _dbContext.Carpetas
            .AsNoTracking()
            .AnyAsync(
                x => x.CarpetaPadreId == request.ParentFolderId &&
                     x.Nombre == name &&
                     !x.EnPapelera,
                cancellationToken);

        if (duplicate)
            return Conflict(new
            {
                message = "Ya existe una carpeta con ese nombre."
            });

        var folder = new Carpeta
        {
            Nombre = name,
            CarpetaPadreId = request.ParentFolderId,
            CreadoPorUsuarioId = userId
        };

        _dbContext.Carpetas.Add(folder);

        await _syncChangeService.RegisterAsync(
            request.ParentFolderId,
            null,
            folder.Id,
            "FOLDER_CREATED",
            sourceDeviceId: deviceId,
            cancellationToken: cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            folder.Id,
            folder.Nombre,
            folder.CarpetaPadreId,
            folder.FechaCreacion
        });
    }

    [HttpPut("{folderId:guid}/rename")]
    public async Task<IActionResult> Rename(
        Guid folderId,
        RenameSyncFolderRequest request,
        CancellationToken cancellationToken)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();

        var folder = await _dbContext.Carpetas
            .FirstOrDefaultAsync(
                x => x.Id == folderId &&
                     !x.EnPapelera,
                cancellationToken);

        if (folder is null)
            return NotFound();

        if (!folder.CarpetaPadreId.HasValue ||
            !await BelongsToDeviceAsync(
                folder.Id,
                deviceId,
                userId,
                cancellationToken))
            return Forbid();

        if (!await _permissionService.HasPermissionAsync(
                userId,
                folder.Id,
                PermisoCarpeta.Modificar,
                cancellationToken))
            return Forbid();

        if (!TryValidateFolderName(
                request.Name,
                out var name,
                out var error))
        {
            return BadRequest(new { message = error });
        }

        if (folder.Nombre == name)
            return NoContent();

        var duplicate = await _dbContext.Carpetas
            .AsNoTracking()
            .AnyAsync(
                x => x.Id != folderId &&
                     x.CarpetaPadreId == folder.CarpetaPadreId &&
                     x.Nombre == name &&
                     !x.EnPapelera,
                cancellationToken);

        if (duplicate)
            return Conflict(new
            {
                message = "Ya existe una carpeta con ese nombre."
            });

        folder.Nombre = name;
        folder.FechaModificacion = DateTime.UtcNow;

        await _syncChangeService.RegisterAsync(
            folder.Id,
            null,
            folder.Id,
            "FOLDER_RENAMED",
            sourceDeviceId: deviceId,
            cancellationToken: cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            folder.Id,
            folder.Nombre,
            folder.CarpetaPadreId,
            folder.FechaModificacion
        });
    }

    [HttpPut("{folderId:guid}/move")]
    public async Task<IActionResult> Move(
        Guid folderId,
        MoveSyncFolderRequest request,
        CancellationToken cancellationToken)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();

        var folder = await _dbContext.Carpetas
            .FirstOrDefaultAsync(
                x => x.Id == folderId &&
                     !x.EnPapelera,
                cancellationToken);

        if (folder is null)
            return NotFound();

        if (!folder.CarpetaPadreId.HasValue ||
            !await BelongsToDeviceAsync(
                folder.Id,
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

        var oldParentId = folder.CarpetaPadreId.Value;

        if (!await _permissionService.HasPermissionAsync(
                userId,
                oldParentId,
                PermisoCarpeta.Modificar,
                cancellationToken))
            return Forbid();

        if (!await _permissionService.HasPermissionAsync(
                userId,
                request.DestinationFolderId,
                PermisoCarpeta.Crear,
                cancellationToken))
            return Forbid();

        if (folder.Id == request.DestinationFolderId)
        {
            return BadRequest(new
            {
                message = "Una carpeta no puede moverse dentro de sí misma."
            });
        }

        if (oldParentId == request.DestinationFolderId)
            return NoContent();

        if (await IsDescendantAsync(
                request.DestinationFolderId,
                folder.Id,
                cancellationToken))
        {
            return BadRequest(new
            {
                message = "Una carpeta no puede moverse dentro de uno de sus descendientes."
            });
        }

        var duplicate = await _dbContext.Carpetas
            .AsNoTracking()
            .AnyAsync(
                x => x.Id != folderId &&
                     x.CarpetaPadreId == request.DestinationFolderId &&
                     x.Nombre == folder.Nombre &&
                     !x.EnPapelera,
                cancellationToken);

        if (duplicate)
            return Conflict(new
            {
                message = "Ya existe una carpeta con ese nombre en el destino."
            });

        folder.CarpetaPadreId = request.DestinationFolderId;
        folder.FechaModificacion = DateTime.UtcNow;

        await _syncChangeService.RegisterAsync(
            oldParentId,
            null,
            folder.Id,
            "FOLDER_MOVED",
            sourceDeviceId: deviceId,
            cancellationToken: cancellationToken);

        await _syncChangeService.RegisterAsync(
            request.DestinationFolderId,
            null,
            folder.Id,
            "FOLDER_MOVED",
            sourceDeviceId: deviceId,
            cancellationToken: cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            folder.Id,
            folder.Nombre,
            folder.CarpetaPadreId,
            folder.FechaModificacion
        });
    }

    [HttpDelete("{folderId:guid}")]
    public async Task<IActionResult> Delete(
        Guid folderId,
        CancellationToken cancellationToken)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();

        var folder = await _dbContext.Carpetas
            .FirstOrDefaultAsync(
                x => x.Id == folderId &&
                     !x.EnPapelera,
                cancellationToken);

        if (folder is null)
            return NotFound();

        if (!folder.CarpetaPadreId.HasValue ||
            !await BelongsToDeviceAsync(
                folder.Id,
                deviceId,
                userId,
                cancellationToken))
            return Forbid();

        if (!await _permissionService.HasPermissionAsync(
                userId,
                folder.Id,
                PermisoCarpeta.Eliminar,
                cancellationToken))
            return Forbid();

        var now = DateTime.UtcNow;
        var batchId = Guid.NewGuid();

        var folders = await GetSubtreeAsync(
            folder.Id,
            cancellationToken);

        var folderIds = folders
            .Select(x => x.Id)
            .ToList();

        var files = await _dbContext.Archivos
            .Where(x =>
                folderIds.Contains(x.CarpetaId) &&
                x.Estado == EstadoArchivo.Activo)
            .ToListAsync(cancellationToken);

        foreach (var currentFolder in folders)
        {
            currentFolder.EnPapelera = true;
            currentFolder.FechaEliminacion = now;
            currentFolder.FechaModificacion = now;
            currentFolder.EliminacionLoteId = batchId;
        }

        foreach (var file in files)
        {
            file.Estado = EstadoArchivo.EnPapelera;
            file.FechaEliminacion = now;
            file.FechaModificacion = now;
            file.EliminacionLoteId = batchId;
            file.Version++;
        }

        var fileIds = files
            .Select(x => x.Id)
            .ToList();

        if (fileIds.Count > 0)
        {
            var locks = await _dbContext.BloqueosArchivos
                .Where(x =>
                    fileIds.Contains(x.ArchivoId) &&
                    x.Activo)
                .ToListAsync(cancellationToken);

            foreach (var fileLock in locks)
                fileLock.Activo = false;
        }

        await _syncChangeService.RegisterAsync(
            folder.Id,
            null,
            folder.Id,
            "FOLDER_DELETED",
            sourceDeviceId: deviceId,
            cancellationToken: cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    private async Task<List<Carpeta>> GetSubtreeAsync(
        Guid rootId,
        CancellationToken cancellationToken)
    {
        var result = new List<Carpeta>();
        var pending = new Queue<Guid>();
        var visited = new HashSet<Guid>();

        pending.Enqueue(rootId);

        while (pending.Count > 0)
        {
            var currentId = pending.Dequeue();

            if (!visited.Add(currentId))
                continue;

            var current = await _dbContext.Carpetas
                .FirstOrDefaultAsync(
                    x => x.Id == currentId &&
                         !x.EnPapelera,
                    cancellationToken);

            if (current is null)
                continue;

            result.Add(current);

            var children = await _dbContext.Carpetas
                .AsNoTracking()
                .Where(x =>
                    x.CarpetaPadreId == currentId &&
                    !x.EnPapelera)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);

            foreach (var childId in children)
                pending.Enqueue(childId);
        }

        return result;
    }

    private async Task<bool> IsDescendantAsync(
        Guid folderId,
        Guid possibleAncestorId,
        CancellationToken cancellationToken)
    {
        Guid? currentId = folderId;
        var visited = new HashSet<Guid>();

        while (currentId.HasValue)
        {
            if (!visited.Add(currentId.Value))
                return true;

            if (currentId.Value == possibleAncestorId)
                return true;

            currentId = await _dbContext.Carpetas
                .AsNoTracking()
                .Where(x => x.Id == currentId.Value)
                .Select(x => x.CarpetaPadreId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return false;
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

    private static bool TryValidateFolderName(
        string? value,
        out string normalizedName,
        out string error)
    {
        normalizedName = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "El nombre es obligatorio.";
            return false;
        }

        if (value.StartsWith(' ') ||
            value.EndsWith(' ') ||
            value.EndsWith('.'))
        {
            error = "El nombre no puede comenzar o terminar en espacio ni terminar en punto.";
            return false;
        }

        normalizedName = value.Normalize(
            NormalizationForm.FormC);

        if (normalizedName.Length > 255)
        {
            error = "El nombre supera los 255 caracteres.";
            return false;
        }

        if (normalizedName is "." or "..")
        {
            error = "El nombre no es válido.";
            return false;
        }

        var invalidCharacters =
            new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

        if (normalizedName.IndexOfAny(invalidCharacters) >= 0 ||
            normalizedName.Any(char.IsControl))
        {
            error = "El nombre contiene caracteres no permitidos.";
            return false;
        }

        return true;
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

public sealed record CreateSyncFolderRequest(
    Guid ParentFolderId,
    string Name);

public sealed record RenameSyncFolderRequest(string Name);

public sealed record MoveSyncFolderRequest(
    Guid DestinationFolderId);