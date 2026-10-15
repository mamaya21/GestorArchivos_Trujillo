namespace GestorArchivos.Sync.Windows.Models;

public sealed class RemoteFolder
{
    public Guid Id { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public Guid? CarpetaPadreId { get; set; }
    public DateTime FechaCreacion { get; set; }
    public DateTime? FechaModificacion { get; set; }
}