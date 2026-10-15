namespace GestorArchivos.Domain.Entities;

public class GrupoUsuario
{
    public Guid GrupoId { get; set; }

    public Grupo Grupo { get; set; } = null!;

    public Guid UsuarioId { get; set; }

    public DateTime FechaAsignacion { get; set; } = DateTime.UtcNow;
}