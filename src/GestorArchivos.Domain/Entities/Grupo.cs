namespace GestorArchivos.Domain.Entities;

public class Grupo
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Nombre { get; set; } = string.Empty;

    public string? Descripcion { get; set; }

    public bool Activo { get; set; } = true;

    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

    public ICollection<GrupoUsuario> Usuarios { get; set; } =
        new List<GrupoUsuario>();

    public ICollection<PermisoCarpetaGrupo> Permisos { get; set; } =
        new List<PermisoCarpetaGrupo>();
}