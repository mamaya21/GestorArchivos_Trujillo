using System.Security.Claims;
using System.Text;
using GestorArchivos.Api.Security;
using GestorArchivos.Application.Common.Interfaces;
using GestorArchivos.Application.Common.Security;
using GestorArchivos.Domain.Entities;
using GestorArchivos.Domain.Security;
using GestorArchivos.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GestorArchivos.Api.Controllers.Sync;

[ApiController]
[Authorize(AuthenticationSchemes = SyncDeviceAuthenticationHandler.SchemeName)]
[Route("api/sync/folders/atomic")]
public sealed class SyncAtomicFolderController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly ISyncChangeService _changes;
    private readonly IFolderPermissionService _permissions;

    public SyncAtomicFolderController(
        ApplicationDbContext db,
        ISyncChangeService changes,
        IFolderPermissionService permissions)
    {
        _db = db;
        _changes = changes;
        _permissions = permissions;
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        CreateAtomicSyncFolderRequest request,
        CancellationToken cancellationToken)
    {
        var deviceId = GetClaimGuid("device_id");
        var userId = GetClaimGuid(ClaimTypes.NameIdentifier);

        if (deviceId == Guid.Empty || userId == Guid.Empty)
            return Unauthorized();

        if (request.ParentFolderId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.LocalPath) ||
            string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Datos obligatorios incompletos." });

        if (request.Name.StartsWith(' ') ||
            request.Name.EndsWith(' ') ||
            request.Name.EndsWith('.') ||
            request.Name is "." or "..")
            return BadRequest(new { message = "Nombre de carpeta inválido." });

        var name = request.Name.Normalize(NormalizationForm.FormC);

        if (name.Length > 255 ||
            name.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0 ||
            name.Any(char.IsControl))
            return BadRequest(new { message = "Nombre de carpeta inválido." });

        var localPath = request.LocalPath.Trim();

        if (localPath.Length > 2048)
            return BadRequest(new { message = "La ruta local supera el límite permitido." });

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
                request.ParentFolderId,
                device.CarpetaRaizId,
                cancellationToken))
            return Forbid();

        if (!await _permissions.HasPermissionAsync(
                userId,
                request.ParentFolderId,
                PermisoCarpeta.Crear,
                cancellationToken))
            return Forbid();

        await using var transaction =
            await _db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable,
                cancellationToken);

        // Idempotencia: una ruta ya registrada debe devolver
        // su asociación existente, nunca crear otra carpeta.
        var existingItem = await _db.ElementosSync
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.DispositivoId == deviceId &&
                     x.RutaLocal == localPath,
                cancellationToken);

        if (existingItem is not null)
        {
            // Una ruta retirada y posteriormente seleccionada conserva su
            // registro histórico inactivo. Solo reutilizarlo si la carpeta
            // anterior está confirmada en papelera; nunca adoptar otra carpeta.
            if (!existingItem.Activo &&
                existingItem.Tipo == TipoElementoSync.Carpeta &&
                existingItem.CarpetaRemotaId.HasValue)
            {
                var previousTrashed = await _db.Carpetas
                    .AsNoTracking()
                    .AnyAsync(x => x.Id == existingItem.CarpetaRemotaId.Value &&
                                   x.EnPapelera, cancellationToken);
                if (!previousTrashed)
                    return Conflict(new { message = "La asociación anterior no está confirmada en la papelera." });

                // Reutilizar el registro evita violar la unicidad de
                // (DispositivoId, RutaLocal) y permite una nueva carpeta.
                var tracked = await _db.ElementosSync
                    .SingleAsync(x => x.Id == existingItem.Id, cancellationToken);
                var occupied = await _db.Carpetas.AsNoTracking().AnyAsync(
                    x => x.CarpetaPadreId == request.ParentFolderId &&
                         x.Nombre == name && !x.EnPapelera, cancellationToken);
                if (occupied)
                    return Conflict(new { message = "Ya existe una carpeta activa con ese nombre." });

                var replacement = new Carpeta
                {
                    Nombre = name,
                    CarpetaPadreId = request.ParentFolderId,
                    CreadoPorUsuarioId = userId
                };
                _db.Carpetas.Add(replacement);
                tracked.CarpetaRemotaId = replacement.Id;
                tracked.Activo = true;
                await _changes.RegisterAsync(request.ParentFolderId, null,
                    replacement.Id, "FOLDER_CREATED", sourceDeviceId: deviceId,
                    cancellationToken: cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Ok(new
                {
                    syncItemId = tracked.Id,
                    remoteFolderId = replacement.Id,
                    replacement.Nombre,
                    replacement.CarpetaPadreId,
                    alreadyExisted = false
                });
            }

            if (existingItem.Activo &&
                existingItem.Tipo == TipoElementoSync.Carpeta &&
                existingItem.CarpetaRemotaId.HasValue)
            {
                var oldFolder = await _db.Carpetas.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == existingItem.CarpetaRemotaId.Value,
                        cancellationToken);
                // Las subcarpetas de una selección enviada a papelera
                // mantienen su asociación activa. Solo reactivar si el
                // objeto antiguo está confirmado en papelera.
                if (oldFolder is not null && oldFolder.EnPapelera)
                {
                    var occupied = await _db.Carpetas.AsNoTracking().AnyAsync(
                        x => x.CarpetaPadreId == request.ParentFolderId &&
                             x.Nombre == name && !x.EnPapelera, cancellationToken);
                    if (occupied)
                        return Conflict(new { message = "Ya existe una carpeta activa con ese nombre." });
                    var tracked = await _db.ElementosSync.SingleAsync(
                        x => x.Id == existingItem.Id, cancellationToken);
                    var replacement = new Carpeta
                    {
                        Nombre = name,
                        CarpetaPadreId = request.ParentFolderId,
                        CreadoPorUsuarioId = userId
                    };
                    _db.Carpetas.Add(replacement);
                    tracked.CarpetaRemotaId = replacement.Id;
                    await _changes.RegisterAsync(request.ParentFolderId, null,
                        replacement.Id, "FOLDER_CREATED", sourceDeviceId: deviceId,
                        cancellationToken: cancellationToken);
                    await _db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return Ok(new
                    {
                        syncItemId = tracked.Id,
                        remoteFolderId = replacement.Id,
                        replacement.Nombre,
                        replacement.CarpetaPadreId,
                        alreadyExisted = false
                    });
                }
            }

            if (!existingItem.Activo ||
                existingItem.Tipo != TipoElementoSync.Carpeta ||
                !existingItem.CarpetaRemotaId.HasValue)
                return Conflict(new { message = "La ruta ya tiene otra asociación." });

            var existingFolder = await _db.Carpetas
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.Id == existingItem.CarpetaRemotaId.Value &&
                         !x.EnPapelera,
                    cancellationToken);

            if (existingFolder is null ||
                existingFolder.CarpetaPadreId != request.ParentFolderId ||
                existingFolder.Nombre != name)
                return Conflict(new { message = "La asociación existente no coincide." });

            return Ok(new
            {
                syncItemId = existingItem.Id,
                remoteFolderId = existingFolder.Id,
                existingFolder.Nombre,
                existingFolder.CarpetaPadreId,
                alreadyExisted = true
            });
        }

        // Una carpeta remota existente no se adopta automáticamente:
        // podría pertenecer a otro usuario o a otra selección.
        var duplicateFolder = await _db.Carpetas
            .AsNoTracking()
            .AnyAsync(
                x => x.CarpetaPadreId == request.ParentFolderId &&
                     x.Nombre == name &&
                     !x.EnPapelera,
                cancellationToken);

        if (duplicateFolder)
            return Conflict(new
            {
                message = "Ya existe una carpeta remota con ese nombre. No se creará otra."
            });

        var folder = new Carpeta
        {
            Nombre = name,
            CarpetaPadreId = request.ParentFolderId,
            CreadoPorUsuarioId = userId
        };

        var item = new ElementoSync
        {
            DispositivoId = deviceId,
            RutaLocal = localPath,
            Tipo = TipoElementoSync.Carpeta,
            CarpetaRemotaId = folder.Id
        };

        _db.Carpetas.Add(folder);
        _db.ElementosSync.Add(item);

        await _changes.RegisterAsync(
            request.ParentFolderId,
            null,
            folder.Id,
            "FOLDER_CREATED",
            sourceDeviceId: deviceId,
            cancellationToken: cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Ok(new
        {
            syncItemId = item.Id,
            remoteFolderId = folder.Id,
            folder.Nombre,
            folder.CarpetaPadreId,
            alreadyExisted = false
        });
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

            if (current.Value == rootId)
                return true;

            current = await _db.Carpetas
                .AsNoTracking()
                .Where(x => x.Id == current.Value && !x.EnPapelera)
                .Select(x => x.CarpetaPadreId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return false;
    }

    private Guid GetClaimGuid(string claimType)
    {
        return Guid.TryParse(
            User.FindFirstValue(claimType),
            out var value)
            ? value
            : Guid.Empty;
    }
}

public sealed record CreateAtomicSyncFolderRequest(
    Guid ParentFolderId,
    string Name,
    string LocalPath);