namespace GestorArchivos.Sync.Windows.Models;

public sealed class RemoteFile
{
    public Guid Id { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public Guid CarpetaId { get; set; }
    public string? Extension { get; set; }
    public string? TipoContenido { get; set; }
    public long TamanoBytes { get; set; }
    public string HashSha256 { get; set; } = string.Empty;
    public long Version { get; set; }
    public DateTime FechaCreacion { get; set; }
    public DateTime? FechaModificacion { get; set; }
}