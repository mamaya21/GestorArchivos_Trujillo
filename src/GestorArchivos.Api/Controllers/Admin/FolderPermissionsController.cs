using System.Security.Claims;
using GestorArchivos.Application.Common.Security;
using GestorArchivos.Domain.Entities;
using GestorArchivos.Domain.Security;
using GestorArchivos.Infrastructure.Identity;
using GestorArchivos.Infrastructure.Identity.Seed;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GestorArchivos.Api.Controllers.Admin;

[ApiController]
[Authorize(Roles = IdentitySeeder.AdministratorRole)]
[Route("api/admin/folders/{folderId:guid}/permissions")]
public sealed class FolderPermissionsController : ControllerBase
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IFolderPermissionService _permissionService;

    public FolderPermissionsController(
        ApplicationDbContext dbContext,
        IFolderPermissionService permissionService)
    {
        _dbContext = dbContext;
        _permissionService = permissionService;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        Guid folderId,
        CancellationToken cancellationToken)
    {
        if (!await FolderExists(folderId, cancellationToken))
            return NotFound();

        var users = await _dbContext.PermisosCarpetasUsuarios
            .AsNoTracking()
            .Where(x => x.CarpetaId == folderId)
            .Select(x => new
            {
                x.Id,
                x.UsuarioId,
                x.Permisos,
                x.HeredarSubcarpetas
            })
            .ToListAsync(cancellationToken);

        var groups = await _dbContext.PermisosCarpetasGrupos
            .AsNoTracking()
            .Where(x => x.CarpetaId == folderId)
            .Select(x => new
            {
                x.Id,
                x.GrupoId,
                x.Permisos,
                x.HeredarSubcarpetas
            })
            .ToListAsync(cancellationToken);

        return Ok(new { users, groups });
    }

    [HttpPut("users/{userId:guid}")]
    public async Task<IActionResult> SetUserPermission(
        Guid folderId,
        Guid userId,
        SetPermissionRequest request,
        CancellationToken cancellationToken)
    {
        if (!await FolderExists(folderId, cancellationToken))
            return NotFound();

        if (!IsValidPermission(request.Permissions))
        {
            return BadRequest(new
            {
                message = "Permisos inválidos."
            });
        }

        var userExists = await _dbContext.Users
            .AsNoTracking()
            .AnyAsync(
                x => x.Id == userId && x.Activo,
                cancellationToken);

        if (!userExists)
        {
            return BadRequest(new
            {
                message = "El usuario no existe o está inactivo."
            });
        }

        var permission =
            await _dbContext.PermisosCarpetasUsuarios
                .FirstOrDefaultAsync(
                    x =>
                        x.CarpetaId == folderId &&
                        x.UsuarioId == userId,
                    cancellationToken);

        if (permission is null)
        {
            permission = new PermisoCarpetaUsuario
            {
                CarpetaId = folderId,
                UsuarioId = userId,
                CreadoPorUsuarioId = CurrentUserId()
            };

            _dbContext.PermisosCarpetasUsuarios.Add(
                permission);
        }

        permission.Permisos = request.Permissions;
        permission.HeredarSubcarpetas =
            request.InheritToSubfolders;

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        return NoContent();
    }

    [HttpPut("groups/{groupId:guid}")]
    public async Task<IActionResult> SetGroupPermission(
        Guid folderId,
        Guid groupId,
        SetPermissionRequest request,
        CancellationToken cancellationToken)
    {
        if (!await FolderExists(folderId, cancellationToken))
            return NotFound();

        if (!IsValidPermission(request.Permissions))
        {
            return BadRequest(new
            {
                message = "Permisos inválidos."
            });
        }

        var groupExists = await _dbContext.Grupos
            .AsNoTracking()
            .AnyAsync(
                x => x.Id == groupId && x.Activo,
                cancellationToken);

        if (!groupExists)
        {
            return BadRequest(new
            {
                message = "El grupo no existe o está inactivo."
            });
        }

        var permission =
            await _dbContext.PermisosCarpetasGrupos
                .FirstOrDefaultAsync(
                    x =>
                        x.CarpetaId == folderId &&
                        x.GrupoId == groupId,
                    cancellationToken);

        if (permission is null)
        {
            permission = new PermisoCarpetaGrupo
            {
                CarpetaId = folderId,
                GrupoId = groupId,
                CreadoPorUsuarioId = CurrentUserId()
            };

            _dbContext.PermisosCarpetasGrupos.Add(
                permission);
        }

        permission.Permisos = request.Permissions;
        permission.HeredarSubcarpetas =
            request.InheritToSubfolders;

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        return NoContent();
    }

    [HttpDelete("users/{userId:guid}")]
    public async Task<IActionResult> DeleteUserPermission(
        Guid folderId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var permission =
            await _dbContext.PermisosCarpetasUsuarios
                .FirstOrDefaultAsync(
                    x =>
                        x.CarpetaId == folderId &&
                        x.UsuarioId == userId,
                    cancellationToken);

        if (permission is null)
            return NotFound();

        _dbContext.Remove(permission);

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        return NoContent();
    }

    [HttpDelete("groups/{groupId:guid}")]
    public async Task<IActionResult> DeleteGroupPermission(
        Guid folderId,
        Guid groupId,
        CancellationToken cancellationToken)
    {
        var permission =
            await _dbContext.PermisosCarpetasGrupos
                .FirstOrDefaultAsync(
                    x =>
                        x.CarpetaId == folderId &&
                        x.GrupoId == groupId,
                    cancellationToken);

        if (permission is null)
            return NotFound();

        _dbContext.Remove(permission);

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        return NoContent();
    }

    [HttpGet("effective/{userId:guid}")]
    public async Task<IActionResult> Effective(
        Guid folderId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (!await FolderExists(
                folderId,
                cancellationToken))
        {
            return NotFound();
        }

        var userExists = await _dbContext.Users
            .AsNoTracking()
            .AnyAsync(
                x => x.Id == userId && x.Activo,
                cancellationToken);

        if (!userExists)
        {
            return NotFound(new
            {
                message = "El usuario no existe o está inactivo."
            });
        }

        var permissions =
            await _permissionService
                .GetEffectivePermissionsAsync(
                    userId,
                    folderId,
                    cancellationToken);

        return Ok(new
        {
            folderId,
            userId,
            permissions,
            value = (int)permissions
        });
    }

    private async Task<bool> FolderExists(
        Guid folderId,
        CancellationToken cancellationToken)
    {
        return await _dbContext.Carpetas
            .AsNoTracking()
            .AnyAsync(
                x =>
                    x.Id == folderId &&
                    !x.EnPapelera,
                cancellationToken);
    }

    private static bool IsValidPermission(
        PermisoCarpeta permission)
    {
        return
            (permission &
             ~PermisoCarpeta.ControlTotal) == 0;
    }

    private Guid CurrentUserId()
    {
        var value =
            User.FindFirstValue(
                ClaimTypes.NameIdentifier);

        if (!Guid.TryParse(value, out var userId))
            throw new UnauthorizedAccessException();

        return userId;
    }
}

public sealed record SetPermissionRequest(
    PermisoCarpeta Permissions,
    bool InheritToSubfolders);