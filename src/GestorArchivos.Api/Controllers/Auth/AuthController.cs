using GestorArchivos.Domain.Entities;
using GestorArchivos.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GestorArchivos.Api.Controllers.Auth;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly ApplicationDbContext _dbContext;

    public AuthController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ApplicationDbContext dbContext)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _dbContext = dbContext;
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var user = await _userManager.FindByNameAsync(request.UserName);

        if (user is null || !user.Activo)
        {
            await WriteAuditAsync(
                null,
                "LOGIN_FAILED",
                "Authentication",
                request.UserName ?? string.Empty);

            return Unauthorized();
        }

        var result = await _signInManager.PasswordSignInAsync(
            user,
            request.Password,
            isPersistent: false,
            lockoutOnFailure: true);

        if (result.IsLockedOut)
        {
            await WriteAuditAsync(
                user.Id,
                "LOGIN_LOCKED",
                "Authentication",
                user.Id.ToString());

            return StatusCode(StatusCodes.Status423Locked);
        }

        if (!result.Succeeded)
        {
            await WriteAuditAsync(
                user.Id,
                "LOGIN_FAILED",
                "Authentication",
                user.Id.ToString());

            return Unauthorized();
        }

        user.UltimoAcceso = DateTime.UtcNow;

        var updateResult =
            await _userManager.UpdateAsync(user);

        if (!updateResult.Succeeded)
        {
            await _signInManager.SignOutAsync();

            return StatusCode(
                StatusCodes.Status500InternalServerError);
        }

        await WriteAuditAsync(
            user.Id,
            "LOGIN_SUCCESS",
            "Authentication",
            user.Id.ToString());

        return Ok();
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var user = await _userManager.GetUserAsync(User);

        if (user is null || !user.Activo)
            return Unauthorized();

        return Ok(new
        {
            user.Id,
            user.UserName,
            user.Email,
            user.Nombre,
            user.Apellido,
            user.UltimoAcceso
        });
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(
        ChangePasswordRequest request)
    {
        var user = await _userManager.GetUserAsync(User);

        if (user is null || !user.Activo)
            return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.CurrentPassword) ||
            string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest(new
            {
                message =
                    "La contraseña actual y la nueva son obligatorias."
            });
        }

        if (request.CurrentPassword == request.NewPassword)
        {
            return BadRequest(new
            {
                message =
                    "La nueva contraseña debe ser diferente a la actual."
            });
        }

        var result =
            await _userManager.ChangePasswordAsync(
                user,
                request.CurrentPassword,
                request.NewPassword);

        if (!result.Succeeded)
        {
            await WriteAuditAsync(
                user.Id,
                "PASSWORD_CHANGE_FAILED",
                "Authentication",
                user.Id.ToString());

            return BadRequest(new
            {
                errors = result.Errors.Select(error => new
                {
                    error.Code,
                    error.Description
                })
            });
        }

        await _signInManager.RefreshSignInAsync(user);

        await WriteAuditAsync(
            user.Id,
            "PASSWORD_CHANGED",
            "Authentication",
            user.Id.ToString());

        return NoContent();
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var user = await _userManager.GetUserAsync(User);

        if (user is not null)
        {
            await WriteAuditAsync(
                user.Id,
                "LOGOUT",
                "Authentication",
                user.Id.ToString());
        }

        await _signInManager.SignOutAsync();

        return NoContent();
    }

    private async Task WriteAuditAsync(
        Guid? userId,
        string action,
        string entity,
        string entityId)
    {
        _dbContext.Auditorias.Add(
            new Auditoria
            {
                UsuarioId = userId,
                Accion = action,
                Entidad = entity,
                EntidadId = entityId,
                FechaUtc = DateTime.UtcNow
            });

        await _dbContext.SaveChangesAsync();
    }
}

public sealed record LoginRequest(
    string UserName,
    string Password);

public sealed record ChangePasswordRequest(
    string CurrentPassword,
    string NewPassword);