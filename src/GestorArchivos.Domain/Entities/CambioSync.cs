namespace GestorArchivos.Domain.Entities;

public class CambioSync
{
    public long Id { get; set; }

    public Guid CarpetaRaizId { get; set; }

    public Guid? ArchivoId { get; set; }

    public Guid? CarpetaId { get; set; }

    public Guid UsuarioId { get; set; }

    public Guid? DispositivoOrigenId { get; set; }

    public string TipoCambio { get; set; } = string.Empty;

    public long? VersionArchivo { get; set; }

    public DateTime FechaUtc { get; set; } = DateTime.UtcNow;
}