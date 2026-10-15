namespace GestorArchivos.Domain.Entities;

public class Carpeta
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Nombre { get; set; } = string.Empty;

    public Guid? CarpetaPadreId { get; set; }

    public Carpeta? CarpetaPadre { get; set; }

    public ICollection<Carpeta> Subcarpetas { get; set; } = new List<Carpeta>();

    public ICollection<Archivo> Archivos { get; set; } = new List<Archivo>();

    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

    public DateTime? FechaModificacion { get; set; }

    public Guid CreadoPorUsuarioId { get; set; }

    public bool EnPapelera { get; set; }

    public DateTime? FechaEliminacion { get; set; }

    public Guid? EliminacionLoteId { get; set; }
}