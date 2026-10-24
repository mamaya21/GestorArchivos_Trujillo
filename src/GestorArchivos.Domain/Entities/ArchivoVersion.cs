namespace GestorArchivos.Domain.Entities;

public sealed class ArchivoVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ArchivoId { get; set; }
    public long Numero { get; set; }
    public string NombreFisico { get; set; } = string.Empty;
    public string HashSha256 { get; set; } = string.Empty;
    public long TamanoBytes { get; set; }
    public string TipoContenido { get; set; } = string.Empty;
    public DateTime FechaUtc { get; set; } = DateTime.UtcNow;
    public Guid UsuarioId { get; set; }
}
