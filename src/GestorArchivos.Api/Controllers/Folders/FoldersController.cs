using System.Security.Claims;
using GestorArchivos.Application.Common.Interfaces;
using GestorArchivos.Application.Common.Security;
using GestorArchivos.Application.Common.Validation;
using GestorArchivos.Domain.Entities;
using GestorArchivos.Domain.Enums;
using GestorArchivos.Domain.Security;
using GestorArchivos.Infrastructure.Identity;
using GestorArchivos.Infrastructure.Identity.Seed;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GestorArchivos.Api.Controllers.Folders;

[ApiController]
[Authorize]
[Route("api/folders")]
public class FoldersController : ControllerBase
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IFolderPermissionService _permissionService;
    private readonly ISyncChangeService _syncChangeService;

    public FoldersController(
        ApplicationDbContext dbContext,
        IFolderPermissionService permissionService,
        ISyncChangeService syncChangeService)
    {
        _dbContext = dbContext;
        _permissionService = permissionService;
        _syncChangeService = syncChangeService;
    }

    [HttpGet]
    public async Task<IActionResult> GetContents(
        [FromQuery] Guid? parentId,
        CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        var isAdmin = User.IsInRole(
            IdentitySeeder.AdministratorRole);

        if (parentId.HasValue)
        {
            var parentExists = await _dbContext.Carpetas
                .AsNoTracking()
                .AnyAsync(
                    x => x.Id == parentId.Value &&
                         !x.EnPapelera,
                    cancellationToken);

            if (!parentExists)
                return NotFound();

            if (!await _permissionService.HasPermissionAsync(
                    userId,
                    parentId.Value,
                    PermisoCarpeta.Leer,
                    cancellationToken))
            {
                return Forbid();
            }
        }

        var candidateFolders = await _dbContext.Carpetas
            .AsNoTracking()
            .Where(x =>
                x.CarpetaPadreId == parentId &&
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

        var visibleFolders = new List<object>();

        foreach (var folder in candidateFolders)
        {
            if (isAdmin ||
                await _permissionService.HasPermissionAsync(
                    userId,
                    folder.Id,
                    PermisoCarpeta.Leer,
                    cancellationToken))
            {
                visibleFolders.Add(folder);
            }
        }

        var files = parentId.HasValue
            ? await _dbContext.Archivos
                .AsNoTracking()
                .Where(x =>
                    x.CarpetaId == parentId.Value &&
                    x.Estado == EstadoArchivo.Activo)
                .OrderBy(x => x.Nombre)
                .Select(x => new
                {
                    x.Id,
                    x.Nombre,
                    x.Extension,
                    x.TipoContenido,
                    x.TamanoBytes,
                    x.Version,
                    x.FechaCreacion,
                    x.FechaModificacion
                })
                .ToListAsync(cancellationToken)
            : [];

        return Ok(new
        {
            parentId,
            folders = visibleFolders,
            files
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        var folder = await _dbContext.Carpetas
            .AsNoTracking()
            .Where(x =>
                x.Id == id &&
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

        if (!await _permissionService.HasPermissionAsync(
                CurrentUserId(),
                id,
                PermisoCarpeta.Leer,
                cancellationToken))
        {
            return Forbid();
        }

        return Ok(folder);
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        CreateFolderRequest request,
        CancellationToken cancellationToken)
    {
        if (!FileNameValidator.TryValidate(
                request.Name,
                out var name,
                out var validationError))
        {
            return BadRequest(new
            {
                message = validationError
            });
        }

        if (request.ParentId.HasValue)
        {
            var parentExists = await _dbContext.Carpetas
                .AsNoTracking()
                .AnyAsync(
                    x => x.Id == request.ParentId.Value &&
                         !x.EnPapelera,
                    cancellationToken);

            if (!parentExists)
            {
                return BadRequest(new
                {
                    message = "La carpeta padre no existe."
                });
            }

            if (!await _permissionService.HasPermissionAsync(
                    CurrentUserId(),
                    request.ParentId.Value,
                    PermisoCarpeta.Crear,
                    cancellationToken))
            {
                return Forbid();
            }
        }
        else if (!User.IsInRole(
                     IdentitySeeder.AdministratorRole))
        {
            return Forbid();
        }

        var duplicateExists = await _dbContext.Carpetas
            .AnyAsync(
                x =>
                    x.CarpetaPadreId == request.ParentId &&
                    x.Nombre == name &&
                    !x.EnPapelera,
                cancellationToken);

        if (duplicateExists)
        {
            return Conflict(new
            {
                message = "Ya existe una carpeta con ese nombre."
            });
        }

        var folder = new Carpeta
        {
            Nombre = name,
            CarpetaPadreId = request.ParentId,
            CreadoPorUsuarioId = CurrentUserId()
        };

        _dbContext.Carpetas.Add(folder);

        if (request.ParentId.HasValue)
        {
            await _syncChangeService.RegisterAsync(
                request.ParentId.Value,
                null,
                folder.Id,
                "FOLDER_CREATED",
                cancellationToken: cancellationToken);
        }

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        return CreatedAtAction(
            nameof(GetById),
            new { id = folder.Id },
            new
            {
                folder.Id,
                folder.Nombre,
                folder.CarpetaPadreId,
                folder.FechaCreacion
            });
    }

    [HttpPut("{id:guid}/rename")]
    public async Task<IActionResult> Rename(
        Guid id,
        RenameFolderRequest request,
        CancellationToken cancellationToken)
    {
        var folder = await _dbContext.Carpetas
            .FirstOrDefaultAsync(
                x =>
                    x.Id == id &&
                    !x.EnPapelera,
                cancellationToken);

        if (folder is null)
            return NotFound();

        if (!folder.CarpetaPadreId.HasValue)
            return BadRequest(new { message = "No se puede renombrar una carpeta raíz protegida." });

        if (await IsSelectorManagedRootAsync(id, cancellationToken))
            return Conflict(new { message = "Esta carpeta sincronizada se administra exclusivamente desde el selector de este dispositivo." });



        if (!await _permissionService.HasPermissionAsync(
                CurrentUserId(),
                id,
                PermisoCarpeta.Modificar,
                cancellationToken))
        {
            return Forbid();
        }

        if (!FileNameValidator.TryValidate(
                request.Name,
                out var name,
                out var validationError))
        {
            return BadRequest(new
            {
                message = validationError
            });
        }

        var duplicateExists = await _dbContext.Carpetas
            .AnyAsync(
                x =>
                    x.Id != id &&
                    x.CarpetaPadreId ==
                    folder.CarpetaPadreId &&
                    x.Nombre == name &&
                    !x.EnPapelera,
                cancellationToken);

        if (duplicateExists)
        {
            return Conflict(new
            {
                message = "Ya existe una carpeta con ese nombre."
            });
        }

        if (folder.Nombre == name)
            return NoContent();

        folder.Nombre = name;
        folder.FechaModificacion = DateTime.UtcNow;

        await _syncChangeService.RegisterAsync(
            folder.Id,
            null,
            folder.Id,
            "FOLDER_RENAMED",
            cancellationToken: cancellationToken);

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        return NoContent();
    }

    [HttpPut("{id:guid}/move")]
    public async Task<IActionResult> Move(Guid id, MoveFolderRequest request, CancellationToken cancellationToken)
    {
        var folder = await _dbContext.Carpetas.FirstOrDefaultAsync(x => x.Id == id && !x.EnPapelera, cancellationToken);
        if (folder is null) return NotFound();
        if (await IsSelectorManagedRootAsync(id, cancellationToken))
            return Conflict(new { message = "Esta carpeta sincronizada se administra exclusivamente desde el selector de este dispositivo." });

        if (!folder.CarpetaPadreId.HasValue) return BadRequest(new { message = "No se puede mover una carpeta raíz." });
        var destination = await _dbContext.Carpetas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.DestinationFolderId && !x.EnPapelera, cancellationToken);
        if (destination is null) return NotFound(new { message = "La carpeta de destino no existe." });
        if (folder.CarpetaPadreId == destination.Id) return NoContent();
        var sourceRoot = await FindRootAsync(folder.Id, cancellationToken);
        var destinationRoot = await FindRootAsync(destination.Id, cancellationToken);
        if (!sourceRoot.HasValue || sourceRoot != destinationRoot)
            return BadRequest(new { message = "No se permite mover elementos entre raíces distintas." });
        var userId = CurrentUserId();
        if (!await _permissionService.HasPermissionAsync(userId, folder.Id, PermisoCarpeta.Modificar, cancellationToken) ||
            !await _permissionService.HasPermissionAsync(userId, folder.CarpetaPadreId.Value, PermisoCarpeta.Modificar, cancellationToken) ||
            !await _permissionService.HasPermissionAsync(userId, destination.Id, PermisoCarpeta.Crear, cancellationToken)) return Forbid();
        Guid? ancestor = destination.Id;
        var visited = new HashSet<Guid>();
        while (ancestor.HasValue)
        {
            if (!visited.Add(ancestor.Value) || ancestor.Value == id)
                return BadRequest(new { message = "No se puede mover una carpeta dentro de sí misma o sus descendientes." });
            ancestor = await _dbContext.Carpetas.AsNoTracking().Where(x => x.Id == ancestor.Value).Select(x => x.CarpetaPadreId).FirstOrDefaultAsync(cancellationToken);
        }
        // Conservar ambos genera un nombre disponible sin alterar el elemento existente.
        // Omitir deja el origen intacto y permite continuar una selección múltiple.
        async Task<bool> ExistsAsync(string name) =>
            await _dbContext.Carpetas.AnyAsync(x => x.Id != id && x.CarpetaPadreId == destination.Id && x.Nombre == name && !x.EnPapelera, cancellationToken) ||
            await _dbContext.Archivos.AnyAsync(x => x.Id != id && x.CarpetaId == destination.Id && x.Nombre == name && x.Estado == EstadoArchivo.Activo, cancellationToken);

        var finalName = folder.Nombre;
        if (await ExistsAsync(finalName))
        {
            if (request.ConflictResolution == "skip")
                return Ok(new { skipped = true });

            if (request.ConflictResolution != "keepBoth")
                return Conflict(new { message = "Ya existe un elemento con ese nombre en el destino." });

            var extension = string.Empty;
            var stem = finalName;
            var found = false;
            for (var suffix = 1; suffix <= 10000; suffix++)
            {
                var candidate = $"{stem} ({suffix}){extension}";
                if (!FileNameValidator.TryValidate(candidate, out _, out _))
                    return Conflict(new { message = "No se pudo generar un nombre válido." });
                if (await ExistsAsync(candidate)) continue;
                finalName = candidate;
                found = true;
                break;
            }
            if (!found)
                return Conflict(new { message = "No hay un nombre disponible en el destino." });
        }
        folder.Nombre = finalName;
        var oldParentId = folder.CarpetaPadreId.Value;
        folder.CarpetaPadreId = destination.Id;
        folder.FechaModificacion = DateTime.UtcNow;
        await _syncChangeService.RegisterAsync(oldParentId, null, folder.Id, "FOLDER_MOVED", cancellationToken: cancellationToken);
        await _syncChangeService.RegisterAsync(destination.Id, null, folder.Id, "FOLDER_MOVED", cancellationToken: cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task<bool> IsSelectorManagedRootAsync(Guid folderId, CancellationToken cancellationToken)
    {
        return await (
            from item in _dbContext.ElementosSync.AsNoTracking()
            join device in _dbContext.DispositivosSync.AsNoTracking()
                on item.DispositivoId equals device.Id
            join folder in _dbContext.Carpetas.AsNoTracking()
                on item.CarpetaRemotaId equals folder.Id
            where item.Tipo == TipoElementoSync.Carpeta &&
                  folder.Id == folderId &&
                  folder.CarpetaPadreId == device.CarpetaRaizId
            select item.Id
        ).AnyAsync(cancellationToken);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> MoveToTrash(
        Guid id,
        CancellationToken cancellationToken)
    {
        var folder = await _dbContext.Carpetas
            .FirstOrDefaultAsync(
                x =>
                    x.Id == id &&
                    !x.EnPapelera,
                cancellationToken);

        if (folder is null)
            return NotFound();

        if (!folder.CarpetaPadreId.HasValue)
            return BadRequest(new { message = "No se puede enviar a papelera una carpeta raíz protegida." });

        if (await IsSelectorManagedRootAsync(id, cancellationToken))
            return Conflict(new { message = "Esta carpeta sincronizada se administra exclusivamente desde el selector de este dispositivo." });



        var userId = CurrentUserId();

        var folderIds = new HashSet<Guid>
        {
            folder.Id
        };

        var pendingIds = new List<Guid>
        {
            folder.Id
        };

        while (pendingIds.Count > 0)
        {
            var children = await _dbContext.Carpetas
                .AsNoTracking()
                .Where(x =>
                    x.CarpetaPadreId.HasValue &&
                    pendingIds.Contains(
                        x.CarpetaPadreId.Value) &&
                    !x.EnPapelera)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);

            pendingIds = children
                .Where(folderIds.Add)
                .ToList();
        }

        foreach (var folderId in folderIds)
        {
            if (!await _permissionService.HasPermissionAsync(
                    userId,
                    folderId,
                    PermisoCarpeta.Eliminar,
                    cancellationToken))
            {
                return Forbid();
            }
        }

        var folders = await _dbContext.Carpetas
            .Where(x =>
                folderIds.Contains(x.Id) &&
                !x.EnPapelera)
            .ToListAsync(cancellationToken);

        var files = await _dbContext.Archivos
            .Where(x =>
                folderIds.Contains(x.CarpetaId) &&
                x.Estado == EstadoArchivo.Activo)
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var deletionBatchId = Guid.NewGuid();

        foreach (var currentFolder in folders)
        {
            currentFolder.EnPapelera = true;
            currentFolder.FechaEliminacion = now;
            currentFolder.FechaModificacion = now;
            currentFolder.EliminacionLoteId =
                deletionBatchId;
        }

        foreach (var file in files)
        {
            file.Estado = EstadoArchivo.EnPapelera;
            file.FechaEliminacion = now;
            file.FechaModificacion = now;
            file.EliminacionLoteId =
                deletionBatchId;
            file.Version++;
        }

        await _syncChangeService.RegisterAsync(
            folder.Id,
            null,
            folder.Id,
            "FOLDER_DELETED",
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
    private async Task<Guid?> FindRootAsync(Guid folderId, CancellationToken cancellationToken)
    {
        var visited = new HashSet<Guid>();
        var current = folderId;
        while (visited.Add(current))
        {
            var folder = await _dbContext.Carpetas.AsNoTracking()
                .Where(x => x.Id == current && !x.EnPapelera)
                .Select(x => new { x.Id, x.CarpetaPadreId })
                .FirstOrDefaultAsync(cancellationToken);
            if (folder is null) return null;
            if (!folder.CarpetaPadreId.HasValue) return folder.Id;
            current = folder.CarpetaPadreId.Value;
        }
        return null;
    }

}

public sealed record CreateFolderRequest(
    string Name,
    Guid? ParentId);

public sealed record RenameFolderRequest(
    string Name);
public sealed record MoveFolderRequest(Guid DestinationFolderId, string? ConflictResolution = null);
