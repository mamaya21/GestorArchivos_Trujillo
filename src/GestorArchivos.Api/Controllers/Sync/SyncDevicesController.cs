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

    [HttpPost("register")]
    public async Task<IActionResult> Register(
        RegisterSyncDeviceRequest request,
        CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();

        var deviceName = request.Name.Trim();

        if (string.IsNullOrWhiteSpace(deviceName))
            return BadRequest(new { message = "El nombre del dispositivo es obligatorio." });

        var duplicate = await _dbContext.DispositivosSync
            .AnyAsync(x =>
                x.UsuarioId == userId &&
                x.Nombre == deviceName &&
                x.Activo,
                cancellationToken);

        if (duplicate)
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
            SistemaOperativo = request.OperatingSystem.Trim(),
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
    string OperatingSystem);