using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using GestorArchivos.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GestorArchivos.Api.Security;

public sealed class SyncDeviceAuthenticationHandler
    : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "SyncDevice";

    private readonly ApplicationDbContext _dbContext;

    public SyncDeviceAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApplicationDbContext dbContext)
        : base(options, logger, encoder)
    {
        _dbContext = dbContext;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authorization))
            return AuthenticateResult.NoResult();

        var value = authorization.ToString();

        if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = value["Bearer ".Length..].Trim();

        if (string.IsNullOrWhiteSpace(token))
            return AuthenticateResult.Fail("Token inválido.");

        var tokenHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        var credential = await _dbContext.CredencialesDispositivosSync
            .Include(x => x.Dispositivo)
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.TokenHash == tokenHash &&
                     !x.Revocada &&
                     x.FechaRevocacionUtc == null &&
                     x.Dispositivo.Activo,
                Context.RequestAborted);

        if (credential is null)
            return AuthenticateResult.Fail("Credencial inválida.");

        var userActive = await _dbContext.Users
            .AsNoTracking()
            .AnyAsync(
                x => x.Id == credential.Dispositivo.UsuarioId &&
                     x.Activo,
                Context.RequestAborted);

        if (!userActive)
            return AuthenticateResult.Fail("Usuario inactivo o inexistente.");

        var claims = new[]
        {
            new Claim(
                ClaimTypes.NameIdentifier,
                credential.Dispositivo.UsuarioId.ToString()),

            new Claim(
                "device_id",
                credential.Dispositivo.Id.ToString())
        };

        var identity = new ClaimsIdentity(claims, SchemeName);

        var principal = new ClaimsPrincipal(identity);

        var ticket = new AuthenticationTicket(
            principal,
            SchemeName);

        return AuthenticateResult.Success(ticket);
    }
}