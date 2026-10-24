using System.Security.Claims;
using GestorArchivos.Api.Security;
using GestorArchivos.Application.Common.Security;
using GestorArchivos.Application.Common.Storage;
using GestorArchivos.Application.Common.Validation;
using GestorArchivos.Domain.Security;
using GestorArchivos.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GestorArchivos.Api.Controllers.Sync;

/// <summary>
/// Device-scoped staging for resumable transfers. This controller does not publish
/// files or create sync mappings: finalization must be atomic and is implemented separately.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = SyncDeviceAuthenticationHandler.SchemeName)]
[Route("api/sync/uploads")]
public sealed class SyncResumableUploadController : ControllerBase
{
    private const long MaxFileSize = 4L * 1024 * 1024 * 1024;
    private readonly ApplicationDbContext _db;
    private readonly IFolderPermissionService _permissions;
    private readonly IChunkUploadService _chunks;

    public SyncResumableUploadController(
        ApplicationDbContext db,
        IFolderPermissionService permissions,
        IChunkUploadService chunks)
    {
        _db = db;
        _permissions = permissions;
        _chunks = chunks;
    }

    [HttpPost("initialize")]
    public async Task<IActionResult> Initialize(
        [FromBody] InitializeSyncUploadRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryIdentity(out var deviceId, out var userId))
            return Unauthorized();

        if (!FileNameValidator.TryValidate(request.FileName, out var name, out var error))
            return BadRequest(new { message = error });

        if (request.FileSize <= 0 || request.FileSize > MaxFileSize)
            return BadRequest(new { message = "Tamaño inválido." });

        if (!await CanWriteAsync(deviceId, userId, request.FolderId, cancellationToken))
            return Forbid();

        var session = await _chunks.InitializeAsync(
            deviceId, request.FolderId, name, request.FileSize,
            "application/octet-stream", cancellationToken);
        return Ok(session);
    }

    [HttpGet("{uploadId:guid}/status")]
    public async Task<IActionResult> Status(Guid uploadId, CancellationToken cancellationToken)
    {
        if (!TryIdentity(out var deviceId, out var userId))
            return Unauthorized();

        var status = await _chunks.GetStatusAsync(uploadId, deviceId, cancellationToken);
        if (status is null)
            return NotFound();

        if (!await CanWriteAsync(deviceId, userId, status.FolderId, cancellationToken))
            return Forbid();

        return Ok(status);
    }

    [HttpPut("{uploadId:guid}/chunks/{index:int}")]
    [RequestSizeLimit(64L * 1024 * 1024)]
    public async Task<IActionResult> UploadChunk(
        Guid uploadId, int index, CancellationToken cancellationToken)
    {
        if (!TryIdentity(out var deviceId, out var userId))
            return Unauthorized();

        var status = await _chunks.GetStatusAsync(uploadId, deviceId, cancellationToken);
        if (status is null)
            return NotFound();

        if (!await CanWriteAsync(deviceId, userId, status.FolderId, cancellationToken))
            return Forbid();

        try
        {
            await _chunks.UploadChunkAsync(
                uploadId, deviceId, index, Request.Body,
                Request.ContentLength, cancellationToken);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidDataException ex)
        {
            return Conflict(new { message = ex.Message });
        }

        return NoContent();
    }

    [HttpDelete("{uploadId:guid}")]
    public async Task<IActionResult> Cancel(Guid uploadId, CancellationToken cancellationToken)
    {
        if (!TryIdentity(out var deviceId, out var userId))
            return Unauthorized();

        var status = await _chunks.GetStatusAsync(uploadId, deviceId, cancellationToken);
        if (status is null)
            return NotFound();

        if (!await CanWriteAsync(deviceId, userId, status.FolderId, cancellationToken))
            return Forbid();

        await _chunks.CancelAsync(uploadId, deviceId, cancellationToken);
        return NoContent();
    }

    private bool TryIdentity(out Guid deviceId, out Guid userId)
    {
        var validDevice = Guid.TryParse(
            User.FindFirstValue("device_id"), out deviceId);
        var validUser = Guid.TryParse(
            User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
        return validDevice && validUser;
    }

    private async Task<bool> CanWriteAsync(
        Guid deviceId, Guid userId, Guid folderId, CancellationToken cancellationToken)
    {
        var root = await _db.DispositivosSync.AsNoTracking()
            .Where(x => x.Id == deviceId && x.UsuarioId == userId && x.Activo)
            .Select(x => (Guid?)x.CarpetaRaizId)
            .FirstOrDefaultAsync(cancellationToken);

        if (!root.HasValue)
            return false;

        var visited = new HashSet<Guid>();
        Guid? current = folderId;
        var withinRoot = false;

        while (current.HasValue && visited.Add(current.Value))
        {
            var folder = await _db.Carpetas.AsNoTracking()
                .Where(x => x.Id == current.Value && !x.EnPapelera)
                .Select(x => new { x.Id, x.CarpetaPadreId })
                .FirstOrDefaultAsync(cancellationToken);

            if (folder is null)
                return false;

            if (folder.Id == root.Value)
            {
                withinRoot = true;
                break;
            }

            current = folder.CarpetaPadreId;
        }

        return withinRoot && await _permissions.HasPermissionAsync(
            userId, folderId, PermisoCarpeta.Crear, cancellationToken);
    }
}

public sealed record InitializeSyncUploadRequest(
    Guid FolderId, string FileName, long FileSize);
