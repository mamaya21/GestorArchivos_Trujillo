using GestorArchivos.Domain.Entities;
using GestorArchivos.Infrastructure.Identity;
using GestorArchivos.Infrastructure.Identity.Seed;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GestorArchivos.Api.Controllers.Admin;

[ApiController]
[Authorize(Roles = IdentitySeeder.AdministratorRole)]
[Route("api/admin/groups")]
public sealed class GroupsController : ControllerBase
{
    private readonly ApplicationDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;

    public GroupsController(
        ApplicationDbContext dbContext,
        UserManager<ApplicationUser> userManager)
    {
        _dbContext = dbContext;
        _userManager = userManager;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(
        CancellationToken cancellationToken)
    {
        var groups = await _dbContext.Grupos
            .AsNoTracking()
            .OrderBy(x => x.Nombre)
            .Select(x => new
            {
                x.Id,
                x.Nombre,
                x.Descripcion,
                x.Activo,
                x.FechaCreacion,
                CantidadUsuarios = x.Usuarios.Count
            })
            .ToListAsync(cancellationToken);

        return Ok(groups);
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        CreateGroupRequest request,
        CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim();

        if (string.IsNullOrWhiteSpace(name) || name.Length > 150)
            return BadRequest(new { message = "Nombre de grupo inválido." });

        var exists = await _dbContext.Grupos.AnyAsync(
            x => x.Nombre == name,
            cancellationToken);

        if (exists)
            return Conflict(new { message = "El grupo ya existe." });

        var group = new Grupo
        {
            Nombre = name,
            Descripcion = request.Description?.Trim()
        };

        _dbContext.Grupos.Add(group);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            group.Id,
            group.Nombre,
            group.Descripcion,
            group.Activo
        });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(
        Guid id,
        UpdateGroupRequest request,
        CancellationToken cancellationToken)
    {
        var group = await _dbContext.Grupos
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (group is null)
            return NotFound();

        var name = request.Name?.Trim();

        if (string.IsNullOrWhiteSpace(name) || name.Length > 150)
            return BadRequest(new { message = "Nombre de grupo inválido." });

        var duplicate = await _dbContext.Grupos.AnyAsync(
            x => x.Id != id && x.Nombre == name,
            cancellationToken);

        if (duplicate)
            return Conflict(new { message = "El grupo ya existe." });

        group.Nombre = name;
        group.Descripcion = request.Description?.Trim();
        group.Activo = request.Active;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    [HttpPost("{groupId:guid}/users/{userId:guid}")]
    public async Task<IActionResult> AddUser(
        Guid groupId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var groupExists = await _dbContext.Grupos.AnyAsync(
            x => x.Id == groupId && x.Activo,
            cancellationToken);

        if (!groupExists)
            return NotFound(new { message = "El grupo no existe o está inactivo." });

        var user = await _userManager.FindByIdAsync(userId.ToString());

        if (user is null || !user.Activo)
            return NotFound(new { message = "El usuario no existe o está inactivo." });

        var exists = await _dbContext.GruposUsuarios.AnyAsync(
            x => x.GrupoId == groupId && x.UsuarioId == userId,
            cancellationToken);

        if (exists)
            return NoContent();

        _dbContext.GruposUsuarios.Add(new GrupoUsuario
        {
            GrupoId = groupId,
            UsuarioId = userId
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    [HttpDelete("{groupId:guid}/users/{userId:guid}")]
    public async Task<IActionResult> RemoveUser(
        Guid groupId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var membership = await _dbContext.GruposUsuarios
            .FirstOrDefaultAsync(
                x => x.GrupoId == groupId &&
                     x.UsuarioId == userId,
                cancellationToken);

        if (membership is null)
            return NotFound();

        _dbContext.GruposUsuarios.Remove(membership);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    [HttpGet("{groupId:guid}/users")]
    public async Task<IActionResult> GetUsers(
    Guid groupId,
    CancellationToken cancellationToken)
    {
        var groupExists = await _dbContext.Grupos
            .AsNoTracking()
            .AnyAsync(
                x => x.Id == groupId,
                cancellationToken);

        if (!groupExists)
            return NotFound(new { message = "El grupo no existe." });

        var users = await _dbContext.GruposUsuarios
            .AsNoTracking()
            .Where(x => x.GrupoId == groupId)
            .Join(
                _userManager.Users,
                membership => membership.UsuarioId,
                user => user.Id,
                (membership, user) => new
                {
                    user.Id,
                    user.UserName,
                    user.Email,
                    user.Nombre,
                    user.Apellido,
                    user.Activo
                })
            .OrderBy(x => x.UserName)
            .ToListAsync(cancellationToken);

        return Ok(users);
    }
}

public sealed record CreateGroupRequest(
    string Name,
    string? Description);

public sealed record UpdateGroupRequest(
    string Name,
    string? Description,
    bool Active);