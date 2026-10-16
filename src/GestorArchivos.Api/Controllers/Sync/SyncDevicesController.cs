using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using GestorArchivos.Domain.Entities;
using GestorArchivos.Domain.Security;
using GestorArchivos.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GestorArchivos.Api.Controllers.Sync;

[ApiController]
[Authorize]
[Route("api/sync/devices")]
public class SyncDevicesController : ControllerBase
{
    private readonly ApplicationDbContext _dbContext;

    public SyncDevicesController(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // Estado de los dispositivos del usuario. No exponer credenciales ni tokens.
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();

        var devices = await _dbContext.DispositivosSync
            .AsNoTracking()
            .Where(x => x.UsuarioId == userId)
            .OrderByDescending(x => x.FechaRegistro)
            .Select(x => new
            {
                deviceId = x.Id,
                deviceName = x.Nombre,
                rootFolderId = x.CarpetaRaizId,
                rootFolderName = x.CarpetaRaiz.Nombre,
                active = x.Activo,
                lastConnectionUtc = x.UltimaConexionUtc
            })
            .ToListAsync(cancellationToken);

        return Ok(devices);
    }

    [HttpPost("{deviceId:guid}/unlink")]
    public async Task<IActionResult> Unlink(Guid deviceId, CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        var device = await _dbContext.DispositivosSync
            .FirstOrDefaultAsync(x => x.Id == deviceId && x.UsuarioId == userId, cancellationToken);

        if (device is null)
            return NotFound();

        device.Activo = false;
        var credentials = await _dbContext.CredencialesDispositivosSync
            .Where(x => x.DispositivoId == deviceId && !x.Revocada)
            .ToListAsync(cancellationToken);

        foreach (var credential in credentials)
        {
            credential.Revocada = true;
            credential.FechaRevocacionUtc = DateTime.UtcNow;
        }

        // La carpeta raíz y los elementos remotos se conservan intactos.
        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(
        RegisterSyncDeviceRequest request,
        CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();

        if (request is null ||
            string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.OperatingSystem))
        {
            return BadRequest(new
            {
                message = "El nombre y el sistema operativo del dispositivo son obligatorios."
            });
        }

        var deviceName = request.Name.Trim();
        var operatingSystem = request.OperatingSystem.Trim();

        if (deviceName.Length > 255 || operatingSystem.Length > 255)
        {
            return BadRequest(new
            {
                message = "El nombre y el sistema operativo no pueden superar 255 caracteres."
            });
        }

        // Solo revincular cuando el usuario eligió expresamente el dispositivo anterior.
        var existing = request.ReuseDeviceId is Guid reuseId
            ? await _dbContext.DispositivosSync.FirstOrDefaultAsync(
                x => x.Id == reuseId && x.UsuarioId == userId, cancellationToken)
            : null;

        if (request.ReuseDeviceId.HasValue && existing is null)
            return NotFound(new { message = "No se encontró el dispositivo anterior en esta cuenta." });

        if (existing?.Activo == true)
            return Conflict(new { message = "El dispositivo seleccionado ya está activo." });

        if (existing is not null)
        {
            if (!string.Equals(existing.Nombre, deviceName, StringComparison.Ordinal))
                return BadRequest(new { message = "El nombre no coincide con el dispositivo seleccionado." });

            // Recuperar la misma raíz sin restaurar credenciales antiguas.
            var oldCredentials = await _dbContext.CredencialesDispositivosSync
                .Where(x => x.DispositivoId == existing.Id && !x.Revocada)
                .ToListAsync(cancellationToken);

            foreach (var old in oldCredentials)
            {
                old.Revocada = true;
                old.FechaRevocacionUtc = DateTime.UtcNow;
            }

            var renewedToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            existing.Activo = true;
            existing.SistemaOperativo = operatingSystem;
            _dbContext.CredencialesDispositivosSync.Add(new CredencialDispositivoSync
            {
                DispositivoId = existing.Id,
                TokenHash = ComputeSha256(renewedToken)
            });
            await _dbContext.SaveChangesAsync(cancellationToken);

            var existingRootName = await _dbContext.Carpetas
                .Where(x => x.Id == existing.CarpetaRaizId)
                .Select(x => x.Nombre)
                .SingleAsync(cancellationToken);

            return Ok(new
            {
                deviceId = existing.Id,
                deviceName = existing.Nombre,
                rootFolderId = existing.CarpetaRaizId,
                rootFolderName = existingRootName,
                token = renewedToken
            });
        }

        // Un alta nueva no puede duplicar un dispositivo activo.
        if (await _dbContext.DispositivosSync.AnyAsync(
            x => x.UsuarioId == userId && x.Nombre == deviceName && x.Activo,
            cancellationToken))
            return Conflict(new { message = "Ya existe un dispositivo activo con ese nombre." });

        var folderName = await GetAvailableRootFolderNameAsync(
            deviceName,
            cancellationToken);

        var rootFolder = new Carpeta
        {
            Nombre = folderName,
            CarpetaPadreId = null,
            CreadoPorUsuarioId = userId
        };

        var device = new DispositivoSync
        {
            UsuarioId = userId,
            Nombre = deviceName,
            SistemaOperativo = operatingSystem,
            CarpetaRaiz = rootFolder
        };

        var permission = new PermisoCarpetaUsuario
        {
            Carpeta = rootFolder,
            UsuarioId = userId,
            Permisos = PermisoCarpeta.ControlTotal,
            HeredarSubcarpetas = true,
            CreadoPorUsuarioId = userId
        };

        var rawToken = Convert.ToHexString(
            RandomNumberGenerator.GetBytes(32));

        var credential = new CredencialDispositivoSync
        {
            Dispositivo = device,
            TokenHash = ComputeSha256(rawToken)
        };

        _dbContext.Carpetas.Add(rootFolder);
        _dbContext.DispositivosSync.Add(device);
        _dbContext.PermisosCarpetasUsuarios.Add(permission);
        _dbContext.CredencialesDispositivosSync.Add(credential);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            deviceId = device.Id,
            deviceName = device.Nombre,
            rootFolderId = rootFolder.Id,
            rootFolderName = rootFolder.Nombre,
            token = rawToken
        });
    }

    private async Task<string> GetAvailableRootFolderNameAsync(
        string requestedName,
        CancellationToken cancellationToken)
    {
        var candidate = requestedName;
        var number = 2;

        while (await _dbContext.Carpetas.AnyAsync(
                   x => x.CarpetaPadreId == null &&
                        x.Nombre == candidate &&
                        !x.EnPapelera,
                   cancellationToken))
        {
            candidate = $"{requestedName} ({number++})";
        }

        return candidate;
    }

    private static string ComputeSha256(string value)
    {
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes(value));

        return Convert.ToHexString(bytes);
    }

    private Guid CurrentUserId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!Guid.TryParse(value, out var userId))
            throw new UnauthorizedAccessException();

        return userId;
    }
}

public sealed record RegisterSyncDeviceRequest(
    string Name,
    string OperatingSystem,
    Guid? ReuseDeviceId = null);