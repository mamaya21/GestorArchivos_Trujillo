using System.Security.Claims;
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
[Route("api/sync")]
public class SyncController : ControllerBase
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IFolderPermissionService _permissionService;
    private readonly ISyncChangeService _changes;

    public SyncController(
        ApplicationDbContext dbContext,
        IFolderPermissionService permissionService,
        ISyncChangeService changes)
    {
        _dbContext = dbContext;
        _permissionService = permissionService;
        _changes = changes;
    }

    [HttpGet("changes")]
    public async Task<IActionResult> GetChanges(
        [FromQuery] long afterId = 0,
        [FromQuery] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();

        if (afterId < 0)
            return BadRequest(new { message = "afterId no puede ser negativo." });

        limit = Math.Clamp(limit, 1, 500);

        var device = await GetDeviceAsync(
            deviceId,
            userId,
            cancellationToken);

        if (device is null)
            return Unauthorized();

        if (!await _permissionService.HasPermissionAsync(
                userId,
                device.RootFolderId,
                PermisoCarpeta.Leer,
                cancellationToken))
            return Forbid();

        var changes = await _dbContext.CambiosSync
            .AsNoTracking()
            .Where(x =>
                x.CarpetaRaizId == device.RootFolderId &&
                x.Id > afterId &&
                (!x.DispositivoOrigenId.HasValue ||
                 x.DispositivoOrigenId.Value != deviceId))
            .OrderBy(x => x.Id)
            .Take(limit + 1)
            .Select(x => new
            {
                x.Id,
                x.ArchivoId,
                x.CarpetaId,
                x.TipoCambio,
                x.VersionArchivo,
                x.DispositivoOrigenId,
                x.FechaUtc
            })
            .ToListAsync(cancellationToken);

        var hasMore = changes.Count > limit;

        if (hasMore)
            changes.RemoveAt(changes.Count - 1);

        return Ok(new
        {
            changes,
            lastId = changes.Count > 0
                ? changes[^1].Id
                : afterId,
            hasMore
        });
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status(
        CancellationToken cancellationToken)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();

        var device = await _dbContext.DispositivosSync
            .AsNoTracking()
            .Where(x =>
                x.Id == deviceId &&
                x.UsuarioId == userId &&
                x.Activo)
            .Select(x => new
            {
                x.Id,
                x.Nombre,
                x.SistemaOperativo,
                x.CarpetaRaizId,
                x.UltimaConexionUtc
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (device is null)
            return Unauthorized();

        return Ok(device);
    }

    [HttpGet("items")]
    public async Task<IActionResult> GetItems(
        CancellationToken cancellationToken)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();

        var device = await GetDeviceAsync(
            deviceId,
            userId,
            cancellationToken);

        if (device is null)
            return Unauthorized();

        if (!await _permissionService.HasPermissionAsync(
                userId,
                device.RootFolderId,
                PermisoCarpeta.Leer,
                cancellationToken))
            return Forbid();

        var items = await _dbContext.ElementosSync
            .AsNoTracking()
            .Where(x =>
                x.DispositivoId == deviceId &&
                x.Activo)
            .OrderBy(x => x.RutaLocal)
            .Select(x => new
            {
                syncItemId = x.Id,
                x.RutaLocal,
                x.Tipo,
                x.CarpetaRemotaId,
                x.ArchivoRemotoId,
                x.FechaCreacion,
                x.UltimaSincronizacionUtc
            })
            .ToListAsync(cancellationToken);

        return Ok(items);
    }

    [HttpPost("items")]
    public async Task<IActionResult> RegisterItem(
        RegisterSyncItemRequest request,
        CancellationToken cancellationToken)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();

        if (string.IsNullOrWhiteSpace(request.LocalPath))
        {
            return BadRequest(new
            {
                message = "La ruta local es obligatoria."
            });
        }

        if (!Enum.IsDefined(typeof(TipoElementoSync), request.Type))
        {
            return BadRequest(new
            {
                message = "El tipo de elemento no es válido."
            });
        }

        var device = await GetDeviceAsync(
            deviceId,
            userId,
            cancellationToken);

        if (device is null)
            return Unauthorized();

        var normalizedPath = request.LocalPath.Trim();

        var duplicatePath = await _dbContext.ElementosSync
            .AnyAsync(
                x =>
                    x.DispositivoId == deviceId &&
                    x.RutaLocal == normalizedPath &&
                    x.Activo,
                cancellationToken);

        if (duplicatePath)
        {
            return Conflict(new
            {
                message = "Esta ruta local ya está sincronizada."
            });
        }

        Guid? remoteFolderId = null;
        Guid? remoteFileId = null;

        if (request.Type == TipoElementoSync.Carpeta)
        {
            if (!request.RemoteFolderId.HasValue ||
                request.RemoteFileId.HasValue)
            {
                return BadRequest(new
                {
                    message = "Para una carpeta debe indicarse únicamente RemoteFolderId."
                });
            }

            var remoteFolder = await _dbContext.Carpetas
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x =>
                        x.Id == request.RemoteFolderId.Value &&
                        !x.EnPapelera,
                    cancellationToken);

            if (remoteFolder is null)
            {
                return BadRequest(new
                {
                    message = "La carpeta remota no existe."
                });
            }

            if (!await BelongsToDeviceRootAsync(
                    remoteFolder.Id,
                    device.RootFolderId,
                    cancellationToken))
                return Forbid();

            if (!await _permissionService.HasPermissionAsync(
                    userId,
                    remoteFolder.Id,
                    PermisoCarpeta.Leer,
                    cancellationToken))
                return Forbid();

            var duplicateRemote = await _dbContext.ElementosSync
                .AnyAsync(
                    x =>
                        x.DispositivoId == deviceId &&
                        x.CarpetaRemotaId == remoteFolder.Id &&
                        x.Activo,
                    cancellationToken);

            if (duplicateRemote)
            {
                return Conflict(new
                {
                    message = "Esta carpeta remota ya está registrada para sincronización."
                });
            }

            remoteFolderId = remoteFolder.Id;
        }
        else
        {
            if (!request.RemoteFileId.HasValue ||
                request.RemoteFolderId.HasValue)
            {
                return BadRequest(new
                {
                    message = "Para un archivo debe indicarse únicamente RemoteFileId."
                });
            }

            var remoteFile = await _dbContext.Archivos
                .AsNoTracking()
                .Where(x =>
                    x.Id == request.RemoteFileId.Value &&
                    x.Estado == EstadoArchivo.Activo)
                .Select(x => new
                {
                    x.Id,
                    x.CarpetaId
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (remoteFile is null)
            {
                return BadRequest(new
                {
                    message = "El archivo remoto no existe."
                });
            }

            if (!await BelongsToDeviceRootAsync(
                    remoteFile.CarpetaId,
                    device.RootFolderId,
                    cancellationToken))
                return Forbid();

            if (!await _permissionService.HasPermissionAsync(
                    userId,
                    remoteFile.CarpetaId,
                    PermisoCarpeta.Leer,
                    cancellationToken))
                return Forbid();

            var duplicateRemote = await _dbContext.ElementosSync
                .AnyAsync(
                    x =>
                        x.DispositivoId == deviceId &&
                        x.ArchivoRemotoId == remoteFile.Id &&
                        x.Activo,
                    cancellationToken);

            if (duplicateRemote)
            {
                return Conflict(new
                {
                    message = "Este archivo remoto ya está registrado para sincronización."
                });
            }

            remoteFileId = remoteFile.Id;
        }

        var item = new ElementoSync
        {
            DispositivoId = deviceId,
            RutaLocal = normalizedPath,
            Tipo = request.Type,
            CarpetaRemotaId = remoteFolderId,
            ArchivoRemotoId = remoteFileId
        };

        _dbContext.ElementosSync.Add(item);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            syncItemId = item.Id,
            item.RutaLocal,
            item.Tipo,
            item.CarpetaRemotaId,
            item.ArchivoRemotoId,
            item.FechaCreacion
        });
    }

    [HttpPut("items/{syncItemId:guid}/path")]
    public async Task<IActionResult> UpdateItemPath(
        Guid syncItemId,
        UpdateSyncItemPathRequest request,
        CancellationToken cancellationToken)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();

        if (string.IsNullOrWhiteSpace(request.LocalPath))
        {
            return BadRequest(new
            {
                message = "La ruta local es obligatoria."
            });
        }

        var device = await GetDeviceAsync(
            deviceId,
            userId,
            cancellationToken);

        if (device is null)
            return Unauthorized();

        var item = await _dbContext.ElementosSync
            .FirstOrDefaultAsync(
                x =>
                    x.Id == syncItemId &&
                    x.DispositivoId == deviceId &&
                    x.Activo,
                cancellationToken);

        if (item is null)
            return NotFound();

        var normalizedPath = request.LocalPath.Trim();

        if (item.RutaLocal == normalizedPath)
            return NoContent();

        var duplicate = await _dbContext.ElementosSync
            .AsNoTracking()
            .AnyAsync(
                x =>
                    x.Id != syncItemId &&
                    x.DispositivoId == deviceId &&
                    x.RutaLocal == normalizedPath &&
                    x.Activo,
                cancellationToken);

        if (duplicate)
        {
            return Conflict(new
            {
                message = "Esta ruta local ya está sincronizada."
            });
        }

        item.RutaLocal = normalizedPath;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            syncItemId = item.Id,
            item.RutaLocal,
            item.Tipo,
            item.CarpetaRemotaId,
            item.ArchivoRemotoId
        });
    }

    [HttpPut("items/{syncItemId:guid}/synced")]
    public async Task<IActionResult> MarkItemSynchronized(
        Guid syncItemId,
        CancellationToken cancellationToken)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();

        var device = await GetDeviceAsync(
            deviceId,
            userId,
            cancellationToken);

        if (device is null)
            return Unauthorized();

        var item = await _dbContext.ElementosSync
            .FirstOrDefaultAsync(
                x =>
                    x.Id == syncItemId &&
                    x.DispositivoId == deviceId &&
                    x.Activo,
                cancellationToken);

        if (item is null)
            return NotFound();

        item.UltimaSincronizacionUtc = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            syncItemId = item.Id,
            item.UltimaSincronizacionUtc
        });
    }

    // Desasociar un elemento seleccionado de nuevo únicamente cuando su objeto
    // remoto anterior está confirmado en la papelera. No elimina archivos.
    [HttpPost("items/{syncItemId:guid}/detach-if-trashed")]
    public async Task<IActionResult> DetachIfTrashed(
        Guid syncItemId, CancellationToken cancellationToken)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();
        var device = await GetDeviceAsync(deviceId, userId, cancellationToken);
        if (device is null) return Unauthorized();
        if (!await _permissionService.HasPermissionAsync(
                userId, device.RootFolderId, PermisoCarpeta.Leer, cancellationToken))
            return Forbid();

        var item = await _dbContext.ElementosSync.FirstOrDefaultAsync(
            x => x.Id == syncItemId && x.DispositivoId == deviceId && x.Activo,
            cancellationToken);
        if (item is null) return NotFound();

        bool trashed;
        if (item.Tipo == TipoElementoSync.Carpeta && item.CarpetaRemotaId.HasValue)
        {
            trashed = await _dbContext.Carpetas.AnyAsync(
                x => x.Id == item.CarpetaRemotaId.Value && x.EnPapelera,
                cancellationToken);
        }
        else if (item.Tipo == TipoElementoSync.Archivo && item.ArchivoRemotoId.HasValue)
        {
            trashed = await _dbContext.Archivos.AnyAsync(
                x => x.Id == item.ArchivoRemotoId.Value &&
                     x.Estado == EstadoArchivo.EnPapelera, cancellationToken);
        }
        else return Conflict(new { message = "La asociación remota no es válida." });

        if (!trashed)
            return Conflict(new { message = "El contenido remoto no está confirmado en la papelera." });

        item.Activo = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    // Restaurar una selección raíz completa desde el agente autorizado.
    // No restaurar por nombre ambiguo ni mezclar árboles de distintos lotes.
    [HttpPost("restore-selection")]
    public async Task<IActionResult> RestoreSelection(
        RestoreSyncSelectionRequest request, CancellationToken cancellationToken)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();
        var device = await GetDeviceAsync(deviceId, userId, cancellationToken);
        if (device is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(request.LocalPath) ||
            !Path.IsPathFullyQualified(request.LocalPath))
            return BadRequest(new { message = "Ruta local inválida." });
        var localPath = Path.GetFullPath(request.LocalPath);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(localPath));
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { message = "La ruta no identifica una carpeta." });
        if (!await _permissionService.HasPermissionAsync(
            userId, device.RootFolderId, PermisoCarpeta.Crear, cancellationToken))
            return Forbid();

        await using var tx = await _dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken);
        var existing = await _dbContext.ElementosSync.FirstOrDefaultAsync(
            x => x.DispositivoId == deviceId && x.RutaLocal == localPath,
            cancellationToken);
        if (existing is not null && existing.Tipo != TipoElementoSync.Carpeta)
            return Conflict(new { message = "La ruta tiene una asociación incompatible." });

        var active = await _dbContext.Carpetas.AnyAsync(
            x => x.CarpetaPadreId == device.RootFolderId &&
                 x.Nombre == name && !x.EnPapelera, cancellationToken);
        if (active)
            return Conflict(new { message = "Ya existe una carpeta activa con ese nombre. No se sobrescribirá." });

        var candidates = await _dbContext.Carpetas
            .Where(x => x.CarpetaPadreId == device.RootFolderId &&
                        x.Nombre == name && x.EnPapelera)
            .ToListAsync(cancellationToken);
        if (candidates.Count != 1)
            return Conflict(new { message = "No existe una única carpeta eliminada que pueda restaurarse." });

        var root = candidates[0];
        if (!root.EliminacionLoteId.HasValue)
            return Conflict(new { message = "Esta eliminación antigua no tiene un lote verificable." });
        if (!await _permissionService.HasPermissionIncludingTrashAsync(
            userId, root.Id, PermisoCarpeta.Modificar, cancellationToken))
            return Forbid();

        var batch = root.EliminacionLoteId.Value;
        var folders = await _dbContext.Carpetas
            .Where(x => x.EnPapelera && x.EliminacionLoteId == batch)
            .ToListAsync(cancellationToken);
        var ids = folders.Select(x => x.Id).ToHashSet();
        if (folders.Count == 0 || folders.Count(x => x.Id == root.Id) != 1 ||
            folders.Any(x => x.Id != root.Id &&
                (!x.CarpetaPadreId.HasValue || !ids.Contains(x.CarpetaPadreId.Value))))
            return Conflict(new { message = "La estructura del lote no es válida." });

        var files = await _dbContext.Archivos
            .Where(x => x.Estado == EstadoArchivo.EnPapelera &&
                        x.EliminacionLoteId == batch)
            .ToListAsync(cancellationToken);
        if (files.Any(x => !ids.Contains(x.CarpetaId)))
            return Conflict(new { message = "El lote contiene archivos fuera de la carpeta." });

        if (existing is not null && existing.Activo &&
            existing.CarpetaRemotaId != root.Id)
            return Conflict(new { message = "La ruta ya está asociada a otra carpeta activa." });

        if (existing is null)
        {
            existing = new ElementoSync
            {
                DispositivoId = deviceId,
                RutaLocal = localPath,
                Tipo = TipoElementoSync.Carpeta,
                CarpetaRemotaId = root.Id,
                Activo = true
            };
            _dbContext.ElementosSync.Add(existing);
        }
        else
        {
            existing.CarpetaRemotaId = root.Id;
            existing.Activo = true;
        }

        var now = DateTime.UtcNow;
        foreach (var folder in folders)
        {
            folder.EnPapelera = false;
            folder.FechaEliminacion = null;
            folder.EliminacionLoteId = null;
            folder.FechaModificacion = now;
        }
        foreach (var file in files)
        {
            file.Estado = EstadoArchivo.Activo;
            file.FechaEliminacion = null;
            file.EliminacionLoteId = null;
            file.FechaModificacion = now;
        }
        await _changes.RegisterAsync(device.RootFolderId, null, root.Id,
            "FOLDER_CREATED", sourceDeviceId: deviceId,
            cancellationToken: cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return Ok(new { remoteFolderId = root.Id, syncItemId = existing.Id });
    }

    [HttpDelete("items/{syncItemId:guid}")]
    public async Task<IActionResult> RemoveItem(
        Guid syncItemId,
        CancellationToken cancellationToken)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();

        var device = await GetDeviceAsync(
            deviceId,
            userId,
            cancellationToken);

        if (device is null)
            return Unauthorized();

        var item = await _dbContext.ElementosSync
            .FirstOrDefaultAsync(
                x =>
                    x.Id == syncItemId &&
                    x.DispositivoId == deviceId &&
                    x.Activo,
                cancellationToken);

        if (item is null)
            return NoContent();

        item.Activo = false;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    [HttpGet("snapshot")]
    public async Task<IActionResult> Snapshot(
        CancellationToken cancellationToken)
    {
        var deviceId = CurrentDeviceId();
        var userId = CurrentUserId();

        var device = await GetDeviceAsync(
            deviceId,
            userId,
            cancellationToken);

        if (device is null)
            return Unauthorized();

        if (!await _permissionService.HasPermissionAsync(
                userId,
                device.RootFolderId,
                PermisoCarpeta.Leer,
                cancellationToken))
            return Forbid();

        var folders = await GetFolderTreeAsync(
            device.RootFolderId,
            userId,
            cancellationToken);

        var folderIds = folders
            .Select(x => x.Id)
            .ToList();

        var files = await _dbContext.Archivos
            .AsNoTracking()
            .Where(x =>
                folderIds.Contains(x.CarpetaId) &&
                x.Estado == EstadoArchivo.Activo)
            .OrderBy(x => x.CarpetaId)
            .ThenBy(x => x.Nombre)
            .Select(x => new
            {
                x.Id,
                x.Nombre,
                x.CarpetaId,
                x.Extension,
                x.TipoContenido,
                x.TamanoBytes,
                x.HashSha256,
                x.Version,
                x.FechaCreacion,
                x.FechaModificacion
            })
            .ToListAsync(cancellationToken);

        var lastChangeId = await _dbContext.CambiosSync
            .AsNoTracking()
            .Where(x => x.CarpetaRaizId == device.RootFolderId)
            .Select(x => (long?)x.Id)
            .MaxAsync(cancellationToken) ?? 0;

        return Ok(new
        {
            rootFolderId = device.RootFolderId,
            lastChangeId,
            folders = folders.Select(x => new
            {
                x.Id,
                x.Nombre,
                x.CarpetaPadreId,
                x.FechaCreacion,
                x.FechaModificacion
            }),
            files
        });
    }

    private async Task<List<Carpeta>> GetFolderTreeAsync(
        Guid rootFolderId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var result = new List<Carpeta>();
        var pending = new Queue<Guid>();
        var visited = new HashSet<Guid>();

        pending.Enqueue(rootFolderId);

        while (pending.Count > 0)
        {
            var currentId = pending.Dequeue();

            if (!visited.Add(currentId))
                continue;

            var folder = await _dbContext.Carpetas
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x =>
                        x.Id == currentId &&
                        !x.EnPapelera,
                    cancellationToken);

            if (folder is null)
                continue;

            var canRead = await _permissionService.HasPermissionAsync(
                userId,
                folder.Id,
                PermisoCarpeta.Leer,
                cancellationToken);

            if (!canRead)
                continue;

            result.Add(folder);

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

    private async Task<DeviceContext?> GetDeviceAsync(
        Guid deviceId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        return await _dbContext.DispositivosSync
            .AsNoTracking()
            .Where(x =>
                x.Id == deviceId &&
                x.UsuarioId == userId &&
                x.Activo)
            .Select(x => new DeviceContext(
                x.Id,
                x.CarpetaRaizId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<bool> BelongsToDeviceRootAsync(
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

    private sealed record DeviceContext(
        Guid Id,
        Guid RootFolderId);
}

public sealed record RegisterSyncItemRequest(
    string LocalPath,
    TipoElementoSync Type,
    Guid? RemoteFolderId,
    Guid? RemoteFileId);

public sealed record UpdateSyncItemPathRequest(
    string LocalPath);
public sealed record RestoreSyncSelectionRequest(string LocalPath);
