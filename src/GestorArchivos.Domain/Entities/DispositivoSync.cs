namespace GestorArchivos.Domain.Entities;

public class DispositivoSync
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UsuarioId { get; set; }

    public string Nombre { get; set; } = string.Empty;

    public string SistemaOperativo { get; set; } = string.Empty;

    public Guid CarpetaRaizId { get; set; }

    public Carpeta CarpetaRaiz { get; set; } = null!;

    public bool Activo { get; set; } = true;

    public DateTime FechaRegistro { get; set; } = DateTime.UtcNow;

    public DateTime? UltimaConexionUtc { get; set; }
}