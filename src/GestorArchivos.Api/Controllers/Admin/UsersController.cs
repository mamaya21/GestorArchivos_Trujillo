using GestorArchivos.Infrastructure.Identity;
using GestorArchivos.Infrastructure.Identity.Seed;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GestorArchivos.Api.Controllers.Admin;

[ApiController]
[Authorize(Roles = IdentitySeeder.AdministratorRole)]
[Route("api/admin/users")]
public sealed class UsersController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;

    public UsersController(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var users = await _userManager.Users
            .AsNoTracking()
            .OrderBy(x => x.UserName)
            .ToListAsync();

        var result = new List<object>();

        foreach (var user in users)
        {
            var roles = await _userManager.GetRolesAsync(user);

            result.Add(new
            {
                user.Id,
                user.UserName,
                user.Email,
                user.Nombre,
                user.Apellido,
                user.Activo,
                user.FechaCreacion,
                user.UltimoAcceso,
                roles
            });
        }

        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var user = await _userManager.FindByIdAsync(id.ToString());

        if (user is null)
            return NotFound();

        var roles = await _userManager.GetRolesAsync(user);

        return Ok(new
        {
            user.Id,
            user.UserName,
            user.Email,
            user.Nombre,
            user.Apellido,
            user.Activo,
            user.FechaCreacion,
            user.UltimoAcceso,
            roles
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateUserRequest request)
    {
        var userName = request.UserName?.Trim();
        var email = request.Email?.Trim();
        var nombre = request.Nombre?.Trim();
        var apellido = request.Apellido?.Trim();

        if (string.IsNullOrWhiteSpace(userName) ||
            string.IsNullOrWhiteSpace(email) ||
            string.IsNullOrWhiteSpace(nombre) ||
            string.IsNullOrWhiteSpace(apellido) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new
            {
                message = "Todos los campos son obligatorios."
            });
        }

        if (await _userManager.FindByNameAsync(userName) is not null)
            return Conflict(new { message = "El usuario ya existe." });

        if (await _userManager.FindByEmailAsync(email) is not null)
            return Conflict(new { message = "El correo ya está registrado." });

        var role = NormalizeRole(request.Role);

        if (role is null)
            return BadRequest(new { message = "Rol inválido." });

        var user = new ApplicationUser
        {
            UserName = userName,
            Email = email,
            Nombre = nombre,
            Apellido = apellido,
            Activo = true,
            EmailConfirmed = true
        };

        var createResult = await _userManager.CreateAsync(
            user,
            request.Password);

        if (!createResult.Succeeded)
            return IdentityError(createResult);

        var roleResult = await _userManager.AddToRoleAsync(user, role);

        if (!roleResult.Succeeded)
        {
            await _userManager.DeleteAsync(user);
            return IdentityError(roleResult);
        }

        return CreatedAtAction(
            nameof(GetById),
            new { id = user.Id },
            new
            {
                user.Id,
                user.UserName,
                user.Email,
                user.Nombre,
                user.Apellido,
                user.Activo,
                role
            });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(
        Guid id,
        UpdateUserRequest request)
    {
        var user = await _userManager.FindByIdAsync(id.ToString());

        if (user is null)
            return NotFound();

        var email = request.Email?.Trim();
        var nombre = request.Nombre?.Trim();
        var apellido = request.Apellido?.Trim();

        if (string.IsNullOrWhiteSpace(email) ||
            string.IsNullOrWhiteSpace(nombre) ||
            string.IsNullOrWhiteSpace(apellido))
        {
            return BadRequest(new
            {
                message = "Nombre, apellido y correo son obligatorios."
            });
        }

        var emailOwner = await _userManager.FindByEmailAsync(email);

        if (emailOwner is not null && emailOwner.Id != id)
            return Conflict(new { message = "El correo ya está registrado." });

        user.Email = email;
        user.Nombre = nombre;
        user.Apellido = apellido;

        var result = await _userManager.UpdateAsync(user);

        return result.Succeeded
            ? NoContent()
            : IdentityError(result);
    }

    [HttpPut("{id:guid}/role")]
    public async Task<IActionResult> ChangeRole(
    Guid id,
    ChangeRoleRequest request)
    {
        var user = await _userManager.FindByIdAsync(id.ToString());

        if (user is null)
            return NotFound();

        var role = NormalizeRole(request.Role);

        if (role is null)
            return BadRequest(new { message = "Rol inválido." });

        var currentUserId = GetCurrentUserId();

        if (id == currentUserId &&
            role != IdentitySeeder.AdministratorRole)
        {
            return Conflict(new
            {
                message = "No puede quitarse su propio rol de Administrador."
            });
        }

        var currentRoles =
            await _userManager.GetRolesAsync(user);

        var managedRoles = currentRoles
            .Where(x =>
                x == IdentitySeeder.AdministratorRole ||
                x == IdentitySeeder.UserRole)
            .ToArray();

        if (!currentRoles.Contains(
                role,
                StringComparer.OrdinalIgnoreCase))
        {
            var addResult =
                await _userManager.AddToRoleAsync(
                    user,
                    role);

            if (!addResult.Succeeded)
                return IdentityError(addResult);
        }

        var rolesToRemove = managedRoles
            .Where(x =>
                !string.Equals(
                    x,
                    role,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (rolesToRemove.Length > 0)
        {
            var removeResult =
                await _userManager.RemoveFromRolesAsync(
                    user,
                    rolesToRemove);

            if (!removeResult.Succeeded)
            {
                if (!currentRoles.Contains(
                        role,
                        StringComparer.OrdinalIgnoreCase))
                {
                    await _userManager.RemoveFromRoleAsync(
                        user,
                        role);
                }

                return IdentityError(removeResult);
            }
        }

        var stampResult =
            await _userManager.UpdateSecurityStampAsync(user);

        if (!stampResult.Succeeded)
            return IdentityError(stampResult);

        return NoContent();
    }

    [HttpPut("{id:guid}/status")]
    public async Task<IActionResult> ChangeStatus(
        Guid id,
        ChangeStatusRequest request)
    {
        var user = await _userManager.FindByIdAsync(id.ToString());

        if (user is null)
            return NotFound();

        if (id == GetCurrentUserId() && !request.Active)
        {
            return Conflict(new
            {
                message = "No puede desactivar su propio usuario."
            });
        }

        user.Activo = request.Active;

        if (!request.Active)
        {
            await _userManager.UpdateSecurityStampAsync(user);
        }

        var result = await _userManager.UpdateAsync(user);

        return result.Succeeded
            ? NoContent()
            : IdentityError(result);
    }

    [HttpPost("{id:guid}/reset-password")]
    public async Task<IActionResult> ResetPassword(
        Guid id,
        ResetPasswordRequest request)
    {
        var user = await _userManager.FindByIdAsync(id.ToString());

        if (user is null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest(new
            {
                message = "La nueva contraseña es obligatoria."
            });
        }

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);

        var result = await _userManager.ResetPasswordAsync(
            user,
            token,
            request.NewPassword);

        if (!result.Succeeded)
            return IdentityError(result);

        await _userManager.UpdateSecurityStampAsync(user);

        return NoContent();
    }

    private Guid GetCurrentUserId()
    {
        var value = _userManager.GetUserId(User);

        if (!Guid.TryParse(value, out var id))
            throw new UnauthorizedAccessException();

        return id;
    }

    private static string? NormalizeRole(string? role)
    {
        if (string.Equals(
                role,
                IdentitySeeder.AdministratorRole,
                StringComparison.OrdinalIgnoreCase))
        {
            return IdentitySeeder.AdministratorRole;
        }

        if (string.Equals(
                role,
                IdentitySeeder.UserRole,
                StringComparison.OrdinalIgnoreCase))
        {
            return IdentitySeeder.UserRole;
        }

        return null;
    }

    private IActionResult IdentityError(IdentityResult result)
    {
        return BadRequest(new
        {
            errors = result.Errors.Select(x => new
            {
                x.Code,
                x.Description
            })
        });
    }
}

public sealed record CreateUserRequest(
    string UserName,
    string Email,
    string Nombre,
    string Apellido,
    string Password,
    string Role);

public sealed record UpdateUserRequest(
    string Email,
    string Nombre,
    string Apellido);

public sealed record ChangeRoleRequest(string Role);

public sealed record ChangeStatusRequest(bool Active);

public sealed record ResetPasswordRequest(string NewPassword);