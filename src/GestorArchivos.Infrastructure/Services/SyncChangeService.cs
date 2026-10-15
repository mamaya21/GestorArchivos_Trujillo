using GestorArchivos.Application.Common.Interfaces;
using GestorArchivos.Domain.Entities;
using GestorArchivos.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace GestorArchivos.Infrastructure.Services;

public sealed class SyncChangeService : ISyncChangeService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ICurrentUserService _currentUserService;

    public SyncChangeService(
        ApplicationDbContext dbContext,
        ICurrentUserService currentUserService)
    {
        _dbContext = dbContext;
        _currentUserService = currentUserService;
    }

    public async Task RegisterAsync(
        Guid folderId,
        Guid? fileId,
        Guid? changedFolderId,
        string changeType,
        long? fileVersion = null,
        Guid? sourceDeviceId = null,
        CancellationToken cancellationToken = default)
    {
        var userId = _currentUserService.UserId;

        if (!userId.HasValue)
            return;

        var rootFolderIds = await FindSyncRootsAsync(
            folderId,
            sourceDeviceId,
            cancellationToken);

        foreach (var rootFolderId in rootFolderIds)
        {
            _dbContext.CambiosSync.Add(new CambioSync
            {
                CarpetaRaizId = rootFolderId,
                ArchivoId = fileId,
                CarpetaId = changedFolderId,
                UsuarioId = userId.Value,
                DispositivoOrigenId = sourceDeviceId,
                TipoCambio = changeType,
                VersionArchivo = fileVersion,
                FechaUtc = DateTime.UtcNow
            });
        }
    }

    private async Task<List<Guid>> FindSyncRootsAsync(
        Guid folderId,
        Guid? sourceDeviceId,
        CancellationToken cancellationToken)
    {
        if (sourceDeviceId.HasValue)
        {
            var sourceRootId = await _dbContext.DispositivosSync
                .AsNoTracking()
                .Where(x =>
                    x.Id == sourceDeviceId.Value &&
                    x.Activo)
                .Select(x => (Guid?)x.CarpetaRaizId)
                .FirstOrDefaultAsync(cancellationToken);

            if (!sourceRootId.HasValue)
                return [];

            return await IsFolderUnderRootAsync(
                    folderId,
                    sourceRootId.Value,
                    cancellationToken)
                ? [sourceRootId.Value]
                : [];
        }

        var roots = await _dbContext.DispositivosSync
            .AsNoTracking()
            .Where(x => x.Activo)
            .Select(x => x.CarpetaRaizId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var result = new List<Guid>();

        foreach (var rootId in roots)
        {
            if (await IsFolderUnderRootAsync(
                    folderId,
                    rootId,
                    cancellationToken))
            {
                result.Add(rootId);
            }
        }

        return result;
    }

    private async Task<bool> IsFolderUnderRootAsync(
        Guid folderId,
        Guid rootFolderId,
        CancellationToken cancellationToken)
    {
        Guid? currentId = folderId;
        var visited = new HashSet<Guid>();

        while (currentId.HasValue)
        {
            if (!visited.Add(currentId.Value))
                return false;

            if (currentId.Value == rootFolderId)
                return true;

            currentId = await _dbContext.Carpetas
                .AsNoTracking()
                .Where(x => x.Id == currentId.Value)
                .Select(x => x.CarpetaPadreId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return false;
    }
}