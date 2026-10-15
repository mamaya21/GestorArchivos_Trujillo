using GestorArchivos.Domain.Enums;

namespace GestorArchivos.Domain.Entities;

public class Archivo
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Nombre { get; set; } = string.Empty;

    public string NombreFisico { get; set; } = string.Empty;

    public string Extension { get; set; } = string.Empty;

    public string TipoContenido { get; set; } = string.Empty;

    public long TamanoBytes { get; set; }

    public string HashSha256 { get; set; } = string.Empty;

    public long Version { get; set; } = 1;

    public Guid CarpetaId { get; set; }

    public Carpeta Carpeta { get; set; } = null!;

    public Guid CreadoPorUsuarioId { get; set; }

    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

    public DateTime? FechaModificacion { get; set; }

    public EstadoArchivo Estado { get; set; } = EstadoArchivo.Activo;

    public DateTime? FechaEliminacion { get; set; }

    public Guid? EliminacionLoteId { get; set; }
}