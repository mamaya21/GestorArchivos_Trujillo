using GestorArchivos.Domain.Security;

namespace GestorArchivos.Application.Common.Security;

public interface IFolderPermissionService
{
    Task<PermisoCarpeta> GetEffectivePermissionsAsync(
        Guid userId,
        Guid folderId,
        CancellationToken cancellationToken = default);

    Task<PermisoCarpeta> GetEffectivePermissionsIncludingTrashAsync(
        Guid userId,
        Guid folderId,
        CancellationToken cancellationToken = default);

    Task<bool> HasPermissionAsync(
        Guid userId,
        Guid folderId,
        PermisoCarpeta requiredPermission,
        CancellationToken cancellationToken = default);

    Task<bool> HasPermissionIncludingTrashAsync(
        Guid userId,
        Guid folderId,
        PermisoCarpeta requiredPermission,
        CancellationToken cancellationToken = default);
}