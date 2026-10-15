
using GestorArchivos.Application.Common.Security;
using GestorArchivos.Domain.Security;
using GestorArchivos.Infrastructure.Identity;
using GestorArchivos.Infrastructure.Identity.Seed;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GestorArchivos.Infrastructure.Security;

public sealed class FolderPermissionService : IFolderPermissionService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;

    public FolderPermissionService(
        ApplicationDbContext dbContext,
        UserManager<ApplicationUser> userManager)
    {
        _dbContext = dbContext;
        _userManager = userManager;
    }

    public async Task<bool> HasPermissionAsync(
        Guid userId,
        Guid folderId,
        PermisoCarpeta requiredPermission,
        CancellationToken cancellationToken = default)
    {
        var permissions = await GetEffectivePermissionsAsync(
            userId,
            folderId,
            cancellationToken);

        return (permissions & requiredPermission) == requiredPermission;
    }

    public async Task<bool> HasPermissionIncludingTrashAsync(
        Guid userId,
        Guid folderId,
        PermisoCarpeta requiredPermission,
        CancellationToken cancellationToken = default)
    {
        var permissions = await GetEffectivePermissionsIncludingTrashAsync(
            userId,
            folderId,
            cancellationToken);

        return (permissions & requiredPermission) == requiredPermission;
    }

    public Task<PermisoCarpeta> GetEffectivePermissionsAsync(
        Guid userId,
        Guid folderId,
        CancellationToken cancellationToken = default)
    {
        return GetEffectivePermissionsInternalAsync(
            userId,
            folderId,
            includeTrash: false,
            cancellationToken);
    }

    public Task<PermisoCarpeta> GetEffectivePermissionsIncludingTrashAsync(
        Guid userId,
        Guid folderId,
        CancellationToken cancellationToken = default)
    {
        return GetEffectivePermissionsInternalAsync(
            userId,
            folderId,
            includeTrash: true,
            cancellationToken);
    }

    private async Task<PermisoCarpeta> GetEffectivePermissionsInternalAsync(
        Guid userId,
        Guid folderId,
        bool includeTrash,
        CancellationToken cancellationToken)
    {
        var user = await _userManager.FindByIdAsync(
            userId.ToString());

        if (user is null || !user.Activo)
            return PermisoCarpeta.Ninguno;

        // Comprobar la existencia y el estado de la carpeta
        // antes de conceder permisos, incluso al administrador.
        var folderExists = await _dbContext.Carpetas
            .AsNoTracking()
            .AnyAsync(
                x => x.Id == folderId &&
                     (includeTrash || !x.EnPapelera),
                cancellationToken);

        if (!folderExists)
            return PermisoCarpeta.Ninguno;

        if (await _userManager.IsInRoleAsync(
                user,
                IdentitySeeder.AdministratorRole))
        {
            return PermisoCarpeta.ControlTotal;
        }

        var groupIds = await _dbContext.GruposUsuarios
            .AsNoTracking()
            .Where(x =>
                x.UsuarioId == userId &&
                x.Grupo.Activo)
            .Select(x => x.GrupoId)
            .ToListAsync(cancellationToken);

        var effectivePermissions = PermisoCarpeta.Ninguno;

        Guid? currentFolderId = folderId;
        var isDirectFolder = true;
        var visitedFolders = new HashSet<Guid>();

        while (currentFolderId.HasValue)
        {
            var currentId = currentFolderId.Value;

            if (!visitedFolders.Add(currentId))
            {
                throw new InvalidOperationException(
                    "Se detectó una referencia circular en la jerarquía de carpetas.");
            }

            var userPermission = await _dbContext
                .PermisosCarpetasUsuarios
                .AsNoTracking()
                .Where(x =>
                    x.CarpetaId == currentId &&
                    x.UsuarioId == userId &&
                    (isDirectFolder || x.HeredarSubcarpetas))
                .Select(x => (int?)x.Permisos)
                .FirstOrDefaultAsync(cancellationToken);

            if (userPermission.HasValue)
            {
                effectivePermissions |=
                    (PermisoCarpeta)userPermission.Value;
            }

            if (groupIds.Count > 0)
            {
                var groupPermissions = await _dbContext
                    .PermisosCarpetasGrupos
                    .AsNoTracking()
                    .Where(x =>
                        x.CarpetaId == currentId &&
                        groupIds.Contains(x.GrupoId) &&
                        (isDirectFolder || x.HeredarSubcarpetas))
                    .Select(x => (int)x.Permisos)
                    .ToListAsync(cancellationToken);

                foreach (var permission in groupPermissions)
                {
                    effectivePermissions |=
                        (PermisoCarpeta)permission;
                }
            }

            currentFolderId = await _dbContext.Carpetas
                .AsNoTracking()
                .Where(x => x.Id == currentId)
                .Select(x => x.CarpetaPadreId)
                .FirstOrDefaultAsync(cancellationToken);

            isDirectFolder = false;
        }

        return effectivePermissions;
    }
}
