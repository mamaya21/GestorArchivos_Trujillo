namespace GestorArchivos.Domain.Entities;

public class Auditoria
{
    public long Id { get; set; }

    public Guid? UsuarioId { get; set; }

    public string Accion { get; set; } = string.Empty;

    public string Entidad { get; set; } = string.Empty;

    public string EntidadId { get; set; } = string.Empty;

    public string? DatosAnteriores { get; set; }

    public string? DatosNuevos { get; set; }

    public DateTime FechaUtc { get; set; } = DateTime.UtcNow;
}