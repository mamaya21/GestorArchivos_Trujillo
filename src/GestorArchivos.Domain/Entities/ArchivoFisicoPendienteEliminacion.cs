
namespace GestorArchivos.Domain.Entities;

public sealed class ArchivoFisicoPendienteEliminacion
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string RutaFisica { get; set; } = string.Empty;

    public DateTime FechaRegistroUtc { get; set; } = DateTime.UtcNow;

    public DateTime EliminarDespuesUtc { get; set; }

    public int Intentos { get; set; }

    public string? UltimoError { get; set; }
}
