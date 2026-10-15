using GestorArchivos.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GestorArchivos.Api.Controllers.Admin;

[ApiController]
[Authorize(Roles = "Administrador")]
[Route("api/admin/audit")]
public sealed class AuditController : ControllerBase
{
    private readonly ApplicationDbContext _dbContext;

    public AuditController(
        ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (page < 1)
            page = 1;

        pageSize = Math.Clamp(
            pageSize,
            1,
            200);

        var query =
            _dbContext.Auditorias
                .AsNoTracking()
                .OrderByDescending(x => x.FechaUtc)
                .ThenByDescending(x => x.Id);

        var total =
            await query.CountAsync(
                cancellationToken);

        var items =
            await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(x => new
                {
                    x.Id,
                    x.UsuarioId,
                    x.Accion,
                    x.Entidad,
                    x.EntidadId,
                    x.DatosAnteriores,
                    x.DatosNuevos,
                    x.FechaUtc
                })
                .ToListAsync(
                    cancellationToken);

        return Ok(new
        {
            page,
            pageSize,
            total,
            items
        });
    }
}