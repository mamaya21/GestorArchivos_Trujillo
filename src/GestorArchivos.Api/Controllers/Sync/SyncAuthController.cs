using System.Security.Cryptography;
using System.Text;
using GestorArchivos.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GestorArchivos.Api.Controllers.Sync;

[ApiController]
[Route("api/sync/auth")]
public class SyncAuthController : ControllerBase
{
    private readonly ApplicationDbContext _dbContext;

    public SyncAuthController(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpPost("validate")]
    public async Task<IActionResult> Validate(
        ValidateSyncTokenRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
            return Unauthorized();

        var tokenHash = ComputeSha256(request.Token);

        var credential = await _dbContext.CredencialesDispositivosSync
            .Include(x => x.Dispositivo)
            .FirstOrDefaultAsync(
                x => x.TokenHash == tokenHash &&
                     !x.Revocada &&
                     x.FechaRevocacionUtc == null &&
                     x.Dispositivo.Activo,
                cancellationToken);

        if (credential is null)
            return Unauthorized();

        var userActive = await _dbContext.Users
            .AsNoTracking()
            .AnyAsync(
                x => x.Id == credential.Dispositivo.UsuarioId &&
                     x.Activo,
                cancellationToken);

        if (!userActive)
            return Unauthorized();

        var now = DateTime.UtcNow;

        credential.UltimoUsoUtc = now;
        credential.Dispositivo.UltimaConexionUtc = now;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            deviceId = credential.Dispositivo.Id,
            userId = credential.Dispositivo.UsuarioId,
            deviceName = credential.Dispositivo.Nombre,
            rootFolderId = credential.Dispositivo.CarpetaRaizId
        });
    }

    private static string ComputeSha256(string value)
    {
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes(value));

        return Convert.ToHexString(bytes);
    }
}

public sealed record ValidateSyncTokenRequest(string Token);