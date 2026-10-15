using GestorArchivos.Domain.Security;

namespace GestorArchivos.Domain.Entities;

public class PermisoCarpetaGrupo
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CarpetaId { get; set; }

    public Carpeta Carpeta { get; set; } = null!;

    public Guid GrupoId { get; set; }

    public Grupo Grupo { get; set; } = null!;

    public PermisoCarpeta Permisos { get; set; }

    public bool HeredarSubcarpetas { get; set; } = true;

    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

    public Guid CreadoPorUsuarioId { get; set; }
}